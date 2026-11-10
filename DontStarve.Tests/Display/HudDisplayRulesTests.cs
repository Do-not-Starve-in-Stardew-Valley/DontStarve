using System.Collections.Generic;
using System.Linq;
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
        var visibility = SanityHudRules.ResolveVisibility(systemEnabled);

        Assert.Equal(expectedBar, visibility.ShowBar);
        Assert.Equal(expectedTooltip, visibility.ShowHeldFoodTooltip);
    }

    [Fact]
    public void LayoutPreservesExistingSanityThenHungerSlots()
    {
        var layout = CreateLayout(
            1000,
            800,
            showingHealth: false,
            maxHealth: 100,
            maxStamina: 270,
            new HudBarLayoutRequest("hunger", 200, 48, 224),
            new HudBarLayoutRequest("sanity", 100, 48, 224)
        );

        Assert.Equal(944, layout.Vanilla.AnchorBounds.X);
        Assert.True(layout.TryGetBounds("hunger", out var hungerBounds));
        Assert.True(layout.TryGetBounds("sanity", out var sanityBounds));
        Assert.Equal(new HudBounds(824, 560, 48, 224), hungerBounds);
        Assert.Equal(new HudBounds(884, 560, 48, 224), sanityBounds);
    }

    [Fact]
    public void LayoutDoesNotReorderProviderRequests()
    {
        var requests = new[]
        {
            new HudBarLayoutRequest("hunger", 200, 48, 224),
            new HudBarLayoutRequest("sanity", 100, 48, 224),
        };
        var originalRequests = requests.ToArray();
        var layout = CreateLayout(
            1000,
            800,
            false,
            100,
            270,
            requests
        );

        Assert.Equal(originalRequests, layout.Requests);
    }

    [Fact]
    public void ReturnedBoundsStayStableWhenTheRequestBufferIsReused()
    {
        var requests = new List<HudBarLayoutRequest>
        {
            new HudBarLayoutRequest("hunger", 200, 48, 224),
        };
        var vanilla = HudDisplayRules.CreateVanillaLayout(1000, 800, false, 100, 270);
        Assert.True(
            HudDisplayRules.TryGetBarBounds(vanilla, requests, "hunger", out var hungerBounds)
        );

        requests.Clear();
        requests.Add(new HudBarLayoutRequest("temperature", 300, 40, 180));
        Assert.True(
            HudDisplayRules.TryGetBarBounds(
                vanilla,
                requests,
                "temperature",
                out var temperatureBounds
            )
        );

        Assert.Equal(new HudBounds(884, 560, 48, 224), hungerBounds);
        Assert.Equal(new HudBounds(892, 604, 40, 180), temperatureBounds);
    }

    [Fact]
    public void LayoutTracksVanillaBarHeightsAndUiViewportCoordinates()
    {
        var layout = CreateLayout(
            1600,
            900,
            showingHealth: true,
            maxHealth: 205,
            maxStamina: 370,
            new HudBarLayoutRequest("hunger", 200, 48, 224),
            new HudBarLayoutRequest("sanity", 100, 48, 224)
        );

        Assert.Equal(new HudBounds(1544, 598, 48, 286), layout.Vanilla.EnergyBounds);
        Assert.Equal(new HudBounds(1488, 555, 48, 329), layout.Vanilla.HealthBounds);
        Assert.True(layout.TryGetBounds("hunger", out var hungerBounds));
        Assert.True(layout.TryGetBounds("sanity", out var sanityBounds));
        Assert.Equal(new HudBounds(1368, 660, 48, 224), hungerBounds);
        Assert.Equal(new HudBounds(1428, 660, 48, 224), sanityBounds);
        Assert.Equal(layout.Vanilla.HealthBounds.Bottom, sanityBounds.Bottom);
    }

    [Theory]
    [InlineData("hunger")]
    [InlineData("sanity")]
    public void ASingleVisibleStatusBarUsesTheFirstCustomSlot(string visibleId)
    {
        var layout = CreateLayout(
            1000,
            800,
            showingHealth: false,
            maxHealth: 100,
            maxStamina: 270,
            new HudBarLayoutRequest(visibleId, 10, 48, 224)
        );

        Assert.True(layout.TryGetBounds(visibleId, out var bounds));
        Assert.Equal(new HudBounds(884, 560, 48, 224), bounds);
        Assert.False(layout.TryGetBounds("hidden", out _));
    }

    [Fact]
    public void LayoutOrdersAnyNumberOfVisibleBarsWithoutStatusSpecificSlots()
    {
        var layout = CreateLayout(
            1000,
            800,
            showingHealth: false,
            maxHealth: 100,
            maxStamina: 270,
            new HudBarLayoutRequest("temperature", 30, 40, 180),
            new HudBarLayoutRequest("hunger", 20, 48, 224),
            new HudBarLayoutRequest("sanity", 10, 48, 224)
        );

        Assert.True(layout.TryGetBounds("sanity", out var sanityBounds));
        Assert.True(layout.TryGetBounds("hunger", out var hungerBounds));
        Assert.True(layout.TryGetBounds("temperature", out var temperatureBounds));
        Assert.Equal(new HudBounds(884, 560, 48, 224), sanityBounds);
        Assert.Equal(new HudBounds(824, 560, 48, 224), hungerBounds);
        Assert.Equal(new HudBounds(772, 604, 40, 180), temperatureBounds);
    }

    [Fact]
    public void SharedFrameUsesOneContainerForDrawingAndMouseHitTesting()
    {
        var success = HudStatusBarFrameRules.TryCreateFrame(
            new HudBounds(884, 560, 48, 224),
            24,
            0.25,
            out var frame,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(new HudBounds(884, 560, 48, 224), frame.ContainerBounds);
        Assert.Equal(frame.ContainerBounds, frame.HitBounds);
        Assert.Equal(new HudPoint(920, 775), frame.FillerPosition);
        Assert.Equal(24, frame.FillerWidth);
        Assert.Equal(42, frame.FillerHeight);
        Assert.True(frame.HitBounds.Contains(884, 560));
        Assert.True(frame.HitBounds.Contains(931, 783));
        Assert.False(frame.HitBounds.Contains(932, 783));
        Assert.False(frame.HitBounds.Contains(931, 784));
    }

    [Theory]
    [InlineData(0, 224, 24, 0.5, "hud-frame-layout-dimensions-are-invalid")]
    [InlineData(48, 224, 0, 0.5, "hud-frame-layout-dimensions-are-invalid")]
    [InlineData(48, 224, 24, double.NaN, "hud-frame-fill-ratio-is-invalid")]
    [InlineData(48, 224, 24, 1.1, "hud-frame-fill-ratio-is-invalid")]
    public void SharedFrameRejectsInvalidGeometryAndFillRatio(
        int boundsWidth,
        int boundsHeight,
        int fillerWidth,
        double fillRatio,
        string expectedReason
    )
    {
        var success = HudStatusBarFrameRules.TryCreateFrame(
            new HudBounds(0, 0, boundsWidth, boundsHeight),
            fillerWidth,
            fillRatio,
            out _,
            out var reason
        );

        Assert.False(success);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void HungerValuesClampNegativeRuntimeValueBeforeBuildingFillRatio()
    {
        var success = HungerHudRules.TryCreateValues(
            -3,
            150,
            out var values,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(0, values.Current);
        Assert.Equal(150, values.Maximum);
        Assert.Equal(0, values.FillRatio);
    }

    [Fact]
    public void HungerValuesProduceFillRatioFromTheClampedDisplayValue()
    {
        var success = HungerHudRules.TryCreateValues(
            75,
            150,
            out var values,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(75, values.Current);
        Assert.Equal(0.5, values.FillRatio);
    }

    [Theory]
    [InlineData(double.NaN, 150, "hunger-hud-current-is-not-finite")]
    [InlineData(1, double.PositiveInfinity, "hunger-hud-maximum-is-not-finite")]
    [InlineData(0, 0, "hunger-hud-maximum-is-not-positive")]
    [InlineData(151, 150, "hunger-hud-current-is-out-of-range")]
    public void HungerValuesPreserveExistingFailClosedRules(
        double current,
        double maximum,
        string expectedReason
    )
    {
        var success = HungerHudRules.TryCreateValues(
            current,
            maximum,
            out _,
            out var reason
        );

        Assert.False(success);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData(double.NaN, 200, "sanity-hud-current-is-not-finite")]
    [InlineData(100, double.PositiveInfinity, "sanity-hud-maximum-is-not-finite")]
    [InlineData(0, 0, "sanity-hud-maximum-is-not-positive")]
    [InlineData(-1, 200, "sanity-hud-current-is-out-of-range")]
    [InlineData(201, 200, "sanity-hud-current-is-out-of-range")]
    public void SanityValuesKeepStrictRangeValidation(
        double current,
        double maximum,
        string expectedReason
    )
    {
        var success = SanityHudRules.TryCreateValues(
            current,
            maximum,
            out _,
            out var reason
        );

        Assert.False(success);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0.5)]
    [InlineData(200, 1)]
    public void SanityValuesProduceValidatedNormalizedFillRatio(
        double current,
        double expectedRatio
    )
    {
        var success = SanityHudRules.TryCreateValues(
            current,
            200,
            out var values,
            out var reason
        );

        Assert.True(success, reason);
        Assert.Equal(expectedRatio, values.FillRatio);
    }

    private static LayoutFixture CreateLayout(
        int viewportWidth,
        int viewportHeight,
        bool showingHealth,
        int maxHealth,
        int maxStamina,
        params HudBarLayoutRequest[] requests
    )
    {
        var requestBuffer = new List<HudBarLayoutRequest>(requests);
        var vanilla = HudDisplayRules.CreateVanillaLayout(
            viewportWidth,
            viewportHeight,
            showingHealth,
            maxHealth,
            maxStamina
        );
        return new LayoutFixture(vanilla, requestBuffer);
    }

    private readonly record struct LayoutFixture(
        VanillaHudLayout Vanilla,
        List<HudBarLayoutRequest> Requests
    )
    {
        internal bool TryGetBounds(string id, out HudBounds bounds)
        {
            return HudDisplayRules.TryGetBarBounds(Vanilla, Requests, id, out bounds);
        }
    }
}
