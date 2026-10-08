using System.Globalization;
using System.Text;

namespace Chatterbox;

// Whether VRChat's log can show presence at all. VRChat's own Settings →
// Debug → Logging can be switched off; the game then still creates each
// session's output_log file but never writes to it. Under Proton the log
// folder can also sit somewhere VrchatLogLocator never looked. Either way
// a watcher that only reacts to lines would sit at "nobody here" with no
// hint why.
public enum VrchatLogHealth
{
    Unknown,   // VRChat is not running, or was seen running less than LogGraceMs ago
    Writing,   // the session's log has lines — presence works
    Empty,     // VRChat seen running for LogGraceMs, and its log file is still empty
    Missing,   // VRChat seen running for LogGraceMs; the log folder exists but holds no log file
    NoFolder,  // VRChat seen running for LogGraceMs, and the log folder was not found
}

// Knows who is in the current VRChat instance by tailing the game's output
// log — written against the observed format in docs/LOG_FORMAT.md. The
// VRChat process is the authority on liveness: log files outlive the game,
// so a roster parsed from disk is only the present while the game runs.
public sealed class PresenceWatcher : IDisposable
{
    public sealed class PlayerInfo
    {
        public string DisplayName { get; set; } = "";
        public string UserId { get; set; } = "";
        public DateTime JoinedAt { get; set; } = DateTime.Now;
    }

    public event Action<string>? DebugLog;
    public event Action<string, string>? WorldChanged;   // (worldId, location)
    public event Action<string>? InstanceClosed;         // (location)
    public event Action<string, string>? PlayerJoined;   // (uid, displayName)
    public event Action<string, string>? PlayerLeft;     // (uid, displayName)
    public event Action<bool>? GameRunningChanged;
    public event Action<VrchatLogHealth>? LogHealthChanged;

    public bool SelfLeftRoom { get; private set; }
    public string? CurrentWorldId { get; private set; }
    public string PendingWorldName { get; private set; } = "";
    public bool GameRunning { get; private set; }
    public int PlayerCount { get { lock (_gate) return _roster.Count; } }
    public VrchatLogHealth LogHealth { get; private set; }
    public string? LogFileName => _logPath == null ? null : Path.GetFileName(_logPath);

    // How long VRChat must be seen running with nothing in its log before
    // the log counts as empty. A logging game writes its first lines within
    // seconds of creating the file; the margin covers slow starts on weak
    // machines and Proton's prefix setup. The clock starts when this watcher
    // first sees the game with the current file, so a late Chatterbox start
    // waits the full margin.
    public const int LogGraceMs = 30_000;

    // Injectable so tests run on machines without VRChat installed.
    internal Func<bool> IsGameRunning = VrchatProcessExists;
    internal Func<long> NowMs = () => Environment.TickCount64;

    private string _logDir;
    private readonly bool _logDirFixed;   // --vrchat-log-dir / tests: never re-resolved
    private long _lastLocateAt;
    private readonly Dictionary<string, PlayerInfo> _roster = new();
    private readonly object _gate = new();
    private readonly object _pollGate = new();

    private string? _logPath;
    private long _offset;
    private byte[] _carryBytes = Array.Empty<byte>();   // the trailing partial line, kept for the next read
    private long _logLines;           // timestamped lines read from the current file
    private long _healthSince = -1;   // NowMs when VRChat was first seen running with this file; -1 = not running
    private string? _location;        // full instance string from the Joining line
    private System.Threading.Timer? _timer;
    private bool _started;
    private bool _disposed;

    // logDirectory override exists for tests and --vrchat-log-dir; null = the
    // game's log dir inside its Proton prefix (VrchatLogLocator; see
    // docs/LOG_FORMAT.md).
    public PresenceWatcher(string? logDirectory = null)
    {
        _logDir = logDirectory ?? VrchatLogLocator.Find();
        _logDirFixed = logDirectory != null;
    }

    // The game's first launch (or a new Proton prefix, or a Flatpak Steam)
    // can create the log folder after this app started: while the folder
    // is missing, look again every ten seconds.
    private void RelocateLogDirIfMissing()
    {
        if (_logDirFixed || Directory.Exists(_logDir)) return;
        long now = Environment.TickCount64;
        if (now - _lastLocateAt < 10_000) return;
        _lastLocateAt = now;
        var found = VrchatLogLocator.Find();
        if (found == _logDir || !Directory.Exists(found)) return;
        _logDir = found;
        Say($"VRChat log folder appeared at {found}");
    }

    public string LogDirectory => _logDir;

    public List<PlayerInfo> GetCurrentPlayers()
    {
        lock (_gate) return new List<PlayerInfo>(_roster.Values);
    }

    public void Start() => Start(poll: true);

    // poll:false lets tests drive PollOnce deterministically.
    internal void Start(bool poll)
    {
        if (_started) return;
        _started = true;

        // Boot catch-up: absorb the whole current file so the roster and
        // world reflect an in-progress session, but raise nothing — the
        // caller reconciles from GetCurrentPlayers() instead of replaying
        // hours of history as live events.
        Drain(live: false);
        Say($"catch-up: {PlayerCount} player(s), world {CurrentWorldId ?? "none"}, file {Path.GetFileName(_logPath ?? "none")}");

        // A log alone can describe a session that already ended.
        GameRunning = true;
        ReconcileGameState(live: false);
        UpdateLogHealth();

        if (poll)
            _timer = new System.Threading.Timer(_ => PollOnce(), null, 1000, 1000);
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }
    public void Dispose() { _disposed = true; Stop(); }

    internal void PollOnce()
    {
        if (_disposed || !System.Threading.Monitor.TryEnter(_pollGate)) return;
        try
        {
            RelocateLogDirIfMissing();
            Drain(live: true);
            ReconcileGameState(live: true);
            UpdateLogHealth();
        }
        catch (Exception ex) { Say($"poll error: {ex.Message}"); }
        finally { System.Threading.Monitor.Exit(_pollGate); }
    }

    // ── log tailing ────────────────────────────────────────────────

    private const int ReadBlockBytes = 1 << 20;
    private byte[]? _block;   // reused: a fresh megabyte per poll is needless large-object churn

    private void Drain(bool live)
    {
        var newest = NewestLogFile();
        if (newest == null) { if (_logPath == null) Say("no output_log_*.txt found"); return; }

        if (newest != _logPath)
        {
            // A new game session writes a new file; start it from zero.
            _logPath = newest;
            _offset = 0;
            _carryBytes = Array.Empty<byte>();
            _logLines = 0;
            _healthSince = GameRunning ? NowMs() : -1;   // a new session gets its own grace period
            ClearRoster();
            Say($"following {Path.GetFileName(newest)}");
        }

        // Read in bounded blocks: a VRChat log can be tens of megabytes
        // after a long session, and boot catch-up used to load all of it
        // into one string. Lines are split on the raw bytes and decoded one
        // at a time, so a multi-byte character straddling two blocks still
        // decodes correctly.
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset)
            {
                // Truncated/replaced in place — start over.
                _offset = 0;
                _carryBytes = Array.Empty<byte>();
                _logLines = 0;
                ClearRoster();
            }
            if (fs.Length == _offset) return;
            fs.Position = _offset;
            var block = _block ??= new byte[ReadBlockBytes];
            int got;
            while ((got = fs.Read(block, 0, block.Length)) > 0)
            {
                _offset += got;
                ConsumeBytes(block, got, live);
            }
        }
        catch (IOException) { return; }
    }

    // Complete lines go to the classifier; a trailing partial line waits
    // for the next read.
    private void ConsumeBytes(byte[] block, int count, bool live)
    {
        byte[] data;
        int length;
        if (_carryBytes.Length > 0)
        {
            data = new byte[_carryBytes.Length + count];
            Buffer.BlockCopy(_carryBytes, 0, data, 0, _carryBytes.Length);
            Buffer.BlockCopy(block, 0, data, _carryBytes.Length, count);
            length = data.Length;
        }
        else
        {
            data = block;
            length = count;
        }

        int start = 0;
        for (int i = 0; i < length; i++)
        {
            if (data[i] != 10) continue;                       // LF
            int end = i;
            if (end > start && data[end - 1] == 13) end--;     // CR
            Classify(Encoding.UTF8.GetString(data, start, end - start), live);
            start = i + 1;
        }
        _carryBytes = start < length ? data[start..length] : Array.Empty<byte>();
    }

    private string? NewestLogFile()
    {
        try
        {
            if (!Directory.Exists(_logDir)) return null;
            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(_logDir, "output_log_*.txt"))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            return best;
        }
        catch { return null; }
    }

    // ── line classification (docs/LOG_FORMAT.md) ───────────────────

    private const string BehaviourMark = "[Behaviour]";
    private const string ConnLostMark = "Lost connection to realtime network";
    private const string InstanceClosedMark = "Instance closed:";

    private void Classify(string line, bool live)
    {
        // A byte-order mark can only open the file; it must not hide the
        // first line from the timestamp check.
        if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
        if (!TryReadStamp(line, out var when)) return;
        _logLines++;

        // These two are not always [Behaviour]-tagged in the wild.
        if (line.Contains(ConnLostMark, StringComparison.Ordinal)) { OnConnectionLost(live); return; }

        int mark = line.IndexOf(BehaviourMark, StringComparison.Ordinal);
        string body = mark >= 0 ? line[(mark + BehaviourMark.Length)..].TrimStart() : "";

        if (body.StartsWith("OnPlayerJoined ", StringComparison.Ordinal))
            OnJoin(SplitIdentity(body["OnPlayerJoined ".Length..]), when, live);
        else if (body.StartsWith("OnPlayerLeft ", StringComparison.Ordinal))
            OnLeave(SplitIdentity(body["OnPlayerLeft ".Length..]), live);
        // NOTE: "OnPlayerLeftRoom" exists as a bare marker — the trailing
        // space in the prefix above is what keeps it from parsing as a
        // player named "Room".
        else if (body.StartsWith("OnLeftRoom", StringComparison.Ordinal))
            SelfLeftRoom = true;
        else if (body.StartsWith("Joining wrld_", StringComparison.Ordinal))
            OnRoomJoin(FirstToken(body["Joining ".Length..]), live);
        else if (body.StartsWith("Entering Room: ", StringComparison.Ordinal))
            OnRoomEnter(body["Entering Room: ".Length..].Trim());
        else if (line.Contains(InstanceClosedMark, StringComparison.Ordinal))
            OnInstanceClosed(line, live);
    }

    // "yyyy.MM.dd HH:mm:ss" prefix; anything without it is not a log line
    // we care about (continuation lines, stack traces, short noise).
    private static bool TryReadStamp(string line, out DateTime when)
    {
        when = default;
        return line.Length > 34 && DateTime.TryParseExact(
            line.AsSpan(0, 19), "yyyy.MM.dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out when);
    }

    // "<display name> (usr_…)" — the id suffix is optional and must look
    // like an id token; display names themselves may contain parentheses.
    private static (string Name, string Uid) SplitIdentity(string s)
    {
        s = s.Trim();
        if (s.EndsWith(')'))
        {
            int open = s.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0)
            {
                var candidate = s[(open + 2)..^1];
                if (candidate.Length > 0 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    return (s[..open].Trim(), candidate);
            }
        }
        return (s, "");
    }

    private static string FirstToken(string s)
    {
        s = s.TrimStart();
        int end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
        return s[..end];
    }

    private static string RosterKey(string uid, string name) =>
        uid.Length > 0 ? "u:" + uid : "n:" + name;

    // ── event handling ─────────────────────────────────────────────

    private void OnJoin((string Name, string Uid) who, DateTime when, bool live)
    {
        bool fresh;
        lock (_gate)
        {
            var key = RosterKey(who.Uid, who.Name);
            fresh = !_roster.ContainsKey(key);
            _roster[key] = new PlayerInfo { DisplayName = who.Name, UserId = who.Uid, JoinedAt = when };
        }
        if (live && fresh)
        {
            Say($"+ {who.Name} ({PlayerCount})");
            PlayerJoined?.Invoke(who.Uid, who.Name);
        }
    }

    private void OnLeave((string Name, string Uid) who, bool live)
    {
        bool known;
        lock (_gate)
        {
            known = _roster.Remove(RosterKey(who.Uid, who.Name));
            if (!known)
            {
                // Joined uid-less but left with a uid (or the reverse):
                // fall back to matching the display name.
                foreach (var pair in _roster)
                {
                    if (pair.Value.DisplayName != who.Name) continue;
                    _roster.Remove(pair.Key);
                    known = true;
                    break;
                }
            }
        }
        if (live && known)
        {
            Say($"- {who.Name} ({PlayerCount})");
            PlayerLeft?.Invoke(who.Uid, who.Name);
        }
    }

    private void OnRoomJoin(string location, bool live)
    {
        if (!location.StartsWith("wrld_", StringComparison.Ordinal)) return;
        _location = location;
        int colon = location.IndexOf(':');
        CurrentWorldId = colon > 0 ? location[..colon] : location;
        SelfLeftRoom = false;
        ClearRoster();
        if (live)
        {
            Say($"world: {location}");
            WorldChanged?.Invoke(CurrentWorldId, location);
        }
    }

    private void OnRoomEnter(string worldName)
    {
        PendingWorldName = worldName;
        SelfLeftRoom = false;
        ClearRoster();
    }

    private void OnInstanceClosed(string line, bool live)
    {
        int at = line.IndexOf(InstanceClosedMark, StringComparison.Ordinal);
        var location = FirstToken(line[(at + InstanceClosedMark.Length)..]);
        if (!location.StartsWith("wrld_", StringComparison.Ordinal)) return;
        if (live)
        {
            Say($"instance closed: {location}");
            InstanceClosed?.Invoke(location);
        }
    }

    // A dropped connection produces no leave lines — the roster is dead.
    // Treat it as the instance ending; a rejoin logs a fresh world join.
    private void OnConnectionLost(bool live)
    {
        ClearRoster();
        if (live)
        {
            Say("connection lost — roster cleared");
            InstanceClosed?.Invoke(_location ?? "");
        }
    }

    // ── game liveness ──────────────────────────────────────────────

    private static bool VrchatProcessExists()
    {
        if (OperatingSystem.IsLinux()) return LinuxHost.ProcessExists(LinuxHost.VrchatProcessNames);
        try
        {
            var found = System.Diagnostics.Process.GetProcessesByName("VRChat");
            try { return found.Length > 0; }
            finally { foreach (var p in found) p.Dispose(); }
        }
        catch { return true; } // unknowable → trust the log
    }

    private void ReconcileGameState(bool live)
    {
        bool running = IsGameRunning();
        if (running == GameRunning) return;
        GameRunning = running;
        GameRunningChanged?.Invoke(running);
        if (running) return;

        bool hadState = _location != null || PlayerCount > 0;
        var closed = _location ?? "";
        _location = null;
        CurrentWorldId = null;
        PendingWorldName = "";
        ClearRoster();
        if (hadState)
        {
            Say("VRChat is not running — roster cleared");
            if (live) InstanceClosed?.Invoke(closed);
        }
    }

    // Runs after every poll. Only a running game can be judged: its log
    // either has lines (Writing), or is still empty, absent, or without a
    // folder once the grace period has passed since the game was seen with
    // this file.
    private void UpdateLogHealth()
    {
        VrchatLogHealth next;
        if (!GameRunning)
        {
            _healthSince = -1;
            next = VrchatLogHealth.Unknown;
        }
        else
        {
            long now = NowMs();
            if (_healthSince < 0) _healthSince = now;
            if (_logLines > 0) next = VrchatLogHealth.Writing;
            else if (now - _healthSince < LogGraceMs) next = VrchatLogHealth.Unknown;
            else if (_logPath != null) next = VrchatLogHealth.Empty;
            else next = Directory.Exists(_logDir) ? VrchatLogHealth.Missing : VrchatLogHealth.NoFolder;
        }
        if (next == LogHealth) return;
        LogHealth = next;
        LogHealthChanged?.Invoke(next);
    }

    // One line for last_boot.log, under the "vrchat log dir:" line: the file
    // being followed and what it held at catch-up. Counts only — never
    // player names or instance ids.
    public string DescribeLog()
    {
        if (_logPath == null)
            return Directory.Exists(_logDir) ? "no output_log file in that folder yet" : "none (folder not found)";
        var inv = CultureInfo.InvariantCulture;
        return $"{Path.GetFileName(_logPath)} — {_offset.ToString("N0", inv)} bytes, {_logLines.ToString("N0", inv)} lines, " +
               $"{(CurrentWorldId != null ? "in a world" : "no world")}, {PlayerCount} player(s)";
    }

    private void ClearRoster() { lock (_gate) _roster.Clear(); }
    private void Say(string msg) => DebugLog?.Invoke("PresenceWatcher: " + msg);
}
