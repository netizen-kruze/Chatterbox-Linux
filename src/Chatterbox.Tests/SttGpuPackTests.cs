using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The GPU pack: pinned sources, which parts a download fetches, and the
// sentences shown when the driver side is incomplete.
public class SttGpuPackTests
{
    [Fact]
    public void EveryPartIsPinnedToAKnownHostWithHashes()
    {
        Assert.Equal(3, SttGpuPack.Parts.Length);
        foreach (var part in SttGpuPack.Parts)
        {
            Assert.StartsWith("https://", part.Url);
            Assert.Contains(new Uri(part.Url).Host, new[] { "api.nuget.org", "files.pythonhosted.org" });
            Assert.True(part.Size > 0);
            Assert.Matches("^[0-9a-f]{64}$", part.Sha256);
            Assert.NotEmpty(part.Files);
            foreach (var f in part.Files)
            {
                Assert.True(f.Size > 0);
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.DoesNotContain('/', f.Name);
                Assert.Contains('/', f.Entry);
            }
        }
        var names = SttGpuPack.Parts.SelectMany(p => p.Files).Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void TheRuntimePartsProvideExactlyTheLibrariesThePreloadExpects()
    {
        var runtimeLibs = SttGpuPack.Parts.Where(p => p.Runtime).SelectMany(p => p.Files)
            .Select(f => f.Name).Where(n => n != SttGpuPack.LicenseFileName).OrderBy(n => n).ToArray();
        Assert.Equal(SttGpuPack.RuntimeLoadOrder.OrderBy(n => n).ToArray(), runtimeLibs);
        // cudart first (Whisper.net asks for it by name), cublasLt before cublas (its dependency).
        Assert.Equal("libcudart.so.13", SttGpuPack.RuntimeLoadOrder[0]);
        Assert.True(Array.IndexOf(SttGpuPack.RuntimeLoadOrder, "libcublasLt.so.13") <
                    Array.IndexOf(SttGpuPack.RuntimeLoadOrder, "libcublas.so.13"));
        Assert.Contains(SttGpuPack.Parts.Where(p => p.Runtime).SelectMany(p => p.Files), f => f.Name == SttGpuPack.LicenseFileName);
    }

    [Theory]
    [InlineData(false, false, false, "whisper-cuda,cuda-runtime,cublas")]
    [InlineData(true, false, false, "cuda-runtime,cublas")]
    [InlineData(true, false, true, "")]
    [InlineData(false, false, true, "whisper-cuda")]
    [InlineData(false, true, false, "whisper-cuda")]
    [InlineData(true, true, false, "")]
    public void ADownloadFetchesWhatIsMissingUnlessTheSystemHasTheRuntime(bool whisper, bool bundled, bool system, string expected)
    {
        var plan = SttGpuPack.Plan(p => p.Runtime ? bundled : whisper, system);
        Assert.Equal(expected, string.Join(",", plan.Select(p => p.Id)));
    }

    [Fact]
    public void TheDriverGapIsNamedOnlyWhenTheModuleIsLoadedWithoutItsLibrary()
    {
        Assert.Contains(SttGpuPack.DriverPackageAdvice, SttGpuPack.DriverGapNote(moduleLoaded: true, libraryPresent: false));
        Assert.Equal("", SttGpuPack.DriverGapNote(true, true));
        Assert.Equal("", SttGpuPack.DriverGapNote(false, false));
        Assert.Equal("", SttGpuPack.DriverGapNote(false, true));
    }

    [Fact]
    public void OutsideLinuxTheRuntimeIsNeverTheProblem()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.True(SttGpuPack.SystemRuntimePresent());
        Assert.Equal("", SttGpuPack.RuntimeNote());
        Assert.Equal("", SttGpuPack.DriverGapNote());
        Assert.Equal("n/a", SttGpuPack.Status());
    }
}
