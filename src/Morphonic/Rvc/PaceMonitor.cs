using System;

namespace Morphonic.Rvc;

// One conversion pass as the engine reports it: how long it took, how much
// audio it produced, and how much captured audio was already waiting
// behind it when it finished.
public readonly record struct PassInfo(int PassMs, int BlockMs, int BacklogMs);

// Is conversion keeping up with the microphone? Two numbers, both smoothed:
//  load = pass time ÷ the block it converts. Above 1.0 every pass falls
//         further behind and the output starts to stutter.
//  lag  = audio captured but not yet converted when a pass finished — the
//         delay a listener hears on top of the block itself.
// The status is the worse of the two. A warning is offered once per
// session, after WarnAfterMs of continuous Behind, so a one-off hiccup
// (a game loading) never nags.
public sealed class PaceMonitor
{
    public enum PaceStatus { Unknown, KeepingUp, Strained, Behind }

    public const double StrainedLoad = 0.7, BehindLoad = 1.0;
    public const int StrainedLagMs = 300, BehindLagMs = 700;
    public const int WarnAfterMs = 10_000;
    private const double Smoothing = 0.3;

    private double _load = -1, _lag = -1;
    private long _behindSince = -1;

    public PaceStatus Status { get; private set; } = PaceStatus.Unknown;
    public double Load => _load < 0 ? 0 : _load;
    public int LagMs => _lag < 0 ? 0 : (int)_lag;
    public bool Warned { get; private set; }
    public PassInfo Last { get; private set; }

    public int Passes { get; private set; }
    public long TotalPassMs { get; private set; }
    public int MaxPassMs { get; private set; }
    public int MaxLagMs { get; private set; }
    public double MaxLoad { get; private set; }

    public PaceStatus Record(PassInfo pass, long nowMs)
    {
        Last = pass;
        Passes++;
        TotalPassMs += pass.PassMs;
        if (pass.PassMs > MaxPassMs) MaxPassMs = pass.PassMs;
        if (pass.BacklogMs > MaxLagMs) MaxLagMs = pass.BacklogMs;

        double load = pass.PassMs / (double)Math.Max(pass.BlockMs, 1);
        _load = _load < 0 ? load : _load + Smoothing * (load - _load);
        _lag = _lag < 0 ? pass.BacklogMs : _lag + Smoothing * (pass.BacklogMs - _lag);
        if (_load > MaxLoad) MaxLoad = _load;

        Status = _load >= BehindLoad || _lag >= BehindLagMs ? PaceStatus.Behind
               : _load >= StrainedLoad || _lag >= StrainedLagMs ? PaceStatus.Strained
               : PaceStatus.KeepingUp;
        if (Status == PaceStatus.Behind) { if (_behindSince < 0) _behindSince = nowMs; }
        else _behindSince = -1;
        return Status;
    }

    // True exactly once: the first call after Behind has lasted WarnAfterMs.
    public bool ShouldWarn(long nowMs)
    {
        if (Warned || _behindSince < 0 || nowMs - _behindSince < WarnAfterMs) return false;
        Warned = true;
        return true;
    }

    public string Describe() =>
        $"pass {Last.PassMs} ms per {Last.BlockMs} ms block, load {Load:0.00}×, lag {LagMs} ms";

    public string Summary() => Passes == 0
        ? "no conversion passes"
        : $"{Passes} passes, avg {TotalPassMs / Passes} ms, max {MaxPassMs} ms, " +
          $"peak load {MaxLoad:0.00}×, worst lag {MaxLagMs} ms";
}
