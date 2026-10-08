using Chatterbox.Stt;
using Xunit;
using static Chatterbox.Stt.SttPaceMonitor;

namespace Chatterbox.Tests;

// The pace monitor turns per-pass timings into "keeping up" / "falling
// behind" and offers its one warning only after ten sustained seconds.
public class SttPaceMonitorTests
{
    [Fact]
    public void FastPassesKeepUpAndNeverWarn()
    {
        var m = new SttPaceMonitor();
        for (int i = 1; i <= 20; i++)
            Assert.Equal(PaceStatus.KeepingUp, m.Record(new SttPassInfo(100, 3000, 300, 0), i * 300));
        Assert.False(m.ShouldWarn(60_000));
        Assert.Equal(20, m.Passes);
    }

    [Fact]
    public void SlowPassesFallBehindAndWarnExactlyOnce()
    {
        var m = new SttPaceMonitor();
        long t = 0;
        for (int i = 0; i < 40; i++) { t += 900; m.Record(new SttPassInfo(900, 8000, 500, 800), t); }
        Assert.Equal(PaceStatus.Behind, m.Status);
        Assert.True(m.Load > 1.5);
        Assert.True(m.ShouldWarn(t));
        Assert.False(m.ShouldWarn(t + 60_000));
        Assert.True(m.Warned);
    }

    [Fact]
    public void WarningWaitsTenContinuousSeconds()
    {
        var m = new SttPaceMonitor();
        m.Record(new SttPassInfo(2000, 5000, 500, 1500), 0);
        Assert.Equal(PaceStatus.Behind, m.Status);
        Assert.False(m.ShouldWarn(5000));
        m.Record(new SttPassInfo(2000, 5000, 500, 1500), 9000);
        Assert.False(m.ShouldWarn(9000));
        Assert.True(m.ShouldWarn(10_500));
    }

    [Fact]
    public void RecoveryResetsTheClock()
    {
        var m = new SttPaceMonitor();
        m.Record(new SttPassInfo(2000, 5000, 500, 1500), 0);
        for (int i = 1; i <= 20; i++) m.Record(new SttPassInfo(50, 3000, 300, 0), i * 300);
        Assert.Equal(PaceStatus.KeepingUp, m.Status);
        Assert.False(m.ShouldWarn(60_000));
    }

    [Fact]
    public void LagAloneCountsAsBehind()
    {
        var m = new SttPaceMonitor();
        for (int i = 0; i < 10; i++) m.Record(new SttPassInfo(200, 5000, 1000, 3000), i * 1000);
        Assert.Equal(PaceStatus.Behind, m.Status);
        Assert.True(m.LagMs > 2000);
    }

    [Fact]
    public void ATinyBudgetDoesNotSpikeTheLoad()
    {
        // A final pass right after a partial one has almost no "new" audio;
        // that must not read as a 50x overload.
        var m = new SttPaceMonitor();
        m.Record(new SttPassInfo(100, 4000, 300, 0), 0);
        m.Record(new SttPassInfo(100, 4000, 2, 0), 100);
        Assert.Equal(PaceStatus.KeepingUp, m.Status);
    }

    [Fact]
    public void SummaryReportsSessionNumbers()
    {
        var m = new SttPaceMonitor();
        Assert.Equal("no recognition passes", m.Summary());
        m.Record(new SttPassInfo(100, 3000, 300, 0), 0);
        m.Record(new SttPassInfo(300, 3000, 300, 400), 300);
        Assert.Contains("2 passes", m.Summary());
        Assert.Contains("avg 200 ms", m.Summary());
        Assert.Contains("max 300 ms", m.Summary());
        Assert.Contains("worst lag 400 ms", m.Summary());
    }
}
