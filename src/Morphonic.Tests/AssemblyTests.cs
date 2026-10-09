using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The checkpoint reader and the model assembly: a tiny file in torch.save's
// layout (Fixtures/tiny_voice.pth, written by tools/make_test_fixture.py
// with the pickle globals and storages torch emits) stands in for a voice,
// and the protobuf and arithmetic helpers are checked on small cases. With
// MORPHONIC_TEST_SOURCES pointing at a folder holding pytorch_model.bin
// (content-vec-best) and f0G40k.pth, the real assemblies are built and
// must reproduce the catalog's pinned hashes to the byte.
public class AssemblyTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadsATorchCheckpointWithoutTorch()
    {
        using var ck = TorchCheckpoint.Open(Fixture("tiny_voice.pth"));
        Assert.Equal("40k", ck.Value("sr"));
        Assert.Equal(1L, ck.Value("f0"));
        Assert.Equal("v2", ck.Value("version"));
        var config = Assert.IsType<List<object?>>(ck.Value("config"));
        Assert.Equal(1025L, config[0]);
        Assert.Equal("1", config[3]);
        Assert.Equal(new List<object?> { 3L, 7L, 11L }, config[4]);
        Assert.Equal(0.0, config[6]);

        var state = ck.StateDict("weight");
        Assert.Equal(5, state.Count);
        var w = state["a.weight"];
        Assert.Equal(TorchDtype.Float32, w.Dtype);
        Assert.Equal(new long[] { 3, 4 }, w.Shape);
        var values = ck.ReadFloat32(w);
        Assert.Equal(12, values.Length);
        Assert.Equal(0.5f, values[5]);
        Assert.Equal(1.1f, values[11]);

        var b = state["a.bias"];
        Assert.Equal(TorchDtype.Float16, b.Dtype);
        Assert.Equal(new[] { 1.5f, -2.0f, 3.25f }, ck.ReadFloat32(b));

        var ints = state["i.weight"];
        Assert.Equal(TorchDtype.Int64, ints.Dtype);
        Assert.Equal(new[] { 7f, -8f, 9f }, ck.ReadFloat32(ints));

        var v = state["n.weight_v"];
        Assert.Equal(new long[] { 2, 3, 5 }, v.Shape);
        Assert.True(v.IsContiguous);
        Assert.Equal(30, ck.ReadFloat32(v).Length);
    }

    [Fact]
    public void RefusesCheckpointsThatCarryCode()
    {
        // A pickle calling os.system: GLOBAL 'os\nsystem\n' with a string argument, REDUCE.
        var pickle = new byte[] { 0x80, 0x02, (byte)'c' }
            .Concat("os\nsystem\n"u8.ToArray())
            .Concat(new byte[] { (byte)'X', 2, 0, 0, 0, (byte)'l', (byte)'s', 0x85, (byte)'R', (byte)'.' }).ToArray();
        var tmp = Path.Combine(Path.GetTempPath(), "morphonic-test-" + Guid.NewGuid().ToString("N") + ".pth");
        try
        {
            using (var zip = new System.IO.Compression.ZipArchive(File.Create(tmp), System.IO.Compression.ZipArchiveMode.Create))
            using (var s = zip.CreateEntry("archive/data.pkl").Open()) s.Write(pickle);
            var ex = Assert.Throws<NotSupportedException>(() => TorchCheckpoint.Open(tmp));
            Assert.Contains("os.system", ex.Message);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void TransposeAndWeightNormMatchTheirDefinitions()
    {
        Assert.Equal(new[] { 1f, 4f, 2f, 5f, 3f, 6f }, ModelAssembler.Transpose(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, 2, 3));

        // v [2,2,2], norm over dims (1,2) for dim 0: slice 0 = {3,4,0,0} (norm 5), slice 1 = {1,2,2,4} (norm 5)
        var v = new[] { 3f, 4f, 0f, 0f, 1f, 2f, 2f, 4f };
        var w = ModelAssembler.WeightNorm(new[] { 10f, 5f }, v, new long[] { 2, 2, 2 }, 0);
        Assert.Equal(new[] { 6f, 8f, 0f, 0f, 1f, 2f, 2f, 4f }, w);
        // dim 2: slice k=0 = {3,0,1,2} (norm sqrt 14), k=1 = {4,0,2,4} (norm 6)
        var w2 = ModelAssembler.WeightNorm(new[] { 1f, 3f }, v, new long[] { 2, 2, 2 }, 2);
        Assert.Equal(2f, w2[1]);
        Assert.Equal((float)(3.0 / Math.Sqrt(14)), w2[0]);
        Assert.Equal((float)(2.0 / Math.Sqrt(14)), w2[6]);
    }

    [Fact]
    public void WeightNormAgreesWithTheReferenceOnTheFixture()
    {
        // n.weight = weight_norm(n.weight_v, n.weight_g, dim=0): fp16 inputs, the
        // definition evaluated in float64 and rounded to float32 (tiny_weightnorm.txt).
        using var ck = TorchCheckpoint.Open(Fixture("tiny_voice.pth"));
        var state = ck.StateDict("weight");
        var g = ck.ReadFloat32(state["n.weight_g"]);
        var v = ck.ReadFloat32(state["n.weight_v"]);
        var w = ModelAssembler.WeightNorm(g, v, state["n.weight_v"].Shape, 0);
        var expected = DspTests.Column("tiny_weightnorm.txt");
        Assert.Equal(expected.Length, w.Length);
        for (int i = 0; i < w.Length; i++) Assert.True(Math.Abs(w[i] - expected[i]) < 2e-6f, $"[{i}] {w[i]} vs {expected[i]}");
    }

    [Fact]
    public void FragmentHeaderIsWellFormedProtobuf()
    {
        var head = ModelAssembler.FragmentHeader("w", new long[] { 2, 300 }, 2400);
        // ModelProto.graph tag, then the graph length covering initializer tag + tensor
        Assert.Equal(0x3A, head[0]);
        int p = 1;
        ulong graphLen = ReadVarint(head, ref p);
        Assert.Equal(0x2A, head[p++]);
        ulong tensorLen = ReadVarint(head, ref p);
        Assert.Equal((ulong)(head.Length - p) + 2400, tensorLen);
        Assert.Equal(1 + (ulong)ModelAssembler.VarintSize(tensorLen) + tensorLen, graphLen);
        // dims 2, 300; data_type FLOAT; name "w"; raw_data length 2400
        Assert.Equal(new byte[] { 0x08, 2, 0x08, 0xAC, 0x02, 0x10, 1, 0x42, 1, (byte)'w', 0x4A, 0xE0, 0x12 }, head.AsSpan(p).ToArray());
    }

    private static ulong ReadVarint(byte[] b, ref int p)
    {
        ulong v = 0; int shift = 0;
        while (true) { byte x = b[p++]; v |= (ulong)(x & 0x7F) << shift; if ((x & 0x80) == 0) return v; shift += 7; }
    }

    [Fact]
    public void RecipesLoadAndNameOnlyKnownOperations()
    {
        foreach (var m in ModelCatalog.Models.Where(m => m.Assembled))
        {
            var r = ModelAssembler.LoadRecipe(m.Recipe!);
            Assert.Equal(m.Download.Sha256, r.Source.Sha256);
            Assert.Equal(m.DownloadBytes, r.Source.SizeBytes);
            Assert.NotEmpty(r.Tensors);
            Assert.All(r.Tensors, t => Assert.Contains(t.Op, new[] { "copy", "transpose", "weightnorm" }));
            Assert.All(r.Tensors, t => Assert.Equal(t.Op == "weightnorm" ? 2 : 1, t.Src.Length));
        }
    }

    [Fact]
    public void AssembledModelsReproduceThePinnedHashes()
    {
        var sources = Environment.GetEnvironmentVariable("MORPHONIC_TEST_SOURCES");
        if (sources == null) return;
        foreach (var m in ModelCatalog.Models.Where(m => m.Assembled))
        {
            var src = Path.Combine(sources, Path.GetFileName(new Uri(m.Download.Url).AbsolutePath));
            if (!File.Exists(src)) continue;
            var outPath = Path.Combine(Path.GetTempPath(), "morphonic-test-" + m.File.FileName);
            try
            {
                long last = -1;
                var (size, hash) = ModelAssembler.Assemble(m.Recipe!, src, outPath, done => { Assert.True(done >= last); last = done; });
                Assert.Equal(m.SizeBytes, size);
                Assert.Equal(m.File.Sha256, hash);
                Assert.Equal(size, new FileInfo(outPath).Length);
            }
            finally { File.Delete(outPath); }
        }
    }
}
