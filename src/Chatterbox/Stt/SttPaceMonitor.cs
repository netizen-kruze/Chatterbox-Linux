using System;

namespace Chatterbox.Stt;

// One re-transcription pass as reported by the pipeline: how long the
// engine took, how much audio the pass covered, how much of that was new
// since the previous pass, and how much captured audio was already
// waiting behind it when it finished.
// Final = the utterance-closing pass, which covers a whole utterance after
// only a few hundred ms of new audio: it counts for lag, not for load.
public readonly record struct SttPassInfo(int PassMs, int WindowMs, int NewAudioMs, int BacklogMs, bool Final = false);

// Is recognition keeping up with speech? Two numbers, both smoothed:
//  load = pass time ÷ the real time that pass had to fit in (the audio
//         that arrived since the previous pass). Above 1.0 every pass
//         falls further behind, and captions lag more the longer a
//         sentence runs — the "it's really slow" a CPU user sees.
//  lag  = audio already captured but not yet covered by any hypothesis
//         when a pass finished — the delay a listener actually notices.
// The status is the worse of the two. A warning is offered once per
// session, after WarnAfterMs of continuous Behind, so a one-off hiccup
// (the game loading a world) never nags.
public sealed class SttPaceMonitor
{
    public enum PaceStatus { Unknown, KeepingUp, Strained, Behind }

    public const double StrainedLoad = 0.7, BehindLoad = 1.0;
    public const int StrainedLagMs = 1000, BehindLagMs = 2000;
    public const int WarnAfterMs = 10_000;
    private const double Smoothing = 0.3;   // weight of the newest pass
    private const int MinBudgetMs = 200;    // a pass never "had" less time than this

    private double _load = -1, _lag = -1;
    private long _behindSince = -1;

    public PaceStatus Status { get; private set; } = PaceStatus.Unknown;
    public double Load => _load < 0 ? 0 : _load;
    public int LagMs => _lag < 0 ? 0 : (int)_lag;
    public bool Warned { get; private set; }
    public SttPassInfo Last { get; private set; }

    // Session statistics for the boot-log summary.
    public int Passes { get; private set; }
    public long TotalPassMs { get; private set; }
    public int MaxPassMs { get; private set; }
    public int MaxLagMs { get; private set; }
    public double MaxLoad { get; private set; }

    public PaceStatus Record(SttPassInfo pass, long nowMs)
    {
        Last = pass;
        Passes++;
        TotalPassMs += pass.PassMs;
        if (pass.PassMs > MaxPassMs) MaxPassMs = pass.PassMs;
        if (pass.BacklogMs > MaxLagMs) MaxLagMs = pass.BacklogMs;

        if (!pass.Final)
        {
            double load = pass.PassMs / (double)Math.Max(pass.NewAudioMs, MinBudgetMs);
            _load = _load < 0 ? load : _load + Smoothing * (load - _load);
        }
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
        $"pass {Last.PassMs} ms over {Last.WindowMs / 1000.0:0.0} s of audio (+{Last.NewAudioMs} ms new), " +
        $"load {Load:0.00}×, lag {LagMs} ms";

    public string Summary() => Passes == 0
        ? "no recognition passes"
        : $"{Passes} passes, avg {TotalPassMs / Passes} ms, max {MaxPassMs} ms, " +
          $"peak load {MaxLoad:0.00}×, worst lag {MaxLagMs} ms";
}
