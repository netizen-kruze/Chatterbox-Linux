using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Chatterbox.Stt;

namespace Chatterbox;

// <data dir>/boot.inprogress exists for the whole life of a run: written
// before the risky work (native extraction, engine loads, window creation)
// with the phase "boot", moved to "window" when the page connects, to
// "captions" while a session runs and back, and deleted only by a clean
// exit. Finding it at the next start means the previous run ended without
// one — it crashed, or was killed — and the phase says when: a start that
// never reached the window, an idle window, or captions in progress (the
// README's own example: a Whisper pass on a GPU that takes the process
// down). That start gets the system's crash record copied into error.log
// (CrashRecord) and, unless the window was merely idle, a safe boot: no
// automatic captions until a human presses Start. A crash can no longer
// be silent, and cannot loop.
public static class BootSentinel
{
    public const string PhaseBoot = "boot", PhaseWindow = "window", PhaseCaptions = "captions";

    // Tests point this at a temp file so they never touch the real marker.
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(SttPaths.DataDir, "boot.inprogress");
    private static readonly object Gate = new();
    private static string? _version;    // set by Arm: this process owns the marker
    private static string _startedAt = "";

    public sealed record Unfinished(string Version, DateTime StartedAt, int Pid, string Phase)
    {
        // "never reached the window" / "ended while captions were running" / …
        public string How => Phase switch
        {
            PhaseCaptions => "ended while captions were running",
            PhaseWindow => "ended without a clean exit while idle",
            _ => "never reached the window",
        };
        // An idle window that was killed is nothing to guard against; the
        // other two are the crash loops the safe boot exists for.
        public bool WantsSafeBoot => Phase != PhaseWindow;
    }

    // Records this start; returns the previous run if it never exited cleanly.
    public static Unfinished? Arm(string version)
    {
        Unfinished? previous = null;
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) previous = Parse(File.ReadAllText(FilePath));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                _version = version;
                _startedAt = DateTime.Now.ToString("o");
                File.WriteAllText(FilePath, Line(PhaseBoot));
            }
            catch { /* diagnostics must never affect startup */ }
        }
        return previous;
    }

    // The run moved on (page connected, captions started or stopped).
    public static void Mark(string phase)
    {
        lock (Gate)
        {
            if (_version == null) return;   // not armed in this process (--bench, tests)
            try { File.WriteAllText(FilePath, Line(phase)); } catch { }
        }
    }

    // A clean exit. Only this process's own marker is removed: a successor
    // started by an in-app restart has armed its own by now. It is also
    // final for this process: the controller is disposed after Main's Clear
    // and stops a running session there, and that Stop must not write the
    // marker back (the next start would report a crash that never was).
    public static void Clear()
    {
        lock (Gate)
        {
            _version = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var owner = Parse(File.ReadAllText(FilePath));
                if (owner.Pid == 0 || owner.Pid == Environment.ProcessId) File.Delete(FilePath);
            }
            catch { try { File.Delete(FilePath); } catch { } }
        }
    }

    private static string Line(string phase) => $"{_version}|{_startedAt}|{Environment.ProcessId}|{phase}";

    // A marker with unreadable contents still means an unfinished start.
    // Three fields is the format before 1.7.2 (no phase: a start).
    internal static Unfinished Parse(string text)
    {
        var parts = (text ?? "").Trim().Split('|');
        if (parts.Length is not (3 or 4)) return new Unfinished("?", DateTime.MinValue, 0, PhaseBoot);
        DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
        int.TryParse(parts[2], out var pid);
        var phase = parts.Length == 4 && parts[3] is PhaseWindow or PhaseCaptions ? parts[3] : PhaseBoot;
        return new Unfinished(parts[0], at, pid, phase);
    }

    // Tests: forget that this process armed anything.
    internal static void ResetForTests() { lock (Gate) { _version = null; _startedAt = ""; } }
}

// systemd keeps the only record of a native crash: coredumpctl lists the
// core dump (time, signal, executable) and the journal holds the lines
// written around it. After an unfinished run, the entries naming this app
// are copied into error.log, where a user finds them without knowing
// either tool exists. Best effort: on a machine where this user may not
// read the system journal, the note says so.
public static class CrashRecord
{
    public static void CollectInBackground(BootSentinel.Unfinished previous)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var since = previous.StartedAt == DateTime.MinValue
                    ? DateTime.Now.AddDays(-1)
                    : previous.StartedAt.AddMinutes(-1);
                var lines = FilterLines(Query(since), "Chatterbox");
                string who = $"run of {previous.Version} started {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) {previous.How}";
                ErrorLog.WriteNote("PreviousStart", lines.Count == 0
                    ? who + "; no crash record found in coredumpctl or the journal — it was probably killed " +
                      "(a task manager, Steam, a logout) or the power went (or this account cannot read the system journal)"
                    : who + "; crash record (coredumpctl / journalctl):" + Environment.NewLine +
                      string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex) { ErrorLog.WriteEntry("CrashRecord", ex); }
        });
    }

    private static string Query(DateTime since)
    {
        if (!OperatingSystem.IsLinux()) return "";
        var stamp = since.ToString("yyyy-MM-dd HH:mm:ss");
        var sb = new StringBuilder();
        // coredumpctl matches the process name exactly, and the kernel keeps
        // 15 bytes of it: "Chatterbox-1.5.1-linux-x64" is "Chatterbox-1.5.".
        sb.Append(LinuxHost.Capture("coredumpctl",
            new[] { "list", "--no-pager", "--no-legend", "--since=" + stamp, ProcessComm() }, 10_000));
        sb.Append('\n');
        sb.Append(LinuxHost.Capture("journalctl",
            new[] { "--no-pager", "-q", "-o", "short-iso", "--since=" + stamp, "--grep=Chatterbox" }, 10_000));
        return sb.ToString();
    }

    internal static string ProcessComm(string? processPath = null)
    {
        var name = Path.GetFileName(processPath ?? Environment.ProcessPath ?? "Chatterbox");
        if (name.Length == 0) name = "Chatterbox";
        return name.Length > 15 ? name[..15] : name;
    }

    // Keeps the lines that name the app, trimmed and capped — a tool's own
    // chatter ("No coredumps found", permission hints) never qualifies.
    internal static List<string> FilterLines(string text, string appName, int maxLines = 40)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.Contains(appName, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add("    " + line);
            if (result.Count >= maxLines) break;
        }
        return result;
    }
}
