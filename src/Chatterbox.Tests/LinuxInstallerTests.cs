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
}
