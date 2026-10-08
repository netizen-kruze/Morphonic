using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The ONNX stages against the Python reference, run only when the model
// files are available: set MORPHONIC_TEST_MODELS to a folder holding
// contentvec-768-layer12.onnx, rmvpe.onnx and sample-voice-40k.onnx (the
// app's data folder after a first-run download works). Without it these
// pass trivially, with a note.
public class OnnxPipelineTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("MORPHONIC_TEST_MODELS");
    private static string? PathOf(string name) => Dir != null && File.Exists(Path.Combine(Dir, name)) ? Path.Combine(Dir, name) : null;

    [Fact]
    public void RmvpeMatchesTheReferencePitchTrack()
    {
        var model = PathOf("rmvpe.onnx");
        if (model == null) return;
        OnnxHost.Configure("cpu");
        using var pitch = new RmvpePitch(model);
        var f0 = pitch.Infer(DspTests.Segment());
        var expected = DspTests.Column("f0_seg.txt");
        Assert.Equal(expected.Length, f0.Length);
        int voicedAgree = 0;
        for (int t = 0; t < f0.Length; t++)
        {
            bool v1 = f0[t] > 0, v2 = expected[t] > 0;
            if (v1 == v2) voicedAgree++;
            if (v1 && v2) Assert.True(Math.Abs(f0[t] - expected[t]) < 1.5f, $"frame {t}: {f0[t]} vs {expected[t]}");
        }
        Assert.True(voicedAgree >= f0.Length - 1, $"voicing agrees on {voicedAgree} of {f0.Length} frames");
    }

    [Fact]
    public void ContentEncoderMatchesTheReferenceFeatures()
    {
        var model = PathOf("contentvec-768-layer12.onnx");
        if (model == null) return;
        OnnxHost.Configure("cpu");
        using var enc = new ContentEncoder(model);
        var clip = Benchmark.LoadFixture16k();
        var feats = enc.Extract(clip.AsSpan(16000, 32000), out int frames);
        Assert.Equal(99, frames);
        var expected = DspTests.Column("feats_frame20.txt");
        double maxDiff = 0;
        for (int d = 0; d < 768; d++) maxDiff = Math.Max(maxDiff, Math.Abs(feats[20 * 768 + d] - expected[d]));
        Assert.True(maxDiff < 2e-3, $"max diff {maxDiff}");
    }

    [Fact]
    public void SampleVoiceLoadsWithItsMetadataAndConvertsTheClip()
    {
        var voice = PathOf("sample-voice-40k.onnx");
        var enc = PathOf("contentvec-768-layer12.onnx");
        var pitch = PathOf("rmvpe.onnx");
        if (voice == null || enc == null || pitch == null) return;
        var meta = VoiceModel.ReadMetadata(voice);
        Assert.Equal(40000, meta.SampleRate);
        Assert.True(meta.SkipHead);
        OnnxHost.Configure("cpu");
        using var converter = new VoiceConverter(new ContentEncoder(enc), new RmvpePitch(pitch), new VoiceModel(voice));
        Assert.Equal(40000, converter.SampleRate);
        Assert.Equal(400, converter.Hop);
        using var engine = new RealtimeEngine(converter, new EngineConfig { BlockMs = 250, ExtraMs = 1000, CrossfadeMs = 50 });
        var clip = Benchmark.LoadFixture16k();
        double energy = 0;
        int blocks = 0;
        for (int at = 0; at + engine.Block16 <= 4 * 16000; at += engine.Block16)
        {
            var block = engine.ProcessBlock(clip.AsSpan(at, engine.Block16));
            Assert.Equal(engine.Block, block.Length);
            foreach (var s in block) energy += s * s;
            blocks++;
        }
        Assert.Equal(16, blocks);
        double rms = Math.Sqrt(energy / (blocks * engine.Block));
        Assert.True(rms > 1e-3, $"converted audio is silent (rms {rms})");
    }
}
