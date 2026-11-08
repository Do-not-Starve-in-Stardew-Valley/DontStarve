using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class FlowerCollectionSanityTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("(O)591", -80, 5d)] // tulip
    [InlineData("(O)421", -80, 5d)] // sunflower, before its harvest ID becomes seeds
    [InlineData("(O)402", -80, 5d)] // sweet pea
    [InlineData("(O)418", -80, 5d)] // crocus
    [InlineData("(O)CustomFlower", -80, 5d)]
    [InlineData("(O)18", -81, 5d)]
    [InlineData("(O)22", -81, 5d)]
    [InlineData("(O)16", -81, 0d)]
    [InlineData("(O)24", -75, 0d)]
    [InlineData("(O)431", -74, 0d)]
    [InlineData("(F)18", -80, 0d)]
    [InlineData("18", -81, 0d)]
    [InlineData(null, -80, 0d)]
    public void Only_flower_objects_and_the_two_explicit_forage_exceptions_restore_sanity(
        string? itemId, int category, double expected)
    {
        Assert.Equal(expected, FlowerCollectionSanityPolicy.GetRecovery(itemId, category));
    }

    [Theory]
    [InlineData(false, true, true, false, false, 0, false, 0, true)]
    [InlineData(false, true, false, false, false, 0, true, 4, true)]
    [InlineData(false, true, false, false, true, 0, true, 4, true)]
    [InlineData(false, true, false, false, false, 0, false, 0, false)] // full inventory
    [InlineData(true, true, true, false, false, 0, false, 0, false)]
    [InlineData(false, false, true, false, false, 0, false, 0, false)]
    [InlineData(false, true, true, true, false, 0, false, 0, false)]
    public void Crop_reward_requires_a_live_ready_player_harvest_including_regrowth(
        bool dead, bool ready, bool removed, bool junimo,
        bool oldGrown, int oldDays, bool newGrown, int newDays, bool expected)
    {
        Assert.Equal(expected, FlowerCollectionSanityPolicy.WasCropHarvested(
            dead, ready, junimo, removed, oldGrown, oldDays, newGrown, newDays));
    }

    [Fact]
    public void Reward_is_given_to_the_collector_only_and_clamped_by_the_real_sanity_service()
    {
        var sanity = Host();
        var settlements = new FlowerCollectionSanitySettlement();
        var request = Request();
        var result = Apply(settlements, sanity, request);

        Assert.Equal(FlowerCollectionSettlementStatus.Applied, result);
        Assert.Equal(100d, sanity.GetCurrent("1"));
        Assert.Equal(200d, sanity.GetCurrent("2")); // 198 + 5 clamps at 200
        Assert.True(sanity.TryGetSnapshot("2", out var snapshot));
        Assert.Equal(1, snapshot.Revision);
    }

    [Fact]
    public void Replayed_receipts_or_another_owner_claiming_the_same_source_do_not_repeat_rewards()
    {
        var sanity = Host();
        var settlements = new FlowerCollectionSanitySettlement();
        var request = Request();
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(settlements, sanity, request));
        Assert.Equal(FlowerCollectionSettlementStatus.Duplicate, Apply(settlements, sanity, request));
        request.Nonce++;
        request.PlayerId = 1;
        Assert.Equal(FlowerCollectionSettlementStatus.Duplicate, Apply(settlements, sanity, request));
        Assert.Equal(100d, sanity.GetCurrent("1"));
    }

    [Fact]
    public void Separate_plants_are_not_lost_when_they_are_harvested_in_the_same_frame()
    {
        var sanity = Host();
        sanity.Set("2", 100, SanityChangeSource.Administration);
        var settlements = new FlowerCollectionSanitySettlement();
        for (var i = 0; i < 9; i++)
        {
            var request = Request();
            request.TileX += i;
            request.Nonce += i;
            Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(settlements, sanity, request));
        }
        Assert.Equal(145d, sanity.GetCurrent("2"));
    }

    [Theory]
    [InlineData(false, true, true, -80, (int)FlowerCollectionSettlementStatus.Rejected)]
    [InlineData(true, false, true, -80, (int)FlowerCollectionSettlementStatus.Rejected)]
    [InlineData(true, true, false, -80, (int)FlowerCollectionSettlementStatus.AwaitingWorld)]
    [InlineData(true, true, true, -75, (int)FlowerCollectionSettlementStatus.Rejected)]
    public void Host_checks_owner_location_reach_world_completion_and_item_category(
        bool ownerAtLocation, bool withinReach, bool collected, int category,
        int expected)
    {
        var sanity = Host();
        var result = Apply(new(), sanity, Request(), new(ownerAtLocation, withinReach, collected, category));
        Assert.Equal((FlowerCollectionSettlementStatus)expected, result);
        Assert.Equal(198d, sanity.GetCurrent("2"));
    }

    [Fact]
    public void Foreign_session_day_or_sender_cannot_award_sanity()
    {
        var sanity = Host();
        var settlements = new FlowerCollectionSanitySettlement();
        var request = Request();
        Assert.Equal(FlowerCollectionSettlementStatus.Rejected,
            settlements.TryApply(request, 9, Session, 10, new(true, true, true, -80),
                (owner, amount) => sanity.Change(owner.ToString(), amount, SanityChangeSource.FlowerCollection)));
        request.SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.Equal(FlowerCollectionSettlementStatus.Rejected, Apply(settlements, sanity, request));
        request.SessionId = Session;
        request.Day = 9;
        Assert.Equal(FlowerCollectionSettlementStatus.Rejected, Apply(settlements, sanity, request));
        Assert.Equal(198d, sanity.GetCurrent("2"));
    }

    [Fact]
    public void World_sync_may_complete_later_without_losing_or_repeating_the_reward()
    {
        var sanity = Host();
        var settlements = new FlowerCollectionSanitySettlement();
        Assert.Equal(FlowerCollectionSettlementStatus.AwaitingWorld,
            Apply(settlements, sanity, Request(), new(true, true, false, -80)));
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(settlements, sanity, Request()));
    }

    [Fact]
    public void New_day_and_session_clear_receipts_and_allow_new_flowers_at_the_same_tile()
    {
        var sanity = Host();
        var settlements = new FlowerCollectionSanitySettlement();
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(settlements, sanity, Request()));
        settlements.Clear();
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(settlements, sanity, Request()));
    }

    [Fact]
    public void Disabled_system_and_debug_lock_keep_their_existing_sanity_guards()
    {
        var sanity = Host();
        sanity.SetTierSystemEnabled(false);
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(new(), sanity, Request()));
        Assert.Equal(198d, sanity.GetCurrent("2"));
        sanity.SetTierSystemEnabled(true);
        sanity.SetDebugSanityLocked(true);
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(new(), sanity, Request()));
        Assert.Equal(198d, sanity.GetCurrent("2"));
    }

    [Fact]
    public void Signed_nonzero_player_ids_are_valid_and_event_overlays_still_freeze_rewards()
    {
        var sanity = Host();
        Assert.True(sanity.TryEnsureHostPlayer("-7", out _, out _));
        sanity.Set("-7", 100, SanityChangeSource.Administration);
        var request = Request();
        request.PlayerId = -7;
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(new(), sanity, request));
        Assert.Equal(105d, sanity.GetCurrent("-7"));
        sanity.SetEffectiveOverlayOwnerActive("-7", true);
        request.TileX++;
        Assert.Equal(FlowerCollectionSettlementStatus.Applied, Apply(new(), sanity, request));
        Assert.Equal(105d, sanity.GetCurrent("-7"));
    }

    private static FlowerCollectionSettlementStatus Apply(
        FlowerCollectionSanitySettlement settlements, SanityChangeService sanity,
        FlowerCollectionRequest request, FlowerCollectionAuthorityObservation? observation = null)
    {
        return settlements.TryApply(request, request.PlayerId, Session, 10,
            observation ?? new(true, true, true, -80),
            (owner, amount) => sanity.Change(owner.ToString(), amount, SanityChangeSource.FlowerCollection));
    }

    private static FlowerCollectionRequest Request() => new()
    {
        SessionId = Session, PlayerId = 2, Nonce = 1, Day = 10,
        LocationName = "Farm", TileX = 12, TileY = 8,
        QualifiedItemId = "(O)591", Kind = FlowerCollectionKind.Crop,
    };

    private static SanityChangeService Host()
    {
        var data = new SanitySaveData();
        data.Players["1"] = new() { Current = 100, MaxAtSave = 200 };
        data.Players["2"] = new() { Current = 198, MaxAtSave = 200 };
        var persistence = new SanityPersistenceResult(SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite, "flowers-test", 100, data);
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        Assert.True(service.BeginHostSession(Session, persistence, "1", out _));
        Assert.True(service.TryEnsureHostPlayer("2", out _, out _));
        return service;
    }
}
