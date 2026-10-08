using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Chatterbox.Stt;

// App settings, one JSON file in the data folder. Property names are part of
// the on-disk format — existing files keep loading across releases.
public class SttSettings
{
    // The file lives in SttPaths.DataDir (stt_settings.json).

    public int InputDeviceIndex { get; set; } = 0;
    public string InputDeviceName { get; set; } = "";
    public string Engine { get; set; } = "parakeet";  // "parakeet" | "whisper"
    public string WhisperModel { get; set; } = "";    // ggml file name; "" = auto by hardware tier
    public int IntervalMs { get; set; } = 1000;       // VRChat's rate floor; clamped to >= 1000 on use
    public bool TypingIndicator { get; set; } = true;
    public bool NameBoost { get; set; } = false;      // experimental name recognition
    public int NewLineGapMs { get; set; } = 2300;     // + ~700 ms VAD ≈ 3 s pause
    public int ClearGapMs { get; set; } = 30_000;     // ≈ VRChat bubble fade

    // Auto-start: captions run while any listed player is in the instance.
    public bool AutoStartEnabled { get; set; } = false;
    public List<AutoFriend> AutoStartFriends { get; set; } = new();

    // Ask GitHub for a newer release once at startup (AppUpdater). Off by
    // default: without it the app never goes online unasked.
    public bool CheckUpdatesAtStartup { get; set; } = false;

    public class AutoFriend
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private static readonly JsonSerializerOptions OnDisk = new() { WriteIndented = true };

    // Tests point this at a temp file.
    internal static string? PathOverride { get; set; }

    // How the last Load() resolved — shown in last_boot.log for support.
    public static string LastLoadSource { get; private set; } = "not loaded";

    // Set by Load(): the file was simply absent (as opposed to unreadable).
    private static bool _lastLoadMissing;

    // PROVISIONAL DEFAULTS. A returning user whose settings file is missing
    // at boot is unusual (a sync or backup tool restoring the folder late,
    // a profile that is slow to mount). Rather than silently adopting
    // defaults — and burying the real file with the next save — the app
    // waits briefly, then runs with defaults marked provisional: nothing is
    // written to disk, and it keeps re-checking for a few minutes; the
    // moment the real file is readable it is reloaded, with any changes
    // made meanwhile kept on top.
    public static bool ProvisionalDefaults { get; private set; }
    public static int LastLoadWaitMs { get; private set; }
    public static event Action? Recovered;

    // "Has this user run the app before?" lives outside the data folder so
    // it stays answerable when the folder itself is what's invisible.
    // ~/.config/Chatterbox/last-run-version (XDG_CONFIG_HOME) — deliberately
    // outside the data folder.
    internal static string MarkerPath => Path.Combine(LinuxHost.ConfigHome, "Chatterbox", "last-run-version");
    internal static bool? HasRunBeforeOverride { get; set; }

    public static bool HasRunBefore()
    {
        if (HasRunBeforeOverride is bool o) return o;
        try { return File.Exists(MarkerPath); }
        catch { return false; }
    }

    public static void MarkHasRun(string version)
    {
        if (HasRunBeforeOverride != null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, version + Environment.NewLine);
        }
        catch { /* best effort */ }
    }

    private static string SettingsPath => PathOverride ?? Path.Combine(SttPaths.DataDir, "stt_settings.json");

    // Whether a settings file exists at all (the provisional-defaults logic
    // treats "ran before, no file" as a file that is momentarily invisible).
    public static bool FileExists => File.Exists(SettingsPath);

    // Load, waiting up to `budget` for a "missing" file to show up when this
    // user has run the app before (see ProvisionalDefaults). First runs
    // never wait.
    public static SttSettings LoadWithRetry(TimeSpan budget)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // "Ran before" only counts when the data folder itself exists: a
        // folder deleted to reset the app is a first run again, not a
        // momentarily unmounted disk.
        bool ranBefore = HasRunBefore() && Directory.Exists(Path.GetDirectoryName(SettingsPath)!);
        while (true)
        {
            var s = Load();
            if (!_lastLoadMissing || !ranBefore)
            {
                ProvisionalDefaults = false;
                LastLoadWaitMs = (int)sw.ElapsedMilliseconds;
                return s;
            }
            if (sw.Elapsed >= budget)
            {
                ProvisionalDefaults = true;
                LastLoadWaitMs = (int)sw.ElapsedMilliseconds;
                LastLoadSource = $"PROVISIONAL defaults — this user has run the app before, but the settings file was not visible for {sw.ElapsedMilliseconds} ms after start; still watching for it";
                ErrorLog.WriteNote("SttSettings.Load", LastLoadSource);
                return s;
            }
            Thread.Sleep(250);
        }
    }

    // While provisional: if the real file is readable now, adopt it — with
    // any fields changed since boot (anything that differs from a pristine
    // default) applied on top — and write it back only if something changed.
    public bool TryRecoverFromDisk()
    {
        if (!ProvisionalDefaults) return false;
        var path = SettingsPath;
        if (!File.Exists(path)) return false;
        SttSettings disk;
        try
        {
            disk = JsonSerializer.Deserialize<SttSettings>(File.ReadAllText(path), OnDisk)
                   ?? throw new InvalidDataException("settings file deserialized to null");
        }
        catch (Exception ex) { ErrorLog.WriteEntry("SttSettings.TryRecoverFromDisk", ex); return false; }

        var pristine = new SttSettings();
        bool changed = false;
        if (InputDeviceIndex != pristine.InputDeviceIndex || InputDeviceName != pristine.InputDeviceName)
        { disk.InputDeviceIndex = InputDeviceIndex; disk.InputDeviceName = InputDeviceName; changed = true; }
        if (Engine != pristine.Engine) { disk.Engine = Engine; changed = true; }
        if (WhisperModel != pristine.WhisperModel) { disk.WhisperModel = WhisperModel; changed = true; }
        if (IntervalMs != pristine.IntervalMs) { disk.IntervalMs = IntervalMs; changed = true; }
        if (TypingIndicator != pristine.TypingIndicator) { disk.TypingIndicator = TypingIndicator; changed = true; }
        if (NameBoost != pristine.NameBoost) { disk.NameBoost = NameBoost; changed = true; }
        if (NewLineGapMs != pristine.NewLineGapMs) { disk.NewLineGapMs = NewLineGapMs; changed = true; }
        if (ClearGapMs != pristine.ClearGapMs) { disk.ClearGapMs = ClearGapMs; changed = true; }
        if (AutoStartEnabled != pristine.AutoStartEnabled) { disk.AutoStartEnabled = AutoStartEnabled; changed = true; }
        foreach (var f in AutoStartFriends)
            if (!disk.AutoStartFriends.Any(d => d.Id == f.Id && d.Name == f.Name))
            { disk.AutoStartFriends.Add(f); changed = true; }

        CopyFrom(disk);
        ProvisionalDefaults = false;
        LastLoadSource = "settings file (recovered after boot — it was not visible at start)";
        ErrorLog.WriteNote("SttSettings",
            $"settings file became readable after boot; reloaded ({AutoStartFriends.Count} auto-start player(s))" +
            (changed ? ", kept the changes made meanwhile" : ""));
        if (changed) Save();
        Recovered?.Invoke();
        return true;
    }

    // Provisional state has lasted long enough to be trusted as real: the
    // file is genuinely gone (or never existed — a first run that changed
    // nothing). Write what we have, so the next start finds a file instead
    // of waiting again.
    public void GiveUpProvisional()
    {
        if (!ProvisionalDefaults) return;
        ProvisionalDefaults = false;
        LastLoadSource = "defaults — settings file never became visible after boot";
        Save();
    }

    private void CopyFrom(SttSettings o)
    {
        InputDeviceIndex = o.InputDeviceIndex; InputDeviceName = o.InputDeviceName;
        Engine = o.Engine; WhisperModel = o.WhisperModel; IntervalMs = o.IntervalMs;
        TypingIndicator = o.TypingIndicator; NameBoost = o.NameBoost;
        NewLineGapMs = o.NewLineGapMs; ClearGapMs = o.ClearGapMs;
        AutoStartEnabled = o.AutoStartEnabled; AutoStartFriends = o.AutoStartFriends;
    }

    internal static void ResetForTests()
    {
        ProvisionalDefaults = false; _lastLoadMissing = false; LastLoadWaitMs = 0;
        LastLoadSource = "not loaded"; HasRunBeforeOverride = null; Recovered = null;
    }

    public static SttSettings Load()
    {
        var path = SettingsPath;
        _lastLoadMissing = !File.Exists(path);
        if (_lastLoadMissing)
        {
            LastLoadSource = "defaults (no settings file yet)";
            return new SttSettings();
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<SttSettings>(File.ReadAllText(path), OnDisk)
                         ?? throw new InvalidDataException("settings file deserialized to null");
            LastLoadSource = "settings file";
            return loaded;
        }
        catch (Exception ex)
        {
            // A damaged file must never silently become the new truth (the
            // next Save would then persist empty defaults over the user's
            // auto-start list). Log it, keep a copy for diagnosis, and fall
            // back to the previous good save that Save() leaves as .bak.
            ErrorLog.WriteEntry("SttSettings.Load", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var restored = JsonSerializer.Deserialize<SttSettings>(File.ReadAllText(bak), OnDisk);
                    if (restored != null)
                    {
                        ErrorLog.WriteNote("SttSettings.Load",
                            "settings file was unreadable; restored the previous good copy (stt_settings.json.bak)");
                        LastLoadSource = "backup (stt_settings.json.bak) - the settings file was unreadable";
                        return restored;
                    }
                }
                catch (Exception ex2) { ErrorLog.WriteEntry("SttSettings.Load(backup)", ex2); }
            }
            LastLoadSource = "defaults - the settings file was unreadable and no backup exists";
            return new SttSettings();
        }
    }

    // Atomic: the content lands in a temp file, then File.Replace swaps it
    // in and keeps the previous file as .bak. A process kill mid-write
    // (a launcher may stop us with SIGKILL) can therefore
    // never truncate the live file, and Load() always has a fallback.
    // The last failure's reason ("" after a successful save) — a full disk
    // or a read-only data folder must not be a silent "Saved".
    public static string LastSaveError { get; private set; } = "";

    public bool Save()
    {
        // Provisional defaults are never written: the real file may be
        // sitting right there, momentarily invisible. Recovery merges and
        // flushes; GiveUpProvisional writes what we have if it never shows up.
        if (ProvisionalDefaults)
        {
            TryRecoverFromDisk();
            return true;
        }
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, OnDisk));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
            LastSaveError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            ErrorLog.WriteEntry("SttSettings.Save", ex);
            return false;
        }
    }
}
