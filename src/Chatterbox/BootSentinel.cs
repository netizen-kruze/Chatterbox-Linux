using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Chatterbox.Stt;

namespace Chatterbox;

// <data dir>/boot.inprogress exists only while a start is under way:
// written before the risky work (native extraction, engine loads, window
// creation) and deleted the moment the page connects. Finding it at the
// next start means the previous start never reached the window — it
// crashed, or was killed — so that start gets the system's crash record
// copied into error.log (CrashRecord) and a safe boot: no automatic
// captions until a human presses Start. A crash can no longer be silent,
// and cannot loop.
public static class BootSentinel
{
    // Tests point this at a temp file so they never touch the real marker.
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(SttPaths.DataDir, "boot.inprogress");

    public sealed record Unfinished(string Version, DateTime StartedAt, int Pid);

    // Records this start; returns the previous start if it never finished.
    public static Unfinished? Arm(string version)
    {
        Unfinished? previous = null;
        try
        {
            if (File.Exists(FilePath)) previous = Parse(File.ReadAllText(FilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{version}|{DateTime.Now:o}|{Environment.ProcessId}");
        }
        catch { /* diagnostics must never affect startup */ }
        return previous;
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    // A marker with unreadable contents still means an unfinished start.
    internal static Unfinished Parse(string text)
    {
        var parts = (text ?? "").Trim().Split('|');
        if (parts.Length != 3) return new Unfinished("?", DateTime.MinValue, 0);
        DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
        int.TryParse(parts[2], out var pid);
        return new Unfinished(parts[0], at, pid);
    }
}

// systemd keeps the only record of a native crash: coredumpctl lists the
// core dump (time, signal, executable) and the journal holds the lines
// written around it. After an unfinished start, the entries naming this
// app are copied into error.log, where a user finds them without knowing
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
                string who = $"start of {previous.Version} at {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) never reached the window";
                ErrorLog.WriteNote("PreviousStart", lines.Count == 0
                    ? who + "; no crash record found in coredumpctl or the journal — it was probably closed or killed " +
                      "before the window loaded (or this account cannot read the system journal)"
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
