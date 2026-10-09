using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// The boot sentinel: a marker that exists only during a start, so the next
// start can tell that the previous one never reached the window.
public class BootSentinelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public BootSentinelTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "boot.inprogress");
        BootSentinel.PathOverride = _path;
        BootSentinel.ResetForTests();
    }

    public void Dispose()
    {
        BootSentinel.PathOverride = null;
        BootSentinel.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ThePhaseFollowsTheRunAndIsReportedNextTime()
    {
        Assert.Null(BootSentinel.Arm("1.7.2"));
        Assert.EndsWith("|boot", File.ReadAllText(_path));
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        Assert.EndsWith("|window", File.ReadAllText(_path));
        BootSentinel.Mark(BootSentinel.PhaseCaptions);
        Assert.EndsWith("|captions", File.ReadAllText(_path));

        // "The next start": the run above died with captions running.
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.NotNull(previous);
        Assert.Equal(BootSentinel.PhaseCaptions, previous!.Phase);
        Assert.Equal("ended while captions were running", previous.How);
        Assert.True(previous.WantsSafeBoot);
        Assert.Equal(Environment.ProcessId, previous.Pid);
    }

    [Fact]
    public void AnIdleWindowThatWasKilledIsReportedButDoesNotForceASafeBoot()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseWindow, previous!.Phase);
        Assert.False(previous.WantsSafeBoot);
        Assert.Contains("idle", previous.How);
    }

    [Fact]
    public void AStartThatNeverReachedTheWindowStillReadsAsBefore()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseBoot, previous!.Phase);
        Assert.Equal("never reached the window", previous.How);
        Assert.True(previous.WantsSafeBoot);
    }

    [Fact]
    public void AMarkerFromBeforeThePhasesIsAStart()
    {
        File.WriteAllText(_path, "1.7.1|2026-10-08T20:00:00.0000000+00:00|4242");
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal("1.7.1", previous!.Version);
        Assert.Equal(4242, previous.Pid);
        Assert.Equal(BootSentinel.PhaseBoot, previous.Phase);
    }

    [Fact]
    public void ClearRemovesOnlyThisProcessesOwnMarker()
    {
        // A successor's marker (another pid) must survive the predecessor's late Clear.
        File.WriteAllText(_path, "1.7.2|2026-10-09T05:00:00.0000000+00:00|99999|window");
        BootSentinel.Clear();
        Assert.True(File.Exists(_path));
        BootSentinel.Arm("1.7.2");               // ours now
        BootSentinel.Clear();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AStopAfterTheCleanExitDoesNotBringTheMarkerBack()
    {
        // Main clears the marker, then the usings dispose the controller,
        // whose Stop marks the window phase for a session that was running.
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseCaptions);
        BootSentinel.Clear();
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        Assert.False(File.Exists(_path));
        Assert.Null(BootSentinel.Arm("1.7.2"));   // the next start: nothing to report
    }

    [Fact]
    public void MarkWithoutArmTouchesNothing()
    {
        BootSentinel.Mark(BootSentinel.PhaseCaptions);   // --bench, tests: never armed
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void FirstStartHasNothingToReportAndLeavesAMarker()
    {
        Assert.Null(BootSentinel.Arm("1.3.0"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void AClearedStartIsNotReported()
    {
        BootSentinel.Arm("1.3.0");
        BootSentinel.Clear();
        Assert.False(File.Exists(_path));
        Assert.Null(BootSentinel.Arm("1.3.0"));
    }

    [Fact]
    public void AnUnclearedStartIsReportedWithItsDetails()
    {
        BootSentinel.Arm("1.2.9");
        var previous = BootSentinel.Arm("1.3.0");
        Assert.NotNull(previous);
        Assert.Equal("1.2.9", previous!.Version);
        Assert.Equal(Environment.ProcessId, previous.Pid);
        Assert.True((DateTime.Now - previous.StartedAt).TotalMinutes < 1);
        Assert.True(File.Exists(_path)); // and this start is armed in turn
    }

    [Fact]
    public void AGarbledMarkerStillCountsAsUnfinished()
    {
        File.WriteAllText(_path, "??");
        var previous = BootSentinel.Arm("1.3.0");
        Assert.NotNull(previous);
        Assert.Equal("?", previous!.Version);
        Assert.Equal(0, previous.Pid);
    }
}

// The crash record, read from coredumpctl and the journal: only lines
// naming the app survive, and only so many of them.
public class CrashRecordTests
{
    [Fact]
    public void KeepsOnlyTheLinesNamingTheApp()
    {
        var text =
            "No coredumps found.\n" +
            "Tue 2026-09-05 05:36:58 UTC 12345 1000 1000 SIGSEGV present /home/u/Apps/Chatterbox/Chatterbox 2.1M\n" +
            "Hint: You are currently not seeing messages from other users and the system.\n" +
            "2026-09-05T05:36:58+0000 box systemd-coredump[12350]: Process 12345 (Chatterbox) of user 1000 dumped core.\n" +
            "\n" +
            "2026-09-05T05:36:59+0000 box other[1]: unrelated\n";
        var lines = CrashRecord.FilterLines(text, "Chatterbox");
        Assert.Equal(2, lines.Count);
        Assert.Contains("SIGSEGV", lines[0]);
        Assert.Contains("dumped core", lines[1]);
        var all = string.Join("\n", lines);
        Assert.DoesNotContain("Hint", all);
        Assert.DoesNotContain("unrelated", all);
        Assert.DoesNotContain("No coredumps", all);
    }

    [Fact]
    public void CapsTheNumberOfLines()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i} Chatterbox\n"));
        Assert.Equal(20, CrashRecord.FilterLines(text, "Chatterbox", maxLines: 20).Count);
    }

    [Fact]
    public void EmptyOutputIsHandled()
    {
        Assert.Empty(CrashRecord.FilterLines("", "Chatterbox"));
        Assert.Empty(CrashRecord.FilterLines(null!, "Chatterbox"));
    }
}
