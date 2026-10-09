using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// What an in-app restart hands its successor: our own switches, never the
// game's command line, never a stale --after.
public class ProgramArgsTests
{
    [Fact]
    public void ForwardedArgsKeepTestHooksAndTheExitWithVrchatSwitch()
    {
        var args = new[] { "--data-dir", "/tmp/x y", "--after", "123", "--assume-vrchat-running", Program.ExitWithVrchatFlag, "--update-url", "http://127.0.0.1:8000/latest.json", "--bogus" };
        Assert.Equal(
            new[] { "--data-dir", "/tmp/x y", "--assume-vrchat-running", Program.ExitWithVrchatFlag, "--update-url", "http://127.0.0.1:8000/latest.json" },
            Program.ForwardedArgs(args));
    }

    [Fact]
    public void AValueSwitchWithoutItsValueIsDropped()
    {
        Assert.Empty(Program.ForwardedArgs(new[] { "--data-dir" }));
        Assert.Empty(Program.ForwardedArgs(Array.Empty<string>()));
    }
}
