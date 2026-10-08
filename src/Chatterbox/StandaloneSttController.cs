using Newtonsoft.Json.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// Owns all caption state and logic: session lifecycle, settings, model
// downloads, and the player-aware auto-start machine. Talks to the UI
// through an injected (type, payload) send delegate and handles the UI's
// {action,...} messages; the UI's message switch is the authoritative
// contract list. Presence comes from PresenceWatcher; capture failures
// wind the session down; a meter timer pushes level changes.
public sealed class StandaloneSttController : IDisposable
{
    private readonly PresenceWatcher _watcher;
    private readonly Action<string, object?> _send; // (type, payload) -> UI
    private readonly SttSettings _settings;
    private readonly SttModelManager _models = new();

    private SttService? _service;
    private SttChatboxRelay? _relay;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadingId;
    private long _lastProgressSentAt;
    private bool _autoStarted;
    private bool _manualHold;
    // Settings are written from two threads (watcher-event backfill and the
    // UI message path); Save is unsynchronized File.WriteAllText, so all
    // mutate+save sequences serialize here.
    private readonly object _settingsLock = new();
    // Session lifecycle (Start/Stop and their check-then-act callers) runs on
    // four threads: the UI message path, the watcher poll timer, the recheck
    // timer, and the capture reader thread (capture failure). Serialize it — two
    // concurrent starts would orphan a live mic capture. Monitor is
    // re-entrant, so Start()->Stop() nesting is fine. Lock order: session
    // outer, settings inner (never the reverse).
    private readonly object _sessionLock = new();
    private readonly System.Threading.Timer _autoRecheck;
    private int _autoRecheckPhase;
    private readonly System.Threading.Timer _meterTimer;
    private int _meterPct = -1;
    // Pace: is recognition keeping up with speech? Fed by every pass,
    // shown in the UI at most once a second, advice offered once per
    // session (see SttPaceMonitor).
    private long _lastMemoryLoggedAt;
    private long _lastPaceLogAt;
    private const int MemoryLogIntervalMs = 600_000;

    // The numbers a leak would move: resident size, managed heap, full
    // collections, threads.
    private static string MemoryLine()
    {
        long rss = 0, managed = 0; int threads = 0;
        try { rss = Environment.WorkingSet; } catch { }
        try { managed = GC.GetTotalMemory(false); } catch { }
        try { using var me = System.Diagnostics.Process.GetCurrentProcess(); threads = me.Threads.Count; } catch { }
        return $"rss {rss / 1_048_576} MB, managed {managed / 1_048_576} MB, gen2 GCs {GC.CollectionCount(2)}, threads {threads}";
    }

    private SttPaceMonitor _pace = new();
    private long _lastPaceSentAt;
    private SttPaceMonitor.PaceStatus _lastPaceStatus;
    private bool _paceBehindLogged;
    private long _sessionStartedAt;
    // An engine load in progress: Start runs off the UI thread and the UI
    // shows "Loading" instead of a frozen window.
    private volatile bool _loading;
    private string _loadingLabel = "";
    // Boot-time auto-start: armed at boot, run once when the page connects
    // (or by the fallback timer), skipped entirely on a safe boot.
    private int _bootReconcileDone;
    private bool _safeBoot;
    private System.Threading.Timer? _bootFallback;
    private const int BootFallbackMs = 8000;
    private int _benchRunning;
    private System.Threading.Timer? _provisionalRecheck;
    private long _provisionalSince;
    private const long ProvisionalGiveUpMs = 3 * 60 * 1000;
    // The voice detector's download while one is in flight (EnsureVadAsync):
    // every path that needs the file awaits this one task instead of
    // starting a second download of the same file.
    private Task<(bool Ok, string? Error)>? _vadEnsure;
    private readonly object _vadEnsureLock = new();
    private const int VadWaitAtStartMs = 30_000;

    // Every 2 s while settings are provisional: adopt the real file as soon
    // as it is readable, then refresh everything the UI showed from
    // defaults and let auto-start react. After three minutes, accept that
    // the file is genuinely gone.
    private void ProvisionalRecheckTick()
    {
        bool recovered = false, gaveUp = false;
        lock (_sessionLock)
        lock (_settingsLock)
        {
            if (_settings.TryRecoverFromDisk()) recovered = true;
            else if (Environment.TickCount64 - _provisionalSince > ProvisionalGiveUpMs)
            {
                _settings.GiveUpProvisional();
                gaveUp = true;
            }
        }
        if (recovered || gaveUp)
        {
            _provisionalRecheck?.Dispose();
            _provisionalRecheck = null;
        }
        if (gaveUp) BootLog.Append("settings: file never became visible; running on defaults");
    }

    private void SendSavedToast()
    {
        var error = SttSettings.LastSaveError;
        _send("toast", error.Length == 0
            ? new { ok = true, msg = "Saved" }
            : new { ok = false, msg = "Settings could not be saved — " + error + " (this session keeps them; the next start will not)" });
    }

    private void OnSettingsRecovered()
    {
        BootLog.Append($"settings: recovered after boot — {_settings.AutoStartFriends.Count} auto-start player(s), engine {_settings.Engine}");
        SendDevices();
        SendModels();
        SendState();
        SendPlayers();
        _send("toast", new { ok = true, msg = "Settings loaded — they weren't readable when Chatterbox started" });
        // Recovery can be raised from inside Save(), under the settings
        // lock; the presence reconcile takes the session lock, which is
        // always the OUTER one — so it runs on its own thread, later.
        RunOffUiThread(ReconcileAutoPresence);
    }

    public bool IsRunning => _service?.IsRunning ?? false;

    public StandaloneSttController(PresenceWatcher watcher, Action<string, object?> sendToUi)
    {
        _watcher = watcher;
        _send = sendToUi;
        // A returning user's "missing" file gets a short wait, then runs
        // provisional (see SttSettings.ProvisionalDefaults) with a background
        // re-check that swaps the real file in the moment it is readable.
        _settings = SttSettings.LoadWithRetry(TimeSpan.FromSeconds(2));
        // Settings from old releases may carry the retired VOSK engine —
        // normalize once so every consumer agrees.
        if (_settings.Engine == "vosk") _settings.Engine = "parakeet";
        SttSettings.Recovered += OnSettingsRecovered;
        if (SttSettings.ProvisionalDefaults)
        {
            _provisionalSince = Environment.TickCount64;
            _provisionalRecheck = new System.Threading.Timer(_ => ProvisionalRecheckTick(), null, 2000, 2000);
        }
        _models.OnProgress += (id, received, total) =>
        {
            long now = Environment.TickCount64;
            if (now - _lastProgressSentAt < 150 && received != total) return;
            _lastProgressSentAt = now;
            _send("sttModelProgress", new { id, received, total });
        };

        _watcher.PlayerJoined += OnInstancePlayerJoined;
        _watcher.PlayerLeft += OnInstancePlayerLeft;
        _watcher.WorldChanged += (_, _) => OnWorldChanged();
        _watcher.InstanceClosed += _ => OnWorldChanged();
        // The Players screen shows who is in the instance live; the name
        // filter follows the roster while a session runs.
        _watcher.PlayerJoined += (_, _) => { SendPlayers(); RefreshNameFilter(); };
        _watcher.PlayerLeft += (_, _) => { SendPlayers(); RefreshNameFilter(); };
        _watcher.WorldChanged += (_, _) => SendPlayers();
        _watcher.InstanceClosed += _ => SendPlayers();
        // The header tells "VRChat is not running" apart from "running, but
        // its log is empty" (Logging switched off inside VRChat).
        _watcher.GameRunningChanged += _ => SendPlayers();
        _watcher.LogHealthChanged += OnLogHealthChanged;
        _autoRecheck = new System.Threading.Timer(_ => AutoRecheckTick());

        // Meter pushes are timer-driven with send-on-change dedup so the
        // UI channel stays quiet while the level is steady.
        _meterTimer = new System.Threading.Timer(_ => MeterTick(), null, 100, 100);
    }

    // The pace advice can ask the host to relaunch the app: the GPU pack
    // binds at native load time, so an install only counts after a restart.
    public event Action? RestartRequested;

    // Boot-time auto-start — PresenceWatcher.Start() rebuilds the roster
    // without events, so a watched player already present at launch must
    // be picked up explicitly. It waits for the page to connect: nothing
    // can then reach the WebView early, and a model load never holds the
    // window back. A fallback timer runs it anyway if the page never shows
    // up. A safe boot (the previous start died before the window) skips
    // it: a crash loop must need a human to press Start.
    public void ArmBootReconcile(bool safeBoot)
    {
        _safeBoot = safeBoot;
        if (safeBoot) { _bootReconcileDone = 1; return; }
        _bootFallback = new System.Threading.Timer(_ => RunBootReconcile("fallback timer"), null, BootFallbackMs, Timeout.Infinite);
    }

    // The host calls this on the page's first message (window thread).
    public void UiConnected()
    {
        if (_safeBoot)
            _send("toast", new { ok = false, msg =
                "Chatterbox didn't start cleanly last time, so captions were not auto-started this time — " +
                "press Start when ready. Details: " + Path.Combine(SttPaths.DataDir, "error.log") });
        // A first run that never touches a setting would otherwise make every
        // later launch wait for a "missing" settings file (the provisional-
        // defaults logic assumes a returning user has one). Write the defaults
        // once the page is up, so the next start finds a real file.
        lock (_settingsLock)
            if (!SttSettings.ProvisionalDefaults && !SttSettings.FileExists) _settings.Save();
        RunBootReconcile("page connected");
    }

    private void RunBootReconcile(string why)
    {
        if (Interlocked.Exchange(ref _bootReconcileDone, 1) != 0) return;
        _bootFallback?.Dispose();
        _bootFallback = null;
        RunOffUiThread(() =>
        {
            BootLog.Append($"boot auto-start check ({why})");
            ReconcileAutoPresence();
        });
    }

    // The voice detector (SttModelCatalog.Vad) is required by every session
    // and is fetched on demand when it is missing. A user returning from a
    // release whose catalog named a different Silero file has that one and
    // not this one; without this, their first Start — or the boot auto-start
    // — would fail with a "model not found" error that reads like a broken
    // install. The host calls this once at boot, after the boot log exists:
    // a returning user (an engine model is installed) gets the 0.9 MB file
    // in the background right away. A fresh machine is left to the first-run
    // screen, which asks before downloading anything.
    public void EnsureVoiceDetectorAtBoot()
    {
        var vad = SttModelCatalog.Vad;
        if (_models.IsInstalled(vad)) return;
        bool returning = SttModelCatalog.Models.Any(m => m.Id != vad.Id && _models.IsInstalled(m));
        if (!returning) return;
        var stale = _models.StaleVadFiles();
        BootLog.Append($"voice detector: {vad.DisplayName} not installed" +
                       (stale.Count > 0 ? $" (found {string.Join(", ", stale)})" : "") +
                       $" — downloading {vad.SizeBytes / 1024} KB in the background");
        _ = EnsureVadAsync();
    }

    // Installed already → done; a download in flight → that one; otherwise a
    // new download. Shared by the boot path, Start, and the Models screen.
    private Task<(bool Ok, string? Error)> EnsureVadAsync()
    {
        lock (_vadEnsureLock)
        {
            if (_models.IsInstalled(SttModelCatalog.Vad)) return Task.FromResult<(bool, string?)>((true, null));
            if (_vadEnsure is { IsCompleted: false }) return _vadEnsure;
            _vadEnsure = Task.Run(DownloadVadAsync);
            return _vadEnsure;
        }
    }

    private async Task<(bool Ok, string? Error)> DownloadVadAsync()
    {
        var vad = SttModelCatalog.Vad;
        // Shown on the Models screen like any download (progress, Cancel)
        // when the slot is free; a download the user already has running
        // keeps the slot and this one runs quietly beside it.
        CancellationTokenSource? cts = null;
        if (_downloadingId == null)
        {
            cts = new CancellationTokenSource();
            _downloadingId = vad.Id;
            _downloadCts = cts;
            SendModels();
        }
        var (ok, error) = await _models.DownloadAsync(vad.Id, cts?.Token ?? CancellationToken.None);
        if (cts != null)
        {
            _downloadingId = null;
            _downloadCts = null;
            cts.Dispose();
        }
        if (ok)
        {
            var removed = _models.RemoveStaleVadFiles();
            BootLog.Append($"voice detector: {vad.DisplayName} downloaded and verified" +
                           (removed.Count > 0 ? $"; removed {string.Join(", ", removed)}" : ""));
            _send("toast", new { ok = true, msg = $"{vad.DisplayName} downloaded and verified" });
        }
        else
        {
            BootLog.Append($"voice detector: download failed — {error}");
            _send("toast", new { ok = false, msg = error ?? "download failed" });
        }
        SendModels();
        SendDevices();
        return (ok, error);
    }

    // Session work requested from the UI thread runs here: an engine load
    // takes seconds and would freeze the window (and Photino's message
    // loop) otherwise. Failures land in error.log, never on the UI thread.
    private static void RunOffUiThread(Action work) =>
        Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { ErrorLog.WriteEntry("SessionWork", ex); }
        });

    // Read-only view for boot diagnostics.
    public SttSettings Settings => _settings;

    // uid wins when both sides have one; display name covers manually-added
    // flags and uid-less log lines. Names are not unique — documented caveat.
    internal static bool MatchesFlag(IEnumerable<SttSettings.AutoFriend> flags, string uid, string name)
    {
        foreach (var f in flags)
        {
            if (!string.IsNullOrEmpty(f.Id) && !string.IsNullOrEmpty(uid))
            {
                if (f.Id == uid) return true;
                continue; // both ids known and different — name is irrelevant
            }
            if (!string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(name) &&
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsAutoFriend(string uid, string name) =>
        MatchesFlag(_settings.AutoStartFriends, uid, name);

    // A name-only flag entry gets its uid filled in the first time the log
    // shows that name with a usr_ id, so later matching survives renames.
    private void BackfillFlagId(string uid, string name)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(name)) return;
        lock (_settingsLock)
        {
            var entry = _settings.AutoStartFriends.FirstOrDefault(f =>
                string.IsNullOrEmpty(f.Id) &&
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            entry.Id = uid;
            _settings.Save();
        }
        SendDevices();
    }

    private void OnInstancePlayerJoined(string uid, string name)
    {
        BackfillFlagId(uid, name);
        if (!_settings.AutoStartEnabled || !IsAutoFriend(uid, name)) return;
        lock (_sessionLock)
        {
            _manualHold = false;
            if (IsRunning) return;

            Start(_settings.InputDeviceIndex, _settings.Engine);
            if (IsRunning)
            {
                _autoStarted = true;
                _send("toast", new { ok = true, msg = $"Captions started — {name} joined" });
            }
        }
    }

    private void OnInstancePlayerLeft(string uid, string name)
    {
        // When WE left, everyone "leaves" at once — the world-change path
        // reports that accurately instead of "<name> left". But if no world
        // join ever follows (user quit VRChat from that room), the delayed
        // reconcile must still stop the auto session.
        if (_watcher.SelfLeftRoom)
        {
            if (_settings.AutoStartEnabled && _autoStarted)
            {
                _autoRecheckPhase = 0;
                TryArmRecheck(5000);
            }
            return;
        }
        lock (_sessionLock)
        {
            if (!_autoStarted || !IsRunning || !IsAutoFriend(uid, name)) return;

            var stillPresent = _watcher.GetCurrentPlayers()
                .Any(p => (p.UserId != uid || string.IsNullOrEmpty(uid)) &&
                          !(string.IsNullOrEmpty(uid) && p.DisplayName == name) &&
                          IsAutoFriend(p.UserId, p.DisplayName));
            if (stillPresent) return;

            Stop();
            _send("toast", new { ok = true, msg = $"Captions stopped — {name} left" });
        }
    }

    // The recheck timer can be disposed while a watcher event is in flight
    // during shutdown — arming must never throw on that race.
    private void TryArmRecheck(int dueMs)
    {
        try { _autoRecheck.Change(dueMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnWorldChanged()
    {
        lock (_sessionLock)
        {
            _manualHold = false;

            if (_autoStarted && IsRunning &&
                !_watcher.GetCurrentPlayers().Any(p => IsAutoFriend(p.UserId, p.DisplayName)))
            {
                Stop();
                _send("toast", new { ok = true, msg = "Captions stopped — no auto-start players in this world" });
            }
        }

        // The new world's player list streams in over several seconds.
        if (_settings.AutoStartEnabled)
        {
            _autoRecheckPhase = 0;
            TryArmRecheck(5000);
        }
    }

    private void AutoRecheckTick()
    {
        // A timer callback that throws takes the process down.
        try
        {
            ReconcileAutoPresence();
            if (_autoRecheckPhase++ == 0) TryArmRecheck(10_000);
        }
        catch (Exception ex) { ErrorLog.WriteEntry("AutoRecheckTick", ex); }
    }

    private void ReconcileAutoPresence()
    {
        if (!_settings.AutoStartEnabled) return;
        lock (_sessionLock)
        {
            var present = _watcher.GetCurrentPlayers().Any(p => IsAutoFriend(p.UserId, p.DisplayName));
            if (!present)
            {
                if (_autoStarted && IsRunning)
                {
                    Stop();
                    _send("toast", new { ok = true, msg = "Captions stopped — no auto-start players in this world" });
                }
                return;
            }
            TryAutoStartFromPresence();
        }
    }

    private void TryAutoStartFromPresence()
    {
        lock (_sessionLock)
        {
            if (!_settings.AutoStartEnabled || _manualHold || IsRunning) return;
            var present = _watcher.GetCurrentPlayers().FirstOrDefault(p => IsAutoFriend(p.UserId, p.DisplayName));
            if (present == null) return;

            Start(_settings.InputDeviceIndex, _settings.Engine);
            if (IsRunning)
            {
                _autoStarted = true;
                _send("toast", new { ok = true, msg = $"Captions started — {present.DisplayName} is here" });
            }
        }
    }

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "sttGetState":
                SendDevices();
                SendModels();
                SendState();
                SendPlayers();
                break;

            // Settings > About: the license documents live inside the
            // assembly (docs/*), not as loose files beside the exe.
            case "sttGetDocs":
                _send("sttDocs", new
                {
                    version = typeof(StandaloneSttController).Assembly.GetName().Version?.ToString(3),
                    readme = ReadEmbeddedDoc("docs/README.md"),
                    license = ReadEmbeddedDoc("docs/LICENSE.txt"),
                    notice = ReadEmbeddedDoc("docs/NOTICE.txt"),
                    machine = MachineProfile.Describe(),
                    installedAt = LinuxInstaller.InstalledAt(),
                });
                break;

            // The page's heartbeat (every few seconds): nothing to do here,
            // the host counts the arrivals (Program.OnUiMessage).
            case "sttPing":
                break;

            // Standalone-only: re-hash every installed catalog file against
            // its pinned checksum (the Models screen's "Verify" action).
            case "sttVerifyModels":
                _ = Task.Run(() =>
                {
                    int ok = 0;
                    var bad = new List<string>();
                    foreach (var m in SttModelCatalog.Models)
                    foreach (var f in m.Files)
                    {
                        var path = _models.PathFor(m, f);
                        if (!File.Exists(path)) continue;   // a truncated file is present, and wrong
                        if (new FileInfo(path).Length == f.SizeBytes &&
                            SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
                        else bad.Add($"{m.DisplayName} ({f.FileName})");
                    }
                    // The Verify button's list includes the GPU pack row —
                    // its natives and its CUDA runtime must be covered too
                    // (install detection is size-only). Absent parts are
                    // skipped, partial ones reported.
                    {
                        var (gok, gbad) = SttGpuPack.VerifyFiles();
                        ok += gok;
                        bad.AddRange(gbad);
                    }
                    if (SttEnginePack.IsInstalled())
                    {
                        var (eok, ebad) = SttEnginePack.VerifyFiles();
                        ok += eok;
                        bad.AddRange(ebad);
                    }
                    _send("toast", bad.Count == 0
                        ? new { ok = true, msg = $"All {ok} installed model file(s) verified" }
                        : new { ok = false, msg = $"Verification FAILED: {string.Join(", ", bad)} — delete and re-download" });
                });
                break;

            case "sttStart":
                RunOffUiThread(() =>
                {
                    lock (_sessionLock)
                    {
                        if (IsRunning) { SendState(); return; }

                        int devIdx = msg["deviceIndex"]?.Value<int?>() ?? _settings.InputDeviceIndex;
                        var engineKind = msg["engine"]?.ToString() ?? _settings.Engine;
                        lock (_settingsLock)
                        {
                            _settings.InputDeviceIndex = devIdx;
                            _settings.InputDeviceName = SttAudioDevices.InputNameAt(devIdx);
                            _settings.Engine = engineKind;
                            _settings.Save();
                        }
                        Start(devIdx, engineKind);
                    }
                });
                break;

            case "sttStop":
                RunOffUiThread(() =>
                {
                    lock (_sessionLock)
                    {
                        _manualHold = true;
                        Stop();
                    }
                });
                break;

            // One-click fix from the falling-behind advice: switch the engine
            // (or the Whisper model) and restart the session on it, keeping
            // an auto session auto.
            case "sttApplyPaceFix":
                {
                    var fix = msg["fix"]?.ToString() ?? "";
                    string label = "";
                    bool applied = false;
                    lock (_settingsLock)
                    {
                        if (fix == "parakeet")
                        {
                            _settings.Engine = "parakeet";
                            label = "Parakeet";
                            applied = true;
                        }
                        else if (fix == "whisper-model")
                        {
                            var file = Path.GetFileName(msg["model"]?.ToString() ?? "");
                            if (file.Length > 0 && File.Exists(Path.Combine(SttPaths.ModelDir, file)))
                            {
                                _settings.Engine = "whisper";
                                _settings.WhisperModel = file;
                                label = Path.GetFileNameWithoutExtension(file);
                                applied = true;
                            }
                        }
                        if (applied) _settings.Save();
                    }
                    if (!applied)
                    {
                        _send("toast", new { ok = false, msg = "That option isn't available any more" });
                        break;
                    }
                    BootLog.Append($"pace fix applied: {label}");
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            bool wasAuto = _autoStarted;
                            Stop();
                            Start(_settings.InputDeviceIndex, _settings.Engine);
                            if (IsRunning)
                            {
                                _autoStarted = wasAuto;
                                _send("toast", new { ok = true, msg = $"Switched to {label} — captions restarted" });
                            }
                        }
                        SendDevices();
                        SendModels();
                    });
                }
                break;

            case "sttRestartApp":
                RestartRequested?.Invoke();
                break;

            // Settings > Desktop: the binary installs itself into the app
            // grid (LinuxInstaller) — a file copy, so off the UI thread.
            case "sttInstallDesktop":
                RunOffUiThread(() =>
                {
                    var result = LinuxInstaller.Install();
                    _send("toast", new { ok = result.Ok, msg = result.Message });
                    _send("sttInstall", new { installedAt = LinuxInstaller.InstalledAt() });
                });
                break;

            // Settings > Speed check: time every installed engine on the
            // bundled clip (SttBenchmark) — the numbers a slow machine's
            // report needs, written to bench.log and the boot log too.
            case "sttRunBench":
                if (IsRunning || _loading)
                {
                    _send("toast", new { ok = false, msg = "Stop captions before running the speed check" });
                    break;
                }
                if (Interlocked.Exchange(ref _benchRunning, 1) != 0) break;
                _send("sttBench", new { running = true, note = "starting…" });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var report = await SttBenchmark.RunAsync(BenchCandidates(),
                            note => _send("sttBench", new { running = true, note }), CancellationToken.None);
                        var text = SttBenchmark.FormatReport(report);
                        try { File.AppendAllText(Path.Combine(SttPaths.DataDir, "bench.log"), text + Environment.NewLine); }
                        catch (Exception ex) { ErrorLog.WriteEntry("Bench.log", ex); }
                        BootLog.Append("speed check — " + report.Summary);
                        _send("sttBench", new
                        {
                            running = false,
                            machine = report.Machine,
                            summary = report.Summary,
                            rows = report.Rows.Select(r => new
                            {
                                engine = r.Engine, loadMs = r.LoadMs, pass6sMs = r.Pass6sMs,
                                passFullMs = r.PassFullMs, accuracyPct = r.AccuracyPct, verdict = r.Verdict,
                            }),
                        });
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.WriteEntry("Bench", ex);
                        _send("sttBench", new { running = false, error = ex.Message });
                    }
                    finally { _benchRunning = 0; }
                });
                break;

            case "sttConfig":
                {
                    lock (_settingsLock)
                    {
                        _settings.IntervalMs = Math.Max(msg["intervalMs"]?.Value<int>() ?? _settings.IntervalMs, SttChatboxRelay.MinIntervalMs);
                        _settings.TypingIndicator = msg["typingIndicator"]?.Value<bool>() ?? _settings.TypingIndicator;
                        _settings.NameBoost = msg["nameBoost"]?.Value<bool>() ?? _settings.NameBoost;
                        _settings.NewLineGapMs = Math.Clamp(msg["newLineGapMs"]?.Value<int>() ?? _settings.NewLineGapMs, 500, 10_000);
                        _settings.ClearGapMs = Math.Clamp(msg["clearGapMs"]?.Value<int>() ?? _settings.ClearGapMs, 5_000, 120_000);
                        // The reset gap must exceed the line-break gap or
                        // line breaks can never trigger.
                        if (_settings.ClearGapMs <= _settings.NewLineGapMs)
                            _settings.ClearGapMs = _settings.NewLineGapMs + 1000;
                        _settings.Engine = msg["engine"]?.ToString() ?? _settings.Engine;
                        if (_settings.Engine == "vosk") _settings.Engine = "parakeet";
                        _settings.WhisperModel = msg["whisperModel"]?.ToString() ?? _settings.WhisperModel;
                        _settings.Save();
                    }

                    // The relay belongs to the session: touch it under the
                    // session lock, and never wait for that lock on the
                    // window thread (an engine load may hold it for seconds).
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            if (_relay == null) return;
                            _relay.TypingIndicator = _settings.TypingIndicator;
                            _relay.NewLineGapMs = _settings.NewLineGapMs;   // applies live
                            _relay.ClearGapMs = _settings.ClearGapMs;
                            if (_relay.IntervalMs != _settings.IntervalMs && _service != null)
                            {
                                _relay.Stop();
                                _relay.IntervalMs = _settings.IntervalMs;
                                _relay.Start();
                            }
                        }
                    });
                    // The UI renders settings state only from these payloads —
                    // without the refresh a changed engine looks unselectable.
                    RefreshNameFilter();
                    SendDevices();
                    SendModels();
                    SendSavedToast();
                }
                break;

            case "sttSetInputDevice":
                {
                    int devIdx = msg["deviceIndex"]?.Value<int?>() ?? 0;
                    lock (_settingsLock)
                    {
                        _settings.InputDeviceIndex = devIdx;
                        _settings.InputDeviceName = SttAudioDevices.InputNameAt(devIdx);
                        _settings.Save();
                    }
                    SendSavedToast();
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            if (IsRunning)
                            {
                                // A device swap must not demote an auto session
                                // to manual — presence-based auto-stop still owns it.
                                bool wasAuto = _autoStarted;
                                Stop();
                                Start(devIdx, _settings.Engine);
                                if (IsRunning) _autoStarted = wasAuto;
                            }
                        }
                    });
                }
                break;

            case "sttAutoSet":
                {
                    lock (_settingsLock)
                    {
                        _settings.AutoStartEnabled = msg["enabled"]?.Value<bool>() ?? _settings.AutoStartEnabled;
                        var friends = msg["friends"]?.ToObject<List<SttSettings.AutoFriend>>();
                        if (friends != null)
                        {
                            // Standalone relaxation: name-only entries are
                            // valid (manual adds have no uid until backfill).
                            var incoming = friends
                                .Where(f => !string.IsNullOrEmpty(f.Id) || !string.IsNullOrEmpty(f.Name))
                                .ToList();
                            // The UI composed this list from a snapshot; keep
                            // any uid backfilled since then.
                            foreach (var f in incoming.Where(f => string.IsNullOrEmpty(f.Id)))
                                f.Id = _settings.AutoStartFriends.FirstOrDefault(o =>
                                    !string.IsNullOrEmpty(o.Id) &&
                                    string.Equals(o.Name, f.Name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
                            _settings.AutoStartFriends = incoming;
                        }
                        _settings.Save();
                    }
                    SendDevices();

                    // A session start loads a model: never on the window thread.
                    RunOffUiThread(() =>
                    {
                        _manualHold = false;
                        TryAutoStartFromPresence();
                    });
                }
                break;

            // One-click ideal configuration for THIS machine: the strongest
            // engine the hardware runs fast, the live-validated 1.0 s rate,
            // and the standard pause behavior. Deliberately untouched: the
            // microphone choice and the auto-start players.
            case "sttRecommendedDefaults":
                {
                    var tier = SttHardwareTier.Detect();
                    bool gpuWhisper = tier == SttTier.Gpu && SttGpuPack.IsInstalled() && FindWhisperModel() != null;
                    bool parakeetInstalled = SttEnginePack.IsInstalled() && _models.IsInstalled(SttModelCatalog.Find(SttModelCatalog.ParakeetId)!);
                    string priorEngine = _settings.Engine;
                    lock (_settingsLock)
                    {
                        _settings.Engine = gpuWhisper ? "whisper" : parakeetInstalled ? "parakeet" : "whisper";
                        _settings.WhisperModel = "";   // Auto — the tier picks the model
                        _settings.IntervalMs = 1000;
                        _settings.TypingIndicator = true;
                        _settings.NewLineGapMs = 2300;
                        _settings.ClearGapMs = 30_000;
                        _settings.NameBoost = false;   // experimental stays opt-in
                        _settings.Save();
                    }
                    // The session lock may be held by an engine load for
                    // seconds — never wait for it on the window thread.
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            if (_relay != null)
                            {
                                _relay.TypingIndicator = true;
                                _relay.NewLineGapMs = 2300;
                                _relay.ClearGapMs = 30_000;
                                if (_relay.IntervalMs != 1000 && _service != null)
                                {
                                    _relay.Stop();
                                    _relay.IntervalMs = 1000;
                                    _relay.Start();
                                }
                            }
                        }
                    });
                    RefreshNameFilter();
                    SendDevices();
                    SendModels();
                    var engineLabel = _settings.Engine == "whisper"
                        ? (gpuWhisper ? "Whisper (GPU)" : "Whisper") : "Parakeet";
                    var suffix = IsRunning && _settings.Engine != priorEngine
                        ? " — engine change applies at the next Start" : "";
                    _send("toast", new { ok = true, msg =
                        $"Recommended defaults applied: {engineLabel}, 1.0 s rate, 3 s line break, 30 s quiet clear{suffix}" });
                }
                break;

            case "sttGetModels":
                SendModels();
                break;

            case "sttDownloadModel":
                {
                    var id = msg["id"]?.ToString() ?? "";
                    if (_downloadingId != null)
                    {
                        _send("toast", new { ok = false, msg = "A model download is already running" });
                        break;
                    }

                    if (id == SttEnginePack.Id)
                    {
                        _downloadingId = id;
                        _downloadCts = new CancellationTokenSource();
                        var engCt = _downloadCts.Token;
                        SendModels();
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await SttEnginePack.DownloadAsync((received, total) =>
                            {
                                long now = Environment.TickCount64;
                                if (now - _lastProgressSentAt < 150 && received != total) return;
                                _lastProgressSentAt = now;
                                _send("sttModelProgress", new { id, received, total });
                            }, engCt);
                            _downloadingId = null;
                            _downloadCts?.Dispose();
                            _downloadCts = null;
                            _send("toast", ok
                                ? new { ok = true, msg = "Parakeet engine installed" }
                                : new { ok = false, msg = error ?? "download failed" });
                            SendModels();
                            SendDevices();
                        });
                        break;
                    }

                    if (id == SttGpuPack.Id)
                    {
                        _downloadingId = id;
                        _downloadCts = new CancellationTokenSource();
                        var gpuCt = _downloadCts.Token;
                        SendModels();
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await SttGpuPack.DownloadAsync((received, total) =>
                            {
                                long now = Environment.TickCount64;
                                if (now - _lastProgressSentAt < 150 && received != total) return;
                                _lastProgressSentAt = now;
                                _send("sttModelProgress", new { id, received, total });
                            }, gpuCt);
                            _downloadingId = null;
                            _downloadCts?.Dispose();
                            _downloadCts = null;
                            _send("toast", ok
                                ? new { ok = SttGpuPack.CudaRuntimePresent(), msg = "GPU acceleration installed — restart Chatterbox to activate it" + SttGpuPack.RuntimeNote() }
                                : new { ok = false, msg = error ?? "download failed" });
                            SendModels();
                        });
                        break;
                    }

                    var info = SttModelCatalog.Find(id);
                    if (info == null) break;

                    // The voice detector takes the shared path: the boot
                    // check may already be fetching it, and the same file
                    // must never download twice at once.
                    if (id == SttModelCatalog.VadId)
                    {
                        _ = EnsureVadAsync();
                        SendModels();
                        break;
                    }

                    _downloadingId = id;
                    _downloadCts = new CancellationTokenSource();
                    var ct = _downloadCts.Token;
                    SendModels();
                    _ = Task.Run(async () =>
                    {
                        var (ok, error) = await _models.DownloadAsync(id, ct);
                        _downloadingId = null;
                        _downloadCts?.Dispose();
                        _downloadCts = null;
                        _send("toast", ok
                            ? new { ok = true, msg = $"{info.DisplayName} downloaded and verified" }
                            : new { ok = false, msg = error ?? "download failed" });
                        SendModels();
                        SendDevices();
                    });
                }
                break;

            case "sttCancelDownload":
                {
                    // The download task disposes the source when it ends;
                    // a click that lands in that gap must not throw.
                    var cts = _downloadCts;
                    try { cts?.Cancel(); } catch (ObjectDisposedException) { }
                }
                break;

            case "sttDeleteModel":
                {
                    var id = msg["id"]?.ToString() ?? "";
                    if (IsRunning)
                    {
                        _send("toast", new { ok = false, msg = "Stop captions before deleting models" });
                        break;
                    }
                    if (id == SttEnginePack.Id)
                    {
                        SttEnginePack.Delete();
                        _send("toast", new { ok = true, msg = "Parakeet engine removed" });
                    }
                    else if (id == SttGpuPack.Id)
                    {
                        SttGpuPack.Delete();
                        _send("toast", new { ok = true, msg = "GPU acceleration removed — takes effect after restarting Chatterbox" });
                    }
                    else
                    {
                        _models.Delete(id);
                    }
                    SendModels();
                    SendDevices();
                }
                break;
        }
    }

    private void Start(int deviceIndex, string engineKind)
    {
        lock (_sessionLock)
        {
            StartCore(deviceIndex, engineKind);
        }
    }

    private void StartCore(int deviceIndex, string engineKind)
    {
        Stop();

        var engine = BuildEngine(engineKind, out var buildError);
        if (engine == null)
        {
            _send("sttState", new { running = false, error = buildError });
            _send("toast", new { ok = false, msg = buildError });
            return;
        }

        // A missing voice detector is fetched here rather than failing the
        // session (see EnsureVoiceDetectorAtBoot). The wait is bounded so a
        // dead connection can't hold the session lock for long; the download
        // itself carries on, and the next Start finds the file.
        if (!_models.IsInstalled(SttModelCatalog.Vad))
        {
            var vad = SttModelCatalog.Vad;
            _loading = true;
            _loadingLabel = "voice detector";
            SendState();
            bool ok; string? error;
            try
            {
                var ensure = EnsureVadAsync();
                if (ensure.Wait(VadWaitAtStartMs)) (ok, error) = ensure.Result;
                else (ok, error) = (false, "the download is taking too long");
            }
            catch (Exception ex) { (ok, error) = (false, ex.InnerException?.Message ?? ex.Message); }
            if (!ok)
            {
                _loading = false;
                engine.Dispose();
                var msg = $"Captions need the voice detector ({vad.DisplayName}, {vad.SizeBytes / 1024} KB) " +
                          $"and it couldn't be downloaded — {error ?? "download failed"}. Check the connection and press Start again.";
                _send("sttState", new { running = false, error = msg });
                _send("toast", new { ok = false, msg });
                return;
            }
        }

        // The model load is the slow part (a large Whisper model from a
        // hard disk takes seconds): the UI shows it as loading meanwhile.
        _loading = true;
        _loadingLabel = engine.Name;
        SendState();
        try { StartSession(deviceIndex, engineKind, engine); }
        catch (Exception ex)
        {
            // Never leave the page in "Loading…" with a disabled button.
            ErrorLog.WriteEntry("StartSession", ex);
            _loading = false;
            try { Stop(); } catch { }
            _send("sttState", new { running = false, error = "captions could not start: " + ex.Message });
            _send("toast", new { ok = false, msg = "Captions could not start — " + ex.Message });
        }
        finally { _loading = false; }
    }

    private void StartSession(int deviceIndex, string engineKind, ISttEngine engine)
    {
        _service = new SttService();
        _service.OnLog += RouteSttLog;
        _service.OnPass += OnPass;
        _service.OnPartial += (committed, pending) => _send("sttPartial", new { committed, pending });
        _service.OnSpeechActive += active => _send("sttSpeech", new { active });
        var mine = _service;
        _service.OnSessionFailed += reason =>
        {
            // Device unplugged, driver failure, a recognition pass that
            // threw: wind the session down and say so, instead of
            // captioning silence forever. A session that has since replaced
            // this one is not ours to stop; an auto-started one is re-armed
            // so the presence recheck brings captions back once it can.
            lock (_sessionLock) { if (!ReferenceEquals(_service, mine)) return; }
            bool wasAuto = _autoStarted;
            Stop();
            _send("toast", new { ok = false, msg = $"Captions stopped — {reason}" });
            if (wasAuto && _settings.AutoStartEnabled)
            {
                BootLog.Append($"auto-started captions stopped ({reason}); presence recheck armed to bring them back");
                TryArmRecheck(5000);
            }
        };

        _relay = new SttChatboxRelay(_service)
        {
            IntervalMs = _settings.IntervalMs,
            TypingIndicator = _settings.TypingIndicator,
            NewLineGapMs = _settings.NewLineGapMs,
            ClearGapMs = _settings.ClearGapMs,
        };
        _relay.OnLog += RouteSttLog;
        _relay.OnTextSent += text => _send("sttSent", new { text });
        _relay.Start();

        // Experimental name recognition: everything below runs ONLY when the
        // user opted in — off means the exact default engine paths.
        if (_settings.NameBoost)
        {
            var names = CollectBiasNames();
            if (names.Count > 0)
            {
                engine.SetBiasTerms(names);
                _service.TextFilter = NameCorrector.Create(names) is { } corrector
                    ? corrector.Correct : null;
            }
        }

        int resolved = SttAudioDevices.ResolveInput(deviceIndex, _settings.InputDeviceName);
        var tier = SttHardwareTier.Detect();
        bool fastHardware = tier is SttTier.Gpu or SttTier.CpuHigh;
        // The monitor is in place before the first pass can report to it.
        _pace = new SttPaceMonitor();
        _lastPaceStatus = SttPaceMonitor.PaceStatus.Unknown;
        _lastPaceSentAt = 0;
        _paceBehindLogged = false;
        if (!_service.Start(resolved, engine, out var error, fastHardware))
        {
            _relay.Dispose();
            _relay = null;
            _service = null;
            _send("sttState", new { running = false, error });
            _send("toast", new { ok = false, msg = error });
            return;
        }

        _loading = false;
        _sessionStartedAt = Environment.TickCount64;
        _lastMemoryLoggedAt = _sessionStartedAt;
        SendState();

        // Whisper on the CPU: say so at once, with the one-click fix for
        // this machine, instead of letting the first sentence feel broken —
        // an idle NVIDIA card or a large model is worth a word before the
        // pace monitor has numbers.
        if (engineKind == "whisper" && _service.EngineName.Contains("Cpu", StringComparison.Ordinal))
        {
            var model = FindWhisperModel();
            bool large = model != null && new FileInfo(model).Length > 400_000_000;
            if (tier == SttTier.Gpu || large)
            {
                var (msg, action) = BuildAdvice(tier == SttTier.Gpu
                    ? "Whisper is running on the CPU — your NVIDIA card is idle. "
                    : "This Whisper model is large for CPU-only recognition and may fall behind. ");
                _send("toast", new { ok = false, msg, action });
            }
        }
    }

    public void Stop()
    {
        lock (_sessionLock)
        {
            _autoStarted = false;
            if (_service == null && _relay == null) return;
            if (_service != null)
                BootLog.Append($"captions session ended after {(Environment.TickCount64 - _sessionStartedAt) / 60000.0:0.0} min — " +
                               $"{_pace.Summary()} — {_service.EngineName}; {MemoryLine()}");
            _service?.Stop();
            _relay?.Dispose();
            _relay = null;
            _service?.Dispose();
            _service = null;
            SendState();
            _meterPct = -1;
            _send("sttMeter", new { level = 0f });
        }
    }

    // Settings files from old releases may name the retired "vosk"
    // engine — treat it as the default engine.
    private ISttEngine? BuildEngine(string kind, out string? error)
    {
        error = null;
        if (kind == "vosk") kind = "parakeet";

        if (kind == "parakeet")
        {
            if (!SttEnginePack.IsInstalled())
            {
                error = "Parakeet engine not installed — download it in the Models section";
                return null;
            }
            var parakeet = SttModelCatalog.Find(SttModelCatalog.ParakeetId)!;
            if (!_models.IsInstalled(parakeet))
            {
                error = "Parakeet model not installed — download it in the Models section";
                return null;
            }
            return new SherpaOnnxEngine();
        }

        var model = FindWhisperModel();
        if (model == null)
        {
            error = "No Whisper model installed — download one in the Models section";
            return null;
        }
        return new WhisperNetEngine(model, "en");
    }

    // Every engine this machine could caption with: Parakeet when installed,
    // the active Whisper model, and the smallest installed Whisper model as
    // the fallback a slow machine would be steered to.
    private List<(string Label, Func<ISttEngine> Create)> BenchCandidates()
    {
        var list = new List<(string, Func<ISttEngine>)>();
        var parakeet = SttModelCatalog.Find(SttModelCatalog.ParakeetId)!;
        if (SttEnginePack.IsInstalled() && _models.IsInstalled(parakeet))
            list.Add(("Parakeet", () => new SherpaOnnxEngine()));
        var active = FindWhisperModel();
        if (active != null)
            list.Add(($"Whisper {Path.GetFileNameWithoutExtension(active)}", () => new WhisperNetEngine(active, "en")));
        var smallest = SmallestWhisperModel();
        if (active != null && smallest != null &&
            !string.Equals(Path.GetFileName(active), smallest, StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(SttPaths.ModelDir, smallest);
            list.Add(($"Whisper {Path.GetFileNameWithoutExtension(smallest)}", () => new WhisperNetEngine(path, "en")));
        }
        return list;
    }

    private string? FindWhisperModel()
    {
        var dir = SttPaths.ModelDir;

        if (!string.IsNullOrEmpty(_settings.WhisperModel))
        {
            // The setting comes from an editable/importable file — keep it a
            // bare file name so it can never point outside the model dir.
            var chosen = Path.Combine(dir, Path.GetFileName(_settings.WhisperModel));
            if (File.Exists(chosen)) return chosen;
        }

        var recommended = SttModelCatalog.Find(RecommendedModelId());
        if (recommended != null && _models.IsInstalled(recommended)) return _models.PathFor(recommended);

        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "ggml-*.bin")
            .Where(f => !f.Contains("silero", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    // The lowest hardware tier has no recommendation of its own — map it
    // to the smallest quantized model (tiny.en-q5, 32 MB).
    private static string RecommendedModelId()
    {
        var id = SttHardwareTier.RecommendedModelId(SttHardwareTier.Detect());
        return string.IsNullOrEmpty(id) ? "tiny.en-q5" : id;
    }

    // Engine and relay log lines are not shown in the UI (it never was
    // wired to display them); the ones that mean something went wrong go
    // to error.log, the rest are dropped without being serialized.
    private static void RouteSttLog(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("did not exit", StringComparison.OrdinalIgnoreCase))
            ErrorLog.WriteNote("Stt", line);
    }

    private void MeterTick()
    {
        var svc = _service; // snapshot — Stop() nulls the field from other threads
        if (svc is not { IsRunning: true }) return;
        int pct = (int)MathF.Round(Math.Clamp(svc.MeterLevel, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);
        if (pct == _meterPct) return;
        _meterPct = pct;
        _send("sttMeter", new { level = pct / 100f });
    }

    private static string ReadEmbeddedDoc(string name)
    {
        using var s = typeof(StandaloneSttController).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Listing devices runs pw-dump/pactl (child processes, seconds when
    // PipeWire is restarting): never on the window thread. Requests that
    // arrive while one is running are folded into one more pass.
    private int _devicesInFlight;
    private volatile bool _devicesPending;
    private void SendDevices()
    {
        if (Interlocked.Exchange(ref _devicesInFlight, 1) != 0) { _devicesPending = true; return; }
        RunOffUiThread(() =>
        {
            try
            {
                do { _devicesPending = false; SendDevicesNow(); } while (_devicesPending);
            }
            finally { Interlocked.Exchange(ref _devicesInFlight, 0); }
        });
    }

    private void SendDevicesNow()
    {
        var devices = SttAudioDevices.GetInputNames();
        var whisperModel = FindWhisperModel();
        var installed = Directory.Exists(SttPaths.ModelDir)
            ? Directory.EnumerateFiles(SttPaths.ModelDir, "ggml-*.bin")
                .Where(f => !f.Contains("silero", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(n => n)
                .ToArray()
            : Array.Empty<string>();
        _send("sttDevices", new
        {
            devices,
            savedIndex = _settings.InputDeviceIndex,
            engine = _settings.Engine,
            intervalMs = _settings.IntervalMs,
            typingIndicator = _settings.TypingIndicator,
            nameBoost = _settings.NameBoost,
            newLineGapMs = _settings.NewLineGapMs,
            clearGapMs = _settings.ClearGapMs,
            autoStartEnabled = _settings.AutoStartEnabled,
            autoStartFriends = _settings.AutoStartFriends.Select(f => new { id = f.Id, name = f.Name }),
            whisperAvailable = whisperModel != null,
            whisperModelName = whisperModel != null ? Path.GetFileName(whisperModel) : "",
            whisperModels = installed,
            whisperModelSetting = _settings.WhisperModel,
            vadAvailable = _models.IsInstalled(SttModelCatalog.Vad),
            parakeetAvailable = SttEnginePack.IsInstalled() && _models.IsInstalled(SttModelCatalog.Find(SttModelCatalog.ParakeetId)!),
            modelDir = SttPaths.ModelDir,
        });
    }

    private void SendModels()
    {
        var tier = SttHardwareTier.Detect();
        var activePath = FindWhisperModel();
        var activeFile = activePath != null ? Path.GetFileName(activePath) : "";
        _send("sttModels", new
        {
            tier = tier.ToString(),
            tierLabel = SttHardwareTier.Label(tier),
            tierNote = SttHardwareTier.TierNote(),
            recommendedId = RecommendedModelId(),
            downloading = _downloadingId ?? "",
            models = SttModelCatalog.Models.Select(m => new
            {
                id = m.Id,
                displayName = m.DisplayName,
                sizeBytes = m.SizeBytes,
                license = m.License,
                attribution = m.Attribution,
                installed = _models.IsInstalled(m),
                active = m.Id == SttModelCatalog.ParakeetId
                    ? _settings.Engine == "parakeet" && _models.IsInstalled(m)
                    : _settings.Engine == "whisper" && m.PrimaryFileName == activeFile,
                isVad = m.Id == SttModelCatalog.VadId,
            }).Append(new
            {
                id = SttEnginePack.Id,
                displayName = SttEnginePack.DisplayName,
                sizeBytes = SttEnginePack.SizeBytes,
                license = SttEnginePack.License,
                attribution = SttEnginePack.Attribution,
                installed = SttEnginePack.IsInstalled(),
                active = false,
                isVad = false,
            }).Append(new
            {
                id = SttGpuPack.Id,
                displayName = SttGpuPack.DisplayName,
                sizeBytes = SttGpuPack.SizeBytes,
                license = SttGpuPack.License,
                attribution = SttGpuPack.Attribution,
                installed = SttGpuPack.IsInstalled(),
                active = false,
                isVad = false,
            }),
        });
    }

    // Player display names in the instance plus the auto-start list — the
    // vocabulary the engines and the corrector should know.
    private List<string> CollectBiasNames()
    {
        var names = _watcher.GetCurrentPlayers().Select(p => p.DisplayName)
            .Concat(_settings.AutoStartFriends.Select(f => f.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
        return names;
    }

    // The correction stage is pure managed work, so it can track roster
    // changes live; engine-side biasing applies at session start only.
    private void RefreshNameFilter()
    {
        var svc = _service;
        if (svc == null) return;
        svc.TextFilter = _settings.NameBoost && CollectBiasNames() is { Count: > 0 } names
            ? (NameCorrector.Create(names) is { } corrector ? corrector.Correct : null)
            : null;
    }

    // An empty or unfound VRChat log is the presence failure a user can fix
    // (VRChat's Settings → Debug → Logging, or --vrchat-log-dir), so the
    // boot log records it when detected, and again once the log is written.
    private bool _logProblemReported;

    private void OnLogHealthChanged(VrchatLogHealth health)
    {
        string file = _watcher.LogFileName ?? "no file";
        int graceS = PresenceWatcher.LogGraceMs / 1000;
        switch (health)
        {
            case VrchatLogHealth.Empty:
                _logProblemReported = true;
                BootLog.Append($"vrchat log: still empty {graceS} s after VRChat was seen running ({file}) — players can't be seen; VRChat's Settings → Debug → Logging is probably off");
                break;
            case VrchatLogHealth.Missing:
                _logProblemReported = true;
                BootLog.Append($"vrchat log: no log file {graceS} s after VRChat was seen running (in {_watcher.LogDirectory}) — players can't be seen; check VRChat's Settings → Debug → Logging, and that this is the prefix VRChat runs from");
                break;
            case VrchatLogHealth.NoFolder:
                _logProblemReported = true;
                BootLog.Append($"vrchat log: log folder not found {graceS} s after VRChat was seen running (looked for {_watcher.LogDirectory}) — players can't be seen; if VRChat's Proton prefix is elsewhere, start Chatterbox with --vrchat-log-dir <folder>");
                break;
            case VrchatLogHealth.Writing when _logProblemReported:
                _logProblemReported = false;
                BootLog.Append($"vrchat log: being written now ({file})");
                break;
        }
        SendPlayers();
    }

    private void SendPlayers() =>
        _send("sttPlayers", new
        {
            game = _watcher.GameRunning,
            log = _watcher.LogHealth.ToString().ToLowerInvariant(),
            world = _watcher.CurrentWorldId ?? "",
            worldName = _watcher.PendingWorldName,
            players = _watcher.GetCurrentPlayers()
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new { id = p.UserId, name = p.DisplayName }),
        });

    private void SendState() =>
        _send("sttState", new
        {
            running = IsRunning,
            loading = _loading,
            engineName = _loading ? _loadingLabel : (_service?.EngineName ?? ""),
        });

    // Every recognition pass reports in from the pipeline worker. The UI
    // gets the status at most once a second (or at once on a change); the
    // boot log records the moment recognition falls behind or catches up,
    // so a bug report carries the numbers.
    private void OnPass(SttPassInfo pass)
    {
        long now = Environment.TickCount64;
        var status = _pace.Record(pass, now);
        if (now - _lastMemoryLoggedAt >= MemoryLogIntervalMs)
        {
            // A leak shows up here long before anyone notices: one line per
            // ten minutes of captions, in the log every bug report carries.
            _lastMemoryLoggedAt = now;
            BootLog.Append($"memory after {_pace.Passes} passes: {MemoryLine()}");
        }
        bool changed = status != _lastPaceStatus;
        if (changed || now - _lastPaceSentAt >= 1000)
        {
            _lastPaceSentAt = now;
            _lastPaceStatus = status;
            _send("sttPace", new
            {
                status = status.ToString(),
                load = Math.Round(_pace.Load, 2),
                lagMs = _pace.LagMs,
                passMs = pass.PassMs,
                windowMs = pass.WindowMs,
            });
            if (changed && status == SttPaceMonitor.PaceStatus.Behind && !_paceBehindLogged && now - _lastPaceLogAt >= 60_000)
            {
                _paceBehindLogged = true;
                _lastPaceLogAt = now;
                BootLog.Append($"recognition falling behind: {_pace.Describe()} — {_service?.EngineName}");
            }
            else if (changed && status == SttPaceMonitor.PaceStatus.KeepingUp && _paceBehindLogged)
            {
                _paceBehindLogged = false;
                BootLog.Append($"recognition keeping up again: {_pace.Describe()}");
            }
        }
        if (_pace.ShouldWarn(now)) SendPaceAdvice();
    }

    // Offered once per session after ten seconds of falling behind: the
    // cause this machine most likely has, with the one-click fix for it.
    private void SendPaceAdvice()
    {
        var (msg, action) = BuildAdvice(
            $"Captions are falling behind — recognition runs {_pace.Load:0.0}× slower than real time, " +
            $"about {_pace.LagMs / 1000.0:0.0} s late. ");
        BootLog.Append($"pace advice: {msg}");
        _send("toast", new { ok = false, msg, action });
    }

    // The likeliest reason recognition is slow on THIS machine, and the
    // one-click fix for it, after a caller-supplied opening sentence.
    private (string msg, object action) BuildAdvice(string head)
    {
        string engineName = _service?.EngineName ?? "";
        bool whisper = _settings.Engine == "whisper";
        bool onCpu = whisper && engineName.Contains("Cpu", StringComparison.Ordinal);
        bool onCuda = whisper && engineName.Contains("Cuda", StringComparison.Ordinal);
        bool nvidia = SttHardwareTier.Detect() == SttTier.Gpu;
        bool parakeetReady = SttEnginePack.IsInstalled() &&
                             _models.IsInstalled(SttModelCatalog.Find(SttModelCatalog.ParakeetId)!);
        string? smaller = whisper ? SmallerWhisperModel() : SmallestWhisperModel();

        string msg;
        object action;
        if (onCpu && nvidia && SttGpuPack.WhisperInstalled() && !SttGpuPack.CudaRuntimePresent())
        {
            // The natives are there but no CUDA runtime is (an install from
            // before the runtime became part of the pack): a restart would
            // change nothing, so send them to the download that fetches it.
            msg = head + "GPU acceleration is missing its CUDA runtime part — download it again on the Models screen " +
                  $"(about {SttGpuPack.SizeBytes / 1_000_000} MB), then restart.";
            action = new { label = "Open Models", view = "models" };
        }
        else if (onCpu && nvidia && SttGpuPack.IsInstalled() && SttGpuPack.ArmedAtStartup)
        {
            // The pack was there when this process started and whisper.cpp
            // still chose the CPU: another restart changes nothing — the
            // loader's own words are in the logs.
            msg = head + "GPU acceleration is installed but whisper.cpp still ran on the CPU — see the 'whisper.net loader:' lines in " +
                  Path.Combine(SttPaths.DataDir, "last_boot.log") + " (an unsupported card or an old driver are the usual causes).";
            action = new { };
        }
        else if (onCpu && nvidia && SttGpuPack.IsInstalled())
        {
            msg = head + "GPU acceleration is installed but not active yet — Chatterbox has to restart to load it.";
            action = new { label = "Restart Chatterbox", send = new { action = "sttRestartApp" } };
        }
        else if (onCpu && nvidia)
        {
            msg = head + "Your NVIDIA card can run Whisper many times faster — install GPU acceleration on the Models screen.";
            action = new { label = "Open Models", view = "models" };
        }
        else if (onCpu && SttGpuPack.DriverGapNote() is { Length: > 0 } gap)
        {
            // An NVIDIA kernel module without the driver's CUDA library:
            // nothing the app can download helps, one package does.
            msg = head + gap;
            action = new { };
        }
        else if (whisper && parakeetReady)
        {
            msg = head + (onCuda
                ? "The GPU is busy with the game; Parakeet runs on the CPU and isn't affected."
                : "Parakeet is installed and runs faster on this CPU.");
            action = new { label = "Switch to Parakeet", send = new { action = "sttApplyPaceFix", fix = "parakeet" } };
        }
        else if (smaller != null)
        {
            msg = head + (whisper
                ? "A smaller Whisper model keeps up on this hardware."
                : "Parakeet is too heavy for this CPU — a small Whisper model is much lighter.");
            action = new
            {
                label = $"Use {Path.GetFileNameWithoutExtension(smaller)}",
                send = new { action = "sttApplyPaceFix", fix = "whisper-model", model = smaller },
            };
        }
        else
        {
            msg = head + (whisper
                ? "Install Parakeet (fast on CPU) or a smaller Whisper model on the Models screen."
                : "Parakeet is too heavy for this CPU — install the Quick start Whisper model (tiny.en) on the Models screen.");
            action = new { label = "Open Models", view = "models" };
        }
        return (msg, action);
    }

    // The next Whisper model down from the active one (by size), or null.
    private string? SmallerWhisperModel()
    {
        var current = FindWhisperModel();
        long currentSize = current != null ? new FileInfo(current).Length : long.MaxValue;
        return InstalledWhisperModels()
            .Where(f => new FileInfo(f).Length < currentSize)
            .OrderByDescending(f => new FileInfo(f).Length)
            .Select(f => Path.GetFileName(f))
            .FirstOrDefault();
    }

    private string? SmallestWhisperModel() =>
        InstalledWhisperModels()
            .OrderBy(f => new FileInfo(f).Length)
            .Select(f => Path.GetFileName(f))
            .FirstOrDefault();

    private static IEnumerable<string> InstalledWhisperModels() =>
        Directory.Exists(SttPaths.ModelDir)
            ? Directory.EnumerateFiles(SttPaths.ModelDir, "ggml-*.bin")
                .Where(f => !f.Contains("silero", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    public void Dispose()
    {
        SttSettings.Recovered -= OnSettingsRecovered;
        // A download in flight is cancelled cleanly (its .partial survives
        // for a resume) instead of dying with the process mid-stream.
        try { _downloadCts?.Cancel(); } catch { }
        // A fallback tick racing this dispose must not start a session on
        // a controller that is going away.
        Interlocked.Exchange(ref _bootReconcileDone, 1);
        _provisionalRecheck?.Dispose();
        _bootFallback?.Dispose();
        // Wait out any in-flight timer ticks before Stop() nulls _service.
        using (var done = new ManualResetEvent(false))
            if (_meterTimer.Dispose(done)) done.WaitOne(1000);
        using (var done = new ManualResetEvent(false))
            if (_autoRecheck.Dispose(done)) done.WaitOne(1000);
        Stop();
        // Settings changed while the file was "momentarily invisible" would
        // otherwise be lost on a quick quit.
        lock (_settingsLock)
            if (SttSettings.ProvisionalDefaults) _settings.GiveUpProvisional();
    }
}
