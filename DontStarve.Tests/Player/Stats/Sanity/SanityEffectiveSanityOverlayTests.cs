using DontStarve.Player.Stats.Sanity.Events;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityEffectiveSanityOverlayTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Two_owners_are_independent_and_effective_ratio_is_full()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        var ownerA = Key("1", 0);
        var ownerB = Key("2", 1);

        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Applied,
            overlay.Begin(ownerA, SanityEffectiveOverlayReason.RegularEvent, "100", 1).Status
        );
        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Applied,
            overlay.Begin(ownerB, SanityEffectiveOverlayReason.Festival, "festival", 2).Status
        );

        Assert.True(overlay.TryGetEffectiveRatio(ownerA, out var ratioA, out var reasonA));
        Assert.Equal(1d, ratioA);
        Assert.Equal(SanityEffectiveOverlayReason.RegularEvent, reasonA);
        Assert.True(overlay.TryGetEffectiveRatio(ownerB, out var ratioB, out var reasonB));
        Assert.Equal(1d, ratioB);
        Assert.Equal(SanityEffectiveOverlayReason.Festival, reasonB);
    }

    [Fact]
    public void Duplicate_start_is_idempotent_and_stale_or_conflicting_revision_is_rejected()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        var key = Key("1", 0);
        overlay.Begin(key, SanityEffectiveOverlayReason.RegularEvent, "100", 4);

        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.NoChange,
            overlay.Begin(key, SanityEffectiveOverlayReason.RegularEvent, "100", 4).Status
        );
        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Stale,
            overlay.Begin(key, SanityEffectiveOverlayReason.RegularEvent, "100", 3).Status
        );
        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Rejected,
            overlay.Begin(key, SanityEffectiveOverlayReason.Festival, "200", 4).Status
        );
        Assert.Equal(1, overlay.Count);
    }

    [Fact]
    public void Nested_ordinary_replacement_requires_higher_revision_and_matching_end()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        var key = Key("1", 0);
        overlay.Begin(key, SanityEffectiveOverlayReason.RegularEvent, "100", 1);
        overlay.Begin(key, SanityEffectiveOverlayReason.Festival, "200", 2);

        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Rejected,
            overlay.EndOrdinary(key, "100", 3).Status
        );
        Assert.True(overlay.TryGetSnapshot(key, out var current));
        Assert.Equal("200", current.EventId);
        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Applied,
            overlay.EndOrdinary(key, "200", 3).Status
        );
        Assert.False(overlay.TryGetSnapshot(key, out _));
    }

    [Fact]
    public void Ordinary_start_and_end_cannot_replace_or_cancel_special_flow()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        var key = Key("1", 0);
        overlay.Begin(
            key,
            SanityEffectiveOverlayReason.SanityTwoAmSpecial,
            "sanity-2am",
            10
        );

        var ordinary = overlay.Begin(
            key,
            SanityEffectiveOverlayReason.RegularEvent,
            "vanilla-event",
            11
        );
        var ordinaryEnd = overlay.EndOrdinary(key, "vanilla-event", 12);

        Assert.Equal(SanityEffectiveOverlayMutationStatus.NoChange, ordinary.Status);
        Assert.Equal("effective-overlay-special-flow-preserved", ordinary.Reason);
        Assert.Equal(SanityEffectiveOverlayMutationStatus.NoChange, ordinaryEnd.Status);
        Assert.True(overlay.TryGetSnapshot(key, out var preserved));
        Assert.Equal(SanityEffectiveOverlayReason.SanityTwoAmSpecial, preserved.Reason);
        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Applied,
            overlay.EndSpecial(key, "sanity-2am", 13).Status
        );
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void Warp_title_session_and_invalid_screen_cleanup_are_bounded_and_idempotent()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        overlay.Begin(Key("1", 0), SanityEffectiveOverlayReason.RegularEvent, "100", 1);
        overlay.Begin(Key("2", 1), SanityEffectiveOverlayReason.Festival, "200", 2);

        Assert.Single(overlay.ClearScreen(0));
        Assert.Empty(overlay.ClearScreen(0));
        Assert.Single(overlay.ClearInvalidScreens(screen => screen == 0));
        Assert.Empty(overlay.ClearSession(Session));
        Assert.Empty(overlay.ClearAll());
    }

    [Fact]
    public void Owner_capacity_is_hard_bounded()
    {
        var overlay = new SanityEffectiveSanityOverlay();
        for (var index = 0; index < SanityEffectiveSanityOverlay.MaximumOwners; index++)
        {
            Assert.Equal(
                SanityEffectiveOverlayMutationStatus.Applied,
                overlay.Begin(
                    Key((index + 1).ToString(), index),
                    SanityEffectiveOverlayReason.RegularEvent,
                    (100 + index).ToString(),
                    index + 1
                ).Status
            );
        }

        Assert.Equal(
            SanityEffectiveOverlayMutationStatus.Rejected,
            overlay.Begin(
                Key("99", 99),
                SanityEffectiveOverlayReason.RegularEvent,
                "overflow",
                99
            ).Status
        );
        Assert.Equal(SanityEffectiveSanityOverlay.MaximumOwners, overlay.Count);
    }

    private static SanityEffectiveOverlayKey Key(string owner, int screen)
    {
        return new SanityEffectiveOverlayKey(owner, screen, Session);
    }
}
