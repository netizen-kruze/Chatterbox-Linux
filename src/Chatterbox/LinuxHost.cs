using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Chatterbox;

// Everything the app needs from the Linux desktop in one place: XDG
// folders, a modal message before the window exists, the libraries the
// WebKitGTK window needs, child processes without Steam's overlay preload,
// and process liveness by name. Every helper is best-effort and never
// throws — a missing tool degrades a message, not the app.
public static class LinuxHost
{
    // ── XDG base directories ───────────────────────────────────────

    public static string Home
    {
        get
        {
            var h = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // No HOME and no passwd entry (a stripped launcher environment):
            // anything is better than paths relative to a working directory
            // we do not control.
            return string.IsNullOrWhiteSpace(h) ? Path.Combine(Path.GetTempPath(), "chatterbox-home") : h;
        }
    }

    // ~/.local/share unless XDG_DATA_HOME says otherwise: settings, logs,
    // models — the one data folder.
    public static string DataHome => Env("XDG_DATA_HOME") ?? Path.Combine(Home, ".local", "share");

    // ~/.config: only the tiny "has run before" marker lives here, outside
    // the data folder on purpose (see SttSettings.HasRunBefore).
    public static string ConfigHome => Env("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static bool HasDisplay =>
        Env("WAYLAND_DISPLAY") != null || Env("DISPLAY") != null;

    // ── environment ────────────────────────────────────────────────

    // On Unix, .NET keeps its own copy of the environment: SetEnvironmentVariable
    // changes what managed code and Process.Start children see, but not the
    // C environ that native code (WebKitGTK, and the web process it spawns)
    // reads with getenv. A variable meant for native code must go both ways.
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    public static void SetProcessEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsLinux()) return;
        try { setenv(name, value, 1); } catch { }
    }

    // ── window prerequisites ───────────────────────────────────────

    // What Photino.Native links or opens at runtime, with the Fedora
    // package that provides each. WebKitGTK is opened lazily by name, so a
    // missing one would otherwise surface as a crash inside the window
    // constructor instead of a sentence.
    public static readonly (string Library, string Package)[] UiDependencies =
    {
        ("libgtk-3.so.0", "gtk3"),
        ("libnotify.so.4", "libnotify"),
        ("libwebkit2gtk-4.1.so.0", "webkit2gtk4.1"),
    };

    public static List<(string Library, string Package)> MissingUiDependencies()
    {
        var missing = new List<(string, string)>();
        if (!OperatingSystem.IsLinux()) return missing;
        foreach (var dep in UiDependencies)
            if (!LibraryPresent(dep.Library)) missing.Add(dep);
        return missing;
    }

    // Is a shared library installed? Answered from the loader's cache
    // (ldconfig -p) and the usual library folders — never by loading it.
    // Loading GTK, WebKitGTK or the CUDA driver just to look, and then
    // unloading it again, left the process crashing in the window
    // constructor: those libraries cannot be unloaded safely.
    public static bool LibraryPresent(string soname) =>
        OperatingSystem.IsLinux() && KnownLibraries.Value.ContainsKey(soname);

    // Where an installed library lives (the loader's answer first, then the
    // scanned folders); null when it is not installed. Lets a library be
    // loaded by full path — the only way to be sure the copy found is the
    // copy used.
    public static string? LibraryPath(string soname) =>
        OperatingSystem.IsLinux() && KnownLibraries.Value.TryGetValue(soname, out var path) ? path : null;

    private static readonly Lazy<Dictionary<string, string>> KnownLibraries = new(() =>
    {
        var libs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var cache = Capture("ldconfig", new[] { "-p" });
            if (cache.Length == 0) cache = Capture("/sbin/ldconfig", new[] { "-p" });
            foreach (var (name, path) in ParseLdconfig(cache)) libs.TryAdd(name, path);
        }
        catch { }
        var dirs = new List<string>
        {
            "/usr/lib64", "/usr/lib", "/usr/lib/x86_64-linux-gnu", "/usr/local/lib64", "/usr/local/lib",
            "/usr/lib/wsl/lib", "/usr/local/cuda/lib64", "/usr/local/cuda/targets/x86_64-linux/lib",
        };
        var ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(ldPath)) dirs.AddRange(ldPath.Split(':', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.so*")) libs.TryAdd(Path.GetFileName(file), file);
            }
            catch { }
        }
        return libs;
    });

    // "\tlibgtk-3.so.0 (libc6,x86-64) => /usr/lib64/libgtk-3.so.0"
    //   -> ("libgtk-3.so.0", "/usr/lib64/libgtk-3.so.0")
    internal static IEnumerable<(string Name, string Path)> ParseLdconfig(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) continue; // entries are indented, headers are not
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (space <= 0 || arrow < 0) continue;
            yield return (line[..space], line[(arrow + 2)..].Trim());
        }
    }

    // ── messages without a window ──────────────────────────────────

    // A modal notice for the moments before (or instead of) the window:
    // zenity on GNOME, kdialog on KDE, a desktop notification, and always
    // stderr — whichever exists first. zenity/kdialog block until dismissed.
    public static void Alert(string title, string text, bool error = true)
    {
        Console.Error.WriteLine($"{title}: {text}");
        if (!OperatingSystem.IsLinux() || !HasDisplay) return;
        var attempts = new (string File, string[] Args)[]
        {
            ("zenity", new[] { error ? "--error" : "--info", "--no-markup", "--title=" + title, "--text=" + text, "--width=460" }),
            ("kdialog", new[] { error ? "--error" : "--msgbox", text, "--title", title }),
            ("notify-send", new[] { "-a", "Chatterbox", title, text }),
        };
        foreach (var a in attempts)
            if (TryRun(a.File, a.Args, 300_000)) return;
    }

    // A desktop notification and nothing else — for moments that deserve a
    // word but not a modal (a second launch).
    public static void Notify(string title, string text)
    {
        if (!OperatingSystem.IsLinux() || !HasDisplay) return;
        TryRun("notify-send", new[] { "-a", "Chatterbox", title, text }, 3000);
    }

    private static bool TryRun(string file, string[] args, int waitMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            StripSteamPreload(psi);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(waitMs);
            return true;
        }
        catch { return false; } // not installed
    }

    // ── child processes ────────────────────────────────────────────

    // Runs a tool and returns its stdout, "" when it is missing, fails or
    // exceeds the timeout. For the machine profile, device lists and the
    // crash record — informational commands only.
    public static string Capture(string file, string[] args, int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            StripSteamPreload(psi);
            using var p = Process.Start(psi);
            if (p == null) return "";
            p.BeginErrorReadLine();            // drained and dropped
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            return stdout.Wait(timeoutMs) ? stdout.Result : "";
        }
        catch { return ""; }
    }

    // Steam runs launch-option commands with its overlay in LD_PRELOAD.
    // Helper processes (dialogs, pw-record, lspci) must not inherit it: the
    // overlay hooks graphics symbols and has no business in a recorder.
    public static void StripSteamPreload(ProcessStartInfo psi)
    {
        if (psi.Environment.TryGetValue("LD_PRELOAD", out var value) &&
            value != null && value.Contains("gameoverlayrenderer", StringComparison.Ordinal))
            psi.Environment.Remove("LD_PRELOAD");
        // Steam's client runs inside its 2012-era "scout" runtime and passes
        // that LD_LIBRARY_PATH on; a Fedora GTK/WebKitGTK/PipeWire process
        // must not pick those libraries over the system's. Steam keeps the
        // original value in SYSTEM_LD_LIBRARY_PATH.
        if (psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var libPath) && IsSteamRuntimeLibraryPath(libPath))
        {
            if (psi.Environment.TryGetValue("SYSTEM_LD_LIBRARY_PATH", out var system) && !string.IsNullOrEmpty(system))
                psi.Environment["LD_LIBRARY_PATH"] = system;
            else
                psi.Environment.Remove("LD_LIBRARY_PATH");
        }
    }

    internal static bool IsSteamRuntimeLibraryPath(string? value) =>
        value != null && (value.Contains("steam-runtime", StringComparison.Ordinal) ||
                          value.Contains("/ubuntu12_32/", StringComparison.Ordinal) ||
                          value.Contains("/ubuntu12_64/", StringComparison.Ordinal));

    // The overlay preload and library path we were started with (wrapper
    // mode), handed back to the game so its Steam overlay keeps working.
    public const string GamePreloadVar = "CHATTERBOX_GAME_LD_PRELOAD";
    public const string GameLibraryPathVar = "CHATTERBOX_GAME_LD_LIBRARY_PATH";

    // Launched by Steam (launch options "…/Chatterbox %command%"), this
    // process carries gameoverlayrenderer.so in LD_PRELOAD — a preload that
    // is known to hang or blank GTK/WebKit programs. Re-run ourselves once
    // without it, remembering the value for the game, and mirror the
    // child's exit code so Steam's bookkeeping sees one process lifetime.
    // Returns null when no re-exec was needed or it could not be done.
    public static int? ReexecWithoutSteamPreload(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var preload = Environment.GetEnvironmentVariable("LD_PRELOAD");
        var libPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        bool overlay = !string.IsNullOrEmpty(preload) && preload.Contains("gameoverlayrenderer", StringComparison.Ordinal);
        bool scout = IsSteamRuntimeLibraryPath(libPath);
        if (!overlay && !scout) return null;
        if (Environment.GetEnvironmentVariable(GamePreloadVar) != null ||
            Environment.GetEnvironmentVariable(GameLibraryPathVar) != null) return null; // already the re-run
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (overlay)
            {
                psi.Environment.Remove("LD_PRELOAD");
                psi.Environment[GamePreloadVar] = preload!;
            }
            if (scout)
            {
                psi.Environment[GameLibraryPathVar] = libPath!;
                StripSteamPreload(psi); // swaps in SYSTEM_LD_LIBRARY_PATH or drops it
            }
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return null; }
    }

    // ── single instance ────────────────────────────────────────────

    // Holds an exclusive lock on a file in the data folder for the life of
    // the process (per user, across login sessions and launchers). Returns
    // null when another instance already holds it; on any other failure the
    // app runs unguarded — better two windows than none.
    public static IDisposable? TryLockInstance(string path)
    {
        // A folder that cannot take a file at all (read-only, full disk)
        // must not read as "already running": probed with a file of its
        // own, NOT by opening the lock file — on Unix every FileStream
        // takes a flock (shared unless FileShare.None), so even a
        // "no-lock" open of the lock file fails while the running
        // instance holds it exclusively. That failure runs the app
        // unguarded and is logged.
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".chatterbox-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) { ErrorLog.WriteEntry("SingleInstance", ex); return new MemoryStream(); }
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { stream.SetLength(0); stream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString())); stream.Flush(); } catch { }
            return stream;
        }
        catch (IOException) { return null; }           // locked by the running instance
        catch (Exception ex) { ErrorLog.WriteEntry("SingleInstance", ex); return new MemoryStream(); }
    }

    // ── process liveness ───────────────────────────────────────────

    // Is a process with one of these names running? Read from /proc
    // directly: cheap enough for a one-second poll, and it sees Wine
    // processes by their exe name (VRChat under Proton is "VRChat.exe").
    public static bool ProcessExists(params string[] names)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var pid = Path.GetFileName(dir);
                if (pid.Length == 0 || !char.IsAsciiDigit(pid[0])) continue;
                string comm;
                try { comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim(); }
                catch { continue; } // exited between the listing and the read
                foreach (var n in names)
                    if (string.Equals(comm, n, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    // The game under Proton shows as "VRChat.exe" (a native build, should
    // one ever exist, as "VRChat"). Linux truncates names to 15 bytes; both
    // fit.
    public static readonly string[] VrchatProcessNames = { "VRChat.exe", "VRChat" };

    internal static bool IsVrchatComm(string comm) =>
        VrchatProcessNames.Any(n => string.Equals(comm.Trim(), n, StringComparison.OrdinalIgnoreCase));
}
