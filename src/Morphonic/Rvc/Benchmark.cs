using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Morphonic.Rvc;

public sealed record BenchRow(string Label, int LoadMs, int AvgPassMs, int MaxPassMs, int BlockMs, double Load, string Verdict, string Note);
public sealed record BenchReport(string Machine, IReadOnlyList<BenchRow> Rows, string Summary);

// The in-app speed check: the active voice converts a bundled 14-second
// speech clip block by block, exactly as the live loop would, timed. Pass
// time over the block it converts decides the verdict — above the block's
// own duration the output cannot keep up. Results go to the UI, bench.log
// and the boot log — the numbers a "it stutters" report needs.
public static class Benchmark
{
    public const string FixtureResource = "bench/fixture.wav";
    public const double FastLoad = 0.5, UsableLoad = 0.9;

    public static float[] LoadFixture16k()
    {
        using var s = typeof(Benchmark).Assembly.GetManifestResourceStream(FixtureResource)
            ?? throw new InvalidOperationException("speed-check clip missing from the build");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var pcm = WavPcm(ms.ToArray());
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
        return samples;
    }

    // The PCM payload (data chunk) of a RIFF/WAVE file; the clip is 16 kHz
    // mono 16-bit, the pipeline's own input format.
    internal static byte[] WavPcm(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            throw new InvalidDataException("not a RIFF file");
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data")
            {
                int len = Math.Max(0, Math.Min(size, wav.Length - pos - 8));
                var pcm = new byte[len];
                Buffer.BlockCopy(wav, pos + 8, pcm, 0, len);
                return pcm;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    // One row per candidate: the converter is created (timed as the load),
    // the clip runs through a RealtimeEngine with the given block size, and
    // the passes after the first two (warm-up) are averaged.
    public static Task<BenchReport> RunAsync(
        IReadOnlyList<(string Label, Func<VoiceConverter> Create)> candidates,
        EngineConfig config, Action<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var clip = LoadFixture16k();
        var rows = new List<BenchRow>();
        foreach (var (label, create) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"{label}: loading…");
            VoiceConverter? converter = null;
            try
            {
                var sw = Stopwatch.StartNew();
                converter = create();
                int loadMs = (int)sw.ElapsedMilliseconds;
                using var engine = new RealtimeEngine(converter, config);
                progress?.Invoke($"{label}: converting the clip…");
                var times = new List<int>();
                double energy = 0;
                long outSamples = 0;
                int passes = 0;
                for (int at = 0; at + engine.Block16 <= clip.Length; at += engine.Block16)
                {
                    ct.ThrowIfCancellationRequested();
                    sw.Restart();
                    var block = engine.ProcessBlock(clip.AsSpan(at, engine.Block16));
                    sw.Stop();
                    passes++;
                    if (passes > 2) times.Add((int)sw.ElapsedMilliseconds);
                    foreach (var s in block) energy += s * s;
                    outSamples += block.Length;
                }
                int avg = times.Count == 0 ? 0 : (int)times.Average();
                int max = times.Count == 0 ? 0 : times.Max();
                double load = avg / (double)(engine.BlockFrames * 10);
                double rms = outSamples == 0 ? 0 : Math.Sqrt(energy / outSamples);
                string note = rms < 1e-4 ? "output was silent" : $"{passes} passes, output RMS {20 * Math.Log10(rms):0} dBFS";
                rows.Add(new BenchRow(label, loadMs, avg, max, engine.BlockFrames * 10, Math.Round(load, 2), Verdict(load, rms), note));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rows.Add(new BenchRow(label, 0, 0, 0, config.BlockMs, 0, "failed", ex.Message));
            }
            finally { converter?.Dispose(); }
        }
        var best = rows.Where(r => r.AvgPassMs > 0 && r.Verdict != "failed").OrderBy(r => r.Load).FirstOrDefault();
        string summary = best == null
            ? "no candidate produced a usable result"
            : $"best: {best.Label} — {best.AvgPassMs} ms per {best.BlockMs} ms block (load {best.Load:0.00}×, {best.Verdict})";
        return new BenchReport(Morphonic.MachineProfile.Describe(), rows, summary);
    }, ct);

    // Pass time against the block it must fit in: live conversion repeats a
    // pass every block, so past 1.0 it never catches up; the margins keep
    // the output steady when the machine is doing other things too.
    internal static string Verdict(double load, double outputRms)
    {
        if (outputRms < 1e-4) return "silent";
        if (load <= FastLoad) return "fast";
        if (load <= UsableLoad) return "usable";
        return "too slow";
    }

    public static string FormatReport(BenchReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"speed check {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"machine: {report.Machine}");
        foreach (var r in report.Rows)
            sb.AppendLine($"  {r.Label}: load {r.LoadMs} ms, avg pass {r.AvgPassMs} ms, max {r.MaxPassMs} ms per {r.BlockMs} ms block, " +
                          $"load {r.Load:0.00}× — {r.Verdict} ({r.Note})");
        sb.AppendLine("  " + report.Summary);
        return sb.ToString();
    }
}
