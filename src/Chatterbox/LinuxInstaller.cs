using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Chatterbox;

// The single binary installs itself — no package, no root, no script: a
// copy of the running file goes to ~/.local/share/Chatterbox/app, an
// app-grid entry to ~/.local/share/applications, the icon into the hicolor
// theme. Reached as "Chatterbox --install" and from Settings. Uninstall
// undoes exactly that; --purge also removes the data folder and the
// has-run marker.
public static class LinuxInstaller
{
    // Tests point these at temp folders so an install/uninstall round
    // trip never touches the real home; null = the XDG folders.
    internal static string? DataHomeOverride;
    internal static string? ConfigHomeOverride;
    internal static string? BundleExtractBaseOverride;
    private static string DataHome => DataHomeOverride ?? LinuxHost.DataHome;
    private static string ConfigHome => ConfigHomeOverride ?? LinuxHost.ConfigHome;

    public static string AppDir => Path.Combine(DataHome, "Chatterbox", "app");
    public static string InstalledBinary => Path.Combine(AppDir, "Chatterbox");
    public static string DesktopFile => Path.Combine(DataHome, "applications", "chatterbox.desktop");
    private static string IconRoot => Path.Combine(DataHome, "icons", "hicolor");
    private static string SvgIcon => Path.Combine(IconRoot, "scalable", "apps", "chatterbox.svg");
    private static string PngIcon => Path.Combine(IconRoot, "32x32", "apps", "chatterbox.png");

    public sealed record Result(bool Ok, string Message);

    // The installed binary's path when the app-grid entry and the binary
    // both exist; null otherwise.
    public static string? InstalledAt()
    {
        try { return File.Exists(DesktopFile) && File.Exists(InstalledBinary) ? InstalledBinary : null; }
        catch { return null; }
    }

    public static bool RunningFromAppDir
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
                return dir != null && Path.GetFullPath(dir) == Path.GetFullPath(AppDir);
            }
            catch { return false; }
        }
    }

    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static Result Install()
    {
        if (!OperatingSystem.IsLinux()) return new Result(false, "the app-grid install is only available on Linux");
        try
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                return new Result(false, "cannot find the running binary to copy");
            Directory.CreateDirectory(AppDir);
            if (!RunningFromAppDir)
            {
                // Write beside, then rename over: a running binary can't be
                // opened for writing, but its directory entry can be replaced
                // — an update while the old copy is still running just works.
                var tmp = InstalledBinary + ".tmp";
                File.Copy(source, tmp, overwrite: true);
                File.SetUnixFileMode(tmp, Executable);
                File.Move(tmp, InstalledBinary, overwrite: true);
            }
            else
            {
                File.SetUnixFileMode(InstalledBinary, Executable);
            }

            WriteResource("install/chatterbox.svg", SvgIcon);
            WriteResource("ui/app.png", PngIcon);
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, DesktopEntry(InstalledBinary));
            LinuxHost.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFile)! });
            LinuxHost.Capture("gtk-update-icon-cache", new[] { "-q", "-t", "-f", "--ignore-theme-index", IconRoot });
            return new Result(true,
                $"Chatterbox is in your app grid (installed to {InstalledBinary}). " +
                (SteamIsFlatpakOnly()
                    ? "Your Steam is the Flatpak, whose sandbox cannot start programs from your home folder — " +
                      "start Chatterbox from the app grid instead of VRChat's launch options."
                    : $"For VRChat's Steam launch options: {InstalledBinary} %command%"));
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("LinuxInstaller.Install", ex);
            return new Result(false, "install failed: " + ex.Message);
        }
    }

    // Everything except the app folder: the grid entry, the icons (and the
    // icon cache that listed them), with --purge the data folder, the
    // has-run marker and the folders the single-file host unpacked its
    // native libraries into. The app folder holds the binary this very
    // process is usually running from — RemoveAppDir takes it away as the
    // caller's last act, after the message is out (see Program.Uninstall).
    public static Result Uninstall(bool purge)
    {
        if (!OperatingSystem.IsLinux()) return new Result(false, "only available on Linux");
        try
        {
            TryDelete(DesktopFile);
            TryDelete(SvgIcon);
            TryDelete(PngIcon);
            LinuxHost.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFile)! });
            RefreshIconCache();
            var data = Path.Combine(DataHome, "Chatterbox");
            if (!purge)
                return new Result(true, $"Removed Chatterbox from the app grid. Your settings, players and models are still in {data} " +
                                        "(--uninstall --purge removes them too).");
            // The app folder lives inside the data folder: everything
            // else in it goes now, the folder itself with RemoveAppDir.
            if (Directory.Exists(data))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(data))
                {
                    if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(AppDir), StringComparison.Ordinal)) continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
            }
            var marker = Path.Combine(ConfigHome, "Chatterbox");
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
            foreach (var dir in BundleExtractDirs())
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            return new Result(true, "Removed Chatterbox and all of its data.");
        }
        catch (Exception ex)
        {
            return new Result(false, "uninstall failed: " + ex.Message);
        }
    }

    // The app folder — and, after a purge, the now-empty data folder
    // around it. Never throws, needs nothing loaded: it may be deleting
    // the file the process runs from, after which no assembly that is
    // not already in memory can be loaded.
    public static void RemoveAppDir()
    {
        try
        {
            if (Directory.Exists(AppDir)) Directory.Delete(AppDir, recursive: true);
            var data = Path.Combine(DataHome, "Chatterbox");
            if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
        }
        catch { }
    }

    // Where the single-file host unpacks the native libraries bundled in
    // the executable (Photino.Native.so): $DOTNET_BUNDLE_EXTRACT_BASE_DIR
    // or ~/.net, one folder per executable name — "Chatterbox" for the
    // installed copy, "Chatterbox-<version>-linux-x64" for every download
    // that was ever run. All of them; the base folder is shared with
    // other single-file apps and stays.
    internal static IEnumerable<string> BundleExtractDirs()
    {
        var baseDir = BundleExtractBaseOverride ?? Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.Combine(LinuxHost.Home, ".net");
        var dirs = new List<string> { Path.Combine(baseDir, "Chatterbox") };
        var running = Path.GetFileName(Environment.ProcessPath ?? "");
        if (running.StartsWith("Chatterbox", StringComparison.Ordinal)) dirs.Add(Path.Combine(baseDir, running));
        try
        {
            if (Directory.Exists(baseDir))
                dirs.AddRange(Directory.EnumerateDirectories(baseDir, "Chatterbox*"));
        }
        catch { }
        return dirs.Distinct(StringComparer.Ordinal);
    }

    // After the icons are gone the cache must not list them; and a
    // hicolor folder we alone populated is left as we found it.
    private static void RefreshIconCache()
    {
        try
        {
            if (!Directory.Exists(IconRoot)) return;
            var cache = Path.Combine(IconRoot, "icon-theme.cache");
            bool othersRemain = Directory.EnumerateFiles(IconRoot, "*", SearchOption.AllDirectories)
                .Any(f => !string.Equals(f, cache, StringComparison.Ordinal));
            if (othersRemain) { LinuxHost.Capture("gtk-update-icon-cache", new[] { "-q", "-t", "-f", "--ignore-theme-index", IconRoot }); return; }
            TryDelete(cache);
            foreach (var dir in new[] { Path.Combine(IconRoot, "scalable", "apps"), Path.Combine(IconRoot, "scalable"), Path.Combine(IconRoot, "32x32", "apps"), Path.Combine(IconRoot, "32x32"), IconRoot })
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        catch { }
    }

    // Flatpak Steam and no native Steam: launch options run inside the
    // sandbox and cannot reach a binary in the home folder.
    public static bool SteamIsFlatpakOnly()
    {
        try
        {
            var home = LinuxHost.Home;
            bool flatpak = Directory.Exists(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam"));
            bool native = Directory.Exists(Path.Combine(home, ".local", "share", "Steam")) ||
                          Directory.Exists(Path.Combine(home, ".steam", "steam"));
            return flatpak && !native;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void WriteResource(string name, string dest)
    {
        using var src = typeof(LinuxInstaller).Assembly.GetManifestResourceStream(name);
        if (src == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var dst = File.Create(dest);
        src.CopyTo(dst);
    }

    // The freedesktop entry; Exec is quoted only when the path needs it.
    internal static string DesktopEntry(string execPath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Chatterbox\n" +
        "Comment=Live captions for VRChat, transcribed on your own PC\n" +
        "Exec=" + ExecQuote(execPath) + "\n" +
        "Icon=chatterbox\n" +
        "Terminal=false\n" +
        "StartupWMClass=Chatterbox\n" +
        "Categories=AudioVideo;Audio;Game;Accessibility;\n" +
        "Keywords=captions;speech;VRChat;chatbox;accessibility;\n" +
        "StartupNotify=false\n";

    // Desktop-entry quoting: double quotes around a path with reserved
    // characters, backslash-escaping the quote, backslash, dollar and
    // backtick inside; a literal percent is written as %% (field codes).
    internal static string ExecQuote(string path)
    {
        if (path.IndexOfAny(new[] { ' ', '\t', '"', '\\', '$', '`', '\'', '%' }) < 0) return path;
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '\\' or '"' or '$' or '`') sb.Append('\\');
            if (c == '%') sb.Append('%');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
