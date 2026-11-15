using System.Runtime.InteropServices;
using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The .index reader and RVC's feature retrieval. Self-contained cases write
// small indexes in FAISS's file layout; with MORPHONIC_TEST_INDEX_REF set to
// a folder of FAISS's own answers (made by faiss: <case>.index, .q,
// .expected, .ids), the reader is checked against the library itself.
public class FeatureIndexTests
{
    private const int D = 768;

    // FAISS's layout (faiss/impl/index_write.cpp), enough for IVF-Flat and flat L2.
    private static void Header(BinaryWriter w, string fourcc, long ntotal)
    {
        w.Write(System.Text.Encoding.ASCII.GetBytes(fourcc));
        w.Write(D); w.Write(ntotal); w.Write(1L << 20); w.Write(1L << 20); w.Write((byte)1); w.Write(1);
    }

    private static void Floats(BinaryWriter w, float[] v, bool counted = true)
    {
        if (counted) w.Write((long)v.Length);
        w.Write(MemoryMarshal.AsBytes(v.AsSpan()));
    }

    private static string WriteIvf(string dir, float[][] centroids, float[][][] lists, long nprobe, bool sparse)
    {
        var path = Path.Combine(dir, "t.index");
        using var w = new BinaryWriter(File.Create(path));
        Header(w, "IwFl", lists.Sum(l => l.Length));
        w.Write((long)centroids.Length); w.Write(nprobe);
        Header(w, "IxF2", centroids.Length);
        Floats(w, centroids.SelectMany(c => c).ToArray());
        w.Write((byte)0); w.Write(0L);                       // direct map: none
        w.Write(System.Text.Encoding.ASCII.GetBytes("ilar"));
        w.Write((long)centroids.Length); w.Write((long)D * 4);
        if (sparse)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("sprs"));
            var pairs = lists.Select((l, i) => (i, l.Length)).Where(p => p.Length > 0).ToArray();
            w.Write((long)pairs.Length * 2);
            foreach (var (i, n) in pairs) { w.Write((long)i); w.Write((long)n); }
        }
        else
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("full"));
            w.Write((long)lists.Length);
            foreach (var l in lists) w.Write((long)l.Length);
        }
        long id = 0;
        foreach (var l in lists)
        {
            if (l.Length == 0) continue;
            Floats(w, l.SelectMany(v => v).ToArray(), counted: false);
            foreach (var _ in l) w.Write(id++);
        }
        return path;
    }

    // A small valid RVC index (one list, three vectors of `fill`), for the
    // library tests; `fill` tells two indexes apart.
    internal static void WriteSmallIndex(string path, float fill)
    {
        var dir = Directory.CreateTempSubdirectory("morphonic-index").FullName;
        try
        {
            var v = Enumerable.Repeat(fill, D).ToArray();
            var made = WriteIvf(dir, new[] { v }, new[] { new[] { v, v, v } }, 1, false);
            File.Copy(made, path, overwrite: true);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static float[] Vec(Random rng, float center = 0f) =>
        Enumerable.Range(0, D).Select(_ => center + (float)(rng.NextDouble() * 2 - 1)).ToArray();

    // RVC's answer by brute force over the given candidates.
    private static float[] Expected(float[] q, IEnumerable<float[]> candidates)
    {
        var best = candidates.Select(v => (v, d: q.Zip(v, (a, b) => (double)(a - b) * (a - b)).Sum()))
            .OrderBy(x => x.d).Take(FeatureIndex.K).ToArray();
        var w = best.Select(x => 1.0 / (x.d * x.d)).ToArray();
        double sum = w.Sum();
        var r = new float[D];
        for (int k = 0; k < best.Length; k++) for (int i = 0; i < D; i++) r[i] += (float)(w[k] / sum * best[k].v[i]);
        return r;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheNearestNeighboursOfTheNearestListAreBlended(bool sparse)
    {
        var dir = Directory.CreateTempSubdirectory("morphonic-index").FullName;
        try
        {
            var rng = new Random(3);
            // three lists around three far-apart centres; the middle one empty when sparse
            var centres = new[] { -3f, 0f, 3f };
            var centroids = centres.Select(c => Enumerable.Repeat(c, D).ToArray()).ToArray();
            var lists = centres.Select((c, i) => sparse && i == 1 ? Array.Empty<float[]>()
                : Enumerable.Range(0, 20).Select(_ => Vec(rng, c)).ToArray()).ToArray();
            var path = WriteIvf(dir, centroids, lists, nprobe: 1, sparse);
            Assert.True(FeatureIndex.Check(path).Ok);
            var index = FeatureIndex.Load(path);
            Assert.Equal(sparse ? 40 : 60, index.Count);
            Assert.Equal(3, index.ListCount);

            var q = Vec(rng, 2.6f);                               // nearest centroid: +3
            var got = new float[D];
            Assert.True(index.Retrieve(q, got));
            var want = Expected(q, lists[2]);
            for (int i = 0; i < D; i++) Assert.True(Math.Abs(want[i] - got[i]) < 1e-4f, $"{i}: {got[i]} vs {want[i]}");

            // Blend: rate 0 leaves the frame, 1 replaces it, 0.5 is halfway
            var feats = q.Concat(q).ToArray();
            index.Blend(feats, 1, 2, 0.5f);
            for (int i = 0; i < D; i++) { Assert.Equal(q[i], feats[i]); Assert.True(Math.Abs(0.5f * want[i] + 0.5f * q[i] - feats[D + i]) < 1e-4f); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FilesThatAreNotAV2IndexAreRefused()
    {
        var dir = Directory.CreateTempSubdirectory("morphonic-index").FullName;
        try
        {
            var junk = Path.Combine(dir, "junk.index");
            File.WriteAllText(junk, "not an index at all");
            Assert.False(FeatureIndex.Check(junk).Ok);
            Assert.ThrowsAny<Exception>(() => FeatureIndex.Load(junk));

            var v1 = Path.Combine(dir, "v1.index");
            using (var w = new BinaryWriter(File.Create(v1)))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("IwFl"));
                w.Write(256); w.Write(10L); w.Write(1L << 20); w.Write(1L << 20); w.Write((byte)1); w.Write(1);
            }
            var c = FeatureIndex.Check(v1);
            Assert.False(c.Ok);
            Assert.Contains("v1", c.Message);

            // a truncated file
            var rng = new Random(1);
            var good = WriteIvf(dir, new[] { Vec(rng) }, new[] { new[] { Vec(rng), Vec(rng) } }, 1, false);
            var bytes = File.ReadAllBytes(good);
            var cut = Path.Combine(dir, "cut.index");
            File.WriteAllBytes(cut, bytes[..(bytes.Length - 100)]);
            Assert.ThrowsAny<Exception>(() => FeatureIndex.Load(cut));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Against FAISS: the real RVC index (IVF-Flat, full list table, nprobe 1),
    // a sparse list table searched with nprobe 64, and a flat index.
    [Theory]
    [InlineData("real")]
    [InlineData("sparse")]
    [InlineData("flat")]
    public void RetrievalMatchesFaiss(string name)
    {
        var dir = Environment.GetEnvironmentVariable("MORPHONIC_TEST_INDEX_REF");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, name + ".index"))) return;
        var index = FeatureIndex.Load(Path.Combine(dir, name + ".index"));
        var q = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(dir, name + ".q"))).ToArray();
        var expected = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(dir, name + ".expected"))).ToArray();
        int n = q.Length / D;
        var got = new float[D];
        double worst = 0;
        for (int f = 0; f < n; f++)
        {
            Assert.True(index.Retrieve(q.AsSpan(f * D, D), got));
            for (int i = 0; i < D; i++) worst = Math.Max(worst, Math.Abs(got[i] - expected[f * D + i]));
        }
        Assert.True(worst < 1e-3, $"{name}: largest difference from FAISS {worst}");
    }
}
