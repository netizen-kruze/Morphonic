using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Morphonic.Rvc;

// An RVC voice's .index: the content features of its training audio,
// stored by FAISS (an "IVF<n>,Flat" index — RVC's and Applio's training
// writes one — or a plain flat index). Feature retrieval, as RVC does it:
// each frame of the speaker's content features is replaced, by `rate`, with
// the inverse-square-distance weighted mean of its 8 nearest neighbours
// among the voice's own features. It pulls the timbre toward the voice
// and away from the speaker (RVC calls the knob index_rate).
//
// Read straight from FAISS's file layout (faiss/impl/index_write.cpp):
// no FAISS library is needed and nothing in the file is executed.
public sealed class FeatureIndex
{
    public const int K = 8;

    public int Dim { get; }
    public int Count { get; }
    public int ListCount => _lists.Length;
    public int Probe { get; }

    private readonly float[] _centroids;      // ListCount × Dim (empty for a flat index)
    private readonly float[] _centroidNorms;
    private readonly float[][] _lists;        // per list: n × Dim
    private readonly float[][] _listNorms;

    private FeatureIndex(int dim, float[] centroids, float[][] lists, int probe)
    {
        Dim = dim;
        _centroids = centroids;
        _lists = lists;
        Probe = Math.Clamp(probe, 1, Math.Max(1, lists.Length));
        int count = 0;
        foreach (var l in lists) count += l.Length / dim;
        Count = count;
        _centroidNorms = Norms(centroids, dim);
        _listNorms = new float[lists.Length][];
        for (int i = 0; i < lists.Length; i++) _listNorms[i] = Norms(lists[i], dim);
    }

    // ── reading ──────────────────────────────────────────────────────

    private static uint FourCc(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
    private static readonly uint IvfFlat = FourCc("IwFl"), FlatL2 = FourCc("IxF2"), FlatIp = FourCc("IxFI"),
        ArrayLists = FourCc("ilar"), Full = FourCc("full"), Sparse = FourCc("sprs");

    // A quick look without loading the vectors: what the Voices screen
    // and the importers use to refuse a file that is not a usable index.
    public static (bool Ok, string Message) Check(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            var (kind, d, ntotal, metric) = Header(r);
            if (kind != IvfFlat && kind != FlatL2) return (false, $"{Path.GetFileName(path)} is a FAISS index of a kind RVC does not write");
            if (metric != 1) return (false, $"{Path.GetFileName(path)} is not an L2 index");
            if (d == 256) return (false, $"{Path.GetFileName(path)} belongs to an RVC v1 voice (256 features); Morphonic runs v2 voices");
            if (d != VoiceModel.FeatureDim) return (false, $"{Path.GetFileName(path)} has {d}-wide features, not an RVC v2 index");
            if (ntotal <= 0) return (false, $"{Path.GetFileName(path)} is empty");
            return (true, $"{ntotal:N0} feature vectors");
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException)
        {
            return (false, $"{Path.GetFileName(path)} is not a FAISS index ({ex.Message})");
        }
    }

    public static FeatureIndex Load(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var r = new BinaryReader(stream);
        var (kind, d, ntotal, metric) = Header(r);
        if (metric != 1) throw new InvalidDataException("not an L2 index");
        if (d != VoiceModel.FeatureDim) throw new InvalidDataException($"{d}-wide features, not an RVC v2 index");
        if (kind == FlatL2)
        {
            var all = ReadFloatVector(r, (long)ntotal * d);
            return new FeatureIndex(d, Array.Empty<float>(), new[] { all }, 1);
        }
        if (kind != IvfFlat) throw new InvalidDataException("not an IVF-Flat or flat index");

        long nlist = r.ReadInt64(), nprobe = r.ReadInt64();
        if (nlist <= 0 || nlist > 1 << 22) throw new InvalidDataException($"implausible list count {nlist}");
        // The coarse quantizer: a flat index holding the list centroids.
        var (qkind, qd, qtotal, _) = Header(r);
        if (qkind != FlatL2 || qd != d || qtotal != nlist) throw new InvalidDataException("the coarse quantizer is not a flat L2 index of the list centroids");
        var centroids = ReadFloatVector(r, nlist * d);
        // The direct map (unused here): a type byte, an id array, and for
        // the hashtable kind a list of pairs.
        byte mapType = r.ReadByte();
        Skip(r, ReadCount(r) * 8);
        if (mapType == 2) Skip(r, ReadCount(r) * 16);

        if (r.ReadUInt32() != ArrayLists) throw new InvalidDataException("inverted lists of a kind RVC does not write");
        if (r.ReadInt64() != nlist) throw new InvalidDataException("inverted list count differs from the quantizer's");
        long codeSize = r.ReadInt64();
        if (codeSize != d * 4L) throw new InvalidDataException($"codes of {codeSize} bytes are not raw {d}-float vectors");
        var sizes = new long[nlist];
        uint listType = r.ReadUInt32();
        long nSizes = ReadCount(r);
        if (listType == Full)
        {
            if (nSizes != nlist) throw new InvalidDataException("list size table has the wrong length");
            for (int i = 0; i < nlist; i++) sizes[i] = r.ReadInt64();
        }
        else if (listType == Sparse)
        {
            if (nSizes % 2 != 0) throw new InvalidDataException("sparse list table has an odd length");
            for (long i = 0; i < nSizes; i += 2)
            {
                long list = r.ReadInt64(), n = r.ReadInt64();
                if (list < 0 || list >= nlist) throw new InvalidDataException("sparse list table names a list that does not exist");
                sizes[list] = n;
            }
        }
        else throw new InvalidDataException("unknown list table");

        long total = 0;
        foreach (var n in sizes) { if (n < 0) throw new InvalidDataException("negative list size"); total += n; }
        if (total != ntotal) throw new InvalidDataException($"the lists hold {total} vectors, the header says {ntotal}");

        var lists = new float[nlist][];
        for (int i = 0; i < nlist; i++)
        {
            if (sizes[i] == 0) { lists[i] = Array.Empty<float>(); continue; }
            lists[i] = ReadFloatVector(r, sizes[i] * d, counted: false);
            Skip(r, sizes[i] * 8);   // the ids: the vectors themselves are what retrieval needs
        }
        return new FeatureIndex(d, centroids, lists, (int)Math.Min(nprobe, int.MaxValue));
    }

    private static (uint Kind, int D, long NTotal, int Metric) Header(BinaryReader r)
    {
        uint kind = r.ReadUInt32();
        if (kind != IvfFlat && kind != FlatL2 && kind != FlatIp)
            throw new InvalidDataException(kind == 0 ? "empty file" : $"unknown index type '{FourCcText(kind)}'");
        int d = r.ReadInt32();
        long ntotal = r.ReadInt64();
        r.ReadInt64(); r.ReadInt64();      // two dummies (1 << 20)
        r.ReadByte();                      // is_trained
        int metric = r.ReadInt32();
        if (metric > 1) r.ReadSingle();    // metric_arg
        if (d <= 0 || d > 4096 || ntotal < 0 || ntotal > 1L << 31) throw new InvalidDataException("implausible header");
        return (kind, d, ntotal, kind == FlatIp ? 0 : metric);
    }

    private static string FourCcText(uint v)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++) { var b = (char)((v >> (8 * i)) & 0xff); c[i] = b is >= ' ' and <= '~' ? b : '?'; }
        return new string(c);
    }

    private static long ReadCount(BinaryReader r)
    {
        long n = r.ReadInt64();
        if (n < 0 || n > 1L << 34) throw new InvalidDataException("implausible vector length");
        return n;
    }

    private static float[] ReadFloatVector(BinaryReader r, long expected, bool counted = true)
    {
        if (counted)
        {
            long n = ReadCount(r);
            if (n != expected) throw new InvalidDataException($"a vector of {n} values where {expected} were expected");
        }
        if (expected > int.MaxValue) throw new InvalidDataException("index too large");
        var data = new float[expected];
        var bytes = MemoryMarshal.AsBytes(data.AsSpan());
        while (bytes.Length > 0)
        {
            int got = r.Read(bytes);
            if (got <= 0) throw new EndOfStreamException("the file ends early");
            bytes = bytes[got..];
        }
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("big-endian");
        return data;
    }

    private static void Skip(BinaryReader r, long bytes)
    {
        if (r.BaseStream.CanSeek) { r.BaseStream.Seek(bytes, SeekOrigin.Current); return; }
        var buf = new byte[81920];
        while (bytes > 0) { int got = r.Read(buf, 0, (int)Math.Min(buf.Length, bytes)); if (got <= 0) throw new EndOfStreamException(); bytes -= got; }
    }

    private static float[] Norms(float[] vecs, int dim)
    {
        var n = new float[vecs.Length / dim];
        for (int i = 0; i < n.Length; i++) { var v = vecs.AsSpan(i * dim, dim); n[i] = Dot(v, v); }
        return n;
    }

    // ── retrieval ────────────────────────────────────────────────────

    // Frames [from, to) of `feats` (frames × Dim, row-major), in place:
    // feats = rate · retrieved + (1 − rate) · feats.
    public void Blend(float[] feats, int from, int to, float rate)
    {
        if (rate <= 0f || Count == 0) return;
        rate = Math.Min(rate, 1f);
        var mixed = new float[Dim];
        for (int f = Math.Max(0, from); f < to; f++)
        {
            var q = feats.AsSpan(f * Dim, Dim);
            if (!Retrieve(q, mixed)) continue;
            for (int i = 0; i < Dim; i++) q[i] = rate * mixed[i] + (1f - rate) * q[i];
        }
    }

    // The weighted mean of the K nearest stored vectors to q (squared L2,
    // weights 1/d², as RVC). Lists probed: the `Probe` nearest centroids,
    // as FAISS searches with the nprobe saved in the file.
    public bool Retrieve(ReadOnlySpan<float> q, Span<float> result)
    {
        float qn = Dot(q, q);
        Span<int> probe = stackalloc int[Math.Min(Probe, 64)];
        int nprobe;
        if (_centroids.Length == 0) { probe[0] = 0; nprobe = 1; }
        else
        {
            Span<float> best = stackalloc float[probe.Length];
            nprobe = 0;
            for (int c = 0; c < _lists.Length; c++)
            {
                float dist = qn - 2f * Dot(q, _centroids.AsSpan(c * Dim, Dim)) + _centroidNorms[c];
                Insert(best, probe, ref nprobe, dist, c);
            }
        }

        Span<float> nd = stackalloc float[K];
        Span<int> nl = stackalloc int[K], ni = stackalloc int[K];
        int found = 0;
        for (int p = 0; p < nprobe; p++)
        {
            var list = _lists[probe[p]];
            var norms = _listNorms[probe[p]];
            for (int v = 0; v < norms.Length; v++)
            {
                float dist = qn - 2f * Dot(q, list.AsSpan(v * Dim, Dim)) + norms[v];
                if (found == K && dist >= nd[K - 1]) continue;
                // insertion into the sorted K-best
                int at = found < K ? found++ : K - 1;
                while (at > 0 && nd[at - 1] > dist) { nd[at] = nd[at - 1]; nl[at] = nl[at - 1]; ni[at] = ni[at - 1]; at--; }
                nd[at] = dist; nl[at] = probe[p]; ni[at] = v;
            }
        }
        if (found == 0) return false;

        result.Clear();
        double wsum = 0;
        for (int k = 0; k < found; k++)
        {
            double d = Math.Max(nd[k], 1e-9f);
            double w = 1.0 / (d * d);
            wsum += w;
            var v = _lists[nl[k]].AsSpan(ni[k] * Dim, Dim);
            float wf = (float)w;
            for (int i = 0; i < Dim; i++) result[i] += wf * v[i];
        }
        float inv = (float)(1.0 / wsum);
        for (int i = 0; i < Dim; i++) result[i] *= inv;
        return true;
    }

    private static void Insert(Span<float> best, Span<int> ids, ref int count, float dist, int id)
    {
        int cap = best.Length;
        if (count == cap && dist >= best[cap - 1]) return;
        int at = count < cap ? count++ : cap - 1;
        while (at > 0 && best[at - 1] > dist) { best[at] = best[at - 1]; ids[at] = ids[at - 1]; at--; }
        best[at] = dist; ids[at] = id;
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length, i = 0;
        var acc = Vector<float>.Zero;
        if (Vector.IsHardwareAccelerated)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            for (int k = 0; k < va.Length; k++) acc += va[k] * vb[k];
            i = va.Length * Vector<float>.Count;
        }
        float s = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
