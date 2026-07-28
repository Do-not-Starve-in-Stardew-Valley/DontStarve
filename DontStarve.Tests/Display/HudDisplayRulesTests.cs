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
    [InlineData(false, 884)]
    [InlineData(true, 829)]
    public void BottomRightAnchorUsesUiViewportAndHealthOffset(
        bool showingHealth,
        int expectedX
    )
    {
        var anchor = HudDisplayRules.GetBottomRightAnchor(1000, 800, showingHealth);

        Assert.Equal(new HudPoint(expectedX, 800), anchor);
    }

    [Fact]
    public void SanityFrameUsesOneContainerBoundsForDrawingAndHitTesting()
    {
        var success = HudDisplayRules.TryCreateSanityFrame(
            new HudPoint(884, 800),
            44,
            60,
            24,
            50,
            200,
            out var frame,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(new HudBounds(884, 560, 44, 60), frame.ContainerBounds);
        Assert.Equal(new HudPoint(920, 775), frame.FillerPosition);
        Assert.Equal(24, frame.FillerWidth);
        Assert.Equal(42, frame.FillerHeight);
        Assert.True(frame.ContainerBounds.Contains(884, 560));
        Assert.True(frame.ContainerBounds.Contains(927, 619));
        Assert.False(frame.ContainerBounds.Contains(928, 619));
        Assert.False(frame.ContainerBounds.Contains(927, 620));
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
                new HudPoint(0, 0),
                1,
                1,
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
            new HudPoint(0, 0),
            1,
            1,
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
