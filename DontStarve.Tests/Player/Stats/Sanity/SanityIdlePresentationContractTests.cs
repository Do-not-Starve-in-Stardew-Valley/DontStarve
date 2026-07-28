using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityIdlePresentationContractTests
{
    [Fact]
    public void Owned_token_is_negative_and_never_the_vanilla_passout_token()
    {
        Assert.True(SanityIdlePresentationContract.AnimationToken < 0);
        Assert.NotEqual(
            SanityIdlePresentationContract.VanillaPassOutAnimationToken,
            SanityIdlePresentationContract.AnimationToken
        );
        Assert.True(
            SanityIdlePresentationContract.IsOwnedToken(
                SanityIdlePresentationContract.AnimationToken
            )
        );
        Assert.False(
            SanityIdlePresentationContract.IsOwnedToken(
                SanityIdlePresentationContract.VanillaPassOutAnimationToken
            )
        );
    }

    [Fact]
    public void Frozen_sequence_is_bounded_callback_free_metadata_and_rejects_frame_five()
    {
        Assert.Equal(4, SanityIdlePresentationContract.Steps.Count);
        Assert.All(
            SanityIdlePresentationContract.Steps,
            step =>
            {
                Assert.InRange(step.DurationMilliseconds, 100, 1000);
                Assert.InRange(step.PositionOffset, -1, 1);
            }
        );
        Assert.False(
            SanityIdlePresentationContract.IsSafeBasicFrame(
                SanityIdlePresentationContract.VanillaPassOutFinalFrame
            )
        );
        Assert.True(SanityIdlePresentationContract.IsSafeBasicFrame(1));
        Assert.True(SanityIdlePresentationContract.IsSafeBasicFrame(113));
    }
}
