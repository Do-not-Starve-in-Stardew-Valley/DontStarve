using DontStarve.Display;
using Xunit;

namespace DontStarve.Tests.Display;

public sealed class HudDisplayRulesTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Sanity_total_switch_only_hides_bar_not_held_food_data(
        bool systemEnabled,
        bool expectedBar,
        bool expectedTooltip
    )
    {
        var visibility = HudDisplayRules.ResolveSanityVisibility(systemEnabled);

        Assert.Equal(expectedBar, visibility.ShowBar);
        Assert.Equal(expectedTooltip, visibility.ShowHeldFoodTooltip);
    }

    [Theory]
    [InlineData(false, true, 824, 884, 944)]
    [InlineData(true, true, 768, 828, 888)]
    [InlineData(false, false, 884, 884, 944)]
    public void LayoutUsesVisibleVanillaBarAsAnchorAndReflowsWhenSanityIsHidden(
        bool showingHealth,
        bool showingSanity,
        int expectedHungerX,
        int expectedSanityX,
        int expectedVanillaAnchorX
    )
    {
        var layout = HudDisplayRules.CreateLayout(
            1000,
            800,
            showingHealth,
            showingSanity,
            100,
            270,
            48,
            224
        );

        Assert.Equal(expectedHungerX, layout.HungerBounds.X);
        Assert.Equal(expectedSanityX, layout.SanityBounds.X);
        Assert.Equal(expectedVanillaAnchorX, layout.AnchorBounds.X);
        Assert.Equal(560, layout.HungerBounds.Y);
        Assert.Equal(560, layout.SanityBounds.Y);
        Assert.Equal(48, layout.HungerBounds.Width);
        Assert.Equal(224, layout.HungerBounds.Height);
        Assert.Equal(showingSanity, layout.ShowingSanity);
    }

    [Fact]
    public void LayoutUsesUiViewportDimensionsAndTracksVanillaBarHeight()
    {
        var layout = HudDisplayRules.CreateLayout(
            1600,
            900,
            true,
            true,
            205,
            370,
            48,
            224
        );

        Assert.Equal(new HudBounds(1544, 598, 48, 286), layout.Vanilla.EnergyBounds);
        Assert.Equal(new HudBounds(1488, 555, 48, 329), layout.Vanilla.HealthBounds);
        Assert.Equal(new HudBounds(1368, 660, 48, 224), layout.HungerBounds);
        Assert.Equal(new HudBounds(1428, 660, 48, 224), layout.SanityBounds);
        Assert.Equal(layout.Vanilla.HealthBounds.Bottom, layout.SanityBounds.Bottom);
    }

    [Fact]
    public void SanityFrameUsesOneContainerBoundsForDrawingAndHitTesting()
    {
        var success = HudDisplayRules.TryCreateSanityFrame(
            new HudBounds(884, 560, 48, 224),
            24,
            50,
            200,
            out var frame,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(new HudBounds(884, 560, 48, 224), frame.ContainerBounds);
        Assert.Equal(new HudPoint(920, 775), frame.FillerPosition);
        Assert.Equal(24, frame.FillerWidth);
        Assert.Equal(42, frame.FillerHeight);
        Assert.True(frame.ContainerBounds.Contains(884, 560));
        Assert.True(frame.ContainerBounds.Contains(931, 783));
        Assert.False(frame.ContainerBounds.Contains(932, 783));
        Assert.False(frame.ContainerBounds.Contains(931, 784));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 84)]
    [InlineData(200, 168)]
    public void SanityFillHeightTracksValidatedCurrentAndMaximum(
        double current,
        int expectedHeight
    )
    {
        Assert.True(
            HudDisplayRules.TryCreateSanityFrame(
                new HudBounds(0, 0, 1, 224),
                1,
                current,
                200,
                out var frame,
                out var reason
            ),
            reason
        );
        Assert.Equal(expectedHeight, frame.FillerHeight);
    }

    [Theory]
    [InlineData(double.NaN, 200, "sanity-hud-current-is-not-finite")]
    [InlineData(100, double.PositiveInfinity, "sanity-hud-maximum-is-not-finite")]
    [InlineData(0, 0, "sanity-hud-maximum-is-not-positive")]
    [InlineData(-1, 200, "sanity-hud-current-is-out-of-range")]
    [InlineData(201, 200, "sanity-hud-current-is-out-of-range")]
    public void InvalidSanityStateFailsClosed(
        double current,
        double maximum,
        string expectedReason
    )
    {
        var success = HudDisplayRules.TryCreateSanityFrame(
            new HudBounds(0, 0, 1, 1),
            1,
            current,
            maximum,
            out _,
            out var reason
        );

        Assert.False(success);
        Assert.Equal(expectedReason, reason);
    }
}
