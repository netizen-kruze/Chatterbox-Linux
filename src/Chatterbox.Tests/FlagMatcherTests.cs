using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The uid-or-name flag matcher (StandaloneSttController.MatchesFlag): uid is
// authoritative when both sides have one; display name covers manual entries
// and uid-less log lines.
public class FlagMatcherTests
{
    private static List<SttSettings.AutoFriend> Flags(params (string id, string name)[] entries) =>
        entries.Select(e => new SttSettings.AutoFriend { Id = e.id, Name = e.name }).ToList();

    [Fact]
    public void UidMatch_Wins()
    {
        var flags = Flags(("usr_alice", "Old Name"));
        Assert.True(StandaloneSttController.MatchesFlag(flags, "usr_alice", "Renamed Alice"));
    }

    [Fact]
    public void UidMismatch_DoesNotFallBackToName()
    {
        // Both ids known and different: a name coincidence must not match —
        // display names are not unique.
        var flags = Flags(("usr_alice", "Alice"));
        Assert.False(StandaloneSttController.MatchesFlag(flags, "usr_impostor", "Alice"));
    }

    [Fact]
    public void NameOnlyFlag_MatchesByName_CaseInsensitive()
    {
        var flags = Flags(("", "alice"));
        Assert.True(StandaloneSttController.MatchesFlag(flags, "usr_whoever", "Alice"));
    }

    [Fact]
    public void UidlessLogLine_MatchesFlagByName()
    {
        var flags = Flags(("usr_alice", "Alice"));
        Assert.True(StandaloneSttController.MatchesFlag(flags, "", "Alice"));
    }

    [Fact]
    public void NoMatch_ReturnsFalse()
    {
        var flags = Flags(("usr_alice", "Alice"), ("", "Bob"));
        Assert.False(StandaloneSttController.MatchesFlag(flags, "usr_carol", "Carol"));
    }

    [Fact]
    public void EmptyFlags_NeverMatch()
    {
        Assert.False(StandaloneSttController.MatchesFlag(Flags(), "usr_alice", "Alice"));
    }
}
