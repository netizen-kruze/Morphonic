#!/usr/bin/env python3
"""Morphonic model export — turns RVC v2 voice checkpoints (.pth) and the
ContentVec encoder into the ONNX files the app runs.

    python tools/morphonic_export.py voice  MyVoice.pth [-o MyVoice.onnx]
    python tools/morphonic_export.py voice  f0G40k.pth --generator-sr 40000 -o base.onnx
    python tools/morphonic_export.py contentvec [-o contentvec-768-layer12.onnx]
    python tools/morphonic_export.py check  some.onnx

Needs Python 3.10+ with torch, onnx and onnxruntime (and transformers for
the ContentVec export): see tools/requirements-export.txt. Runs on the CPU;
no GPU needed. The app itself never needs Python — only this converter does.

A voice export carries an "morphonic" metadata entry (sample rate, speaker
count, whether it takes the skip_head input) so the app can load it without
guessing. Files exported by other tools (RVC WebUI, w-okada's client) load
too: the app measures their sample rate from a test pass.
"""
import argparse
import hashlib
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# ── standard RVC v2 training configurations, by sample rate ───────────
# Used when the checkpoint is a raw training generator (pretrained_v2/
# f0G*.pth, "model" state dict and no config) rather than an extracted
# voice ("weight" + "config"). Order follows the RVC config list.
STANDARD_V2 = {
    32000: dict(segment_size=40, upsample_rates=[10, 8, 2, 2], upsample_kernel_sizes=[20, 16, 4, 4]),
    40000: dict(segment_size=32, upsample_rates=[10, 10, 2, 2], upsample_kernel_sizes=[16, 16, 4, 4]),
    48000: dict(segment_size=36, upsample_rates=[12, 10, 2, 2], upsample_kernel_sizes=[24, 20, 4, 4]),
}


def standard_config(sr, spk_embed_dim):
    s = STANDARD_V2[sr]
    return [1025, s["segment_size"], 192, 192, 768, 2, 6, 3, 0, "1",
            [3, 7, 11], [[1, 3, 5], [1, 3, 5], [1, 3, 5]],
            s["upsample_rates"], 512, s["upsample_kernel_sizes"], spk_embed_dim, 256, sr]


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_checkpoint(path, unsafe):
    import torch
    # RVC checkpoints are pickles. weights_only=True refuses anything but
    # tensors and plain Python values — exactly what a genuine checkpoint
    # holds. A file that needs more is either damaged or not a checkpoint;
    # --unsafe-load lets it through for people who trust the file anyway.
    try:
        return torch.load(path, map_location="cpu", weights_only=True)
    except Exception as ex:
        if not unsafe:
            raise SystemExit(f"could not load {path} with weights_only=True ({ex}); "
                             "pass --unsafe-load only if you trust this file completely")
        return torch.load(path, map_location="cpu", weights_only=False)


# ── voice export ──────────────────────────────────────────────────────

def build_synth(config):
    import torch
    from torch import nn
    from rvc_infer_pack.models import (TextEncoder768, ResidualCouplingBlock, GeneratorNSF)

    (spec_channels, segment_size, inter_channels, hidden_channels, filter_channels,
     n_heads, n_layers, kernel_size, p_dropout, resblock, resblock_kernel_sizes,
     resblock_dilation_sizes, upsample_rates, upsample_initial_channel,
     upsample_kernel_sizes, spk_embed_dim, gin_channels, sr) = config

    class SynthesizerOnnx(nn.Module):
        # The RVC v2 inference graph: ContentVec features + pitch -> the
        # prior, the flow in reverse, the NSF-HiFiGAN decoder. The decoder
        # only sees the frames from skip_head on: everything before it is
        # context the encoder needed, and decoding it again every block
        # would be the single largest cost of real-time conversion.
        def __init__(self):
            super().__init__()
            self.enc_p = TextEncoder768(inter_channels, hidden_channels, filter_channels,
                                        n_heads, n_layers, kernel_size, p_dropout)
            self.dec = GeneratorNSF(inter_channels, resblock, resblock_kernel_sizes,
                                    resblock_dilation_sizes, upsample_rates, upsample_initial_channel,
                                    upsample_kernel_sizes, gin_channels=gin_channels, sr=sr, is_half=False)
            self.flow = ResidualCouplingBlock(inter_channels, hidden_channels, 5, 1, 3, gin_channels=gin_channels)
            self.emb_g = nn.Embedding(spk_embed_dim, gin_channels)

        def forward(self, feats, p_len, pitch, pitchf, sid, skip_head):
            g = self.emb_g(sid).unsqueeze(-1)
            m_p, logs_p, x_mask = self.enc_p(feats, pitch, p_len)
            z_p = (m_p + torch.exp(logs_p) * torch.randn_like(m_p) * 0.66666) * x_mask
            z = self.flow(z_p, x_mask, g=g, reverse=True) * x_mask
            n = z.size(2)
            keep = torch.arange(n, device=z.device)
            keep = keep[keep >= skip_head]
            z = torch.index_select(z, 2, keep)
            f0 = torch.index_select(pitchf, 1, keep)
            o = self.dec(z, f0, g=g)
            return torch.clamp(o[0, 0], -1.0, 1.0)

    return SynthesizerOnnx(), sr, spk_embed_dim


def cmd_voice(args):
    import torch
    import onnx
    t0 = time.time()
    cpt = load_checkpoint(args.model, args.unsafe_load)
    if not isinstance(cpt, dict):
        raise SystemExit("not an RVC checkpoint (no dictionary at the top level)")

    if "weight" in cpt and "config" in cpt:
        state = cpt["weight"]
        config = list(cpt["config"])
        version = cpt.get("version", "v1")
        f0 = int(cpt.get("f0", 1)) == 1
        kind = "extracted voice"
    elif "model" in cpt:
        state = cpt["model"]
        if not args.generator_sr:
            raise SystemExit("this is a raw training generator (no config inside); pass --generator-sr 32000|40000|48000")
        spk = int(state["emb_g.weight"].shape[0])
        config = standard_config(args.generator_sr, spk)
        version = "v2" if state["enc_p.emb_phone.weight"].shape[1] == 768 else "v1"
        f0 = "enc_p.emb_pitch.weight" in state
        kind = "training generator"
    else:
        raise SystemExit("unrecognised checkpoint layout (expected 'weight'+'config' or 'model')")

    if version != "v2":
        raise SystemExit(f"only RVC v2 voices (768-dim ContentVec) are supported; this is {version}")
    if not f0:
        raise SystemExit("only pitch-guided voices (f0 = 1) are supported; this one was trained without pitch")

    # The RVC export fixes the speaker count from the embedding, and the
    # sample rate may be stored as "40k".
    config[-3] = int(state["emb_g.weight"].shape[0])
    sr = config[-1]
    if isinstance(sr, str):
        sr = {"32k": 32000, "40k": 40000, "48k": 48000}[sr]
        config[-1] = sr
    sr = int(sr)
    hop = 1
    for r in config[12]:
        hop *= int(r)
    if hop * 100 != sr:
        raise SystemExit(f"unexpected upsampling ({hop} per 10 ms frame for {sr} Hz) — not a standard RVC v2 voice")

    print(f"{kind}: RVC {version}, {sr} Hz, {config[-3]} speaker(s), pitch-guided")
    net, sr, speakers = build_synth(config)
    missing, unexpected = net.load_state_dict(state, strict=False)
    missing = [k for k in missing if not k.startswith("enc_q.")]
    if missing:
        raise SystemExit("checkpoint is missing weights the synthesizer needs: " + ", ".join(missing[:8]))
    net.eval()

    T = 200
    feats = torch.randn(1, T, 768)
    p_len = torch.tensor([T], dtype=torch.int64)
    pitch = torch.randint(1, 255, (1, T), dtype=torch.int64)
    pitchf = torch.rand(1, T) * 200 + 80
    sid = torch.tensor([0], dtype=torch.int64)
    skip_head = torch.tensor(0, dtype=torch.int64)

    out = args.output or os.path.splitext(os.path.basename(args.model))[0] + ".onnx"
    print(f"exporting to {out} …")
    with torch.no_grad():
        torch.onnx.export(
            net, (feats, p_len, pitch, pitchf, sid, skip_head), out,
            input_names=["feats", "p_len", "pitch", "pitchf", "sid", "skip_head"],
            output_names=["audio"],
            dynamic_axes={"feats": {1: "frames"}, "pitch": {1: "frames"}, "pitchf": {1: "frames"}, "audio": {0: "samples"}},
            opset_version=17, do_constant_folding=True, dynamo=False)

    model = onnx.load(out)
    meta = {
        "format": "morphonic-rvc-voice", "formatVersion": 1, "rvcVersion": "v2", "sr": sr, "hop": hop,
        "f0": True, "embChannels": 768, "speakers": speakers, "skipHead": True,
        "name": args.name or os.path.splitext(os.path.basename(args.model))[0],
        "source": os.path.basename(args.model), "sourceSha256": sha256_of(args.model),
        "exporter": "morphonic_export.py",
    }
    del model.metadata_props[:]
    entry = model.metadata_props.add()
    entry.key = "morphonic"
    entry.value = json.dumps(meta)
    onnx.save(model, out)

    verify_voice(out, hop)
    print(f"done in {time.time() - t0:.1f} s: {out} ({os.path.getsize(out) / 1e6:.1f} MB), SHA-256 {sha256_of(out)}")


def verify_voice(path, hop):
    import numpy as np
    import onnxruntime as ort
    s = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    names = [i.name for i in s.get_inputs()]
    T = 300
    feed = {
        "feats": np.random.randn(1, T, 768).astype(np.float32),
        "p_len": np.array([T], dtype=np.int64),
        "pitch": np.random.randint(1, 255, (1, T)).astype(np.int64),
        "pitchf": (np.random.rand(1, T) * 200 + 80).astype(np.float32),
        "sid": np.array([0], dtype=np.int64),
    }
    full = s.run(["audio"], {**feed, "skip_head": np.array(0, dtype=np.int64)})[0]
    tail = s.run(["audio"], {**feed, "skip_head": np.array(200, dtype=np.int64)})[0]
    assert full.shape == (T * hop,), f"full output {full.shape}, expected {(T * hop,)}"
    assert tail.shape == (100 * hop,), f"skip_head output {tail.shape}, expected {(100 * hop,)} — the dynamic slice did not export"
    assert np.isfinite(full).all() and np.abs(full).max() > 1e-4, "silent or invalid output"
    print(f"verified: inputs {names}, {T} frames -> {full.shape[0]} samples; skip_head=200 -> {tail.shape[0]} samples")


# ── ContentVec export ─────────────────────────────────────────────────

def cmd_contentvec(args):
    import torch
    from torch import nn
    import onnx
    from transformers import HubertModel

    class HubertModelWithFinalProj(HubertModel):
        # The checkpoint keeps the legacy projection layer; RVC v2 uses
        # the last encoder layer directly, so it is never called here.
        def __init__(self, config):
            super().__init__(config)
            self.final_proj = nn.Linear(config.hidden_size, config.classifier_proj_size)

    src = args.source or "lengyue233/content-vec-best"
    print(f"loading ContentVec from {src} …")
    model = HubertModelWithFinalProj.from_pretrained(src, torch_dtype=torch.float32)
    model.eval()

    class Encoder(nn.Module):
        def __init__(self, m):
            super().__init__()
            self.m = m

        def forward(self, source):
            return self.m(input_values=source).last_hidden_state

    enc = Encoder(model)
    out = args.output or "contentvec-768-layer12.onnx"
    source = torch.randn(1, 16000)
    with torch.no_grad():
        reference = enc(source).numpy()
        torch.onnx.export(enc, (source,), out, input_names=["source"], output_names=["features"],
                          dynamic_axes={"source": {1: "samples"}, "features": {1: "frames"}},
                          opset_version=17, do_constant_folding=True, dynamo=False)
    m = onnx.load(out)
    del m.metadata_props[:]
    e = m.metadata_props.add()
    e.key = "morphonic"
    e.value = json.dumps({"format": "morphonic-contentvec", "layer": 12, "dim": 768, "sampleRate": 16000,
                          "source": src, "license": "MIT (ContentVec, auspicious3000; Transformers port lengyue233)"})
    onnx.save(m, out)

    import numpy as np
    import onnxruntime as ort
    s = ort.InferenceSession(out, providers=["CPUExecutionProvider"])
    got = s.run(["features"], {"source": source.numpy()})[0]
    assert got.shape == reference.shape, (got.shape, reference.shape)
    err = float(np.abs(got - reference).max())
    assert err < 1e-2, f"ONNX output differs from PyTorch by {err}"
    long = s.run(["features"], {"source": np.random.randn(1, 48000).astype(np.float32)})[0]
    print(f"verified: 16000 samples -> {got.shape[1]} frames (max diff {err:.2e}); 48000 samples -> {long.shape[1]} frames")
    print(f"done: {out} ({os.path.getsize(out) / 1e6:.1f} MB), SHA-256 {sha256_of(out)}")


# ── inspect ───────────────────────────────────────────────────────────

def cmd_check(args):
    import onnxruntime as ort
    s = ort.InferenceSession(args.model, providers=["CPUExecutionProvider"])
    print(args.model, f"({os.path.getsize(args.model) / 1e6:.1f} MB)")
    for i in s.get_inputs():
        print("  input ", i.name, i.type, i.shape)
    for o in s.get_outputs():
        print("  output", o.name, o.type, o.shape)
    for k, v in s.get_modelmeta().custom_metadata_map.items():
        print("  meta  ", k, "=", v)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    v = sub.add_parser("voice", help="export an RVC v2 .pth voice to ONNX")
    v.add_argument("model")
    v.add_argument("-o", "--output")
    v.add_argument("--name", help="display name stored in the file (default: the file name)")
    v.add_argument("--generator-sr", type=int, choices=sorted(STANDARD_V2), help="sample rate for a raw training generator checkpoint")
    v.add_argument("--unsafe-load", action="store_true", help="allow arbitrary pickles in the checkpoint (only for files you trust)")
    v.set_defaults(fn=cmd_voice)
    c = sub.add_parser("contentvec", help="export the ContentVec encoder (768-dim, layer 12) to ONNX")
    c.add_argument("-o", "--output")
    c.add_argument("--source", help="Transformers checkpoint folder or Hub id (default lengyue233/content-vec-best)")
    c.set_defaults(fn=cmd_contentvec)
    k = sub.add_parser("check", help="print an ONNX file's inputs, outputs and metadata")
    k.add_argument("model")
    k.set_defaults(fn=cmd_check)
    args = p.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
