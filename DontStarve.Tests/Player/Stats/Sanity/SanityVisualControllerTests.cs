using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVisualControllerTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Capability_matrix_exposes_all_owner_local_production_layers()
    {
        AssertCapability(SanityVisualCapabilityCatalog.LowSaturation, SanityVisualCapabilityStatus.AvailableProduction, "available-owner-local-world-composition");
        AssertCapability(SanityVisualCapabilityCatalog.ViewShake, SanityVisualCapabilityStatus.AvailableProduction, "available-owner-local-transform");
        AssertCapability(SanityVisualCapabilityCatalog.DangerBorder, SanityVisualCapabilityStatus.AvailableProduction, "available-owner-local-hud-nine-slice");
        AssertCapability(SanityVisualCapabilityCatalog.Grayscale, SanityVisualCapabilityStatus.AvailableProduction, "available-owner-local-world-composition");
        AssertCapability(SanityVisualCapabilityCatalog.IdlePresentation, SanityVisualCapabilityStatus.AvailableProduction, "available-owner-local-non-passout-token");
    }

    [Fact]
    public void Frozen_visual_tiers_and_safe_border_strength_are_explicit()
    {
        AssertTier(SanityTierIds.DarkHand, 0.75d, 0.75d);
        AssertTier(SanityTierIds.Eyes, 0.60d, 0.60d);
        AssertTier(SanityTierIds.Danger, 0.15d, 0.175d);
        AssertTier(SanityTierIds.Terrorbeak, 0.10d, 0.10d);
        Assert.Equal(0.8f, SanityVisualController.DangerBorderOpacity);
    }

    [Theory]
    [InlineData(SanityTierIds.DarkHand, (int)SanityVisualLayerMask.LowSaturation)]
    [InlineData(SanityTierIds.Eyes, (int)SanityVisualLayerMask.ViewShake)]
    [InlineData(SanityTierIds.Danger, (int)SanityVisualLayerMask.DangerBorder)]
    [InlineData(SanityTierIds.Terrorbeak, (int)SanityVisualLayerMask.Grayscale)]
    public void Frozen_tier_ids_map_to_the_four_requested_visual_layers(
        string tierId,
        int expectedValue
    )
    {
        var expected = (SanityVisualLayerMask)expectedValue;
        var controller = new SanityVisualController();
        var key = Key("1", 0);

        Assert.Equal(
            SanityVisualMutationStatus.Applied,
            controller.Observe(Observation(key, 1, new[] { tierId })).Status
        );
        Assert.True(controller.TryGetSnapshot(key, out var snapshot));
        Assert.Equal(expected, snapshot.RequestedLayers);
        Assert.Equal(expected, snapshot.RenderableLayers);
    }

    [Fact]
    public void Combined_layers_keep_fixed_order_and_all_are_renderable()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(
            Observation(
                key,
                1,
                new[]
                {
                    SanityTierIds.Terrorbeak,
                    SanityTierIds.Danger,
                    SanityTierIds.Eyes,
                    SanityTierIds.DarkHand,
                }
            )
        );

        Assert.True(controller.TryGetSnapshot(key, out var snapshot));
        Assert.Equal(SanityVisualLayerMask.All, snapshot.RequestedLayers);
        Assert.Equal(SanityVisualLayerMask.All, snapshot.RenderableLayers);
        Assert.Equal(
            new[]
            {
                SanityVisualLayerMask.LowSaturation,
                SanityVisualLayerMask.ViewShake,
                SanityVisualLayerMask.DangerBorder,
                SanityVisualLayerMask.Grayscale,
            },
            SanityVisualCapabilityCatalog.LayerOrder
        );
    }

    [Fact]
    public void Existing_effective_sanity_overlay_suppresses_visuals_and_idle()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(
            Observation(
                key,
                1,
                new[] { SanityTierIds.Danger, SanityTierIds.Terrorbeak },
                effectiveSanityOverrideActive: true
            )
        );

        var idle = controller.ObserveIdle(
            key,
            TimeSpan.FromSeconds(2),
            EligibleIdle()
        );
        Assert.True(controller.TryGetSnapshot(key, out var snapshot));
        Assert.Equal(SanityVisualLayerMask.None, snapshot.RenderableLayers);
        Assert.True(snapshot.EffectiveSanityOverrideActive);
        Assert.False(idle.ThresholdReached);
        Assert.Equal("idle.effective-sanity-override", idle.Reason);
    }

    [Fact]
    public void Idle_requires_below_half_and_authoritative_shadow_creatures_tier()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(
            Observation(
                key,
                1,
                new[] { SanityTierIds.ShadowCreatures },
                current: 50d
            )
        );

        Assert.True(controller.TryGetSnapshot(key, out var exactHalf));
        Assert.False(exactHalf.IdleEligible);
        Assert.Equal(
            "idle.sanity-tier-ineligible",
            controller.ObserveIdle(
                key,
                SanityIdleDetector.IdleThreshold,
                EligibleIdle()
            ).Reason
        );

        controller.Observe(
            Observation(
                key,
                2,
                new[] { SanityTierIds.ShadowCreatures },
                current: 49.99d
            )
        );
        Assert.True(controller.TryGetSnapshot(key, out var belowHalf));
        Assert.True(belowHalf.IdleEligible);
        Assert.True(
            controller.ObserveIdle(
                key,
                SanityIdleDetector.IdleThreshold,
                EligibleIdle()
            ).ThresholdReached
        );

        controller.Observe(
            Observation(key, 3, Array.Empty<string>(), current: 10d)
        );
        Assert.True(controller.TryGetSnapshot(key, out var tierMissing));
        Assert.False(tierMissing.IdleEligible);
    }

    [Fact]
    public void Effective_override_edges_do_not_rewrite_base_visual_revision()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(Observation(key, 9, new[] { SanityTierIds.Danger }));

        Assert.Equal(SanityVisualMutationStatus.Applied, controller.UpdateEffectiveSanityOverride(key, true).Status);
        Assert.True(controller.TryGetSnapshot(key, out var covered));
        Assert.Equal(9, covered.Revision);
        Assert.Equal(SanityVisualLayerMask.None, covered.RenderableLayers);

        Assert.Equal(SanityVisualMutationStatus.Applied, controller.UpdateEffectiveSanityOverride(key, false).Status);
        Assert.True(controller.TryGetSnapshot(key, out var restored));
        Assert.Equal(9, restored.Revision);
        Assert.Equal(SanityVisualLayerMask.DangerBorder, restored.RenderableLayers);
        Assert.Equal("idle.effective-sanity-override-ended", restored.Idle.Reason);
    }

    [Fact]
    public void Higher_revision_replaces_while_duplicate_stale_and_conflict_do_not()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        var initial = Observation(key, 4, new[] { SanityTierIds.Danger });

        Assert.Equal(SanityVisualMutationStatus.Applied, controller.Observe(initial).Status);
        Assert.Equal(SanityVisualMutationStatus.IgnoredDuplicate, controller.Observe(initial).Status);
        Assert.Equal(SanityVisualMutationStatus.IgnoredStale, controller.Observe(Observation(key, 3, Array.Empty<string>())).Status);
        Assert.Equal(SanityVisualMutationStatus.Invalid, controller.Observe(Observation(key, 4, Array.Empty<string>())).Status);
        Assert.Equal(SanityVisualMutationStatus.Applied, controller.Observe(Observation(key, 5, Array.Empty<string>())).Status);
        Assert.True(controller.TryGetSnapshot(key, out var snapshot));
        Assert.Equal(5, snapshot.Revision);
        Assert.Equal(SanityVisualLayerMask.None, snapshot.RequestedLayers);
    }

    [Fact]
    public void Two_owner_screens_are_independent_and_screen_cleanup_is_local()
    {
        var controller = new SanityVisualController();
        var first = Key("1", 0);
        var second = Key("2", 1);
        controller.Observe(Observation(first, 1, new[] { SanityTierIds.Danger }));
        controller.Observe(Observation(second, 1, new[] { SanityTierIds.Terrorbeak }));

        Assert.Equal(1, controller.ClearScreen(0));
        Assert.False(controller.TryGetSnapshot(first, out _));
        Assert.True(controller.TryGetSnapshot(second, out _));
        Assert.Equal(0, controller.ClearScreen(0));
    }

    [Fact]
    public void Disabling_immediately_clears_state_and_reenable_requires_fresh_observation()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(Observation(key, 1, new[] { SanityTierIds.Danger }));

        controller.SetEnabled(false);

        Assert.Equal(0, controller.Count);
        Assert.Equal(SanityVisualMutationStatus.Disabled, controller.Observe(Observation(key, 2, new[] { SanityTierIds.Danger })).Status);
        controller.SetEnabled(true);
        Assert.False(controller.TryGetSnapshot(key, out _));
    }

    [Fact]
    public void Owner_capacity_is_hard_bounded()
    {
        var controller = new SanityVisualController();
        for (var index = 0; index < SanityVisualController.MaximumOwners; index++)
        {
            Assert.Equal(
                SanityVisualMutationStatus.Applied,
                controller.Observe(Observation(Key((index + 1).ToString(), index), 1, Array.Empty<string>())).Status
            );
        }

        Assert.Equal(
            SanityVisualMutationStatus.CapacityExceeded,
            controller.Observe(Observation(Key("99", 99), 1, Array.Empty<string>())).Status
        );
        Assert.Equal(SanityVisualController.MaximumOwners, controller.Count);
    }

    [Fact]
    public void Viewport_resize_recomputes_nine_slice_without_changing_revision()
    {
        var controller = new SanityVisualController();
        var key = Key("1", 0);
        controller.Observe(Observation(key, 7, new[] { SanityTierIds.Danger }, width: 1920, height: 1080));
        Assert.True(controller.TryGetSnapshot(key, out var before));

        Assert.Equal(SanityVisualMutationStatus.Applied, controller.UpdateViewport(key, 1280, 720).Status);
        Assert.True(controller.TryGetSnapshot(key, out var after));

        Assert.Equal(7, after.Revision);
        Assert.Equal(1920 - 32, before.DangerBorderLayout.MiddleCenter.Destination.Width);
        Assert.Equal(1280 - 32, after.DangerBorderLayout.MiddleCenter.Destination.Width);
        Assert.Equal(720 - 32, after.DangerBorderLayout.MiddleCenter.Destination.Height);
    }

    [Fact]
    public void Tiny_viewport_scales_margins_without_negative_rectangles()
    {
        Assert.True(SanityNineSliceLayout.TryCreate(64, 64, 16, 16, 16, 16, 24, 20, out var layout, out var reason), reason);

        Assert.Equal(0, layout.MiddleCenter.Destination.Width);
        Assert.Equal(0, layout.MiddleCenter.Destination.Height);
        Assert.All(layout.Slices, slice =>
        {
            Assert.True(slice.Destination.Width >= 0);
            Assert.True(slice.Destination.Height >= 0);
        });
    }

    [Fact]
    public void Owner_session_invalid_screen_and_all_cleanup_are_bounded_and_idempotent()
    {
        var controller = new SanityVisualController();
        controller.Observe(Observation(Key("1", 0), 1, Array.Empty<string>()));
        controller.Observe(Observation(Key("1", 1), 1, Array.Empty<string>()));
        controller.Observe(Observation(new SanityVisualOwnerKey("2", 2, "fedcba9876543210fedcba9876543210"), 1, Array.Empty<string>()));

        Assert.Equal(2, controller.ClearOwner("1"));
        Assert.Equal(0, controller.ClearOwner("1"));
        Assert.Equal(1, controller.ClearInvalidScreens(screen => screen < 2));
        Assert.Equal(0, controller.ClearSession(Session));
        Assert.Equal(0, controller.ClearAll());
    }

    [Fact]
    public void Invalid_observation_fails_closed_without_owner_state()
    {
        var controller = new SanityVisualController();
        var invalid = Observation(Key("1", 0), 1, new[] { SanityTierIds.Danger }) with
        {
            Maximum = double.NaN,
        };

        Assert.Equal(SanityVisualMutationStatus.Invalid, controller.Observe(invalid).Status);
        Assert.Equal(0, controller.Count);
    }

    private static SanityVisualOwnerKey Key(string player, int screen)
    {
        return new SanityVisualOwnerKey(player, screen, Session);
    }

    private static SanityVisualObservation Observation(
        SanityVisualOwnerKey key,
        long revision,
        IReadOnlyCollection<string> tiers,
        bool effectiveSanityOverrideActive = false,
        int width = 1920,
        int height = 1080,
        double current = 10d
    )
    {
        return new SanityVisualObservation(
            key,
            revision,
            current,
            100d,
            tiers,
            effectiveSanityOverrideActive,
            width,
            height
        );
    }

    private static SanityIdleObservation EligibleIdle()
    {
        return new SanityIdleObservation(true, true, true, false, false, false, false, false, false, true, false, false, false, false);
    }

    private static void AssertCapability(
        SanityVisualCapability capability,
        SanityVisualCapabilityStatus status,
        string reason
    )
    {
        Assert.Equal(status, capability.Status);
        Assert.Equal(reason, capability.Reason);
    }

    private static void AssertTier(string tierId, double enter, double exit)
    {
        var rule = Assert.Single(SanityTierCatalog.Rules, value => value.Id == tierId);
        Assert.Equal(enter, rule.EnterRatio);
        Assert.Equal(exit, rule.ExitRatio);
    }
}
