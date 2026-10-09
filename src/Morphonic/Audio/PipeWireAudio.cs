using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Morphonic.Audio;

// Linux audio through child processes: pw-record delivers the microphone
// as raw 16 kHz mono s16 on its stdout (PipeWire does the resampling),
// pw-play takes raw s16 at the voice's rate on its stdin. PulseAudio's
// parec/pacat and ALSA's arecord/aplay stand in on machines without
// PipeWire. No native audio library to ship, and the processes can be
// killed from any thread.
internal static class PipeWireAudio
{
    public sealed record Command(string Label, string File, string[] Args);

    // pw-record / pw-play: "--raw" (raw samples on the pipe) exists from
    // PipeWire 1.2; 1.0 (Ubuntu 24.04 LTS, Debian 12 backports) rejects the
    // option and exits at once — there a pipe is raw anyway, so the same
    // tool is tried again without it before falling back to PulseAudio's.
    public static IEnumerable<Command> CaptureCommands(string? target)
    {
        foreach (var raw in new[] { true, false })
        {
            var pw = new List<string> { "--rate=16000", "--channels=1", "--format=s16" };
            if (raw) pw.Add("--raw");
            pw.Add("--latency=20ms");
            if (target != null) pw.Add("--target=" + target);
            pw.Add("-");
            yield return new Command("pw-record", "pw-record", pw.ToArray());
        }

        var pa = new List<string> { "--rate=16000", "--channels=1", "--format=s16le", "--raw", "--latency-msec=20" };
        if (target != null) pa.Add("--device=" + target);
        yield return new Command("parec", "parec", pa.ToArray());

        yield return new Command("arecord", "arecord", new[] { "-q", "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "raw", "-" });
    }

    public static IEnumerable<Command> PlaybackCommands(string? target, int rate)
    {
        foreach (var raw in new[] { true, false })
        {
            var pw = new List<string> { $"--rate={rate}", "--channels=1", "--format=s16" };
            if (raw) pw.Add("--raw");
            pw.Add("--latency=20ms");
            if (target != null) pw.Add("--target=" + target);
            pw.Add("-");
            yield return new Command("pw-play", "pw-play", pw.ToArray());
        }

        var pa = new List<string> { $"--rate={rate}", "--channels=1", "--format=s16le", "--raw", "--latency-msec=20" };
        if (target != null) pa.Add("--device=" + target);
        yield return new Command("pacat", "pacat", pa.ToArray());

        yield return new Command("aplay", "aplay", new[] { "-q", "-f", "S16_LE", "-r", rate.ToString(), "-c", "1", "-t", "raw", "-" });
    }

    public static Process Launch(Command cmd, bool stdin, StringBuilder stderr)
    {
        var psi = new ProcessStartInfo(cmd.File)
        {
            UseShellExecute = false,
            RedirectStandardInput = stdin,
            RedirectStandardOutput = !stdin,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        LinuxHost.StripSteamPreload(psi);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"{cmd.File} did not start");
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) if (stderr.Length < 4000) stderr.AppendLine(e.Data);
        };
        p.BeginErrorReadLine();
        return p;
    }

    public static string Tail(StringBuilder stderr)
    {
        string text;
        lock (stderr) text = stderr.ToString();
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0) return "";
        if (text.Length > 200) text = "…" + text[^200..];
        return " — " + text;
    }

    // A tool that is missing exits at once (or fails to start); one that
    // ran into a problem prints and exits within the grace period.
    public static bool DiedEarly(Process p, int graceMs)
    {
        try { return p.WaitForExit(graceMs); } catch { return true; }
    }

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
    private const int F_SETPIPE_SZ = 1031;

    // Shrinks the pipe into the player to one page, so a blocking write is
    // paced by the player's own consumption and at most ~4 KB (50 ms at
    // 40 kHz s16) ever waits in the pipe. Best effort.
    public static void ShrinkPipe(Stream stdin)
    {
        try
        {
            if (stdin is FileStream fs) fcntl((int)fs.SafeFileHandle.DangerousGetHandle(), F_SETPIPE_SZ, 4096);
        }
        catch { }
    }
}

internal sealed class PipeWireInput : IAudioInput
{
    private const int ChunkBytes = 640;   // 20 ms of 16 kHz s16
    private const int NoDataProbeMs = 5000;

    private readonly AudioDevice? _device;
    private Process? _proc;
    private Thread? _reader;
    private Timer? _noDataProbe;
    private long _bytesRead, _bytesAtProbe;
    private volatile bool _stopping, _noData;
    private Action<float[]>? _onSamples;

    public string Backend { get; private set; } = "";
    public event Action<Exception>? OnFailed;

    public PipeWireInput(AudioDevice? device) { _device = device; }

    public void Start(Action<float[]> onSamples16k)
    {
        _onSamples = onSamples16k;
        string? target = _device != null && _device.Id.Length > 0 ? _device.Id : null;
        var failures = new List<string>();
        foreach (var cmd in PipeWireAudio.CaptureCommands(target))
        {
            var stderr = new StringBuilder();
            Process p;
            try { p = PipeWireAudio.Launch(cmd, stdin: false, stderr); }
            catch (Exception ex) { failures.Add($"{cmd.Label}: {ex.Message}"); continue; }
            if (PipeWireAudio.DiedEarly(p, 400))
            {
                failures.Add($"{cmd.Label}: exited with code {p.ExitCode}{PipeWireAudio.Tail(stderr)}");
                p.Dispose();
                continue;
            }
            _proc = p;
            Backend = cmd.Label + (target != null ? " → " + _device!.Name : " (default input)");
            _reader = new Thread(() => ReadLoop(p, stderr)) { IsBackground = true, Name = "Morphonic capture" };
            _reader.Start();
            // A recorder that runs but never delivers a byte (a vanished
            // target, a suspended device) is reported, not waited on forever.
            _noDataProbe = new Timer(_ =>
            {
                long now = Interlocked.Read(ref _bytesRead);
                if (now == _bytesAtProbe && !_stopping)
                {
                    _noData = true;
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
                _bytesAtProbe = now;
            }, null, NoDataProbeMs, NoDataProbeMs);
            return;
        }
        throw new InvalidOperationException("no recorder could be started (pw-record, parec, arecord): " + string.Join("; ", failures) +
                                            ". On Fedora: sudo dnf install pipewire-utils");
    }

    private void ReadLoop(Process p, StringBuilder stderr)
    {
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[ChunkBytes];
        var samples = new float[ChunkBytes / 2];
        bool eof = false;
        try
        {
            while (!_stopping && !eof)
            {
                int filled = 0;
                while (filled < buf.Length)
                {
                    int n = stream.Read(buf, filled, buf.Length - filled);
                    if (n <= 0) { eof = true; break; }
                    Interlocked.Add(ref _bytesRead, n);
                    filled += n;
                }
                if (eof) break;
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = unchecked((short)(buf[2 * i] | (buf[2 * i + 1] << 8))) / 32768f;
                _onSamples?.Invoke((float[])samples.Clone());
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) OnFailed?.Invoke(ex);
            return;
        }
        if (_stopping) return;
        try { p.WaitForExit(1000); } catch { }
        string why = _noData
            ? $"{Backend} delivered no audio for {NoDataProbeMs / 1000} s — is the microphone still connected and selected?"
            : $"{Backend} stopped unexpectedly";
        try { if (!_noData && p.HasExited) why += $" (exit code {p.ExitCode})"; } catch { }
        if (!_noData) why += PipeWireAudio.Tail(stderr);
        OnFailed?.Invoke(new IOException(why));
    }

    public void Stop()
    {
        var p = _proc;
        _stopping = true;
        _proc = null;
        _noDataProbe?.Dispose();
        _noDataProbe = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        if (_reader != null && _reader != Thread.CurrentThread) _reader.Join(2000);
        _reader = null;
        try { p.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}

internal sealed class PipeWireOutput : IAudioOutput
{
    private readonly AudioDevice? _device;
    private Process? _proc;
    private Thread? _writer;
    private volatile bool _stopping;

    public string Backend { get; private set; } = "";
    public event Action<Exception>? OnFailed;

    public PipeWireOutput(AudioDevice? device) { _device = device; }

    public void Start(Func<float[], int> pull, int sourceRate)
    {
        string? target = _device != null && _device.Id.Length > 0 ? _device.Id : null;
        var failures = new List<string>();
        foreach (var cmd in PipeWireAudio.PlaybackCommands(target, sourceRate))
        {
            var stderr = new StringBuilder();
            Process p;
            try { p = PipeWireAudio.Launch(cmd, stdin: true, stderr); }
            catch (Exception ex) { failures.Add($"{cmd.Label}: {ex.Message}"); continue; }
            if (PipeWireAudio.DiedEarly(p, 400))
            {
                failures.Add($"{cmd.Label}: exited with code {p.ExitCode}{PipeWireAudio.Tail(stderr)}");
                p.Dispose();
                continue;
            }
            _proc = p;
            Backend = cmd.Label + (target != null ? " → " + _device!.Name : " (default output)");
            var stdin = p.StandardInput.BaseStream;
            PipeWireAudio.ShrinkPipe(stdin);
            _writer = new Thread(() => WriteLoop(p, stdin, pull, sourceRate, stderr)) { IsBackground = true, Name = "Morphonic playback", Priority = ThreadPriority.AboveNormal };
            _writer.Start();
            return;
        }
        throw new InvalidOperationException("no player could be started (pw-play, pacat, aplay): " + string.Join("; ", failures));
    }

    // 10 ms at a time; the write blocks when the (shrunken) pipe is full,
    // which is exactly the pacing wanted — silence is written when the
    // engine has nothing yet, so the stream never stalls.
    private void WriteLoop(Process p, Stream stdin, Func<float[], int> pull, int rate, StringBuilder stderr)
    {
        int chunk = Math.Max(1, rate / 100);
        var samples = new float[chunk];
        var bytes = new byte[chunk * 2];
        try
        {
            while (!_stopping)
            {
                pull(samples);
                for (int i = 0; i < chunk; i++)
                {
                    short s = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), -32768, 32767);
                    bytes[2 * i] = (byte)(s & 0xFF);
                    bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
                }
                stdin.Write(bytes, 0, bytes.Length);
                stdin.Flush();
            }
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            string why = $"{Backend} stopped unexpectedly";
            try { if (p.HasExited) why += $" (exit code {p.ExitCode})"; } catch { }
            why += PipeWireAudio.Tail(stderr);
            OnFailed?.Invoke(new IOException(why, ex));
        }
    }

    public void Stop()
    {
        var p = _proc;
        _stopping = true;
        _proc = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        if (_writer != null && _writer != Thread.CurrentThread) _writer.Join(2000);
        _writer = null;
        try { p.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
