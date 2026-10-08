using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// LocalAgreement-2 commits, and the frozen prefix that lets the pipeline
// trim audio under committed words without any word moving or repeating.
public class LocalAgreementBufferTests
{
    [Fact]
    public void TwoAgreeingPassesCommitThePrefix()
    {
        var b = new LocalAgreementBuffer();
        b.Update("hello world how");
        Assert.Equal("", b.Committed);
        b.Update("hello world how are");
        Assert.Equal("hello world how", b.Committed);
        Assert.Equal("are", b.Pending);
    }

    [Fact]
    public void FreezeMovesWordsOutOfTheWindowAndRebasesTheComparison()
    {
        var b = new LocalAgreementBuffer();
        b.Update("one two three four");
        b.Update("one two three four five");
        Assert.Equal("one two three four", b.Committed);
        Assert.Equal(4, b.ActiveCommittedWordCount);

        b.FreezeCommittedPrefix(2);
        Assert.Equal("one two three four", b.Committed);
        Assert.Equal(2, b.ActiveCommittedWordCount);
        Assert.Equal(4, b.CommittedWordCount);

        // The next hypothesis starts right after the cut and still agrees.
        b.Update("three four five six");
        Assert.Equal("one two three four five", b.Committed);
        Assert.Equal("six", b.Pending);
    }

    [Fact]
    public void FinalizeIncludesTheFrozenPrefix()
    {
        var b = new LocalAgreementBuffer();
        b.Update("one two three four");
        b.Update("one two three four five");
        b.FreezeCommittedPrefix(3);
        b.Finalize("four five six.");
        Assert.Equal("one two three four five six.", b.Committed);
        Assert.Equal("", b.Pending);
    }

    [Fact]
    public void FreezeIsClampedToWhatIsCommitted()
    {
        var b = new LocalAgreementBuffer();
        b.Update("a b");
        b.Update("a b c");
        b.FreezeCommittedPrefix(5);
        Assert.Equal(0, b.ActiveCommittedWordCount);
        Assert.Equal(2, b.CommittedWordCount);
        Assert.Equal("a b", b.Committed);
        b.FreezeCommittedPrefix(0);
        Assert.Equal("a b", b.Committed);
    }

    [Fact]
    public void ResetClearsTheFrozenPrefixToo()
    {
        var b = new LocalAgreementBuffer();
        b.Update("a b c");
        b.Update("a b c d");
        b.FreezeCommittedPrefix(3);
        b.Reset();
        Assert.Equal("", b.Committed);
        Assert.Equal(0, b.CommittedWordCount);
    }
}
