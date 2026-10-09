using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// Snapshot of the last launch, overwritten every boot:
// <data dir>\last_boot.log. For "it started but looked wrong" reports —
// records what the app actually saw at startup (the machine, which
// settings file it read, how many auto-start players, which mic, whether
// VRChat was already running and what its log held) so the cause is
// visible without a debugger.
// Later events of note (a settings recovery, a pace change, a speed check)
// are appended.
public static class BootLog
{
    private static string Dir => SttPaths.DataDir;
    private static readonly object Gate = new();
    // Lines appended before Write — the natives being moved, the whisper
    // loader's say, the presence watcher's catch-up — used to vanish when
    // Write replaced the file. They are kept here and written after the
    // header (a process that never calls Write, --bench, appends to the
    // previous boot's file as before).
    private static List<string>? _early = new();

    public static void Write(string version, string[] args, SttSettings settings, bool gameRunning, string machine, string vrchatLogDir, string vrchatLog, string runtimeRoot)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            int entries = -1;
            try { entries = Directory.EnumerateFileSystemEntries(Dir).Count(); } catch { }
            var lines = new[]
            {
                $"started:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"version:        {version}",
                $"machine:        {machine}",
                $"exe:            {Environment.ProcessPath}",
                $"args:           {(args.Length == 0 ? "(none)" : string.Join(" ", args))}",
                $"run before:     {(SttSettings.HasRunBefore() ? "yes" : "no")} (marker file)",
                $"data folder:    {Dir} — {(entries < 0 ? "NOT listable" : entries + " entries visible")} at boot",
                $"settings from:  {SttSettings.LastLoadSource}" + (SttSettings.LastLoadWaitMs > 300 ? $" (waited {SttSettings.LastLoadWaitMs} ms)" : ""),
                $"auto-start:     {(settings.AutoStartEnabled ? "on" : "off")}, {settings.AutoStartFriends.Count} player(s)",
                $"engine:         {settings.Engine}",
                $"mic:            index {settings.InputDeviceIndex} '{settings.InputDeviceName}' ({SttAudioDevices.GetInputNames().Length} input device(s) present)",
                $"vrchat running: {gameRunning}",
                $"vrchat log dir: {vrchatLogDir} — {(Directory.Exists(vrchatLogDir) ? "found" : "NOT found")}",
                $"vrchat log:     {vrchatLog}",
                $"runtimes:       {Path.Combine(runtimeRoot, "runtimes")}",
            };
            lock (Gate)
            {
                File.WriteAllLines(Path.Combine(Dir, "last_boot.log"), lines.Concat(_early ?? Enumerable.Empty<string>()));
                _early = null;
            }
        }
        catch { /* diagnostics must never affect startup */ }
    }

    public static void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        try
        {
            lock (Gate)
            {
                _early?.Add(stamped);
                File.AppendAllText(Path.Combine(Dir, "last_boot.log"), stamped + Environment.NewLine);
            }
        }
        catch { }
    }
}
