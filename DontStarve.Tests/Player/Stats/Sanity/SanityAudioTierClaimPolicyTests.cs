using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Audio;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityAudioTierClaimPolicyTests
{
    [Fact]
    public void Fifty_percent_shadow_creatures_requests_ambience_and_music_only()
    {
        var flags = SanityAudioTierClaimPolicy.Evaluate(
            new[] { SanityTierIds.ShadowCreatures }
        );

        Assert.True(flags.AmbienceActive);
        Assert.False(flags.WhispersActive);
        Assert.False(flags.DangerActive);
        Assert.True(flags.MusicSuppressionRequested);
    }

    [Fact]
    public void Forty_five_percent_whispers_adds_only_the_whisper_pool()
    {
        var flags = SanityAudioTierClaimPolicy.Evaluate(
            new[] { SanityTierIds.ShadowCreatures, SanityTierIds.Whispers }
        );

        Assert.True(flags.AmbienceActive);
        Assert.True(flags.WhispersActive);
        Assert.False(flags.DangerActive);
        Assert.True(flags.MusicSuppressionRequested);
    }

    [Fact]
    public void Fifteen_percent_danger_adds_the_one_shot_lane_without_changing_music_ownership()
    {
        var flags = SanityAudioTierClaimPolicy.Evaluate(
            new[]
            {
                SanityTierIds.ShadowCreatures,
                SanityTierIds.Whispers,
                SanityTierIds.Danger,
            }
        );

        Assert.True(flags.AmbienceActive);
        Assert.True(flags.WhispersActive);
        Assert.True(flags.DangerActive);
        Assert.True(flags.MusicSuppressionRequested);
    }
}
