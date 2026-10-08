using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Chatterbox.Stt;

// Microphones on Linux: PipeWire's capture nodes (pw-dump), or PulseAudio's
// sources (pactl) on a machine still running PulseAudio. Entry 0 is always
// the system default input, which needs no device name at all. A choice is
// remembered as index + name and re-resolved every time it's used, because
// the list reorders whenever hardware comes or goes — the name (PipeWire's
// node.description, or the node.name from pactl) identifies the device;
// its node.name is what the recorder gets as a target.
public static class SttAudioDevices
{
    public const string DefaultName = "System default input";

    public sealed record InputDevice(string Name, string Target);
    public sealed record CaptureCommand(string Label, string File, string[] Args);

    // Test hook (--capture-command): one shell command that writes raw
    // 16 kHz mono s16le PCM to stdout stands in for every recorder, so a
    // soak test can feed hours of speech without a microphone (tools/soak.sh).
    public static string? CaptureCommandOverride { get; set; }

    private static readonly object Gate = new();
    private static InputDevice[]? _cache;
    private static long _cacheAt;
    private const int CacheMs = 3000;

    // Cached briefly: the UI asks for the list several times around boot,
    // and each answer is a child process.
    public static InputDevice[] GetInputs()
    {
        lock (Gate)
        {
            if (_cache != null && Environment.TickCount64 - _cacheAt < CacheMs) return _cache;
            var list = new List<InputDevice> { new(DefaultName, "") };
            try { list.AddRange(Enumerate()); } catch { }
            _cache = list.ToArray();
            _cacheAt = Environment.TickCount64;
            return _cache;
        }
    }

    public static string[] GetInputNames() => GetInputs().Select(d => d.Name).ToArray();

    public static string InputNameAt(int index)
    {
        var names = GetInputNames();
        return index >= 0 && index < names.Length ? names[index] : "";
    }

    public static int ResolveInput(int savedIndex, string? savedName) =>
        ResolveAmong(GetInputNames(), savedIndex, savedName);

    // The recorder target for a resolved index; null for the default input
    // (index 0, or anything out of range).
    public static string? TargetFor(int index)
    {
        var devices = GetInputs();
        return index > 0 && index < devices.Length && devices[index].Target.Length > 0 ? devices[index].Target : null;
    }

    // Single scored pass: an exact name match wins outright; failing that,
    // the best prefix match (a saved name may be longer than what a tool
    // reports). With no name saved, a still-in-range index is honored;
    // every dead end resolves to the default input (-1).
    internal static int ResolveAmong(string[] names, int savedIndex, string? savedName)
    {
        if (string.IsNullOrEmpty(savedName))
            return savedIndex >= 0 && savedIndex < names.Length ? savedIndex : -1;

        int best = -1, bestRank = 0;
        for (int device = 0; device < names.Length; device++)
        {
            int rank = Rank(names[device], savedName);
            if (rank <= bestRank) continue;
            best = device;
            bestRank = rank;
            if (rank == 2) break;
        }
        return best;
    }

    private static int Rank(string reported, string saved)
    {
        if (reported.Length == 0) return 0;
        if (string.Equals(reported, saved, StringComparison.Ordinal)) return 2;
        return saved.StartsWith(reported, StringComparison.Ordinal) ? 1 : 0;
    }

    private static IEnumerable<InputDevice> Enumerate()
    {
        if (!OperatingSystem.IsLinux()) return Array.Empty<InputDevice>();
        var dump = LinuxHost.Capture("pw-dump", Array.Empty<string>(), 5000);
        if (dump.Length > 0)
        {
            var fromPipeWire = ParsePwDump(dump);
            if (fromPipeWire.Count > 0) return fromPipeWire;
        }
        return ParsePactlShort(LinuxHost.Capture("pactl", new[] { "list", "short", "sources" }, 5000));
    }

    // pw-dump: one JSON array of every PipeWire object. Capture devices are
    // nodes whose media.class is Audio/Source (virtual ones included — an
    // echo-cancelled or noise-suppressed mic is exactly what people pick).
    internal static List<InputDevice> ParsePwDump(string json)
    {
        var list = new List<InputDevice>();
        JArray objects;
        try { objects = JArray.Parse(json); }
        catch { return list; }
        foreach (var obj in objects)
        {
            if (obj["type"]?.ToString() != "PipeWire:Interface:Node") continue;
            var props = obj["info"]?["props"];
            if (props == null) continue;
            var mediaClass = props["media.class"]?.ToString() ?? "";
            if (mediaClass != "Audio/Source" && mediaClass != "Audio/Source/Virtual") continue;
            var name = props["node.name"]?.ToString() ?? "";
            if (name.Length == 0 || name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
            var label = props["node.description"]?.ToString();
            if (string.IsNullOrWhiteSpace(label)) label = props["node.nick"]?.ToString();
            if (string.IsNullOrWhiteSpace(label)) label = name;
            if (list.Any(d => d.Target == name)) continue;
            list.Add(new InputDevice(label.Trim(), name));
        }
        return list;
    }

    // pactl list short sources: "<id>\t<name>\t<driver>\t<format>\t<state>".
    // Sink monitors are sources too and are not microphones.
    internal static List<InputDevice> ParsePactlShort(string text)
    {
        var list = new List<InputDevice>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            var name = parts[1].Trim();
            if (name.Length == 0 || name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
            if (list.Any(d => d.Target == name)) continue;
            list.Add(new InputDevice(name, name));
        }
        return list;
    }

    // Recorder command lines, most likely first. All produce raw 16 kHz
    // mono s16le on stdout. ALSA's device names are a different namespace,
    // so arecord only ever records the default device.
    public static IEnumerable<CaptureCommand> CaptureCommands(string? target)
    {
        if (!string.IsNullOrEmpty(CaptureCommandOverride))
        {
            yield return new CaptureCommand("custom", "/bin/sh", new[] { "-c", CaptureCommandOverride });
            yield break;
        }
        var pw = new List<string> { "--raw", "--format=s16", "--rate=16000", "--channels=1", "--latency=100ms" };
        if (target != null) pw.Add("--target=" + target);
        pw.Add("-");
        yield return new CaptureCommand("pw-record", "pw-record", pw.ToArray());

        var pa = new List<string> { "--raw", "--format=s16le", "--rate=16000", "--channels=1", "--latency-msec=100" };
        if (target != null) pa.Add("--device=" + target);
        yield return new CaptureCommand("parec", "parec", pa.ToArray());

        yield return new CaptureCommand("arecord", "arecord",
            new[] { "-q", "-t", "raw", "-f", "S16_LE", "-r", "16000", "-c", "1", "-" });
    }
}
