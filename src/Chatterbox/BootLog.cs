using System;
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
            File.WriteAllLines(Path.Combine(Dir, "last_boot.log"), lines);
        }
        catch { /* diagnostics must never affect startup */ }
    }

    public static void Append(string line)
    {
        try { File.AppendAllText(Path.Combine(Dir, "last_boot.log"), $"{DateTime.Now:HH:mm:ss} {line}" + Environment.NewLine); }
        catch { }
    }
}
