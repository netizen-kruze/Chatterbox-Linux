using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Parakeet's token stream → timed words for the pipeline's trimming.
public class SherpaWordSpanTests
{
    private const char Mark = (char)0x2581;   // SentencePiece word opener

    [Fact]
    public void RebuildsWordsWithPunctuationAttached()
    {
        var spans = SherpaOnnxEngine.BuildWordSpans(
            new[] { Mark + "Hello", ",", Mark + "world", "." },
            new[] { 0.1f, 0.4f, 0.6f, 1.0f });
        Assert.Equal(2, spans.Count);
        Assert.Equal(new SttSpan("Hello,", 100, 600), spans[0]);
        Assert.Equal(new SttSpan("world.", 600, 1300), spans[1]);
    }

    [Fact]
    public void ALoneWordMarkOpensTheNextWord()
    {
        var spans = SherpaOnnxEngine.BuildWordSpans(
            new[] { Mark.ToString(), "Hi", Mark + "there" },
            new[] { 0.0f, 0.05f, 0.5f });
        Assert.Equal(2, spans.Count);
        Assert.Equal(new SttSpan("Hi", 0, 500), spans[0]);
        Assert.Equal(new SttSpan("there", 500, 800), spans[1]);
    }

    [Fact]
    public void MismatchedOrMissingTimingYieldsNoSpans()
    {
        Assert.Empty(SherpaOnnxEngine.BuildWordSpans(new[] { Mark + "a" }, new[] { 0.1f, 0.2f }));
        Assert.Empty(SherpaOnnxEngine.BuildWordSpans(null, null));
        Assert.Empty(SherpaOnnxEngine.BuildWordSpans(Array.Empty<string>(), Array.Empty<float>()));
    }
}
