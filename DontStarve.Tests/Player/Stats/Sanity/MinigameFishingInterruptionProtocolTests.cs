using DontStarve.Player.Stats.Sanity.Minigames;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MinigameFishingInterruptionProtocolTests
{
    [Fact]
    public void ValidHostNotificationMatchesSessionAndTarget()
    {
        var message = new MinigameFishingInterruptionMessage
        {
            SessionId = "session-1",
            EventId = 4,
            TargetPlayerId = 123,
        };

        Assert.True(
            MinigameFishingInterruptionProtocol.IsValid(
                message,
                expectedSessionId: "session-1",
                expectedTargetPlayerId: 123,
                out _
            )
        );
    }

    [Fact]
    public void Negative_player_id_is_a_valid_target_identity()
    {
        var message = new MinigameFishingInterruptionMessage
        {
            SessionId = "session-1",
            EventId = 4,
            TargetPlayerId = -123,
        };

        Assert.True(
            MinigameFishingInterruptionProtocol.IsValid(
                message,
                expectedSessionId: "session-1",
                expectedTargetPlayerId: -123,
                out _
            )
        );
    }

    [Theory]
    [InlineData("session-2", 4, 123)]
    [InlineData("session-1", 0, 123)]
    [InlineData("session-1", 4, 456)]
    public void StaleOrMisroutedHostNotificationIsRejected(
        string sessionId,
        long eventId,
        long targetPlayerId
    )
    {
        var message = new MinigameFishingInterruptionMessage
        {
            SessionId = sessionId,
            EventId = eventId,
            TargetPlayerId = targetPlayerId,
        };

        Assert.False(
            MinigameFishingInterruptionProtocol.IsValid(
                message,
                expectedSessionId: "session-1",
                expectedTargetPlayerId: 123,
                out _
            )
        );
    }

    [Fact]
    public void UnknownProtocolVersionIsRejected()
    {
        var message = new MinigameFishingInterruptionMessage
        {
            ProtocolVersion = MinigameFishingInterruptionProtocol.ProtocolVersion + 1,
            SessionId = "session-1",
            EventId = 1,
            TargetPlayerId = 123,
        };

        Assert.False(
            MinigameFishingInterruptionProtocol.IsValid(
                message,
                expectedSessionId: "session-1",
                expectedTargetPlayerId: 123,
                out _
            )
        );
    }
}
