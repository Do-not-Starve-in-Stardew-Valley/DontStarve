using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVignettePolicyTests
{
    [Theory]
    [InlineData(true, true, 0.16d, "Basic")]
    [InlineData(false, true, 0.16d, "Basic")]
    [InlineData(true, true, 0.15d, "Insane")]
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
}
