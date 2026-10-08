using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The voice detector is wired through the catalog: the segmenter's default
// path, the download, and the manifest all read one entry, so a version
// change can't leave a second literal behind. The stale-file cleanup after
// an update is guarded so a failed download never deletes the old file.
public class SttModelCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));

    public SttModelCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Vad_IsSileroV62_PinnedToItsUpstreamHash()
    {
        var vad = SttModelCatalog.Vad;
        Assert.Equal("vad", vad.Id); // the id settings and the first-run lists use
        var file = Assert.Single(vad.Files);
        Assert.Equal("ggml-silero-v6.2.0.bin", file.FileName);
        Assert.Equal(885_098L, file.SizeBytes);
        Assert.Equal("2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987", file.Sha256);
        Assert.Equal("https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin", file.Url);
        Assert.Equal("MIT", vad.License);
    }

    [Fact]
    public void SegmenterDefaultPath_ComesFromTheCatalog()
    {
        var path = SileroVadSegmenter.DefaultModelPath;
        Assert.Equal(SttModelCatalog.Vad.PrimaryFileName, Path.GetFileName(path));
        Assert.Equal(Path.GetFullPath(SttPaths.ModelDir), Path.GetFullPath(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void StaleVadFiles_ListsOtherSileroFilesOnly()
    {
        var m = new SttModelManager(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "ggml-silero-v5.1.2.bin"), new byte[16]);
        File.WriteAllBytes(Path.Combine(_dir, "ggml-tiny.en-q5_1.bin"), new byte[16]);

        Assert.Equal(new[] { "ggml-silero-v5.1.2.bin" }, m.StaleVadFiles());
        Assert.False(m.IsInstalled(SttModelCatalog.Vad));
    }

    [Fact]
    public void RemoveStaleVadFiles_WaitsForTheCurrentFile()
    {
        var m = new SttModelManager(_dir);
        var old = Path.Combine(_dir, "ggml-silero-v5.1.2.bin");
        File.WriteAllBytes(old, new byte[16]);

        // Current file missing (the download may have failed): nothing goes.
        Assert.Empty(m.RemoveStaleVadFiles());
        Assert.True(File.Exists(old));

        // Current file present at its catalog size (IsInstalled is size
        // based): the old one goes, the current one and a Whisper model stay.
        File.WriteAllBytes(m.PathFor(SttModelCatalog.Vad), new byte[SttModelCatalog.Vad.SizeBytes]);
        var whisper = Path.Combine(_dir, "ggml-tiny.en-q5_1.bin");
        File.WriteAllBytes(whisper, new byte[16]);

        Assert.Equal(new[] { "ggml-silero-v5.1.2.bin" }, m.RemoveStaleVadFiles());
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(m.PathFor(SttModelCatalog.Vad)));
        Assert.True(File.Exists(whisper));
        Assert.Empty(m.StaleVadFiles());
    }

    [Fact]
    public void StaleVadFiles_NoModelDir_IsEmpty()
    {
        var m = new SttModelManager(Path.Combine(_dir, "never-created"));
        Assert.Empty(m.StaleVadFiles());
        Assert.Empty(m.RemoveStaleVadFiles());
    }
}
