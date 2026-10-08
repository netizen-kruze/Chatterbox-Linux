using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// Drives PresenceWatcher against a synthetic VRChat log file — the
// behaviors specified by docs/LOG_FORMAT.md, verified directly.
public sealed class PresenceWatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public PresenceWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "chatterbox-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "output_log_2026-08-28_12-00-00.txt");
        File.WriteAllText(_logPath, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Real log lines are "yyyy.MM.dd HH:mm:ss <level> -  <content>" and the
    // watcher skips anything under 30 chars, so pad like VRChat does.
    private void Append(string content) =>
        File.AppendAllText(_logPath, $"2026.08.28 12:00:00 Log        -  {content}\n");

    private PresenceWatcher StartWatcher()
    {
        var w = new PresenceWatcher(_dir);
        w.IsGameRunning = () => true; // the test machine has no VRChat process
        w.Start(poll: false); // tests drive PollOnce themselves
        return w;
    }

    [Fact]
    public void CatchUp_RebuildsRoster_WithoutRaisingEvents()
    {
        Append("[Behaviour] Joining wrld_aaa:1234~private(usr_me)");
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        Append("[Behaviour] OnPlayerJoined Bob (usr_bob)");

        int events = 0;
        using var w = new PresenceWatcher(_dir);
        w.IsGameRunning = () => true;
        w.PlayerJoined += (_, _) => events++;
        w.WorldChanged += (_, _) => events++;
        w.Start(poll: false);

        Assert.Equal(0, events);
        Assert.Equal(2, w.PlayerCount);
        Assert.Equal("wrld_aaa", w.CurrentWorldId);
        Assert.Contains(w.GetCurrentPlayers(), p => p.UserId == "usr_alice" && p.DisplayName == "Alice");
    }

    [Fact]
    public void LiveJoin_RaisesEvent_WithUidAndName()
    {
        using var w = StartWatcher();
        (string uid, string name)? seen = null;
        w.PlayerJoined += (uid, name) => seen = (uid, name);

        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        Assert.Equal(("usr_alice", "Alice"), seen);
        Assert.Equal(1, w.PlayerCount);
    }

    [Fact]
    public void UidlessJoin_KeysByName_AndRaisesWithEmptyUid()
    {
        using var w = StartWatcher();
        (string uid, string name)? seen = null;
        w.PlayerJoined += (uid, name) => seen = (uid, name);

        Append("[Behaviour] OnPlayerJoined Old Format Player");
        w.PollOnce();

        Assert.Equal(("", "Old Format Player"), seen);
        Assert.Contains(w.GetCurrentPlayers(), p => p.DisplayName == "Old Format Player" && p.UserId == "");
    }

    [Fact]
    public void DuplicateJoinLines_AreDeduped()
    {
        using var w = StartWatcher();
        int events = 0;
        w.PlayerJoined += (_, _) => events++;

        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        Assert.Equal(1, events);
        Assert.Equal(1, w.PlayerCount);
    }

    [Fact]
    public void Leave_RemovesPlayer_AndRaises()
    {
        using var w = StartWatcher();
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        (string uid, string name)? left = null;
        w.PlayerLeft += (uid, name) => left = (uid, name);
        Append("[Behaviour] OnPlayerLeft Alice (usr_alice)");
        w.PollOnce();

        Assert.Equal(("usr_alice", "Alice"), left);
        Assert.Equal(0, w.PlayerCount);
    }

    [Fact]
    public void UidlessLeave_FallsBackToNameRemoval()
    {
        using var w = StartWatcher();
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        Append("[Behaviour] OnPlayerLeft Alice");
        w.PollOnce();

        Assert.Equal(0, w.PlayerCount);
    }

    [Fact]
    public void LeaveOfUnknownPlayer_RaisesNoEvent()
    {
        using var w = StartWatcher();
        int events = 0;
        w.PlayerLeft += (_, _) => events++;

        Append("[Behaviour] OnPlayerLeft Stranger (usr_x)");
        w.PollOnce();

        Assert.Equal(0, events);
    }

    [Fact]
    public void WorldChange_ClearsRoster_AndRaises()
    {
        using var w = StartWatcher();
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        string? world = null;
        w.WorldChanged += (id, _) => world = id;
        Append("[Behaviour] Joining wrld_bbb:5678~public");
        w.PollOnce();

        Assert.Equal("wrld_bbb", world);
        Assert.Equal(0, w.PlayerCount);
        Assert.False(w.SelfLeftRoom);
    }

    [Fact]
    public void OnLeftRoom_SetsSelfLeftRoom_UntilNextWorldJoin()
    {
        using var w = StartWatcher();
        Append("[Behaviour] OnLeftRoom (leaving world now)");
        w.PollOnce();
        Assert.True(w.SelfLeftRoom);

        Append("[Behaviour] Joining wrld_ccc:1~public padded");
        w.PollOnce();
        Assert.False(w.SelfLeftRoom);
    }

    [Fact]
    public void ConnectionLost_ClearsRoster_AndFiresInstanceClosed()
    {
        using var w = StartWatcher();
        Append("[Behaviour] Joining wrld_ddd:9~public padded");
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();

        int closed = 0;
        w.InstanceClosed += _ => closed++;
        Append("[Behaviour] Lost connection to realtime network xxxx");
        w.PollOnce();

        Assert.Equal(1, closed);
        Assert.Equal(0, w.PlayerCount);
    }

    [Fact]
    public void TruncatedLogFile_ResetsCleanly()
    {
        using var w = StartWatcher();
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();
        Assert.Equal(1, w.PlayerCount);

        File.WriteAllText(_logPath, "");
        w.PollOnce();
        Assert.Equal(0, w.PlayerCount);
    }

    // The newest log survives after VRChat exits — a boot with the game
    // closed must not present the previous session's roster as the present.
    [Fact]
    public void GameNotRunning_AtBoot_IgnoresStaleLogRoster()
    {
        Append("[Behaviour] Joining wrld_old:1~public padded");
        Append("[Behaviour] OnPlayerJoined Ghost (usr_ghost)");

        int closed = 0;
        using var w = new PresenceWatcher(_dir);
        w.IsGameRunning = () => false;
        w.InstanceClosed += _ => closed++;
        w.Start(poll: false);

        Assert.Equal(0, w.PlayerCount);
        Assert.Null(w.CurrentWorldId);
        Assert.False(w.GameRunning);
        Assert.Equal(0, closed); // boot reconcile is silent — no event replay
    }

    [Fact]
    public void GameExit_MidSession_ClearsRoster_AndFiresInstanceClosed()
    {
        bool running = true;
        using var w = new PresenceWatcher(_dir);
        w.IsGameRunning = () => running;
        w.Start(poll: false);
        Append("[Behaviour] Joining wrld_live:2~public padded");
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();
        Assert.Equal(1, w.PlayerCount);

        int closed = 0;
        w.InstanceClosed += _ => closed++;
        running = false;
        w.PollOnce();

        Assert.Equal(1, closed);
        Assert.Equal(0, w.PlayerCount);
        Assert.Null(w.CurrentWorldId);
        Assert.False(w.GameRunning);
    }

    [Fact]
    public void ShortLines_AreIgnored()
    {
        using var w = StartWatcher();
        File.AppendAllText(_logPath, "OnPlayerJoined X (usr_x)\n"); // < 30 chars
        w.PollOnce();
        Assert.Equal(0, w.PlayerCount);
    }

    // VRChat logs grow to tens of megabytes in a session. Catch-up must
    // stream them, and a multi-byte character split across two read blocks
    // must still decode — here the "ë" lands exactly on the 1 MB boundary.
    [Fact]
    public void LargeLog_IsStreamed_AndCharactersAcrossBlockBoundariesSurvive()
    {
        const string prefix = "2026.08.28 12:00:00 Log        -  [Behaviour] OnPlayerJoined Zo";
        const string filler = "2026.08.28 12:00:00 Log        -  [Behaviour] filler line to pad the file out to a megabyte\n";
        long target = (1L << 20) - 1 - System.Text.Encoding.UTF8.GetByteCount(prefix);
        using (var fs = new FileStream(_logPath, FileMode.Create))
        using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            long written = 0;
            while (written + filler.Length <= target - 2) { sw.Write(filler); written += filler.Length; }
            int rem = (int)(target - written);
            if (rem > 0) { sw.Write(new string('x', rem - 1)); sw.Write('\n'); }
            sw.Write(prefix + "ë Ångström (usr_zoe)\n");
            sw.Write("2026.08.28 12:00:00 Log        -  [Behaviour] OnPlayerJoined Bob (usr_bob)\n");
        }

        using var w = new PresenceWatcher(_dir);
        w.IsGameRunning = () => true;
        w.Start(poll: false);

        Assert.Equal(2, w.PlayerCount);
        Assert.Contains(w.GetCurrentPlayers(), p => p.DisplayName == "Zoë Ångström" && p.UserId == "usr_zoe");
    }

    // PowerShell 5.1 and some editors write a byte-order mark; the first
    // line of the file must still count.
    [Fact]
    public void ByteOrderMark_DoesNotHideTheFirstLine()
    {
        File.WriteAllText(_logPath, "2026.08.28 12:00:00 Log        -  [Behaviour] Joining wrld_bom:1~public padded\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var w = StartWatcher();
        Assert.Equal("wrld_bom", w.CurrentWorldId);
    }

    // ── log health: VRChat running with its own Logging setting off ──

    private long _now = 1_000_000;

    private PresenceWatcher StartTimedWatcher(Func<bool>? running = null)
    {
        var w = new PresenceWatcher(_dir);
        w.IsGameRunning = running ?? (() => true);
        w.NowMs = () => _now;
        w.Start(poll: false);
        return w;
    }

    [Fact]
    public void EmptyLog_WhileGameRuns_IsReportedOnce_AfterTheGracePeriod()
    {
        var changes = new List<VrchatLogHealth>();
        using var w = StartTimedWatcher();
        w.LogHealthChanged += changes.Add;

        _now += PresenceWatcher.LogGraceMs - 1;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Unknown, w.LogHealth);

        _now += 1;
        w.PollOnce();
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Empty, w.LogHealth);
        Assert.Equal(new[] { VrchatLogHealth.Empty }, changes);
        Assert.Equal("output_log_2026-08-28_12-00-00.txt", w.LogFileName);
    }

    [Fact]
    public void LogWithLines_IsWriting_AndNeverReportedEmpty()
    {
        Append("[Behaviour] Joining wrld_aaa:1~public padded");
        using var w = StartTimedWatcher();
        Assert.Equal(VrchatLogHealth.Writing, w.LogHealth);

        _now += PresenceWatcher.LogGraceMs * 10L;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Writing, w.LogHealth);
    }

    // Logging switched on mid-session: the first line ends the warning,
    // though this instance's world line was never written.
    [Fact]
    public void EmptyLog_ThatStartsGettingLines_RecoversToWriting()
    {
        using var w = StartTimedWatcher();
        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Empty, w.LogHealth);

        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Writing, w.LogHealth);
        Assert.Null(w.CurrentWorldId);
        Assert.Equal(1, w.PlayerCount);
    }

    [Fact]
    public void GameNotRunning_IsNeverReportedEmpty()
    {
        using var w = StartTimedWatcher(() => false);
        _now += PresenceWatcher.LogGraceMs * 10L;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Unknown, w.LogHealth);
    }

    [Fact]
    public void NoLogFile_WhileGameRuns_IsReportedMissing()
    {
        File.Delete(_logPath);
        using var w = StartTimedWatcher();
        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Missing, w.LogHealth);
        Assert.Null(w.LogFileName);
        Assert.Equal("no output_log file in that folder yet", w.DescribeLog());
    }

    // A VRChat restart writes a new file: the new session gets its own grace
    // period instead of inheriting the old file's verdict.
    [Fact]
    public void NewSessionFile_RestartsTheGracePeriod()
    {
        Append("[Behaviour] Joining wrld_aaa:1~public padded");
        using var w = StartTimedWatcher();
        Assert.Equal(VrchatLogHealth.Writing, w.LogHealth);

        _now += 60_000;
        var next = Path.Combine(_dir, "output_log_2026-08-28_13-00-00.txt");
        File.WriteAllText(next, "");
        File.SetLastWriteTimeUtc(next, DateTime.UtcNow.AddMinutes(1));
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Unknown, w.LogHealth);

        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Empty, w.LogHealth);
    }

    [Fact]
    public void GameExit_ClearsTheEmptyVerdict()
    {
        bool running = true;
        using var w = StartTimedWatcher(() => running);
        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Empty, w.LogHealth);

        running = false;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Unknown, w.LogHealth);
    }

    // The boot log line carries counts only: no names, user ids or instances.
    [Fact]
    public void DescribeLog_CountsWithoutNamesOrIds()
    {
        Append("[Behaviour] Joining wrld_aaa:1234~private(usr_me)");
        Append("[Behaviour] OnPlayerJoined Alice (usr_alice)");
        using var w = StartTimedWatcher();

        var line = w.DescribeLog();
        Assert.StartsWith("output_log_2026-08-28_12-00-00.txt — ", line);
        Assert.Contains("2 lines, in a world, 1 player(s)", line);
        Assert.DoesNotContain("Alice", line);
        Assert.DoesNotContain("usr_", line);
        Assert.DoesNotContain("wrld_", line);
    }

    // Under Proton the folder itself can be missing: a Steam library the
    // locator doesn't know, or a prefix that isn't created yet.
    [Fact]
    public void NoLogFolder_WhileGameRuns_IsReportedNoFolder()
    {
        using var w = new PresenceWatcher(Path.Combine(_dir, "not-there"));
        w.IsGameRunning = () => true;
        w.NowMs = () => _now;
        w.Start(poll: false);

        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.NoFolder, w.LogHealth);
        Assert.Equal("none (folder not found)", w.DescribeLog());
    }

    // A first launch creates the prefix after the game is already running:
    // the new folder's log gets a fresh grace period, then counts as written.
    [Fact]
    public void LogFolderAppearingLater_IsJudgedAfresh()
    {
        var later = Path.Combine(_dir, "later");
        using var w = new PresenceWatcher(later);
        w.IsGameRunning = () => true;
        w.NowMs = () => _now;
        w.Start(poll: false);
        _now += PresenceWatcher.LogGraceMs;
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.NoFolder, w.LogHealth);

        var file = Path.Combine(later, "output_log_2026-08-28_13-00-00.txt");
        Directory.CreateDirectory(later);
        File.WriteAllText(file, "");
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Unknown, w.LogHealth);

        File.AppendAllText(file, "2026.08.28 13:00:00 Log        -  [Behaviour] Joining wrld_late:1~public padded\n");
        w.PollOnce();
        Assert.Equal(VrchatLogHealth.Writing, w.LogHealth);
        Assert.Equal("wrld_late", w.CurrentWorldId);
    }
}
