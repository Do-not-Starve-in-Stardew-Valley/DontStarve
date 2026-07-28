using DontStarve.Player.Stats.Sanity.Events;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityEventConditionClassifierTests
{
    [Theory]
    [InlineData("f Alex 1000")]
    [InlineData("Friendship \"Abigail\" 2500")]
    [InlineData("!f \"Mr. Qi\" 0")]
    [InlineData("f Alex 1000 Abigail 2000")]
    public void Old_slash_friendship_conditions_are_known(string condition)
    {
        var result = Classify("100", condition, "t 600 2600");

        Assert.Equal(SanityEventClassification.Friendship, result.Classification);
        Assert.Equal("known-relationship-condition", result.Reason);
    }

    [Theory]
    [InlineData("G PLAYER_FRIENDSHIP_POINTS Current Alex 1000")]
    [InlineData("GameStateQuery PLAYER_HEARTS Current Abigail 8 10")]
    [InlineData("G PLAYER_NPC_RELATIONSHIP Current Leah dating married")]
    [InlineData("G \"PLAYER_HEARTS Current \\\"Mr. Qi\\\" 2\"")]
    public void Direct_known_relationship_gsq_conditions_are_known(string condition)
    {
        var result = Classify("101", condition);

        Assert.Equal(SanityEventClassification.Friendship, result.Classification);
        Assert.Equal("known-relationship-game-state-query", result.Reason);
    }

    [Fact]
    public void Ordinary_non_relationship_conditions_are_allowed()
    {
        var result = Classify("102", "d Mon Tue", "w sunny", "t 900 1700");

        Assert.Equal(SanityEventClassification.NonFriendship, result.Classification);
        Assert.Equal("no-relationship-condition", result.Reason);
    }

    [Theory]
    [InlineData("f Alex")]
    [InlineData("Friendship Alex not-a-number")]
    [InlineData("G ANY 'PLAYER_HEARTS Current Alex 2'")]
    [InlineData("G PLAYER_HEARTS Current Alex 2, PLAYER_MONEY_EARNED Current 10")]
    [InlineData("G MODDED_RELATIONSHIP_QUERY Current Alex 2")]
    [InlineData("f \"unterminated 1000")]
    public void Malformed_nested_or_custom_relationship_candidates_fail_open_as_unknown(
        string condition
    )
    {
        var result = Classify("103", condition);

        Assert.Equal(SanityEventClassification.Unknown, result.Classification);
    }

    [Fact]
    public void Explicit_event_override_is_authoritative_but_cannot_be_unknown()
    {
        Assert.Equal(
            SanityEventClassification.Friendship,
            SanityEventConditionClassifier.Classify(
                new[] { "104", "t 600 2600" },
                SanityEventClassification.Friendship
            ).Classification
        );
        Assert.Equal(
            SanityEventClassification.NonFriendship,
            SanityEventConditionClassifier.Classify(
                new[] { "105", "f Alex 2500" },
                SanityEventClassification.NonFriendship
            ).Classification
        );
        Assert.Equal(
            SanityEventClassification.Unknown,
            SanityEventConditionClassifier.Classify(
                new[] { "106" },
                SanityEventClassification.Unknown
            ).Classification
        );
    }

    [Fact]
    public void Gate_uses_true_base_ratio_and_allows_exactly_fifty_percent()
    {
        var below = SanityFriendshipEventGate.Evaluate(
            SanityEventClassification.Friendship,
            99.999d,
            200d
        );
        var exact = SanityFriendshipEventGate.Evaluate(
            SanityEventClassification.Friendship,
            100d,
            200d
        );
        var above = SanityFriendshipEventGate.Evaluate(
            SanityEventClassification.Friendship,
            101d,
            200d
        );

        Assert.Equal(SanityFriendshipEventGateDecision.Block, below.Decision);
        Assert.Equal(SanityFriendshipEventGateDecision.Allow, exact.Decision);
        Assert.Equal(SanityFriendshipEventGateDecision.Allow, above.Decision);
    }

    [Fact]
    public void Unknown_or_invalid_base_fails_open_and_nonfriendship_does_not_read_ratio()
    {
        Assert.Equal(
            SanityFriendshipEventGateDecision.FailOpen,
            SanityFriendshipEventGate.Evaluate(
                SanityEventClassification.Unknown,
                1d,
                200d
            ).Decision
        );
        Assert.Equal(
            SanityFriendshipEventGateDecision.FailOpen,
            SanityFriendshipEventGate.Evaluate(
                SanityEventClassification.Friendship,
                double.NaN,
                200d
            ).Decision
        );
        Assert.Equal(
            SanityFriendshipEventGateDecision.Allow,
            SanityFriendshipEventGate.Evaluate(
                SanityEventClassification.NonFriendship,
                double.NaN,
                0d
            ).Decision
        );
    }

    private static SanityEventClassificationResult Classify(
        string eventId,
        params string[] conditions
    )
    {
        return SanityEventConditionClassifier.Classify(
            new[] { eventId }.Concat(conditions).ToArray()
        );
    }
}
