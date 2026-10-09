using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// The single-instance lock: one holder at a time, released on dispose,
// and a folder that cannot take the file is not "already running".
public class InstanceLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void ASecondHolderIsRefusedUntilTheFirstLetsGo()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_dir, "data", "instance.lock");
        var first = LinuxHost.TryLockInstance(path);
        Assert.NotNull(first);
        Assert.IsType<FileStream>(first);            // a real lock, not the unguarded fallback
        Assert.Null(LinuxHost.TryLockInstance(path));   // "already running"
        Assert.Null(LinuxHost.TryLockInstance(path));   // still
        first!.Dispose();
        var again = LinuxHost.TryLockInstance(path);
        Assert.IsType<FileStream>(again);
        again!.Dispose();
    }

    [Fact]
    public void AFolderThatCannotBeCreatedRunsUnguardedInsteadOfQuitting()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A file where the folder should be: CreateDirectory fails.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "data");
        File.WriteAllText(blocker, "not a folder");
        var guard = LinuxHost.TryLockInstance(Path.Combine(blocker, "instance.lock"));
        Assert.NotNull(guard);                       // never null: that would mean "already running"
        Assert.IsNotType<FileStream>(guard);
        guard!.Dispose();
    }
}
