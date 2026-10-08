using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;

namespace Morphonic.Rvc;

// What a voice ONNX file says about itself, read without loading it: the
// "morphonic" metadata entry tools/morphonic_export.py writes, or the "metadata"
// entry w-okada's exporter writes. Absent both, the sample rate is
// measured from a probe pass when the voice is loaded.
public sealed record VoiceMetadata(string Name, int SampleRate, int Speakers, bool SkipHead, string Source, int Version)
{
    public static readonly VoiceMetadata Unknown = new("", 0, 0, false, "", 2);
}

// An RVC v2 voice: ContentVec features and pitch in, audio at the voice's
// sample rate out. Two input conventions are understood:
//   feats / p_len / pitch / pitchf / sid [/ skip_head]  — tools/morphonic_export.py and w-okada's client
//   phone / phone_lengths / pitch / pitchf / ds / rnd  — the RVC WebUI's own export
// With skip_head the decoder runs on the tail only; without it the whole
// window is synthesised and the tail is cut out afterwards.
public sealed class VoiceModel : IDisposable
{
    public const int FeatureDim = 768, NoiseDim = 192;

    public string Path { get; }
    public VoiceMetadata Metadata { get; }
    public int SampleRate { get; private set; }
    public int Hop { get; private set; }          // samples per 10 ms frame
    public bool HasSkipHead { get; }
    public string Convention { get; }

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly string _nFeats, _nLen, _nPitch, _nPitchf, _nSid, _nSkip = "", _nRnd = "", _nOut;
    private readonly Random _noise = new(1234);

    public VoiceModel(string path)
    {
        Path = path;
        Metadata = ReadMetadata(path);
        _session = new InferenceSession(path, OnnxHost.CreateSessionOptions());
        var inputs = _session.InputMetadata;
        if (inputs.ContainsKey("feats"))
        {
            Convention = inputs.ContainsKey("skip_head") ? "morphonic" : "vcclient";
            _nFeats = "feats"; _nLen = "p_len"; _nPitch = "pitch"; _nPitchf = "pitchf"; _nSid = "sid";
            if (inputs.ContainsKey("skip_head")) { _nSkip = "skip_head"; HasSkipHead = true; }
        }
        else if (inputs.ContainsKey("phone"))
        {
            Convention = "rvc-webui";
            _nFeats = "phone"; _nLen = "phone_lengths"; _nPitch = "pitch"; _nPitchf = "pitchf"; _nSid = "ds";
            if (inputs.ContainsKey("rnd")) _nRnd = "rnd";
        }
        else
            throw new InvalidOperationException("not an RVC voice export (inputs: " + string.Join(", ", inputs.Keys) + ")");
        if (!inputs.ContainsKey(_nPitch) || !inputs.ContainsKey(_nPitchf))
            throw new InvalidOperationException("this voice was exported without pitch inputs; only pitch-guided (f0) voices are supported");
        var feats = inputs[_nFeats];
        if (feats.ElementDataType != TensorElementType.Float)
            throw new InvalidOperationException("this voice export is half precision; export it in float32 (tools/morphonic_export.py)");
        if (feats.Dimensions.Length == 3 && feats.Dimensions[2] > 0 && feats.Dimensions[2] != FeatureDim)
            throw new InvalidOperationException($"this voice takes {feats.Dimensions[2]}-d features — an RVC v1 voice; only v2 (768-d) is supported");
        _nOut = _session.OutputMetadata.ContainsKey("audio") ? "audio" : _session.OutputMetadata.Keys.First();

        SampleRate = Metadata.SampleRate;
        Hop = SampleRate / 100;
        if (SampleRate <= 0) Probe();
    }

    // One short pass to learn the sample rate: 10 ms frames in, so the
    // samples out per frame are the hop, and the rate is hop * 100.
    private void Probe()
    {
        const int T = 40;
        var feats = new float[T * FeatureDim];
        var pitch = new long[T];
        var pitchf = new float[T];
        for (int i = 0; i < T; i++) { pitch[i] = 120; pitchf[i] = 150f; }
        var audio = Run(feats, T, pitch, pitchf, 0, 0);
        int hop = audio.Length / T;
        int sr = hop * 100;
        if (sr is not (32000 or 40000 or 44100 or 48000))
            throw new InvalidOperationException($"could not tell this voice's sample rate (probe gave {audio.Length} samples for {T} frames)");
        Hop = hop;
        SampleRate = sr;
    }

    // Audio for frames [skipHead, frames): (frames - skipHead) * Hop samples.
    public float[] Synthesize(float[] feats, int frames, long[] pitch, float[] pitchf, int sid, int skipHead)
    {
        if (HasSkipHead) return Run(feats, frames, pitch, pitchf, sid, skipHead);
        var all = Run(feats, frames, pitch, pitchf, sid, 0);
        int start = Math.Min(all.Length, skipHead * Hop);
        return all.AsSpan(start).ToArray();
    }

    private float[] Run(float[] feats, int frames, long[] pitch, float[] pitchf, int sid, int skipHead)
    {
        var names = new List<string> { _nFeats, _nLen, _nPitch, _nPitchf, _nSid };
        var values = new List<OrtValue>
        {
            OrtValue.CreateTensorValueFromMemory(feats, new long[] { 1, frames, FeatureDim }),
            OrtValue.CreateTensorValueFromMemory(new long[] { frames }, new long[] { 1 }),
            OrtValue.CreateTensorValueFromMemory(pitch, new long[] { 1, frames }),
            OrtValue.CreateTensorValueFromMemory(pitchf, new long[] { 1, frames }),
            OrtValue.CreateTensorValueFromMemory(new long[] { sid }, new long[] { 1 }),
        };
        if (HasSkipHead)
        {
            names.Add(_nSkip);
            values.Add(OrtValue.CreateTensorValueFromMemory(new long[] { skipHead }, Array.Empty<long>()));
        }
        if (_nRnd.Length > 0)
        {
            // The WebUI export takes the prior's noise from outside; the
            // in-graph exports use 0.66666 × N(0, 1), so this does too.
            var rnd = new float[NoiseDim * frames];
            for (int i = 0; i < rnd.Length; i += 2)
            {
                double u1 = 1.0 - _noise.NextDouble(), u2 = _noise.NextDouble();
                double r = Math.Sqrt(-2.0 * Math.Log(u1));
                rnd[i] = (float)(0.66666 * r * Math.Cos(2 * Math.PI * u2));
                if (i + 1 < rnd.Length) rnd[i + 1] = (float)(0.66666 * r * Math.Sin(2 * Math.PI * u2));
            }
            names.Add(_nRnd);
            values.Add(OrtValue.CreateTensorValueFromMemory(rnd, new long[] { 1, NoiseDim, frames }));
        }
        try
        {
            using var results = _session.Run(_run, names, values, new[] { _nOut });
            var span = results[0].GetTensorDataAsSpan<float>();
            var audio = span.ToArray();
            for (int i = 0; i < audio.Length; i++) audio[i] = Math.Clamp(audio[i], -1f, 1f);
            return audio;
        }
        finally
        {
            foreach (var v in values) v.Dispose();
        }
    }

    public void Dispose()
    {
        _run.Dispose();
        _session.Dispose();
    }

    // ── metadata without loading the graph ─────────────────────────

    // An ONNX file is a protobuf ModelProto; its top-level fields are walked
    // and only metadata_props (field 14, repeated StringStringEntryProto:
    // key = 1, value = 2) is read. The graph (field 7, hundreds of MB) is
    // skipped by seeking, so this costs nothing for a library listing.
    public static VoiceMetadata ReadMetadata(string path)
    {
        try
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            using var f = File.OpenRead(path);
            while (f.Position < f.Length)
            {
                if (!TryReadVarint(f, out var tag)) break;
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                switch (wire)
                {
                    case 0: TryReadVarint(f, out _); break;
                    case 1: f.Seek(8, SeekOrigin.Current); break;
                    case 5: f.Seek(4, SeekOrigin.Current); break;
                    case 2:
                        if (!TryReadVarint(f, out var len)) return VoiceMetadata.Unknown;
                        if (field == 14 && len < 1_000_000)
                        {
                            var buf = new byte[len];
                            if (f.Read(buf, 0, (int)len) != (int)len) return VoiceMetadata.Unknown;
                            var (k, v) = ParseStringEntry(buf);
                            if (k.Length > 0) props[k] = v;
                        }
                        else f.Seek((long)len, SeekOrigin.Current);
                        break;
                    default: return FromProps(props, path);
                }
            }
            return FromProps(props, path);
        }
        catch { return VoiceMetadata.Unknown; }
    }

    private static VoiceMetadata FromProps(Dictionary<string, string> props, string path)
    {
        try
        {
            if (props.TryGetValue("morphonic", out var morphonic))
            {
                var j = JObject.Parse(morphonic);
                if (j["format"]?.ToString() == "morphonic-rvc-voice")
                    return new VoiceMetadata(j["name"]?.ToString() ?? "", j["sr"]?.Value<int>() ?? 0,
                        j["speakers"]?.Value<int>() ?? 0, j["skipHead"]?.Value<bool>() ?? false,
                        j["source"]?.ToString() ?? "", 2);
            }
            if (props.TryGetValue("metadata", out var meta))
            {
                // w-okada's client: {"samplingRate": 40000, "f0": true, "embChannels": 768, ...}
                var j = JObject.Parse(meta);
                int sr = j["samplingRate"]?.Value<int>() ?? 0;
                int emb = j["embChannels"]?.Value<int>() ?? 768;
                return new VoiceMetadata("", sr, 0, false, "vcclient export", emb == 768 ? 2 : 1);
            }
        }
        catch { }
        return VoiceMetadata.Unknown;
    }

    private static (string Key, string Value) ParseStringEntry(byte[] buf)
    {
        string key = "", value = "";
        int pos = 0;
        while (pos < buf.Length)
        {
            ulong tag = ReadVarint(buf, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (wire != 2) break;
            int len = (int)ReadVarint(buf, ref pos);
            if (pos + len > buf.Length) break;
            var s = Encoding.UTF8.GetString(buf, pos, len);
            pos += len;
            if (field == 1) key = s; else if (field == 2) value = s;
        }
        return (key, value);
    }

    private static ulong ReadVarint(byte[] buf, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static bool TryReadVarint(Stream s, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
    }
}
