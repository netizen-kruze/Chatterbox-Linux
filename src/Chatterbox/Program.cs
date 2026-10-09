using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photino.NET;
using Chatterbox.Stt;

namespace Chatterbox;

// Photino host: a WebKitGTK window showing wwwroot/, speaking the same
// {action,...} / {type,payload} JSON contract. Outbound messages queue in a
// channel until the page has loaded and sent its first request, then drain
// through a single dispatcher so window-thread marshaling stays in one place.
//
// The gate is the page's first message, NOT Photino's WindowCreated: that
// event fires right after the native window is constructed, and a message
// sent into a web view that has not finished loading is lost at best and a
// native crash at worst (the Windows build died exactly that way once).
// Auto-start on boot (a watched player already present) queues state and
// toast messages that early.
internal static class Program
{
    private static PhotinoWindow _window = null!;
    // Bounded: if the page never connects, hours of meter and state updates
    // must not pile up in memory — the page re-requests state when it does.
    private static readonly Channel<string> ToUi = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    // Completed on the window thread inside a WebView callback — run the
    // dispatcher's continuation elsewhere so draining never happens inline
    // in that callback.
    private static readonly TaskCompletionSource UiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _exiting;
    // Our own switches (never the game's command line in wrapper mode) and
    // the binary's path as it was at start — read before any update
    // renames it (/proc/self/exe follows the inode to its new name).
    private static string[] _ownArgs = Array.Empty<string>();
    private static string? _exePath;
    // Set by RestartApp: after the window has closed, Main starts the
    // successor and leaves.
    private static string? _restartWhy;
    // The native window exists (WindowCreated), so Invoke can be marshaled
    // to it. Before that, a game-exit notice has nowhere to go.
    private static volatile bool _windowUp;
    private static long _bootTick;
    private const int PageConnectWatchdogMs = 20_000;

    private static int Main(string[] args)
    {
        _bootTick = Environment.TickCount64;
        _exePath = Environment.ProcessPath;
        InstallCrashHandlers();
        // Steam launch-option (wrapper) mode is recognized first: everything
        // after our own name is the game's command line, and none of it may
        // be read as one of our switches or test hooks.
        bool wrapperMode = args.Length > 0 && !args[0].StartsWith('-') && File.Exists(args[0]);
        var own = wrapperMode ? Array.Empty<string>() : args;
        _ownArgs = own;
        // Switches that never open a window (LinuxInstaller does the work).
        if (own.Contains("--help") || own.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        if (own.Contains("--install")) return Report(LinuxInstaller.Install());
        if (own.Contains("--uninstall")) return Uninstall(purge: own.Contains("--purge"));
        if (own.Contains("--bench")) return RunBench(own);
        if (own.Contains("--install-gpu")) return InstallGpu(own);
        if (own.Contains("--update")) return RunUpdate(own);
        // Started by Steam with its overlay preloaded: run again without it
        // (LinuxHost explains why) and report the child's exit code.
        if (LinuxHost.ReexecWithoutSteamPreload(args) is int reexecCode) return reexecCode;
        WaitForPredecessor(own);
        // Live captions are latency work: trade a little memory for GC
        // pauses that stay short during long sessions.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Test hooks, used by tools/smoke.sh and the checklist: an alternate
        // data folder, an alternate VRChat log folder, and "treat VRChat as
        // running" so the auto-start path is exercised without the game.
        if (ArgValue(own, "--data-dir") is { } dataDir) SttPaths.DataDir = Path.GetFullPath(dataDir);
        var vrchatLogDir = ArgValue(own, "--vrchat-log-dir");
        bool assumeGame = own.Contains("--assume-vrchat-running");
        if (ArgValue(own, "--capture-command") is { } captureCmd) SttAudioDevices.CaptureCommandOverride = captureCmd;
        // A fake "latest release" document standing in for the GitHub API.
        if (ArgValue(own, "--update-url") is { } updateUrl) AppUpdater.LatestReleaseUrlOverride = updateUrl;

        // Steam launch-option wrapper mode: setting VRChat's launch options
        // to  /path/to/Chatterbox %command%  makes Steam start US with the
        // game's own command line (on Linux it begins with Steam's launch
        // helper, an absolute path). Launch the game FIRST, unconditionally
        // — even if another Chatterbox instance owns the mutex, VRChat must
        // start. In this mode the app also exits when the game exits. The
        // working directory is inherited: Steam already set it for the game.
        // A successor of a wrapper-mode instance (an in-app restart for an
        // update or the GPU pack) does not launch the game again, but it
        // still has to leave with it — the switch carries that over.
        bool withVrchat = own.Contains(ExitWithVrchatFlag);
        _withVrchat = withVrchat;
        if (wrapperMode)
        {
            try
            {
                var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false };
                foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
                // The overlay preload stripped from this process goes back
                // to the game, so its Steam overlay keeps working.
                var gamePreload = Environment.GetEnvironmentVariable(LinuxHost.GamePreloadVar);
                if (!string.IsNullOrEmpty(gamePreload))
                {
                    psi.Environment["LD_PRELOAD"] = gamePreload;
                    psi.Environment.Remove(LinuxHost.GamePreloadVar);
                }
                var gameLibPath = Environment.GetEnvironmentVariable(LinuxHost.GameLibraryPathVar);
                if (gameLibPath != null)
                {
                    psi.Environment["LD_LIBRARY_PATH"] = gameLibPath;
                    psi.Environment.Remove(LinuxHost.GameLibraryPathVar);
                }
                Process.Start(psi)?.Dispose();
                withVrchat = true;
                _withVrchat = true;
            }
            catch (Exception ex) { ErrorLog.WriteEntry("WrapperLaunch", ex); }
        }

        // Two instances would double-capture the mic, double-write the
        // chatbox, and race the settings file. (The game, if any, was
        // already launched above — the running instance takes it from here.)
        // A lock file, not a named mutex: on Linux .NET scopes those to the
        // login session, and every launcher (app grid, Steam, a terminal)
        // tends to be its own session — four windows opened side by side.
        using var instanceLock = LinuxHost.TryLockInstance(Path.Combine(SttPaths.DataDir, "instance.lock"));
        if (instanceLock == null)
        {
            // Silence here reads as "nothing happened": say it once, without a modal.
            Console.Error.WriteLine("Chatterbox is already running (instance.lock in the data folder is held).");
            LinuxHost.Notify("Chatterbox", "Chatterbox is already running.");
            return 0;
        }

        // The window needs GTK 3, libnotify and WebKitGTK 4.1. Name the
        // packages here instead of dying inside the window constructor.
        var missing = LinuxHost.MissingUiDependencies();
        if (missing.Count > 0)
        {
            LinuxHost.Alert("Chatterbox",
                "Chatterbox needs " + string.Join(", ", missing.Select(m => m.Library)) + " to show its window.\n\n" +
                "Install with:\n  sudo dnf install " + string.Join(" ", missing.Select(m => m.Package).Distinct()));
            return 1;
        }
        if (OperatingSystem.IsLinux() && !LinuxHost.HasDisplay)
        {
            Console.Error.WriteLine("Chatterbox: no display (neither WAYLAND_DISPLAY nor DISPLAY is set) — the window cannot open.");
            return 1;
        }

        // WebKitGTK's DMA-BUF renderer is known to blank or crash on the
        // proprietary NVIDIA driver; its GL path works there. Applied only
        // when that driver is present and nobody chose otherwise.
        if (Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") == null &&
            File.Exists("/proc/driver/nvidia/version"))
            LinuxHost.SetProcessEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        // From here on, a start that dies before the page connects leaves
        // the sentinel behind for the next start to report (BootSentinel).
        var unfinished = BootSentinel.Arm(version);
        if (unfinished != null) CrashRecord.CollectInBackground(unfinished);
        // Gate on THIS run's extraction succeeding, not on index.html
        // existing — a leftover from a previous version would otherwise
        // mask a failure and silently run a stale UI against a new backend.
        bool uiOk = false;
        try { ExtractUiAssets(); uiOk = true; }
        catch (Exception ex) { ErrorLog.WriteEntry("ExtractUiAssets", ex); }
        if (!uiOk || !File.Exists(Path.Combine(UiDir, "wwwroot", "index.html")))
        {
            LinuxHost.Alert("Chatterbox",
                $"Chatterbox couldn't unpack its interface files into {SttPaths.DataDir}.\n\n" +
                $"Details were written to {Path.Combine(SttPaths.DataDir, "error.log")}.");
            BootSentinel.Clear();
            return 1;
        }
        ExtractWhisperNatives();
        if (!File.Exists(Path.Combine(SttPaths.NativeDir, "libwhisper.so")))
        {
            // Captions cannot run without these natives (the voice detector
            // loads through them) — name the cause now, or Start would
            // later fail with a misleading model-load error.
            LinuxHost.Alert("Chatterbox",
                $"Chatterbox couldn't unpack its speech components into {SttPaths.NativeDir}.\n\n" +
                "Check that this folder can be written and has free space, then start Chatterbox again. " +
                "Captions can't run until then.", error: false);
        }
        SttEnginePack.RegisterNativeResolver();
        SttGpuPack.PreloadRuntime();
        HookWhisperLoaderLog();
        // The translation runtime's loader says which build it picked
        // (vulkan or a CPU variant) — the only truthful source for the
        // "runs on your GPU" claim.
        LlamaTranslator.OnLoaderLog += line => BootLog.Append("llama loader: " + line);

        // Model/GPU-pack downloads identify this app by name and version.
        var ua = $"Chatterbox/{version}";
        SttModelManager.SetUserAgent(ua);
        SttGpuPack.SetUserAgent(ua);
        SttEnginePack.SetUserAgent(ua);
        SttNativePack.SetUserAgent(ua);
        AppUpdater.SetUserAgent(ua);
        // The machine profile runs tools (nvidia-smi, lspci) that can each take
        // seconds on a bad day; it is gathered while the window comes up.
        var machineTask = Task.Run(() => MachineProfile.Describe());

        using var watcher = new PresenceWatcher(vrchatLogDir);
        if (assumeGame) watcher.IsGameRunning = () => true;
        // What the watcher does and sees — which file it follows, a folder
        // that appears, a poll that fails — goes to the boot log, where a
        // "Players shows nothing" report can be read off. Repeats are
        // folded and the total is capped: a failure every second must not
        // write a log that grows all session.
        watcher.DebugLog += line =>
        {
            if (line == _lastWatcherLine || Interlocked.Increment(ref _watcherLines) > 300) return;
            _lastWatcherLine = line;
            BootLog.Append(line);
        };
        using var ctrl = new StandaloneSttController(watcher, SendToUi);

        // No tray on Linux: closing the window quits, cleanly (captions
        // stop, the chatbox is cleared, the microphone is released).
        // Minimizing keeps captions running.
        _window = new PhotinoWindow()
            .SetTitle("Chatterbox")
            .SetSize(920, 640)
            .SetMinSize(720, 520)
            .Center()
            .SetResizable(true)
            .RegisterWindowCreatedHandler((_, _) => _windowUp = true)
            .RegisterWebMessageReceivedHandler((_, raw) => OnUiMessage(ctrl, raw));
        var icon = Path.Combine(UiDir, "app.png");
        if (File.Exists(icon)) _window.SetIconFile(icon);

        // kill, Steam's Stop, Ctrl+C: leave the way the close button does
        // (captions stop, the chatbox is cleared, the session summary is
        // written) — with a hard exit if the window thread never answers.
        try
        {
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestExit("SIGTERM"); });
            _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestExit("SIGINT"); });
        }
        catch (Exception ex) { ErrorLog.WriteEntry("SignalHandlers", ex); }

        // Wrapper mode: once the game has been seen running, its exit ends
        // this app too (the watcher's process check is the authority).
        bool sawGame = false;
        watcher.GameRunningChanged += running =>
        {
            if (running) { sawGame = true; return; }
            if (!withVrchat || !sawGame || _exiting) return;
            // Like a SIGTERM: logged, through the window, with the hard
            // exit backstop should the window thread never answer.
            RequestExit("VRChat exiting (Steam launch-option mode)");
        };

        _ = RunUiDispatcherAsync();
        watcher.Start();
        // The game may already be running when the watcher boots (fast
        // spawn) — that state change fires no event, so seed it here.
        if (watcher.GameRunning) sawGame = true;
        BootLog.Write(version, args, ctrl.Settings, watcher.GameRunning,
            machineTask.Wait(3000) ? machineTask.Result : "(still profiling — see the machine: line below)",
            watcher.LogDirectory, watcher.DescribeLog(), SttPaths.RuntimeRoot);
        if (!machineTask.IsCompleted)
            _ = machineTask.ContinueWith(t => BootLog.Append("machine: " + (t.IsCompletedSuccessfully ? t.Result : "profile failed")));
        BootLog.Append("cuda: " + SttGpuPack.Status());
        // An update the previous start installed: clear its leftovers and
        // say so (the toast waits for the page like everything else).
        if (AppUpdater.LatestReleaseUrlOverride is { } updateSource)
            BootLog.Append($"update source overridden: {updateSource}");
        if (new AppUpdater { ExePath = _exePath ?? Path.Combine(AppContext.BaseDirectory, AppUpdater.ExeName) }.FinishPendingUpdate() is { } updated)
        {
            BootLog.Append("update: " + updated);
            SendToUi("toast", new { ok = true, msg = "Chatterbox " + updated });
        }
        if (unfinished != null)
            BootLog.Append($"previous run ({unfinished.Version} started {unfinished.StartedAt:HH:mm:ss}, pid {unfinished.Pid}) " +
                           $"{unfinished.How} — crashed or was killed" +
                           (unfinished.WantsSafeBoot ? ". SAFE BOOT: captions won't auto-start until Start is pressed" : "") +
                           "; the crash record (coredumpctl/journal) goes to error.log");
        SttSettings.MarkHasRun(version);
        ctrl.EnsureVoiceDetectorAtBoot();
        ctrl.ArmBootReconcile(safeBootReason: unfinished is { WantsSafeBoot: true } ? unfinished.How : null);
        ctrl.RestartRequested += RestartApp;

        // A page that never connects means a blank window (WebKitGTK
        // trouble, a broken extraction) — say so where a report finds it.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (UiReady.Task.IsCompleted) return;
            var note = $"page has not connected {PageConnectWatchdogMs / 1000} s after start — the window is probably blank; " +
                       $"WebKitGTK 4.1 {(LinuxHost.LibraryPresent("libwebkit2gtk-4.1.so.0") ? "is installed" : "NOT found")}, " +
                       $"session {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"}, " +
                       $"WEBKIT_DISABLE_DMABUF_RENDERER={Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") ?? "unset"}";
            BootLog.Append("ui: " + note);
            ErrorLog.WriteNote("Ui", note);
        }, null, PageConnectWatchdogMs, Timeout.Infinite);

        // After the page connected it pings every 5 s; a WebKit web process
        // that crashed goes silent while captions run on — the page is
        // reloaded (WebKitGTK starts a fresh web process for it) and the
        // event logged, at most once a minute.
        var indexPath = Path.Combine(UiDir, "wwwroot", "index.html");
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            if (!UiReady.Task.IsCompleted || _exiting) return;
            long silent = Environment.TickCount64 - Interlocked.Read(ref _lastPingAt);
            if (silent < HeartbeatTimeoutMs) return;
            Interlocked.Exchange(ref _lastPingAt, Environment.TickCount64); // one reload per timeout
            var note = $"ui: no heartbeat from the page for {silent / 1000} s — reloading it (web process gone?)";
            BootLog.Append(note);
            ErrorLog.WriteNote("Ui", note);
            try { _window.Invoke(() => _window.Load(indexPath)); } catch (Exception ex) { ErrorLog.WriteEntry("UiReload", ex); }
        }, null, HeartbeatTimeoutMs, 15_000);

        _window.Load(indexPath);
        _window.WaitForClose();
        _exiting = true;
        if (_restartWhy != null) LaunchSuccessor(_restartWhy, ctrl.PendingUpdateSwap);
        // The watcher's poll must not reach a controller that is being
        // disposed (the usings unwind ctrl first).
        watcher.Stop();
        BootSentinel.Clear();
        return 0;
    }

    private const int HeartbeatTimeoutMs = 60_000;
    private static long _lastPingAt;
    private static string? _lastWatcherLine;
    private static int _watcherLines;

    private const string Usage =
        "Chatterbox — live captions for VRChat (Linux)\n" +
        "  Chatterbox                         open the app\n" +
        "  Chatterbox --install               copy this binary to ~/.local/share/Chatterbox/app and add it to the app grid\n" +
        "  Chatterbox --uninstall [--purge]   remove that install; --purge also removes settings, models and logs\n" +
        "  Chatterbox --bench                 Settings > Speed check from a terminal: time every installed engine, report on stdout\n" +
        "  Chatterbox --install-gpu           Download GPU acceleration for Whisper (CUDA) from a terminal, progress on stdout\n" +
        "  Chatterbox --update                Settings > Updates > Update now from a terminal: fetch the latest release, verify it, swap it in when this command exits\n" +
        "  Chatterbox <game command...>       Steam launch-option mode (\"/path/to/Chatterbox %command%\"): start the game, exit with it\n" +
        "  --data-dir <dir>  --vrchat-log-dir <dir>  --assume-vrchat-running   test hooks (tools/smoke.sh, tools/scenarios.sh)\n" +
        "  --exit-with-vrchat                 internal: set on the restarted instance of a Steam-launched one, so it still exits with the game\n" +
        "  --update-url <url>                 test hook: a \"latest release\" JSON document standing in for GitHub's API\n" +
        "  --capture-command <cmd>            test hook: a shell command writing raw 16 kHz mono s16le PCM to stdout replaces the recorder (tools/soak.sh)\n";

    // "--install-gpu": the Models screen's GPU acceleration download from a
    // terminal — every part still missing, progress on stdout, then the
    // same load check the next start would do. --data-dir applies.
    private static int InstallGpu(string[] args)
    {
        if (ArgValue(args, "--data-dir") is { } dataDir) SttPaths.DataDir = Path.GetFullPath(dataDir);
        SttGpuPack.SetUserAgent($"Chatterbox/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
        MigrateRuntimes();
        if (SttGpuPack.IsInstalled())
        {
            Console.WriteLine("GPU acceleration is already installed in " + SttGpuPack.InstallDir);
        }
        else
        {
            Console.WriteLine($"Downloading GPU acceleration ({SttGpuPack.SizeBytes / 1_000_000} MB) into {SttGpuPack.InstallDir} ...");
            long lastPct = -1;
            var (ok, error) = SttGpuPack.DownloadAsync((received, total) =>
            {
                long pct = total > 0 ? received * 100 / total : 0;
                if (pct / 5 == lastPct / 5 && received != total) return; // every 5 %
                lastPct = pct;
                Console.WriteLine($"  {received / 1_000_000} of {total / 1_000_000} MB ({pct}%)");
            }).GetAwaiter().GetResult();
            if (!ok) { Console.WriteLine("GPU acceleration: " + error); return 1; }
            Console.WriteLine("GPU acceleration installed — it loads on the next start.");
        }
        SttGpuPack.PreloadRuntime();
        HookWhisperLoaderLog();
        Console.WriteLine("cuda: " + SttGpuPack.Status());
        return 0;
    }

    private static int Report(LinuxInstaller.Result result)
    {
        Console.WriteLine(result.Message);
        return result.Ok ? 0 : 1;
    }

    // "--uninstall": everything but the app folder, then the message,
    // then the app folder as the very last thing. The running binary
    // usually lives in that folder (the README says to run the installed
    // copy), and a single-file app reads the assemblies it has not loaded
    // yet from its own file — so once the folder is gone, not even
    // Console.WriteLine is safe (1.7.1 died there with an unhandled
    // FileNotFoundException after a successful uninstall).
    private static int Uninstall(bool purge)
    {
        var result = LinuxInstaller.Uninstall(purge);
        Console.WriteLine(result.Message);
        Console.Out.Flush();
        if (result.Ok) LinuxInstaller.RemoveAppDir();
        return result.Ok ? 0 : 1;
    }

    // "--update": Settings > Updates > Update now from a terminal — the
    // check, the verified download, and the swap once this command has
    // exited (AppUpdater.SwapHelper, without a relaunch). --data-dir and
    // --update-url apply.
    private static int RunUpdate(string[] args)
    {
        if (ArgValue(args, "--data-dir") is { } dataDir) SttPaths.DataDir = Path.GetFullPath(dataDir);
        if (ArgValue(args, "--update-url") is { } updateUrl) AppUpdater.LatestReleaseUrlOverride = updateUrl;
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        AppUpdater.SetUserAgent($"Chatterbox/{version}");
        var exe = _exePath ?? Path.Combine(AppContext.BaseDirectory, AppUpdater.ExeName);
        var updater = new AppUpdater { ExePath = exe };
        if (!AppUpdater.IsConfigured) { Console.WriteLine("This build has no update source configured."); return 1; }
        // The previous run's leftovers, as the window start would clear them.
        if (updater.FinishPendingUpdate() is { } finished) Console.WriteLine("Previous update: " + finished);
        Console.WriteLine($"Chatterbox {version} — checking {AppUpdater.DefaultReleaseUrl} ...");
        var check = updater.CheckAsync().GetAwaiter().GetResult();
        if (check.Error != null) { Console.WriteLine("Couldn't check for updates — " + check.Error); return 1; }
        if (check.Update is not { } u)
        {
            Console.WriteLine($"You have the latest version ({check.Latest?.ToString(3) ?? version}).");
            return 0;
        }
        Console.WriteLine($"Downloading Chatterbox {u.Version.ToString(3)} ({u.AssetSize / 1_000_000} MB) ...");
        long lastPct = -1;
        var (staged, error) = updater.DownloadAsync(u, (received, total) =>
        {
            long pct = total > 0 ? received * 100 / total : 0;
            if (pct / 5 == lastPct / 5 && received != total) return; // every 5 %
            lastPct = pct;
            Console.WriteLine($"  {received / 1_000_000} of {total / 1_000_000} MB ({pct}%)");
        }).GetAwaiter().GetResult();
        if (staged == null) { Console.WriteLine("Update failed — " + error); return 1; }
        if (updater.PrepareSwap(staged, u.Version) is { } prepareError) { Console.WriteLine("Update failed — " + prepareError); return 1; }
        if (AppUpdater.StartSwapHelper(exe, relaunch: false, Array.Empty<string>()) is { } helperError)
        {
            Console.WriteLine($"Update failed — couldn't start the swap helper ({helperError}); the verified file is at {staged}");
            return 1;
        }
        Console.WriteLine($"Chatterbox {u.Version.ToString(3)} is verified and takes the place of {exe} as soon as this command exits.");
        return 0;
    }

    // "--bench": the Settings > Speed check without a window — the same
    // controller code path (every installed engine timed on the bundled
    // clip, bench.log and the boot log written), the table on stdout.
    // --data-dir applies, so a throwaway folder can be measured too.
    private static int RunBench(string[] args)
    {
        if (ArgValue(args, "--data-dir") is { } dataDir) SttPaths.DataDir = Path.GetFullPath(dataDir);
        ExtractWhisperNatives();
        SttEnginePack.RegisterNativeResolver();
        SttGpuPack.PreloadRuntime();
        HookWhisperLoaderLog();
        using var done = new ManualResetEventSlim();
        int code = 1;
        using var watcher = new PresenceWatcher(ArgValue(args, "--vrchat-log-dir"));
        using var ctrl = new StandaloneSttController(watcher, (type, payload) =>
        {
            if (type == "toast")
            {
                Console.WriteLine(JObject.FromObject(payload ?? new { })["msg"]?.ToString());
                return;
            }
            if (type != "sttBench") return;
            var json = JObject.FromObject(payload ?? new { });
            if (json["running"]?.Value<bool>() == true)
            {
                var note = json["note"]?.ToString();
                if (!string.IsNullOrEmpty(note)) Console.WriteLine("... " + note);
                return;
            }
            if (json["error"] != null)
            {
                Console.WriteLine("speed check failed: " + json["error"]);
            }
            else
            {
                Console.WriteLine(json["machine"]?.ToString());
                foreach (var row in json["rows"] as JArray ?? new JArray())
                    Console.WriteLine($"{row["engine"],-34} load {row["loadMs"],6} ms   6 s pass {row["pass6sMs"],6} ms   " +
                                      $"full clip {row["passFullMs"],6} ms   {row["accuracyPct"],3}% words   {row["verdict"]}");
                Console.WriteLine(json["summary"]?.ToString());
                code = 0;
            }
            done.Set();
        });
        ctrl.HandleMessage("sttRunBench", new JObject());
        if (!done.Wait(TimeSpan.FromMinutes(15))) { Console.WriteLine("speed check timed out"); return 1; }
        return code;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Exit requested by the game closing: through the window when it
    // exists, straight out when it doesn't yet.
    private static PosixSignalRegistration? _sigterm, _sigint;

    private static void RequestExit(string why)
    {
        if (_exiting) return;
        _exiting = true;
        BootLog.Append($"exit requested by {why}");
        CloseWindowOrExit();
        // The backstop is a wanted exit, not a crash: the marker goes too.
        _ = Task.Delay(10_000).ContinueWith(_ => { try { BootLog.Append("exit: hard exit after 10 s"); BootSentinel.Clear(); Environment.Exit(0); } catch { } });
    }

    private static void CloseWindowOrExit()
    {
        if (_windowUp)
        {
            try { _window.Invoke(() => _window.Close()); return; } catch { }
        }
        BootSentinel.Clear();
        Environment.Exit(0);
    }

    // Managed crashes used to die silently; now they leave a stack in
    // error.log and say so. Native crashes still can't be caught — the boot
    // sentinel reports those at the next start.
    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            ErrorLog.WriteEntry("Unhandled", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            BootLog.Append($"unhandled exception: {ex?.GetType().Name}: {ex?.Message}");
            LinuxHost.Alert("Chatterbox",
                "Chatterbox hit an unexpected error and has to close.\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"Details were written to {Path.Combine(SttPaths.DataDir, "error.log")}.");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.WriteEntry("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    // "--after <pid>": this process was launched by a running instance that
    // is about to exit (RestartApp) — let it release the single-instance
    // mutex first instead of losing the race and quitting.
    private static void WaitForPredecessor(string[] args)
    {
        int i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(30_000);
        }
        catch { /* already gone */ }
    }

    // Relaunch — for a native-runtime change (the GPU pack binds at load
    // time) or to run a freshly installed update. The window closes first;
    // once WaitForClose returns, Main launches the successor
    // (LaunchSuccessor), so the new process starts after this one has
    // left its window behind and finds the instance lock about to go.
    private static void RestartApp(string why)
    {
        if (_exiting) return;
        _restartWhy = why;
        _exiting = true;
        BootLog.Append($"restarting {why}");
        // Off the window thread: this runs inside a WebView callback, and
        // the close should happen after that callback has returned.
        _ = Task.Run(() => { try { _window.Invoke(() => _window.Close()); } catch { } });
    }

    // Starts the next instance from the path this one was started from,
    // which waits for this process (--after) before taking the instance
    // lock. After an update the staged binary is swapped in first — by
    // the helper, once this process is gone (AppUpdater explains why not
    // here) — and the helper starts the new version under the same name.
    // Only our own switches are carried over: in Steam wrapper mode the
    // game's command line was never ours, and the restarted app must not
    // launch the game again.
    private static void LaunchSuccessor(string why, string? pendingSwap)
    {
        try
        {
            var exe = _exePath ?? throw new InvalidOperationException("no process path");
            var args = ForwardedArgs(_ownArgs).ToList();
            // Launched by Steam with the game: the successor must still
            // exit when VRChat does (without launching it again).
            if (_withVrchat && !args.Contains(ExitWithVrchatFlag)) args.Add(ExitWithVrchatFlag);
            args.Add("--after");
            args.Add(Environment.ProcessId.ToString());
            if (pendingSwap != null)
            {
                if (AppUpdater.StartSwapHelper(exe, relaunch: true, args) is { } helperError)
                {
                    // Without the helper the old version comes back; the
                    // next start clears the staged file and the marker
                    // explains that the swap did not happen.
                    BootLog.Append($"update: the swap helper could not be started ({helperError}); restarting without the update");
                    ErrorLog.WriteNote("RestartApp", "swap helper failed to start: " + helperError);
                }
                else return;
            }
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("RestartApp", ex);
            BootLog.Append($"restart {why} FAILED to launch the new process: {ex.Message}");
        }
    }

    internal const string ExitWithVrchatFlag = "--exit-with-vrchat";
    private static volatile bool _withVrchat;
    private static readonly string[] ValueFlags = { "--data-dir", "--vrchat-log-dir", "--capture-command", "--update-url" };
    private static readonly string[] SwitchFlags = { "--assume-vrchat-running", ExitWithVrchatFlag };

    // The switches of this instance that its successor must see again —
    // never the game's command line, never --after.
    internal static IEnumerable<string> ForwardedArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (SwitchFlags.Contains(args[i])) yield return args[i];
            else if (ValueFlags.Contains(args[i]) && i + 1 < args.Length)
            {
                yield return args[i];
                yield return args[++i];
            }
        }
    }

    private static void SendToUi(string type, object? payload) =>
        ToUi.Writer.TryWrite(JsonConvert.SerializeObject(new { type, payload }));

    private static async Task RunUiDispatcherAsync()
    {
        await UiReady.Task.ConfigureAwait(false);
        await foreach (var json in ToUi.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_exiting) return;
            try { _window.Invoke(() => _window.SendWebMessage(json)); }
            catch (Exception ex) { ErrorLog.WriteEntry("UiDispatcher", ex); }
        }
    }

    // Runs on the window thread, called by the attached WebView — the first
    // call is the proof that the page is up and listening, which releases
    // everything queued during boot (in order, ahead of this reply).
    private static void OnUiMessage(StandaloneSttController ctrl, string raw)
    {
        Interlocked.Exchange(ref _lastPingAt, Environment.TickCount64);   // any message counts as a heartbeat
        if (UiReady.TrySetResult())
        {
            BootLog.Append($"ui: page connected {Environment.TickCount64 - _bootTick} ms after start, " +
                           $"{ToUi.Reader.Count} queued message(s) released");
            BootSentinel.Mark(BootSentinel.PhaseWindow);   // the start made it; the marker now guards the run
            ctrl.UiConnected();
        }
        try
        {
            var msg = JObject.Parse(raw);
            ctrl.HandleMessage(msg["action"]?.ToString() ?? "", msg);
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OnUiMessage", ex); }
    }

    // The UI lives inside the executable (single-file friendly) and is
    // re-extracted into the data folder on every launch — a few hundred KB.
    // The data folder rather than /tmp: per user by construction, and the
    // page may keep referencing it for hours.
    internal static string UiDir { get; private set; } = "";

    private static void ExtractUiAssets()
    {
        UiDir = Path.Combine(SttPaths.DataDir, "ui");
        var asm = typeof(Program).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            var rel = name[3..].Replace('/', Path.DirectorySeparatorChar)
                               .Replace('\\', Path.DirectorySeparatorChar);
            var dest = Path.Combine(UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // A stray read-only bit from a previous run must not wedge every
            // future launch.
            if (File.Exists(dest)) { try { File.SetAttributes(dest, FileAttributes.Normal); } catch { } }
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    // whisper.cpp CPU natives ride inside the executable and are placed in
    // <data folder>/runtimes/linux-x64 at boot: under a single-file publish
    // the host would extract bundled natives to its own cache dir, which
    // Whisper.net's loader never probes; it does probe <root>/runtimes/…
    // once pointed at the root (SttPaths.RuntimeRoot — always the data
    // folder, where the packs live too). The VAD loads through whisper.cpp
    // too, so captions need these even on Parakeet. Always overwritten
    // (~2.5 MB): an update must never leave a stale native behind.
    // Packs used to live under runtimes/ beside the binary; they now live in
    // the data folder (SttPaths.RuntimeRoot), so a copy made by "Add to app
    // grid" or a new version finds them and --purge removes them. A leftover
    // from an older version is moved once, folder by folder.
    private static int _migrated;
    private static void MigrateRuntimes()
    {
        if (Interlocked.Exchange(ref _migrated, 1) != 0) return;
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            var from = Path.Combine(exeDir, "runtimes");
            var to = Path.Combine(SttPaths.RuntimeRoot, "runtimes");
            if (exeDir.Length == 0 || !Directory.Exists(from) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal)) return;
            Directory.CreateDirectory(to);
            foreach (var sub in new[] { Path.Combine("cuda", SttPaths.Rid), SttPaths.Rid })
            {
                var src = Path.Combine(from, sub);
                var dst = Path.Combine(to, sub);
                if (!Directory.Exists(src) || Directory.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try { Directory.Move(src, dst); }
                catch (IOException)
                {
                    // Across filesystems: copy, then remove the source.
                    Directory.CreateDirectory(dst);
                    foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                    Directory.Delete(src, recursive: true);
                }
                BootLog.Append($"moved {src} to {dst}");
            }
        }
        catch (Exception ex) { ErrorLog.WriteEntry("MigrateRuntimes", ex); }
    }

    // Whisper.net's loader says exactly why it skipped CUDA ("cudaGetDeviceCount
    // returned error 35", "Couldn't load dependency …") and whisper.cpp says
    // what it found ("ggml_cuda_init: found 1 CUDA devices"): those lines go
    // to the boot log, capped, so "it doesn't detect CUDA" can be read off a
    // bug report.
    private static IDisposable? _whisperLog;
    private static int _whisperLogLines;
    private static void HookWhisperLoaderLog()
    {
        if (_whisperLog != null) return;
        try
        {
            _whisperLog = Whisper.net.Logger.LogProvider.AddLogger((_, message) =>
            {
                if (message == null) return;
                var m = message.Trim();
                // Per-pass chatter (a state is created for every pass) would eat the cap.
                if (m.Length == 0 || m.StartsWith("Searching for runtime directory", StringComparison.Ordinal) ||
                    m.StartsWith("Runtime directory for", StringComparison.Ordinal) ||
                    m.StartsWith("whisper_backend_init_gpu", StringComparison.Ordinal) ||
                    m.StartsWith("whisper_model_load", StringComparison.Ordinal)) return;
                if (!(m.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
                      m.Contains("Couldn't load", StringComparison.OrdinalIgnoreCase) ||
                      m.Contains("Loading dependency", StringComparison.Ordinal) ||
                      m.Contains("not available", StringComparison.OrdinalIgnoreCase))) return;
                if (Interlocked.Increment(ref _whisperLogLines) > 80) return;
                BootLog.Append("whisper.net loader: " + m);
            });
        }
        catch (Exception ex) { ErrorLog.WriteEntry("HookWhisperLoaderLog", ex); }
    }

    private static void ExtractWhisperNatives()
    {
        MigrateRuntimes();
        try
        {
            var asm = typeof(Program).Assembly;
            // The AVX2/FMA build and the no-AVX build, each into the folder
            // Whisper.net probes for it; the loader picks by what the CPU
            // has (the no-AVX folder must EXIST for an older CPU not to be
            // refused outright — see Chatterbox.csproj).
            foreach (var (prefix, dir) in new[]
            {
                ("natives/" + SttPaths.Rid + "/", SttPaths.NativeDir),
                ("natives/noavx/" + SttPaths.Rid + "/", SttPaths.NoAvxNativeDir),
            })
            {
                Directory.CreateDirectory(dir);
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var dest = Path.Combine(dir, name[prefix.Length..]);
                    // Written beside and renamed over: another instance (a
                    // --bench in a terminal, an update) keeps its mapped copy —
                    // truncating a mapped library in place is a SIGBUS.
                    var tmp = dest + ".tmp";
                    using (var src = asm.GetManifestResourceStream(name)!)
                    using (var dst = File.Create(tmp))
                        src.CopyTo(dst);
                    File.Move(tmp, dest, overwrite: true);
                }
            }
            // Whisper.net expects an assembly path here and probes
            // <its directory>/runtimes/…; a directory would lose its last
            // segment, so the root is named through a file inside it.
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(SttPaths.RuntimeRoot, "Chatterbox");
            BootLog.Append("whisper natives: " + (Stt.WhisperNetEngine.CpuHasAvx
                ? "AVX2+FMA present — the AVX build is used"
                : $"this CPU lacks AVX2/FMA — the no-AVX build in {SttPaths.NoAvxNativeDir} is used (slower; Parakeet is the better engine here)"));
        }
        catch (Exception ex) { ErrorLog.WriteEntry("ExtractWhisperNatives", ex); }
    }
}
