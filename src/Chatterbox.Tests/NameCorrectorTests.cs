using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

public class NameCorrectorTests
{
    private static NameCorrector C(params string[] names) => NameCorrector.Create(names)!;

    [Fact]
    public void NoNames_YieldsNoCorrector()
    {
        Assert.Null(NameCorrector.Create(new string[0]));
        Assert.Null(NameCorrector.Create(new[] { "ab" })); // too short to ever match safely
    }

    [Fact]
    public void ExactText_PassesThroughUnchanged()
    {
        var c = C("Alice");
        Assert.Equal("hello Alice how are you", c.Correct("hello Alice how are you"));
    }

    [Fact]
    public void NearMiss_SnapsToExactSpelling()
    {
        var c = C("DigDoug13");
        Assert.Equal("is DigDoug13 here", c.Correct("is digdug13 here"));
    }

    [Fact]
    public void TwoWordWindow_JoinsToOneName()
    {
        var c = C("DigDoug13");
        Assert.Equal("hey DigDoug13 come look", c.Correct("hey dig doug13 come look"));
    }

    [Fact]
    public void CasingOnlyDifference_UsesDisplayCasing()
    {
        var c = C("PixelFerret");
        Assert.Equal("PixelFerret waved", c.Correct("pixelferret waved"));
    }

    [Fact]
    public void ShortAndOrdinaryWords_AreNeverCorrected()
    {
        var c = C("Nova_Signs");
        // "no" and "signs" alone are too far / too short; ordinary text survives.
        Assert.Equal("no signals here at all", c.Correct("no signals here at all"));
    }

    [Fact]
    public void TrailingPunctuation_Survives()
    {
        var c = C("Alazu");
        Assert.Equal("thanks Alazu!", c.Correct("thanks alasu!"));
    }

    [Fact]
    public void UnrelatedWords_NotDraggedIn()
    {
        var c = C("quietstorm60");
        Assert.Equal("the fountain is crowded", c.Correct("the fountain is crowded"));
    }
}
