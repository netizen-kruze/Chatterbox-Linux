using System.Text.RegularExpressions;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Translation: the catalog pin, the runtime packs' pin tables, the prompt
// the model is asked with, and the clean-up of what it answers.
public class TranslationTests
{
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$");

    [Fact]
    public void TranslationModel_IsHyMt2_PinnedToItsUpstreamRevisionAndHash()
    {
        var m = SttModelCatalog.Translation;
        Assert.Equal("hy-mt2-1.8b", m.Id);
        var file = Assert.Single(m.Files);
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M.gguf", file.FileName);
        Assert.Equal(1_133_080_448L, file.SizeBytes);
        Assert.Equal("dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", file.Sha256);
        Assert.Contains("/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/", file.Url); // revision-pinned, never "main"
        Assert.Equal("Apache-2.0", m.License);
        Assert.True(SttModelCatalog.IsComponent(m.Id));
        Assert.False(SttModelCatalog.IsComponent(SttModelCatalog.ParakeetId));
    }

    [Fact]
    public void Packs_HaveCompleteUniquePins_AndNoMultimodalLibrary()
    {
        foreach (var pack in new[] { SttTranslatePacks.Cpu, SttTranslatePacks.Gpu })
        {
            Assert.NotEmpty(pack.Files);
            Assert.True(pack.SizeBytes > 1_000_000);
            Assert.Equal(pack.Files.Count, pack.Files.Select(f => f.Subdir + "/" + f.Name).Distinct().Count());
            foreach (var f in pack.Files)
            {
                Assert.True(f.Size > 0, f.Name);
                Assert.Matches(Sha256, f.Sha256);
                Assert.NotEqual("libmtmd.so", f.Name); // vision support: not shipped, not needed
            }
            Assert.Contains(pack.Files, f => f.Name == "libllama.so");
        }
        Assert.Equal(4, SttTranslatePacks.Cpu.Files.Select(f => f.Subdir).Distinct().Count()); // avx, avx2, avx512, noavx
        Assert.All(SttTranslatePacks.Gpu.Files, f => Assert.Equal("vulkan", f.Subdir));
        Assert.Same(SttTranslatePacks.Cpu, SttTranslatePacks.Find("translate-engine"));
        Assert.Same(SttTranslatePacks.Gpu, SttTranslatePacks.Find("translate-gpu-pack"));
        Assert.Null(SttTranslatePacks.Find("parakeet-engine"));
    }

    [Fact]
    public void Instruction_NamesTheTargetLanguage_AndAsksForTheTranslationOnly()
    {
        var p = LlamaTranslator.Instruction("Nice to meet you!", "ja");
        Assert.StartsWith("Translate the following text into Japanese.", p);
        Assert.EndsWith("\nNice to meet you!", p);
        Assert.Contains("without any additional explanation", p);
        Assert.Equal("Korean", LlamaTranslator.LanguageName("ko"));
        Assert.Equal("English", LlamaTranslator.LanguageName("xx")); // unknown code: never a crash
        Assert.Contains(LlamaTranslator.Languages, l => l.Code == "ja");
        Assert.Equal(LlamaTranslator.Languages.Count, LlamaTranslator.Languages.Select(l => l.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("こんにちは<|im_end|>", "こんにちは")]
    [InlineData("  \"Hola, ¿qué tal?\"  ", "Hola, ¿qué tal?")]
    [InlineData("「はじめまして」", "はじめまして")]
    [InlineData("<think>\nreasoning\n</think>\n\nBonjour", "Bonjour")]
    [InlineData("line one\nline two", "line one line two")]
    [InlineData("<|im_end|>", "")]
    public void Clean_StripsStopTokensQuotesThinkingAndLineBreaks(string raw, string expected) =>
        Assert.Equal(expected, LlamaTranslator.Clean(raw));

    [Fact]
    public void Settings_DefaultToTranslationOff_Japanese_TranslationOnly()
    {
        var s = new SttSettings();
        Assert.False(s.TranslateEnabled);
        Assert.Equal("ja", s.TranslateTarget);
        Assert.False(s.TranslateShowOriginal);
    }

    [Theory]
    [InlineData("見てください", "Look at this", false, "見てください")]
    [InlineData("見てください", "Look at this", true, "見てください (Look at this)")]
    [InlineData(null, "Look at this", true, "Look at this")]   // no translation: the original, never nothing
    public void ChatboxText_ForATranslatedUtterance(string? translated, string original, bool showOriginal, string expected) =>
        Assert.Equal(expected, SttChatboxRelay.ChatboxText(original, translated, showOriginal));
}
