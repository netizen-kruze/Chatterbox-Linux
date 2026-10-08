using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Settings durability: saves are atomic with a .bak of the previous good
// file, and a damaged file is recovered from that backup instead of
// silently becoming empty defaults (which the next save would persist —
// the way an auto-start list gets lost for good).
public class SttSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SttSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "stt_settings.json");
        SttSettings.PathOverride = _path;
        ErrorLog.PathOverride = Path.Combine(_dir, "error.log");
    }

    public void Dispose()
    {
        SttSettings.PathOverride = null;
        ErrorLog.PathOverride = null;
        SttSettings.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── provisional defaults (data folder momentarily invisible at boot) ──

    [Fact]
    public void FirstRun_NeverWaits_NotProvisional()
    {
        SttSettings.HasRunBeforeOverride = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = SttSettings.LoadWithRetry(TimeSpan.FromSeconds(5));
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.False(SttSettings.ProvisionalDefaults);
        Assert.Empty(s.AutoStartFriends);
    }

    [Fact]
    public void ReturningUser_FileAppearsDuringWait_IsLoadedNormally()
    {
        SttSettings.HasRunBeforeOverride = true;
        var writer = new Thread(() => { Thread.Sleep(400); WithFriends("alice", "bob").Save(); });
        writer.Start();
        var s = SttSettings.LoadWithRetry(TimeSpan.FromSeconds(5));
        writer.Join();
        Assert.False(SttSettings.ProvisionalDefaults);
        Assert.Equal("settings file", SttSettings.LastLoadSource);
        Assert.Equal(new[] { "alice", "bob" }, s.AutoStartFriends.Select(f => f.Name));
    }

    [Fact]
    public void ReturningUser_FileStillMissing_GoesProvisional_NeverWritesDefaults_ThenRecoversAndMerges()
    {
        SttSettings.HasRunBeforeOverride = true;
        var s = SttSettings.LoadWithRetry(TimeSpan.FromMilliseconds(300));
        Assert.True(SttSettings.ProvisionalDefaults);

        // A change made while provisional must not hit the disk...
        s.IntervalMs = 2000;
        s.Save();
        Assert.False(File.Exists(_path));
        Assert.False(s.TryRecoverFromDisk());      // nothing to recover from yet

        // ...the real file "becomes visible" (written behind the app's back —
        // Save() itself is deferred while provisional): adopt it, keep the change.
        bool recoveredEvent = false;
        SttSettings.Recovered += () => recoveredEvent = true;
        File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(WithFriends("alice", "bob")));
        Assert.True(s.TryRecoverFromDisk());
        Assert.True(recoveredEvent);
        Assert.False(SttSettings.ProvisionalDefaults);
        Assert.Equal(new[] { "alice", "bob" }, s.AutoStartFriends.Select(f => f.Name));
        Assert.True(s.AutoStartEnabled);
        Assert.Equal(2000, s.IntervalMs);
        var onDisk = SttSettings.Load();
        Assert.Equal(2000, onDisk.IntervalMs);       // merged and written
        Assert.Equal(2, onDisk.AutoStartFriends.Count);
    }

    [Fact]
    public void GiveUpProvisional_FlushesPendingChanges()
    {
        SttSettings.HasRunBeforeOverride = true;
        var s = SttSettings.LoadWithRetry(TimeSpan.FromMilliseconds(200));
        s.Engine = "whisper";
        s.Save();                                    // deferred
        Assert.False(File.Exists(_path));
        s.GiveUpProvisional();
        Assert.False(SttSettings.ProvisionalDefaults);
        Assert.Equal("whisper", SttSettings.Load().Engine);
    }

    private static SttSettings WithFriends(params string[] names)
    {
        var s = new SttSettings { AutoStartEnabled = true };
        foreach (var n in names) s.AutoStartFriends.Add(new SttSettings.AutoFriend { Id = "usr_" + n, Name = n });
        return s;
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndKeepsBackupOfPreviousSave()
    {
        WithFriends("alice").Save();
        Assert.False(File.Exists(_path + ".bak"));   // nothing to back up yet
        WithFriends("alice", "bob").Save();
        Assert.True(File.Exists(_path + ".bak"));    // previous good file kept

        var loaded = SttSettings.Load();
        Assert.Equal("settings file", SttSettings.LastLoadSource);
        Assert.Equal(new[] { "alice", "bob" }, loaded.AutoStartFriends.Select(f => f.Name));
        Assert.False(File.Exists(_path + ".tmp"));   // temp file never left behind
    }

    [Fact]
    public void DamagedFile_RecoversFromBackup_AndKeepsCorruptCopy()
    {
        WithFriends("alice", "bob").Save();
        WithFriends("alice", "bob", "carol").Save(); // .bak now = alice+bob
        File.WriteAllText(_path, "{ \"AutoStartFriends\": [ {");   // truncated mid-write

        var loaded = SttSettings.Load();
        Assert.StartsWith("backup", SttSettings.LastLoadSource);
        Assert.Equal(new[] { "alice", "bob" }, loaded.AutoStartFriends.Select(f => f.Name));
        Assert.True(File.Exists(_path + ".corrupt"));
        Assert.Contains("restored the previous good copy", File.ReadAllText(ErrorLog.PathOverride!));
    }

    [Fact]
    public void DamagedFile_NoBackup_FallsBackToDefaults_ButNeverSilently()
    {
        File.WriteAllText(_path, "not json at all");
        var loaded = SttSettings.Load();
        Assert.StartsWith("defaults", SttSettings.LastLoadSource);
        Assert.Empty(loaded.AutoStartFriends);
        Assert.True(File.Exists(_path + ".corrupt"));
        Assert.Contains("SttSettings.Load", File.ReadAllText(ErrorLog.PathOverride!));
    }

    [Fact]
    public void MissingFile_IsDefaults_WithoutLoggingAnError()
    {
        var loaded = SttSettings.Load();
        Assert.Equal("defaults (no settings file yet)", SttSettings.LastLoadSource);
        Assert.Empty(loaded.AutoStartFriends);
        Assert.False(File.Exists(ErrorLog.PathOverride!));
    }

    [Fact]
    public void GivingUpProvisionalWritesTheDefaultsSoTheNextStartFindsAFile()
    {
        SttSettings.HasRunBeforeOverride = true;           // the marker says "ran before"
        var s = SttSettings.LoadWithRetry(TimeSpan.Zero);   // but there is no file: provisional
        Assert.True(SttSettings.ProvisionalDefaults);
        s.GiveUpProvisional();
        Assert.False(SttSettings.ProvisionalDefaults);
        Assert.True(File.Exists(_path));
        Assert.Equal("settings file", (SttSettings.Load(), SttSettings.LastLoadSource).LastLoadSource);
    }
}
