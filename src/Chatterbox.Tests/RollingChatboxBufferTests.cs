using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The chatbox window: 144-char cap, whole-word trimming from the front,
// new lines after pauses, typographic punctuation mapped to ASCII.
public class RollingChatboxBufferTests
{
    [Fact]
    public void LiveTextFollowsCommitsAndFinals()
    {
        var b = new RollingChatboxBuffer();
        Assert.True(b.IsEmpty);
        b.UpdateLive("hello world");
        Assert.Equal("hello world", b.Window);
        b.CommitUtterance("hello world.");
        Assert.Equal("hello world.", b.Window);
        b.UpdateLive("next thing");
        Assert.Equal("hello world. next thing", b.Window);
    }

    [Fact]
    public void AnUtteranceAfterAPauseStartsANewLine()
    {
        var b = new RollingChatboxBuffer();
        b.CommitUtterance("first sentence.");
        b.UpdateLive("second", onNewLine: true);
        Assert.Equal("first sentence.\nsecond", b.Window);
        b.CommitUtterance("second one.", onNewLine: true);
        Assert.Equal("first sentence.\nsecond one.", b.Window);
    }

    [Fact]
    public void TheCapDropsLeadingWholeWordsNeverMidWord()
    {
        var b = new RollingChatboxBuffer(maxChars: 20);
        b.CommitUtterance("aaaa bbbb cccc dddd eeee");
        Assert.Equal("bbbb cccc dddd eeee", b.Window);
        Assert.True(b.Window.Length <= 20);
    }

    [Fact]
    public void ASingleOverlongWordKeepsItsTail()
    {
        var b = new RollingChatboxBuffer(maxChars: 8);
        b.CommitUtterance("abcdefghijkl");
        Assert.Equal("efghijkl", b.Window);
    }

    [Fact]
    public void EmptyLeadingLinesDropWithTheirWords()
    {
        var b = new RollingChatboxBuffer(maxChars: 12);
        b.CommitUtterance("one two");
        b.CommitUtterance("three four five", onNewLine: true);
        Assert.Equal("four five", b.Window);
        Assert.DoesNotContain("\n", b.Window);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        var b = new RollingChatboxBuffer();
        b.CommitUtterance("some words");
        b.UpdateLive("more");
        b.Clear();
        Assert.True(b.IsEmpty);
        Assert.Equal("", b.Window);
    }

    [Fact]
    public void TypographicPunctuationBecomesAscii()
    {
        Assert.Equal("it's \"quoted\" - so... done", RollingChatboxBuffer.NormalizePunctuation("it’s “quoted” — so… done"));
        Assert.Equal("plain", RollingChatboxBuffer.NormalizePunctuation("plain"));
    }
}
