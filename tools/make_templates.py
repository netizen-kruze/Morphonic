#!/usr/bin/env python3
"""Morphonic model templates: the build-time half of the in-app model assembly.

The app never downloads an ONNX file nobody publishes. For the content
encoder and the sample voice it downloads the ORIGINAL checkpoints (MIT,
Hugging Face), reads the tensors itself (src/Morphonic/Rvc/TorchCheckpoint.cs)
and appends them to a weightless ONNX graph it carries inside (the
"template"), following a "recipe" that says which checkpoint tensor each
graph tensor is and how the export transformed it (copied, transposed, or
weight-norm folded). This script makes those templates and recipes from an
export produced by morphonic_export.py, and reproduces the app's assembly in
Python so the result can be hash-pinned and checked against the export.

    python tools/make_templates.py template export.onnx checkpoint.pth -o src/Morphonic/Assets --name contentvec-768-layer12
    python tools/make_templates.py assemble src/Morphonic/Assets/contentvec-768-layer12.recipe.json checkpoint.pth -o out.onnx [--check export.onnx]

Assembly (here and in the app) is deterministic to the byte, so the
catalog pins the SHA-256 of the assembled file as well as of the download.
"""
import argparse
import hashlib
import json
import os
import sys
import time

import numpy as np

INLINE_LIMIT = 1024          # smaller tensors stay inside the template
FLOAT = 1                    # onnx.TensorProto.FLOAT


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_state(path):
    import torch
    ck = torch.load(path, map_location="cpu", weights_only=True)
    key = ""
    if isinstance(ck, dict):
        for k in ("model", "weight"):
            if k in ck and isinstance(ck[k], dict):
                ck, key = ck[k], k
                break
    state = {k: v.float().numpy() for k, v in ck.items() if hasattr(v, "numpy")}
    return state, key


# -- the recipe operations, exactly as the app computes them -----------
# copy:       float32 of the tensor, same element order (the graph may
#             give it another shape, e.g. a [512] norm weight as [512, 1])
# transpose:  the 2-D tensor transposed (nn.Linear weights on 3-D inputs
#             export as MatMul with the transposed weight)
# weightnorm: g * v / norm(v) with the norm over every axis but `dim`;
#             each norm is a sequential float64 sum of squares in memory
#             order, the scale g/norm is a float64, and each output is the
#             float64 product rounded once to float32. The app does the
#             same, so the bytes match.

def op_weightnorm(g, v, dim):
    v = np.ascontiguousarray(v, dtype=np.float32)
    g = np.ascontiguousarray(g, dtype=np.float32).ravel()
    n = v.shape[dim]
    outer = int(np.prod(v.shape[:dim], dtype=np.int64))
    inner = int(np.prod(v.shape[dim + 1:], dtype=np.int64))
    v3 = v.reshape(outer, n, inner).astype(np.float64)
    out = np.empty_like(v3)
    for k in range(n):
        sl = np.ascontiguousarray(v3[:, k, :]).ravel()
        norm = float(np.sqrt(np.cumsum(sl * sl)[-1])) if sl.size else 0.0
        scale = float(g[k]) / norm
        out[:, k, :] = v3[:, k, :] * scale
    return out.astype(np.float32).reshape(v.shape)


def apply_op(entry, state):
    op = entry["op"]
    if op == "copy":
        return np.ascontiguousarray(state[entry["src"][0]], dtype=np.float32).reshape(entry["dims"])
    if op == "transpose":
        return np.ascontiguousarray(state[entry["src"][0]].T, dtype=np.float32)
    if op == "weightnorm":
        return op_weightnorm(state[entry["src"][0]], state[entry["src"][1]], entry["dim"])
    raise SystemExit("unknown op " + op)


# -- protobuf fragments ------------------------------------------------
# A serialized protobuf message followed by another message of the same
# type parses as their merge: repeated fields concatenate and embedded
# messages merge recursively. So the assembled file is the template
# followed by one ModelProto{graph{initializer{...}}} fragment per tensor.

def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def ld(field, payload):
    return varint((field << 3) | 2) + varint(len(payload)) + payload


def tensor_fragment(name, dims, raw):
    t = b"".join(varint((1 << 3) | 0) + varint(int(d)) for d in dims)   # dims (field 1)
    t += varint((2 << 3) | 0) + varint(FLOAT)                            # data_type (field 2)
    t += ld(8, name.encode("utf-8"))                                     # name (field 8)
    t += ld(9, raw)                                                      # raw_data (field 9)
    return ld(7, ld(5, t))                                               # ModelProto.graph(7).initializer(5)


# -- template ----------------------------------------------------------

def cmd_template(args):
    import onnx
    from onnx import numpy_helper
    t0 = time.time()
    m = onnx.load(args.onnx)
    state, state_key = load_state(args.checkpoint)
    print("%d checkpoint tensors (%s), %d graph initializers" % (len(state), state_key or "top level", len(m.graph.initializer)))
    by_size = {}
    for k, v in state.items():
        by_size.setdefault(v.size, []).append(k)
    folds = {}
    for k in state:
        if k.endswith("weight_g"):
            base = k[: -len("weight_g")]
            vk = base + "weight_v"
            if vk in state:
                for dim in range(state[vk].ndim):
                    if state[k].size == state[vk].shape[dim]:
                        folds[(base + "weight", dim)] = (k, vk)

    recipe = []
    keep = []
    small = []
    external_bytes = 0
    for t in m.graph.initializer:
        a = numpy_helper.to_array(t)
        if a.nbytes < INLINE_LIMIT or t.data_type != FLOAT:
            # A voice template serves every voice of its configuration, so
            # a small tensor of the checkpoint must come from the user's
            # checkpoint too, not stay with this one's values (decided below).
            (small if args.voice and t.data_type == FLOAT else keep).append(t)
            continue
        hit = None
        for k in by_size.get(a.size, []):
            s = state[k]
            if np.array_equal(s.ravel(), a.ravel()):      # same elements, same order (a reshape is free)
                hit = {"op": "copy", "src": [k]}
                break
            if s.ndim == 2 and s.T.shape == a.shape and np.array_equal(s.T, a):
                hit = {"op": "transpose", "src": [k]}
                break
        if hit is None:
            for (wname, dim), (gk, vk) in folds.items():
                if state[vk].shape != a.shape:
                    continue
                f = op_weightnorm(state[gk], state[vk], dim)
                d = float(np.abs(f - a).max())
                if d < 1e-4:
                    hit = {"op": "weightnorm", "dim": dim, "src": [gk, vk], "maxDiffFromExport": d}
                    break
        if hit is None:
            raise SystemExit("no checkpoint tensor matches initializer %s %s" % (t.name, a.shape))
        if args.voice and hit["src"][0] == "emb_g.weight":
            hit["dimsFromSource"] = True
        recipe.append({"name": t.name, "dims": list(a.shape), **hit})
        external_bytes += a.nbytes
    if small:
        linked, inline = link_small(small, state, recipe)
        recipe.extend(linked)
        keep.extend(inline)
    del m.graph.initializer[:]
    m.graph.initializer.extend(keep)
    if args.voice:
        del m.metadata_props[:]
        state_key = "weight"

    os.makedirs(args.out, exist_ok=True)
    template = os.path.join(args.out, args.name + ".template")
    onnx.save(m, template)
    ops = {}
    for e in recipe:
        ops[e["op"]] = ops.get(e["op"], 0) + 1
    meta = {
        "format": "morphonic-model-recipe", "formatVersion": 1,
        "template": os.path.basename(template),
        "templateSha256": sha256_of(template),
        "source": {"file": os.path.basename(args.checkpoint), "sha256": sha256_of(args.checkpoint),
                   "sizeBytes": os.path.getsize(args.checkpoint), "stateKey": state_key},
        "export": {"file": os.path.basename(args.onnx), "sha256": sha256_of(args.onnx)},
        "tensors": recipe,
    }
    with open(os.path.join(args.out, args.name + ".recipe.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1)
    print("template %s: %s bytes inline (%d small tensors); %d tensors / %s bytes from the checkpoint %s; %.1f s"
          % (template, format(os.path.getsize(template), ","), len(keep), len(recipe), format(external_bytes, ","), ops, time.time() - t0))


def link_small(small, state, recipe):
    """The small float initializers of a voice template that are checkpoint
    tensors, as recipe entries; the rest (graph constants) stay inline.
    Matched by name (the exporter keeps parameter names), and a renamed one
    ("onnx::MatMul_…", a transposed Linear) by elimination: the only
    checkpoint tensor of its size that nothing else uses. Values are never
    used to match: a small tensor's values say nothing about which voice
    tensor it is (zeros, ones and single numbers recur)."""
    from onnx import numpy_helper
    used = {s for e in recipe for s in e["src"]}
    linked, inline, unnamed = [], [], []
    for t in small:
        a = numpy_helper.to_array(t)
        if t.name in state and state[t.name].size == a.size:
            linked.append({"name": t.name, "dims": list(a.shape), "op": "copy", "src": [t.name]})
            used.add(t.name)
        else:
            unnamed.append((t, a))
    for t, a in unnamed:
        free = [k for k, v in state.items() if k not in used and v.size == a.size and not k.startswith("enc_q.")
                and not k.endswith(("weight_g", "weight_v"))]
        if len(free) != 1:
            inline.append(t)          # a graph constant (or nothing decidable): stays as it is
            continue
        k = free[0]
        s = state[k]
        op = "transpose" if s.ndim == 2 and a.ndim == 2 and s.shape != a.shape and s.T.shape == a.shape else "copy"
        if s.ndim == 2 and a.ndim == 2 and s.shape == a.shape and s.shape[0] == s.shape[1] and s.shape[0] > 1:
            raise SystemExit("cannot tell whether %s is %s or its transpose" % (t.name, k))
        linked.append({"name": t.name, "dims": list(a.shape), "op": op, "src": [k]})
        used.add(k)
    return linked, inline


def cmd_relink(args):
    """Fixes an existing voice template: its small checkpoint tensors move from
    inline (the template source's values) to the recipe (the user's)."""
    import onnx
    from onnx import numpy_helper
    with open(args.recipe, encoding="utf-8") as f:
        meta = json.load(f)
    template = os.path.join(os.path.dirname(args.recipe), meta["template"])
    if sha256_of(template) != meta["templateSha256"]:
        raise SystemExit("the template does not match its recipe")
    state, _ = load_state(args.checkpoint)
    m = onnx.load(template)
    small = [t for t in m.graph.initializer if t.data_type == FLOAT]
    other = [t for t in m.graph.initializer if t.data_type != FLOAT]
    linked, inline = link_small(small, state, meta["tensors"])
    del m.graph.initializer[:]
    m.graph.initializer.extend(other + inline)
    onnx.save(m, template)
    meta["tensors"].extend(linked)
    meta["templateSha256"] = sha256_of(template)
    with open(args.recipe, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1)
    print("%s: %d small tensors now come from the checkpoint (%s); %d stay inline: %s"
          % (os.path.basename(template), len(linked), ", ".join(sorted({e["op"] for e in linked})), len(inline), [t.name for t in inline]))


def deterministic_session(path):
    """An ONNX Runtime session of a voice with its sampling switched off:
    every Random*Like node yields zeros, so two files that hold the same
    function give the same audio and can be compared exactly."""
    import onnx
    import onnxruntime as ort
    m = onnx.load(path)
    for n in m.graph.node:
        if n.op_type in ("RandomNormalLike", "RandomUniformLike"):
            x = n.input[0]
            n.op_type = "Sub"
            del n.attribute[:]
            del n.input[:]
            n.input.extend([x, x])
    return ort.InferenceSession(m.SerializeToString(), providers=["CPUExecutionProvider"])


# -- assemble (what the app does) --------------------------------------

def assemble(recipe_path, checkpoint, out):
    with open(recipe_path, encoding="utf-8") as f:
        recipe = json.load(f)
    template = os.path.join(os.path.dirname(recipe_path), recipe["template"])
    state, _ = load_state(checkpoint)
    h = hashlib.sha256()
    size = 0
    with open(out, "wb") as o:
        with open(template, "rb") as t:
            data = t.read()
        o.write(data)
        h.update(data)
        size += len(data)
        for e in recipe["tensors"]:
            a = apply_op(e, state)
            assert list(a.shape) == e["dims"], (e["name"], a.shape, e["dims"])
            frag = tensor_fragment(e["name"], e["dims"], a.tobytes())
            o.write(frag)
            h.update(frag)
            size += len(frag)
    return size, h.hexdigest()


def cmd_assemble(args):
    t0 = time.time()
    size, digest = assemble(args.recipe, args.checkpoint, args.output)
    print("assembled %s: %s bytes, SHA-256 %s (%.1f s)" % (args.output, format(size, ","), digest, time.time() - t0))
    if args.check:
        # Sampling off on both sides: the same function must give the same
        # output, so a wrong tensor cannot hide in the noise.
        a = deterministic_session(args.output)
        b = deterministic_session(args.check)
        rng = np.random.default_rng(7)
        feed = {}
        for i in a.get_inputs():
            shape = [d if isinstance(d, int) else 300 for d in i.shape]
            if i.name == "skip_head":
                feed[i.name] = np.array(0, dtype=np.int64)
            elif i.name in ("p_len", "phone_lengths"):
                feed[i.name] = np.array([300], dtype=np.int64)
            elif i.name in ("sid", "ds"):
                feed[i.name] = np.array([0], dtype=np.int64)
            elif i.name == "pitch":
                feed[i.name] = rng.integers(1, 255, size=shape).astype(np.int64)
            elif i.name == "pitchf":
                feed[i.name] = (rng.random(shape) * 200 + 80).astype(np.float32)
            elif i.name == "source":
                feed[i.name] = rng.standard_normal([1, 48000]).astype(np.float32)
            else:
                feed[i.name] = rng.standard_normal(shape).astype(np.float32)
        ya = a.run(None, feed)[0]
        yb = b.run(None, feed)[0]
        d = float(np.abs(ya - yb).max())
        print("check vs %s (sampling off): output %s, max |diff| %.3e (output peak %.3f)" % (args.check, ya.shape, d, float(np.abs(yb).max())))
        if d > 1e-3:
            raise SystemExit("the assembled model does not compute what the export does")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("template", help="split an export into a weightless template and a recipe")
    t.add_argument("onnx")
    t.add_argument("checkpoint")
    t.add_argument("-o", "--out", required=True, help="folder for <name>.template and <name>.recipe.json")
    t.add_argument("--name", required=True)
    t.add_argument("--voice", action="store_true",
                   help="a template for user voices of this configuration: tensors are read from the checkpoint's "
                        "'weight' dict, the speaker table takes the checkpoint's size, and the metadata is left to the app")
    t.set_defaults(fn=cmd_template)
    a = sub.add_parser("assemble", help="assemble a model from a recipe and its checkpoint, as the app does")
    a.add_argument("recipe")
    a.add_argument("checkpoint")
    a.add_argument("-o", "--output", required=True)
    a.add_argument("--check", help="an export to compare the assembled model's output with")
    a.set_defaults(fn=cmd_assemble)
    r = sub.add_parser("relink", help="fix a voice template made before small tensors were read from the checkpoint")
    r.add_argument("recipe")
    r.add_argument("checkpoint", help="any voice checkpoint of the template's configuration (only names and sizes are used)")
    r.set_defaults(fn=cmd_relink)
    args = p.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
