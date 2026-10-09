using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The truth about which translation build runs: read off LLamaSharp's own
// loader log, never off which pack happens to be installed; and the tool
// LLamaSharp needs to see a Vulkan GPU at all.
public class TranslatorRuntimeTests : IDisposable
{
    public void Dispose() => LlamaTranslator.ResetForTests();

    [Theory]
    [InlineData("/home/u/.local/share/Chatterbox/runtimes/linux-x64/native/vulkan/libllama.so", "vulkan")]
    [InlineData("/home/u/.local/share/Chatterbox/runtimes/linux-x64/native/avx2/libllama.so", "avx2")]
    [InlineData("runtimes/linux-x64/native/noavx/libllama.so", "noavx")]
    [InlineData("/somewhere/libllama.so", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheVariantIsTheFolderUnderNative(string? path, string expected) =>
        Assert.Equal(expected, LlamaTranslator.ParseVariant(path));

    [Fact]
    public void TheLoadedLibraryComesFromTheSuccessfullyLoadedLine()
    {
        LlamaTranslator.ResetForTests();
        var seen = new List<string>();
        void Collect(string l) => seen.Add(l);
        LlamaTranslator.OnLoaderLog += Collect;
        try
        {
            LlamaTranslator.NoteLoaderLine("Detected OS Platform: 'LINUX'");
            LlamaTranslator.NoteLoaderLine("Got relative library path 'runtimes/linux-x64/native/vulkan/libllama.so' from local with {...}, trying to load it...");
            LlamaTranslator.NoteLoaderLine("Successfully loaded dependency '/x/runtimes/linux-x64/native/avx2/libggml-cpu.so'");
            LlamaTranslator.NoteLoaderLine("Successfully loaded '/x/runtimes/linux-x64/native/vulkan/libllama.so'\n");
            Assert.Equal("/x/runtimes/linux-x64/native/vulkan/libllama.so", LlamaTranslator.LoadedLibrary);
            Assert.Equal("vulkan", LlamaTranslator.LoadedVariant);
            // Only load/fail lines reach the boot log, not the OS chatter.
            Assert.Equal(2, seen.Count);
            Assert.All(seen, l => Assert.Contains("loaded", l));
        }
        finally { LlamaTranslator.OnLoaderLog -= Collect; }
    }

    [Fact]
    public void ToolOnPathSearchesLikeAShell()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        try
        {
            var tool = Path.Combine(bin, "vulkaninfo");
            File.WriteAllText(tool, "#!/bin/sh\n");
            Assert.Null(LinuxHost.ToolOnPath("vulkaninfo", "/nonexistent:" + bin));   // present but not executable
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal(tool, LinuxHost.ToolOnPath("vulkaninfo", "/nonexistent:" + bin));
            Assert.Null(LinuxHost.ToolOnPath("vulkaninfo", "/nonexistent"));
            Assert.Null(LinuxHost.ToolOnPath("vulkaninfo", ""));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void TheBinaryCarriesBothWhisperBuilds()
    {
        // The AVX2 build and the no-AVX build (the voice detector loads
        // through whisper.cpp, so an older CPU needs the latter even for
        // Parakeet). Each is the same four libraries.
        var names = typeof(LlamaTranslator).Assembly.GetManifestResourceNames();
        foreach (var lib in new[] { "libwhisper.so", "libggml-whisper.so", "libggml-base-whisper.so", "libggml-cpu-whisper.so" })
        {
            Assert.Contains("natives/linux-x64/" + lib, names);
            Assert.Contains("natives/noavx/linux-x64/" + lib, names);
        }
        Assert.EndsWith(Path.Combine("runtimes", "noavx", "linux-x64"), SttPaths.NoAvxNativeDir);
    }
}
