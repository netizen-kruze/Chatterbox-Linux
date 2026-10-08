using System;
using System.IO;
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
    public static string AppDir => Path.Combine(LinuxHost.DataHome, "Chatterbox", "app");
    public static string InstalledBinary => Path.Combine(AppDir, "Chatterbox");
    public static string DesktopFile => Path.Combine(LinuxHost.DataHome, "applications", "chatterbox.desktop");
    private static string IconRoot => Path.Combine(LinuxHost.DataHome, "icons", "hicolor");
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

    public static Result Uninstall(bool purge)
    {
        if (!OperatingSystem.IsLinux()) return new Result(false, "only available on Linux");
        try
        {
            TryDelete(DesktopFile);
            TryDelete(SvgIcon);
            TryDelete(PngIcon);
            if (Directory.Exists(AppDir)) Directory.Delete(AppDir, recursive: true);
            LinuxHost.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFile)! });
            var data = Path.Combine(LinuxHost.DataHome, "Chatterbox");
            if (!purge)
                return new Result(true, $"Removed Chatterbox from the app grid. Your settings, players and models are still in {data} " +
                                        "(--uninstall --purge removes them too).");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            var marker = Path.Combine(LinuxHost.ConfigHome, "Chatterbox");
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
            return new Result(true, "Removed Chatterbox and all of its data.");
        }
        catch (Exception ex)
        {
            return new Result(false, "uninstall failed: " + ex.Message);
        }
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
