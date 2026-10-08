using System.Globalization;
using Morphonic.Audio;
using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The signal-processing core, checked against values the Python pipeline
// (torch.stft + librosa's mel + RVC's RMVPE decode) produced for the same
// bench clip — see Fixtures/ and the test project file.
public class DspTests
{
    internal static float[] Column(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))
            .Where(l => l.Trim().Length > 0)
            .Select(l => float.Parse(l.Trim(), CultureInfo.InvariantCulture)).ToArray();

    // The pitch-window segment the references were computed on: 4960
    // samples from one second into the clip (an RVC 250 ms block's window).
    internal static float[] Segment()
    {
        var clip = Benchmark.LoadFixture16k();
        return clip.AsSpan(16000, 4960).ToArray();
    }

    // ── mel front end ──────────────────────────────────────────────

    [Fact]
    public void MelFilterBankMatchesLibrosa()
    {
        var basis = MelSpectrogram.FilterBank();
        var row40 = Column("mel_basis_bin40.txt");
        Assert.Equal(513, row40.Length);
        for (int k = 0; k < 513; k++) Assert.True(Math.Abs(basis[40, k] - row40[k]) < 1e-6, $"bin 40, freq {k}: {basis[40, k]} vs {row40[k]}");
        var sums = Column("mel_basis_rowsums.txt");
        for (int m = 0; m < 128; m++)
        {
            double s = 0;
            for (int k = 0; k < 513; k++) s += basis[m, k];
            Assert.True(Math.Abs(s - sums[m]) < 1e-5, $"row {m} sum {s} vs {sums[m]}");
        }
    }

    [Fact]
    public void LogMelFramesMatchTorch()
    {
        var seg = Segment();
        var mel = new MelSpectrogram().Compute(seg);
        int frames = MelSpectrogram.FrameCount(seg.Length);
        Assert.Equal(32, frames);
        var f0 = Column("mel_frame0.txt");
        var f10 = Column("mel_frame10.txt");
        for (int b = 0; b < 128; b++)
        {
            Assert.True(Math.Abs(mel[b * frames + 0] - f0[b]) < 2e-3, $"frame 0 bin {b}: {mel[b * frames]} vs {f0[b]}");
            Assert.True(Math.Abs(mel[b * frames + 10] - f10[b]) < 2e-3, $"frame 10 bin {b}: {mel[b * frames + 10]} vs {f10[b]}");
        }
    }

    [Fact]
    public void FftOfASineHasOnePeak()
    {
        const int n = 1024;
        var fft = new Fft(n);
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = MathF.Sin(2 * MathF.PI * 64 * i / n);
        fft.Transform(re, im);
        int peak = 0;
        float best = 0;
        for (int k = 0; k < n / 2; k++)
        {
            float mag = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (mag > best) { best = mag; peak = k; }
        }
        Assert.Equal(64, peak);
        Assert.True(Math.Abs(best - n / 2) < 1e-2);
    }

    // ── pitch decode and coarse mapping ────────────────────────────

    [Fact]
    public void SalienceDecodesToTheReferencePitch()
    {
        var salience = Column("salience_seg.txt");
        Assert.Equal(32 * 360, salience.Length);
        var f0 = PitchMath.DecodeSalience(salience, 32);
        var expected = Column("f0_seg.txt");
        for (int t = 0; t < 32; t++)
            Assert.True(Math.Abs(f0[t] - expected[t]) < 0.05f, $"frame {t}: {f0[t]} vs {expected[t]}");
    }

    [Fact]
    public void CoarsePitchMatchesTheRvcFormula()
    {
        var f0 = Column("f0_full_head.txt");
        var coarse = Column("coarse_full_head.txt");
        for (int i = 0; i < f0.Length; i++)
            Assert.Equal((long)coarse[i], PitchMath.Coarse(f0[i]));
        Assert.Equal(1, PitchMath.Coarse(0));
        Assert.Equal(255, PitchMath.Coarse(5000));
    }

    [Fact]
    public void TransposeIsInSemitones()
    {
        Assert.True(Math.Abs(PitchMath.Transpose(100, 12) - 200) < 1e-3);
        Assert.True(Math.Abs(PitchMath.Transpose(200, -12) - 100) < 1e-3);
    }

    // ── resampler ──────────────────────────────────────────────────

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    [InlineData(40000, 48000)]
    [InlineData(32000, 48000)]
    public void ResamplerKeepsAToneAndItsLevel(int inRate, int outRate)
    {
        const double hz = 440;
        var r = new Resampler(inRate, outRate);
        int n = inRate;    // one second
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = 0.5f * MathF.Sin((float)(2 * Math.PI * hz * i / inRate));
        var output = new float[r.MaxOutput(n)];
        int got = r.Process(input, output);
        Assert.InRange(got, outRate - 40, outRate + 2);
        // Steady-state RMS of a 0.5 amplitude sine is 0.3536.
        double energy = 0;
        int from = got / 4, to = got * 3 / 4;
        for (int i = from; i < to; i++) energy += output[i] * output[i];
        double rms = Math.Sqrt(energy / (to - from));
        Assert.InRange(rms, 0.34, 0.37);
        // Zero crossings count the frequency.
        int crossings = 0;
        for (int i = from + 1; i < to; i++) if ((output[i - 1] < 0) != (output[i] < 0)) crossings++;
        double measured = crossings / 2.0 / ((to - from) / (double)outRate);
        Assert.InRange(measured, hz - 3, hz + 3);
    }

    [Fact]
    public void ResamplerIsSeamlessAcrossChunks()
    {
        var whole = new Resampler(48000, 16000);
        var chunked = new Resampler(48000, 16000);
        var input = new float[48000];
        var rng = new Random(7);
        for (int i = 0; i < input.Length; i++) input[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
        var a = new float[whole.MaxOutput(input.Length)];
        int na = whole.Process(input, a);
        var b = new List<float>();
        for (int at = 0; at < input.Length; at += 480)
        {
            var tmp = new float[chunked.MaxOutput(480)];
            int nb = chunked.Process(input.AsSpan(at, Math.Min(480, input.Length - at)), tmp);
            b.AddRange(tmp.Take(nb));
        }
        Assert.InRange(Math.Abs(na - b.Count), 0, 2);
        int n = Math.Min(na, b.Count);
        for (int i = 0; i < n; i++) Assert.True(Math.Abs(a[i] - b[i]) < 1e-5f, $"sample {i}: {a[i]} vs {b[i]}");
    }

    // ── ring buffer ────────────────────────────────────────────────

    [Fact]
    public void RingBufferWrapsAndDropsOldest()
    {
        var ring = new RingBuffer(8);
        ring.Write(new float[] { 1, 2, 3, 4, 5 });
        var dst = new float[3];
        Assert.Equal(3, ring.Read(dst));
        Assert.Equal(new float[] { 1, 2, 3 }, dst);
        ring.Write(new float[] { 6, 7, 8, 9, 10, 11 });   // 2 left + 6 = 8, fits exactly
        Assert.Equal(0, ring.Dropped);
        ring.Write(new float[] { 12, 13 });                // overflow drops 4, 5
        Assert.Equal(2, ring.Dropped);
        var all = new float[8];
        Assert.Equal(8, ring.Read(all));
        Assert.Equal(new float[] { 6, 7, 8, 9, 10, 11, 12, 13 }, all);
        Assert.Equal(0, ring.Read(all));
    }

    // ── SOLA ───────────────────────────────────────────────────────

    [Fact]
    public void SolaFindsTheShiftThatAlignsBlocks()
    {
        int sola = 1600, search = 400;
        var prev = new float[sola];
        for (int i = 0; i < sola; i++) prev[i] = MathF.Sin(2 * MathF.PI * 220 * i / 40000f);
        // The candidate has the same wave starting 137 samples in.
        var candidate = new float[sola + search];
        for (int i = 0; i < candidate.Length; i++) candidate[i] = i < 137 ? 0.01f * (i % 3) : MathF.Sin(2 * MathF.PI * 220 * (i - 137) / 40000f);
        Assert.Equal(137, Sola.FindOffset(candidate, prev, search));
        var fade = Sola.FadeIn(sola);
        Assert.Equal(0f, fade[0]);
        Assert.True(Math.Abs(fade[^1] - 1f) < 1e-6);
        var head = new float[sola];
        for (int i = 0; i < sola; i++) head[i] = 1f;
        Sola.Crossfade(head, prev, fade);
        Assert.True(Math.Abs(head[0] - prev[0]) < 1e-6);
        Assert.True(Math.Abs(head[^1] - 1f) < 1e-6);
    }

    // ── pace monitor ───────────────────────────────────────────────

    [Fact]
    public void PaceMonitorKeepsUpThenFallsBehindAndWarnsOnce()
    {
        var m = new PaceMonitor();
        for (int i = 1; i <= 10; i++) Assert.Equal(PaceMonitor.PaceStatus.KeepingUp, m.Record(new PassInfo(60, 250, 0), i * 250));
        long t = 2500;
        for (int i = 0; i < 40; i++) { t += 300; m.Record(new PassInfo(300, 250, 600), t); }
        Assert.Equal(PaceMonitor.PaceStatus.Behind, m.Status);
        Assert.True(m.Load > 1.0);
        Assert.True(m.ShouldWarn(t));
        Assert.False(m.ShouldWarn(t + 60_000));
        Assert.Contains("passes", m.Summary());
    }

    // ── engine helpers ─────────────────────────────────────────────

    [Fact]
    public void NoiseGateSilencesQuietFrames()
    {
        var block = new float[320];
        for (int i = 0; i < 160; i++) block[i] = 0.2f * (i % 2 == 0 ? 1 : -1);  // loud
        for (int i = 160; i < 320; i++) block[i] = 0.0005f;                     // -66 dB
        RealtimeEngine.Gate(block, -40);
        Assert.Equal(0.2f, Math.Abs(block[0]));
        Assert.Equal(0f, block[200]);
    }

    [Fact]
    public void FrameRmsIsCenteredAndZeroPadded()
    {
        var x = new float[1600];
        for (int i = 0; i < x.Length; i++) x[i] = 0.5f;
        var rms = RealtimeEngine.FrameRms(x, 640, 160, 11);
        Assert.Equal(11, rms.Length);
        Assert.True(rms[5] > 0.49f);                // a full window of 0.5
        Assert.True(rms[0] < rms[5] && rms[0] > 0.3f); // half the window is padding
    }

    [Fact]
    public void BenchVerdictsFollowLoadAndSilence()
    {
        Assert.Equal("fast", Benchmark.Verdict(0.3, 0.1));
        Assert.Equal("usable", Benchmark.Verdict(0.8, 0.1));
        Assert.Equal("too slow", Benchmark.Verdict(1.4, 0.1));
        Assert.Equal("silent", Benchmark.Verdict(0.3, 0));
    }

    [Fact]
    public void TheBenchClipIsFourteenSecondsOf16kAudio()
    {
        var clip = Benchmark.LoadFixture16k();
        Assert.InRange(clip.Length, 13 * 16000, 15 * 16000);
        Assert.True(clip.Max() > 0.05f);
    }
}
