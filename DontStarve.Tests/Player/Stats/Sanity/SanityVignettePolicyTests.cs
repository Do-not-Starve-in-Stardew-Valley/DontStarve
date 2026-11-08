using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVignettePolicyTests
{
    [Theory]
    [InlineData(true, true, 0.16d, "Basic")]
    [InlineData(false, true, 0.16d, "Basic")]
    [InlineData(true, true, 0.15d, "Basic")]
    [InlineData(true, false, 0.14d, "Insane")]
    [InlineData(false, true, 0.14d, "Basic")]
    [InlineData(false, false, 0.14d, "Hidden")]
    [InlineData(true, false, 0.16d, "Hidden")]
    public void Existing_filter_and_independent_vignette_controls_select_the_expected_animation(
        bool lowSanityFilterEnabled,
        bool vignetteEnabled,
        double sanityRatio,
        string expectedName
    )
    {
        Assert.Equal(
            Enum.Parse<SanityVignetteMode>(expectedName),
            SanityVignettePolicy.Resolve(
                lowSanityFilterEnabled,
                vignetteEnabled,
                sanityRatio
            )
        );
    }

    [Fact]
    public void Invalid_sanity_ratio_fails_closed_without_a_vignette()
    {
        Assert.Equal(
            SanityVignetteMode.Hidden,
            SanityVignettePolicy.Resolve(true, true, double.NaN)
        );
        Assert.Equal(
            SanityVignetteMode.Hidden,
            SanityVignettePolicy.Resolve(true, true, -0.001d)
        );
        Assert.Equal(
            SanityVignetteMode.Hidden,
            SanityVignettePolicy.Resolve(true, true, 1.001d)
        );
    }

    [Theory]
    [InlineData(0.15d, false, false, "Hidden")]
    [InlineData(0.149d, false, false, "Insane")]
    [InlineData(0.16d, true, false, "Insane")]
    [InlineData(0.175d, true, false, "Insane")]
    [InlineData(0.175001d, false, false, "Hidden")]
    [InlineData(0.16d, false, true, "Basic")]
    public void Danger_active_receipt_preserves_the_insane_vignette_until_strict_exit(
        double sanityRatio,
        bool dangerTierActive,
        bool vignetteEnabled,
        string expectedName
    )
    {
        Assert.Equal(
            Enum.Parse<SanityVignetteMode>(expectedName),
            SanityVignettePolicy.Resolve(
                lowSanityFilterEnabled: true,
                vignetteEnabled: vignetteEnabled,
                sanityRatio: sanityRatio,
                dangerTierActive: dangerTierActive
            )
        );
    }

    [Fact]
    public void Vignette_follows_the_authoritative_danger_tier_sequence()
    {
        var machine = new SanityTierStateMachine();
        var ratios = new[] { 0.149d, 0.16d, 0.175d, 0.175001d };
        var expected = new[]
        {
            SanityVignetteMode.Insane,
            SanityVignetteMode.Insane,
            SanityVignetteMode.Insane,
            SanityVignetteMode.Basic,
        };

        for (var index = 0; index < ratios.Length; index++)
        {
            machine.Observe(
                new SanityPlayerSnapshot
                {
                    PlayerKey = "123456789",
                    Current = ratios[index] * 200d,
                    Maximum = 200d,
                    Revision = index,
                },
                isSystemEnabled: true
            );
            Assert.True(
                machine.TryGetOwnerState("123456789", out var snapshot)
            );
            Assert.NotNull(snapshot);

            var dangerTierActive = snapshot!.ActiveTierIds.Contains(
                SanityTierIds.Danger
            );
            Assert.Equal(
                expected[index],
                SanityVignettePolicy.Resolve(
                    lowSanityFilterEnabled: true,
                    vignetteEnabled: true,
                    sanityRatio: ratios[index],
                    dangerTierActive: dangerTierActive
                )
            );
        }
    }

    [Fact]
    public void Danger_receipt_does_not_bypass_the_low_sanity_filter_switch()
    {
        Assert.Equal(
            SanityVignetteMode.Hidden,
            SanityVignettePolicy.Resolve(
                lowSanityFilterEnabled: false,
                vignetteEnabled: false,
                sanityRatio: 0.16d,
                dangerTierActive: true
            )
        );
    }
}
