using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerRequestValidatorTests
{
    private const string Session = "11111111111111111111111111111111";

    [Fact]
    public void Valid_food_request_contains_only_recomputable_facts()
    {
        var request = new HungerFoodChangeRequest
        {
            SessionId = Session,
            PlayerKey = "123",
            InteractionId = "434",
            Nonce = 1,
            ExpectedRevision = 4,
        };

        var result = HungerProtocol.ValidateFoodRequest(request, 123, Session, true, 0, 4);

        Assert.True(result.Accepted, result.Reason);
        Assert.True(result.CanConsumeNonce);
        Assert.Null(typeof(HungerFoodChangeRequest).GetProperty("Delta"));
    }

    [Fact]
    public void Wrong_sender_is_rejected_without_becoming_a_snapshot_request()
    {
        var request = Request(nonce: 1, expectedRevision: 0);

        var result = HungerProtocol.ValidateFoodRequest(request, 999, Session, true, 0, 0);

        Assert.False(result.Accepted);
        Assert.False(result.NeedsSnapshot);
        Assert.False(result.CanConsumeNonce);
    }

    [Fact]
    public void Revision_mismatch_consumes_nonce_and_requires_full_snapshot()
    {
        var request = Request(nonce: 5, expectedRevision: 2);

        var result = HungerProtocol.ValidateFoodRequest(request, 123, Session, true, 4, 3);

        Assert.False(result.Accepted);
        Assert.True(result.NeedsSnapshot);
        Assert.True(result.CanConsumeNonce);
    }

    private static HungerFoodChangeRequest Request(long nonce, long expectedRevision) =>
        new()
        {
            SessionId = Session,
            PlayerKey = "123",
            InteractionId = "434",
            Nonce = nonce,
            ExpectedRevision = expectedRevision,
        };
}
