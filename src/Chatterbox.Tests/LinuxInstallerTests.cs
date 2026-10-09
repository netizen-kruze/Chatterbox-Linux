using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// The self-install: the app-grid entry it writes and the quoting rules of
// its Exec line.
public class LinuxInstallerTests
{
    [Fact]
    public void DesktopEntryPointsAtTheBinary()
    {
        var entry = LinuxInstaller.DesktopEntry("/home/u/.local/share/Chatterbox/app/Chatterbox");
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("\nExec=/home/u/.local/share/Chatterbox/app/Chatterbox\n", entry);
        Assert.Contains("\nIcon=chatterbox\n", entry);
        Assert.Contains("\nType=Application\n", entry);
        Assert.Contains("\nTerminal=false\n", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void ExecIsQuotedOnlyWhenThePathNeedsIt()
    {
        Assert.Equal("/opt/chatterbox/Chatterbox", LinuxInstaller.ExecQuote("/opt/chatterbox/Chatterbox"));
        Assert.Equal("\"/home/my user/Chatterbox\"", LinuxInstaller.ExecQuote("/home/my user/Chatterbox"));
        Assert.Equal("\"/home/u/it\\\"s/Chatterbox\"", LinuxInstaller.ExecQuote("/home/u/it\"s/Chatterbox"));
        Assert.Equal("\"/home/u/100%%/Chatterbox\"", LinuxInstaller.ExecQuote("/home/u/100%/Chatterbox"));
        Assert.Equal("\"/home/u/\\$HOME/Chatterbox\"", LinuxInstaller.ExecQuote("/home/u/$HOME/Chatterbox"));
    }

    [Fact]
    public void PathsHangOffTheDataHome()
    {
        Assert.EndsWith(Path.Combine("Chatterbox", "app", "Chatterbox"), LinuxInstaller.InstalledBinary);
        Assert.EndsWith(Path.Combine("applications", "chatterbox.desktop"), LinuxInstaller.DesktopFile);
        Assert.StartsWith(LinuxInstaller.AppDir, LinuxInstaller.InstalledBinary, StringComparison.Ordinal);
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(LinuxInstaller.Install().Ok);
            Assert.False(LinuxInstaller.Uninstall(purge: false).Ok);
        }
    }

    // A throwaway home for the real install and uninstall: data, config
    // and the host's extraction folder all under one temp directory.
    private sealed class TempHome : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string Config => Path.Combine(Root, "config");
        public string Extract => Path.Combine(Root, "dotnet-extract");
        public TempHome()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Config);
            LinuxInstaller.DataHomeOverride = Data;
            LinuxInstaller.ConfigHomeOverride = Config;
            LinuxInstaller.BundleExtractBaseOverride = Extract;
        }
        public void Dispose()
        {
            LinuxInstaller.DataHomeOverride = LinuxInstaller.ConfigHomeOverride = LinuxInstaller.BundleExtractBaseOverride = null;
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InstallThenUninstallLeavesTheAppFolderForTheLastStep()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new TempHome();
        var r = LinuxInstaller.Install();   // copies the running test host — any file does
        Assert.True(r.Ok, r.Message);
        Assert.Equal(LinuxInstaller.InstalledBinary, LinuxInstaller.InstalledAt());
        Assert.True(File.GetUnixFileMode(LinuxInstaller.InstalledBinary).HasFlag(UnixFileMode.UserExecute | UnixFileMode.OtherExecute));
        Assert.Contains("Exec=" + LinuxInstaller.InstalledBinary + "\n", File.ReadAllText(LinuxInstaller.DesktopFile));
        Assert.True(File.Exists(Path.Combine(home.Data, "icons", "hicolor", "scalable", "apps", "chatterbox.svg")));
        Assert.True(File.Exists(Path.Combine(home.Data, "icons", "hicolor", "32x32", "apps", "chatterbox.png")));
        Assert.Contains("%command%", r.Message);
        // Someone's settings beside the app folder.
        File.WriteAllText(Path.Combine(home.Data, "Chatterbox", "stt_settings.json"), "{}");

        var u = LinuxInstaller.Uninstall(purge: false);
        Assert.True(u.Ok, u.Message);
        Assert.Contains("still in", u.Message);
        Assert.False(File.Exists(LinuxInstaller.DesktopFile));
        Assert.False(Directory.Exists(Path.Combine(home.Data, "icons", "hicolor")));   // ours alone: gone, cache included
        // The folder with the binary (the one this process may be running
        // from) is still there — Program removes it after the message.
        Assert.True(File.Exists(LinuxInstaller.InstalledBinary));
        Assert.Null(LinuxInstaller.InstalledAt());
        Assert.True(File.Exists(Path.Combine(home.Data, "Chatterbox", "stt_settings.json")));

        LinuxInstaller.RemoveAppDir();
        Assert.False(Directory.Exists(LinuxInstaller.AppDir));
        Assert.True(File.Exists(Path.Combine(home.Data, "Chatterbox", "stt_settings.json")));   // data kept
    }

    [Fact]
    public void PurgeRemovesTheDataTheMarkerAndTheExtractionFolders()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new TempHome();
        Assert.True(LinuxInstaller.Install().Ok);
        var data = Path.Combine(home.Data, "Chatterbox");
        Directory.CreateDirectory(Path.Combine(data, "models", "stt"));
        File.WriteAllText(Path.Combine(data, "models", "stt", "model.bin"), "x");
        File.WriteAllText(Path.Combine(data, "stt_settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(home.Config, "Chatterbox"));
        File.WriteAllText(Path.Combine(home.Config, "Chatterbox", "has_run"), "1");
        foreach (var dir in LinuxInstaller.BundleExtractDirs())
        {
            Directory.CreateDirectory(Path.Combine(dir, "abc123"));
            File.WriteAllText(Path.Combine(dir, "abc123", "Photino.Native.so"), "so");
        }
        Assert.Contains(Path.Combine(home.Extract, "Chatterbox"), LinuxInstaller.BundleExtractDirs());
        // Other people's icons share the hicolor folder: those stay, and the cache with them.
        var foreign = Path.Combine(home.Data, "icons", "hicolor", "48x48", "apps", "other.png");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "png");

        var u = LinuxInstaller.Uninstall(purge: true);
        Assert.True(u.Ok, u.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "models")));
        Assert.False(File.Exists(Path.Combine(data, "stt_settings.json")));
        Assert.False(Directory.Exists(Path.Combine(home.Config, "Chatterbox")));
        // Only our folders under the (shared) extraction base are gone.
        foreach (var dir in LinuxInstaller.BundleExtractDirs()) Assert.False(Directory.Exists(dir), dir);
        Assert.True(Directory.Exists(home.Extract));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(LinuxInstaller.InstalledBinary));   // last step pending

        LinuxInstaller.RemoveAppDir();
        Assert.False(Directory.Exists(data));                        // nothing of ours is left
    }
}
