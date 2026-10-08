using System;
using System.IO;
using Chatterbox.Stt;

namespace Chatterbox;

// Append-only error journal at <data dir>\error.log — the place to look
// when something misbehaves with no window open. Rotates once past
// MaxBytes (the previous journal is kept as error.log.1), so a chatty
// failure can never fill a disk or make the file unreadable.
public static class ErrorLog
{
    public const long MaxBytes = 2 * 1024 * 1024;

    // Tests point this at a temp file so they never touch the real log.
    internal static string? PathOverride { get; set; }

    private static readonly object Gate = new();

    private static string LogPath => PathOverride ?? Path.Combine(SttPaths.DataDir, "error.log");

    public static void WriteEntry(string source, Exception ex) => Append($"{source}: {ex}");

    // Notable non-exception events (a recovery, a fallback) — same journal,
    // so the story reads in order.
    public static void WriteNote(string source, string message) => Append($"{source}: {message}");

    private static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}" + Environment.NewLine);
            }
        }
        catch { /* error logging must never throw */ }
    }
}
