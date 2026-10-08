using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Morphonic.Rvc;

// A recipe (tools/make_templates.py): the weightless ONNX graph it goes
// with, the checkpoint it expects, and for every tensor the graph needs,
// which checkpoint tensor(s) it comes from and how.
public sealed class ModelRecipe
{
    public sealed class SourceInfo
    {
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long SizeBytes { get; set; }
        public string StateKey { get; set; } = "";
    }

    public sealed class TensorEntry
    {
        public string Name { get; set; } = "";
        public long[] Dims { get; set; } = Array.Empty<long>();
        public string Op { get; set; } = "";
        public int Dim { get; set; }
        public string[] Src { get; set; } = Array.Empty<string>();
        // The tensor's shape is the checkpoint's, not the template's (a
        // voice's speaker table has as many rows as it was trained with).
        public bool DimsFromSource { get; set; }
    }

    public string Format { get; set; } = "";
    public int FormatVersion { get; set; }
    public string Template { get; set; } = "";
    public SourceInfo Source { get; set; } = new();
    public TensorEntry[] Tensors { get; set; } = Array.Empty<TensorEntry>();
}

// Builds a model file the way tools/make_templates.py does, byte for
// byte: the embedded template (an ONNX graph without its big tensors)
// followed by one protobuf fragment per tensor, each read from the
// downloaded checkpoint and transformed as the export did (copy,
// transpose, weight-norm fold). Protobuf merges a message with the
// fragments appended after it, so the result is one ordinary ONNX file
// whose hash the catalog pins.
public static class ModelAssembler
{
    public static ModelRecipe LoadRecipe(string name)
    {
        using var s = OpenAsset(name + ".recipe.json");
        using var r = new StreamReader(s, Encoding.UTF8);
        var recipe = JsonConvert.DeserializeObject<ModelRecipe>(r.ReadToEnd())
                     ?? throw new InvalidDataException("empty recipe " + name);
        if (recipe.Format != "morphonic-model-recipe" || recipe.FormatVersion != 1)
            throw new InvalidDataException($"unsupported recipe {name} ({recipe.Format} v{recipe.FormatVersion})");
        return recipe;
    }

    private static Stream OpenAsset(string file) =>
        typeof(ModelAssembler).Assembly.GetManifestResourceStream("assets/" + file)
        ?? throw new FileNotFoundException("embedded asset missing: " + file);

    // Writes outPath and returns its size and SHA-256. Progress reports
    // bytes written so far.
    public static (long Size, string Sha256) Assemble(string recipeName, string checkpointPath, string outPath,
        Action<long>? progress = null, CancellationToken ct = default, string? metadataJson = null)
    {
        var recipe = LoadRecipe(recipeName);
        using var checkpoint = TorchCheckpoint.Open(checkpointPath);
        var state = ResolveState(checkpoint, recipe.Source.StateKey);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        long written = 0;
        void Write(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            sha.AppendData(bytes);
            written += bytes.Length;
        }

        using (var t = OpenAsset(recipe.Template))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = t.Read(buf, 0, buf.Length)) > 0) Write(buf.AsSpan(0, n));
        }
        progress?.Invoke(written);

        foreach (var e in recipe.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            var (data, dims) = Produce(e, state, checkpoint);
            long count = dims.Aggregate(1L, (a, b) => a * b);
            if (data.Length != count) throw new InvalidDataException($"{e.Name}: recipe expects {count} values, checkpoint gave {data.Length}");
            Write(FragmentHeader(e.Name, dims, data.Length * 4L));
            if (BitConverter.IsLittleEndian) Write(MemoryMarshal.AsBytes(data.AsSpan()));
            else
            {
                var le = new byte[data.Length * 4];
                for (int i = 0; i < data.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(le.AsSpan(i * 4), data[i]);
                Write(le);
            }
            progress?.Invoke(written);
        }
        if (metadataJson != null) Write(MetadataFragment("morphonic", metadataJson));
        output.Flush();
        return (written, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    // The recipe names where the tensors sit ("model" for a training
    // generator, "weight" for an extracted voice); a checkpoint laid out
    // the other way is accepted too.
    private static Dictionary<string, TorchTensor> ResolveState(TorchCheckpoint ck, string key)
    {
        foreach (var k in new[] { key, "weight", "model", "" }.Distinct())
        {
            try
            {
                var s = ck.StateDict(k);
                if (s.Count > 0) return s;
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("the checkpoint holds no tensors");
    }

    private static (float[] Data, long[] Dims) Produce(ModelRecipe.TensorEntry e, Dictionary<string, TorchTensor> state, TorchCheckpoint ck)
    {
        TorchTensor Need(string key) =>
            state.TryGetValue(key, out var t) ? t : throw new InvalidDataException($"the checkpoint has no tensor '{key}' (needed for {e.Name})");
        switch (e.Op)
        {
            case "copy":
            {
                var t = Need(e.Src[0]);
                return (ck.ReadFloat32(t), e.DimsFromSource ? t.Shape : e.Dims);
            }
            case "transpose":
            {
                var t = Need(e.Src[0]);
                if (t.Shape.Length != 2) throw new InvalidDataException($"{e.Src[0]}: transpose needs a 2-D tensor");
                return (Transpose(ck.ReadFloat32(t), (int)t.Shape[0], (int)t.Shape[1]), e.DimsFromSource ? new[] { t.Shape[1], t.Shape[0] } : e.Dims);
            }
            case "weightnorm":
            {
                var g = Need(e.Src[0]);
                var v = Need(e.Src[1]);
                return (WeightNorm(ck.ReadFloat32(g), ck.ReadFloat32(v), v.Shape, e.Dim), e.DimsFromSource ? v.Shape : e.Dims);
            }
            default:
                throw new InvalidDataException($"unknown recipe operation '{e.Op}'");
        }
    }

    // ModelProto.metadata_props (14) { StringStringEntryProto key (1), value (2) }
    internal static byte[] MetadataFragment(string key, string value)
    {
        var k = Encoding.UTF8.GetBytes(key);
        var v = Encoding.UTF8.GetBytes(value);
        var entry = new MemoryStream();
        Varint(entry, (1 << 3) | 2); Varint(entry, (ulong)k.Length); entry.Write(k);
        Varint(entry, (2 << 3) | 2); Varint(entry, (ulong)v.Length); entry.Write(v);
        var outer = new MemoryStream();
        Varint(outer, (14 << 3) | 2); Varint(outer, (ulong)entry.Length);
        entry.Position = 0;
        entry.CopyTo(outer);
        return outer.ToArray();
    }

    // a[r, c] -> out[c, r]
    internal static float[] Transpose(float[] a, int rows, int cols)
    {
        var o = new float[a.Length];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                o[c * rows + r] = a[r * cols + c];
        return o;
    }

    // torch's weight_norm folded: w = g * v / ||v||, the norm over every
    // axis but `dim`. Deterministic by definition (see make_templates.py):
    // a sequential float64 sum of squares in memory order, the scale as a
    // float64, each product rounded once to float32.
    internal static float[] WeightNorm(float[] g, float[] v, long[] shape, int dim)
    {
        long n = shape[dim];
        long outer = 1, inner = 1;
        for (int i = 0; i < dim; i++) outer *= shape[i];
        for (int i = dim + 1; i < shape.Length; i++) inner *= shape[i];
        if (g.Length != n) throw new InvalidDataException($"weight_g has {g.Length} values for {n} slices");
        var o = new float[v.Length];
        for (long k = 0; k < n; k++)
        {
            double sum = 0;
            for (long a = 0; a < outer; a++)
            {
                long b = (a * n + k) * inner;
                for (long i = 0; i < inner; i++) { double x = v[b + i]; sum += x * x; }
            }
            double scale = g[k] / Math.Sqrt(sum);
            for (long a = 0; a < outer; a++)
            {
                long b = (a * n + k) * inner;
                for (long i = 0; i < inner; i++) o[b + i] = (float)(v[b + i] * scale);
            }
        }
        return o;
    }

    // ── protobuf ────────────────────────────────────────────────────
    // ModelProto.graph (7) { GraphProto.initializer (5) { TensorProto:
    // dims (1), data_type (2) = FLOAT, name (8), raw_data (9) } }; the
    // raw bytes follow the header.
    internal static byte[] FragmentHeader(string name, long[] dims, long rawLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var head = new MemoryStream();
        foreach (var d in dims) { Varint(head, (1 << 3) | 0); Varint(head, (ulong)d); }
        Varint(head, (2 << 3) | 0); Varint(head, 1);
        Varint(head, (8 << 3) | 2); Varint(head, (ulong)nameBytes.Length); head.Write(nameBytes);
        Varint(head, (9 << 3) | 2); Varint(head, (ulong)rawLength);
        long tensorLen = head.Length + rawLength;
        long graphLen = 1 + VarintSize((ulong)tensorLen) + tensorLen;
        var outer = new MemoryStream();
        Varint(outer, (7 << 3) | 2); Varint(outer, (ulong)graphLen);
        Varint(outer, (5 << 3) | 2); Varint(outer, (ulong)tensorLen);
        head.Position = 0;
        head.CopyTo(outer);
        return outer.ToArray();
    }

    internal static void Varint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    internal static int VarintSize(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }
}
