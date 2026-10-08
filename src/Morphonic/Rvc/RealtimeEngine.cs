using System;
using System.Diagnostics;
using System.Threading;
using Morphonic.Audio;

namespace Morphonic.Rvc;

public sealed class EngineConfig
{
    public int BlockMs { get; set; } = 250;
    public int ExtraMs { get; set; } = 2500;
    public int CrossfadeMs { get; set; } = 50;
    public int NoiseGateDb { get; set; } = -60;   // -60 = off
    public double RmsMixRate { get; set; } = 0.5; // 1 = keep the voice's own loudness
    public int OutputGainDb { get; set; } = 0;
}

// The real-time loop around VoiceConverter, after RVC's realtime GUI:
// 16 kHz microphone audio accumulates in a queue; every BlockMs a worker
// shifts the new block into the analysis window (context + crossfade +
// search + block), converts the window's tail, aligns it to the previous
// output with SOLA and crossfades, and queues BlockMs of converted audio at
// the voice's sample rate for the playback device. Everything is sized in
// whole 10 ms frames (zc samples), as RVC does.
public sealed class RealtimeEngine : IDisposable
{
    public const int InputRate = 16000;

    private readonly VoiceConverter _converter;
    private readonly EngineConfig _config;

    public int SampleRate { get; }
    public int Zc { get; }                 // samples per 10 ms at the voice's rate
    public int BlockFrames { get; }
    public int Block { get; }              // samples at SampleRate
    public int Block16 { get; }            // samples at 16 kHz
    public int ExtraFrames { get; }
    public int CrossfadeFrames { get; }
    public int SolaBuffer { get; }         // samples at SampleRate
    public int SolaSearch { get; }
    public int ReturnFrames { get; }
    public int WindowFrames { get; }

    private readonly float[] _window16;
    private readonly float[] _solaPrev;
    private readonly float[] _fadeIn;
    private readonly RingBuffer _pending;  // 16 kHz input waiting for a pass
    private readonly RingBuffer _output;   // converted audio waiting for playback
    private readonly float[] _blockBuf;
    private readonly float _gain;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _running;
    private bool _primed;
    // Playback starts draining a little after the first block lands, so
    // the ring keeps a cushion against pass-to-pass timing jitter: the
    // hold is half the first pass (the jitter a later pass can add),
    // between 30 ms and half a block. It is the only latency added here.
    private long _primedAt;
    private int _primeHoldMs;

    public float InputLevel { get; private set; }
    public float OutputLevel { get; private set; }
    public long SkippedSamples { get; private set; }
    public long Underruns => _underruns;
    private long _underruns;

    public event Action<PassInfo>? OnPass;
    public event Action<Exception>? OnFailed;

    public RealtimeEngine(VoiceConverter converter, EngineConfig config)
    {
        _converter = converter;
        _config = config;
        SampleRate = converter.SampleRate;
        Zc = SampleRate / 100;
        BlockFrames = Math.Max(1, (int)Math.Round(config.BlockMs / 10.0));
        Block = BlockFrames * Zc;
        Block16 = BlockFrames * 160;
        ExtraFrames = Math.Max(0, (int)Math.Round(config.ExtraMs / 10.0));
        CrossfadeFrames = Math.Max(1, (int)Math.Round(config.CrossfadeMs / 10.0));
        SolaBuffer = Math.Min(CrossfadeFrames, 4) * Zc;
        SolaSearch = Zc;
        WindowFrames = ExtraFrames + CrossfadeFrames + 1 + BlockFrames;
        ReturnFrames = BlockFrames + SolaBuffer / Zc + 1;
        _window16 = new float[WindowFrames * 160];
        _solaPrev = new float[SolaBuffer];
        _fadeIn = Sola.FadeIn(SolaBuffer);
        _pending = new RingBuffer(InputRate * 10);
        _output = new RingBuffer(SampleRate * 10);
        _blockBuf = new float[Block16];
        _gain = (float)Math.Pow(10, config.OutputGainDb / 20.0);
        converter.ResetCache();
    }

    // The delay the pipeline itself adds: a block must be complete before
    // it can be converted, plus the crossfade region.
    public int LatencyMs => (BlockFrames + SolaBuffer / Zc + 1) * 10;

    public int PendingMs => _pending.Count * 1000 / InputRate;

    // From the capture thread: 16 kHz mono.
    public void PushInput(ReadOnlySpan<float> samples16k)
    {
        _pending.Write(samples16k);
        _wake.Set();
    }

    // From the playback thread: converted audio at SampleRate; what is not
    // available yet is silence (counted as an underrun once the engine has
    // produced its first block).
    public int PullOutput(Span<float> dst)
    {
        if (!_primed || Environment.TickCount64 - _primedAt < _primeHoldMs)
        {
            dst.Clear();
            return 0;
        }
        int n = _output.Read(dst);
        if (n < dst.Length)
        {
            dst.Slice(n).Clear();
            Interlocked.Increment(ref _underruns);
        }
        return n;
    }

    // The hold itself, for the session log.
    public int PrimeHoldMs => _primeHoldMs;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "Morphonic conversion", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        _wake.Set();
        _worker?.Join(5000);
        _worker = null;
    }

    private void Loop()
    {
        var sw = new Stopwatch();
        while (_running)
        {
            if (_pending.Count < Block16)
            {
                _wake.WaitOne(5);
                continue;
            }
            try
            {
                // Hopelessly behind (more than three blocks queued): drop
                // the oldest audio and say so, rather than lagging forever.
                int backlog = _pending.Count - Block16;
                if (backlog > 3 * Block16)
                {
                    int skip = backlog - Block16;
                    _pending.Skip(skip);
                    SkippedSamples += skip;
                }
                _pending.Read(_blockBuf);
                sw.Restart();
                var outBlock = ProcessBlock(_blockBuf);
                sw.Stop();
                _output.Write(outBlock);
                if (!_primed)
                {
                    _primeHoldMs = Math.Clamp((int)sw.ElapsedMilliseconds / 2, 30, BlockFrames * 5);
                    _primedAt = Environment.TickCount64;
                    _primed = true;
                }
                OnPass?.Invoke(new PassInfo((int)sw.ElapsedMilliseconds, BlockFrames * 10, PendingMs));
            }
            catch (Exception ex)
            {
                _running = false;
                OnFailed?.Invoke(ex);
                return;
            }
        }
    }

    // One pass, synchronous: Block16 new samples in, Block converted
    // samples out. Public for the speed check and the tests.
    public float[] ProcessBlock(ReadOnlySpan<float> block16)
    {
        if (block16.Length != Block16) throw new ArgumentException($"expected {Block16} samples, got {block16.Length}");
        var gated = block16.ToArray();
        InputLevel = Level(gated);
        if (_config.NoiseGateDb > -60) Gate(gated, _config.NoiseGateDb);

        // Shift the window left by a block and append the new one.
        Array.Copy(_window16, Block16, _window16, 0, _window16.Length - Block16);
        gated.CopyTo(_window16.AsSpan(_window16.Length - Block16));

        var infer = _converter.Convert(_window16, Block16, ExtraFrames, ReturnFrames);
        if (infer.Length < Block + SolaBuffer + SolaSearch)
        {
            // The voice yielded less than the recipe needs (a short
            // window): pad with silence rather than fail.
            Array.Resize(ref infer, Block + SolaBuffer + SolaSearch);
        }

        if (_config.RmsMixRate < 1.0) MixLoudness(infer, _config.RmsMixRate);

        // SOLA: slide within the search range to line up with the kept
        // tail of the previous block, then crossfade.
        int offset = Sola.FindOffset(infer.AsSpan(0, SolaBuffer + SolaSearch), _solaPrev, SolaSearch);
        var aligned = infer.AsSpan(offset);
        Sola.Crossfade(aligned.Slice(0, SolaBuffer), _solaPrev, _fadeIn);
        var outBlock = aligned.Slice(0, Block).ToArray();
        aligned.Slice(Block, SolaBuffer).CopyTo(_solaPrev);

        if (_gain != 1f) for (int i = 0; i < outBlock.Length; i++) outBlock[i] = Math.Clamp(outBlock[i] * _gain, -1f, 1f);
        OutputLevel = Level(outBlock);
        return outBlock;
    }

    // Per 10 ms frame: below the threshold (dBFS of the frame's RMS) the
    // frame is silenced, so room noise never reaches the voice.
    internal static void Gate(float[] block16, int thresholdDb)
    {
        for (int start = 0; start < block16.Length; start += 160)
        {
            int len = Math.Min(160, block16.Length - start);
            double energy = 0;
            for (int i = 0; i < len; i++) energy += block16[start + i] * block16[start + i];
            double db = 20 * Math.Log10(Math.Sqrt(energy / len) + 1e-9);
            if (db < thresholdDb) Array.Clear(block16, start, len);
        }
    }

    // The RVC "RMS mix": the converted audio's loudness envelope is pulled
    // toward the input's by (1 - rate). Envelopes are centered 40 ms RMS
    // frames every 10 ms, linearly interpolated to each sample.
    private void MixLoudness(float[] infer, double rate)
    {
        int frames = infer.Length / Zc + 1;
        var inTail = _window16.AsSpan(_window16.Length - Math.Min(_window16.Length, ReturnFrames * 160));
        var rms1 = FrameRms(inTail, 640, 160, frames);
        var rms2 = FrameRms(infer, 4 * Zc, Zc, frames);
        int n = infer.Length;
        for (int s = 0; s < n; s++)
        {
            double x = (double)s * (frames - 1) / n;
            int k = (int)x;
            double t = x - k;
            double a = Lerp(rms1, k, t), b = Math.Max(Lerp(rms2, k, t), 1e-3);
            infer[s] = (float)(infer[s] * Math.Pow(a / b, 1.0 - rate));
        }
    }

    private static double Lerp(float[] v, int k, double t)
    {
        if (k + 1 >= v.Length) return v[Math.Min(k, v.Length - 1)];
        return v[k] + (v[k + 1] - v[k]) * t;
    }

    // Centered RMS frames (zero padding at the ends), `count` of them.
    internal static float[] FrameRms(ReadOnlySpan<float> x, int frame, int hop, int count)
    {
        var rms = new float[count];
        for (int k = 0; k < count; k++)
        {
            int center = k * hop;
            double energy = 0;
            int n = 0;
            for (int i = center - frame / 2; i < center + frame / 2; i++)
            {
                n++;
                if (i < 0 || i >= x.Length) continue;
                energy += x[i] * x[i];
            }
            rms[k] = n == 0 ? 0f : (float)Math.Sqrt(energy / n);
        }
        return rms;
    }

    // Block RMS lifted so normal speech reaches the upper half of a meter.
    private const float MeterGain = 6f;
    private static float Level(ReadOnlySpan<float> block)
    {
        if (block.Length == 0) return 0f;
        double energy = 0;
        foreach (var s in block) energy += s * s;
        return MathF.Min(1f, MathF.Sqrt((float)(energy / block.Length)) * MeterGain);
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
