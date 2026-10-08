using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Chatterbox;

// Where VRChat writes its output log on Linux: inside the game's Proton
// prefix, which lives in whichever Steam library holds the game —
// <library>/steamapps/compatdata/438100/pfx/drive_c/users/steamuser/
// AppData/LocalLow/VRChat/VRChat. Steam itself may be a native install,
// a Flatpak or a Snap, and games may sit in extra libraries listed in
// libraryfolders.vdf; every combination is tried, first existing wins.
public static class VrchatLogLocator
{
    public const string SteamAppId = "438100";

    private static readonly string PrefixTail = Path.Combine(
        "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow", "VRChat", "VRChat");

    // The first candidate that exists; otherwise the first candidate, so the
    // boot log names where the app looked.
    public static string Find()
    {
        string? first = null;
        foreach (var dir in Candidates())
        {
            first ??= dir;
            try { if (Directory.Exists(dir)) return dir; } catch { }
        }
        return first ?? Path.Combine(LinuxHost.DataHome, "Steam", "steamapps", "compatdata", SteamAppId, PrefixTail);
    }

    public static IEnumerable<string> Candidates()
    {
        var home = LinuxHost.Home;
        var roots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            bool rootExists;
            try { rootExists = Directory.Exists(root); } catch { rootExists = false; }
            var libraries = new List<string> { root };
            if (rootExists)
            {
                foreach (var vdf in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
                {
                    try { if (File.Exists(vdf)) libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf))); }
                    catch { }
                }
            }
            foreach (var lib in libraries)
            {
                var dir = Path.Combine(lib, "steamapps", "compatdata", SteamAppId, PrefixTail);
                if (seen.Add(dir)) yield return dir;
            }
        }
    }

    // Steam's KeyValues text: every  "path"  "/some/where"  entry is a
    // library root (backslash escapes undone).
    internal static List<string> ParseLibraryFolders(string vdfText)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(vdfText ?? "", "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var path = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            if (path.Length > 0 && !list.Contains(path)) list.Add(path);
        }
        return list;
    }
}
