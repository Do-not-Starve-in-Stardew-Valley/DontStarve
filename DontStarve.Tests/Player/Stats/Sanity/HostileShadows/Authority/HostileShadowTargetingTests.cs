using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowTargetingTests
{
    private const string Owner = "123456789";
    private const string Attacker = "223456789";
    private const string Observer = "323456789";

    [Fact]
    public void Owner_is_preferred_over_a_nearer_observer_in_the_same_location()
    {
        var index = Index(
            Player(Owner, "Farm", 100, 0),
            Player(Observer, "Farm", 10, 0)
        );

        var result = Evaluate(index);

        Assert.Equal(Owner, result.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.Owner, result.TargetSource);
        Assert.Equal(HostileShadowStateIds.Chase, result.StateId);
    }

    [Fact]
    public void Host_revalidated_recent_attacker_overrides_owner_without_changing_owner()
    {
        var index = Index(
            Player(Owner, "Farm", 20, 0),
            Player(Attacker, "Farm", 40, 0)
        );

        var result = Evaluate(index, recentAttacker: Attacker);

        Assert.Equal(Attacker, result.TargetPlayerKey);
        Assert.Equal(
            HostileShadowTargetSource.RecentAttacker,
            result.TargetSource
        );
    }

    [Fact]
    public void Disconnected_attacker_is_dropped_and_owner_is_reselected()
    {
        var index = Index(Player(Owner, "Farm", 20, 0));

        var result = Evaluate(index, recentAttacker: Attacker);

        Assert.Equal(Owner, result.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.Owner, result.TargetSource);
    }

    [Fact]
    public void Owner_off_map_does_not_move_entity_and_remaining_player_is_targeted()
    {
        var index = Index(
            Player(Owner, "Town", 20, 0),
            Player(Observer, "Farm", 40, 0)
        );

        var result = Evaluate(index);

        Assert.Equal(Observer, result.TargetPlayerKey);
        Assert.Equal(
            HostileShadowTargetSource.NearestPlayer,
            result.TargetSource
        );
        Assert.Equal(10d, result.PositionX);
        Assert.Equal(20d, result.PositionY);
    }

    [Fact]
    public void Everyone_off_map_leaves_entity_idle_and_ttl_continues()
    {
        var index = Index(Player(Owner, "Town", 20, 0));

        var result = Evaluate(index, currentMinute: 119);

        Assert.False(result.NaturalTtlExpired);
        Assert.Equal(HostileShadowStateIds.Idle, result.StateId);
        Assert.Equal(string.Empty, result.TargetPlayerKey);
    }

    [Fact]
    public void Natural_ttl_starts_when_target_is_lost_and_expires_after_two_hours()
    {
        var index = Index(Player(Owner, "Farm", 5000, 0, isDangerActive: false));

        var before = Evaluate(index, currentMinute: 119, noTargetSince: 0);
        var atLimit = Evaluate(index, currentMinute: 120, noTargetSince: 0);

        Assert.False(before.NaturalTtlExpired);
        Assert.True(atLimit.NaturalTtlExpired);
    }

    [Fact]
    public void Natural_ttl_continues_when_another_player_is_online_on_the_shadow_map()
    {
        var index = Index(
            Player(Owner, "Town", 20, 0, isDangerActive: false),
            Player(Observer, "Farm", 5000, 0, isDangerActive: false)
        );

        var result = Evaluate(index, currentMinute: 120, noTargetSince: 0);

        Assert.True(result.NaturalTtlExpired);
        Assert.Equal(string.Empty, result.TargetPlayerKey);
    }

    [Fact]
    public void Ordinary_targeting_does_not_acquire_a_high_sanity_player_without_a_lock()
    {
        var index = Index(
            Player(Owner, "Farm", 20, 0, isDangerActive: false),
            Player(Observer, "Farm", 30, 0, isDangerActive: false),
            Player(Attacker, "Farm", 40, 0, isDangerActive: false)
        );

        var ordinary = Evaluate(index);

        Assert.Equal(string.Empty, ordinary.TargetPlayerKey);
        Assert.Equal(HostileShadowStateIds.Idle, ordinary.StateId);
    }

    [Fact]
    public void Existing_high_sanity_target_is_preserved_and_recent_attacker_still_overrides_it()
    {
        var index = Index(
            Player(Owner, "Farm", 100_000, 0, isDangerActive: false),
            Player(Observer, "Farm", 30, 0, isDangerActive: true),
            Player(Attacker, "Farm", 40, 0, isDangerActive: false)
        );

        var ordinary = Evaluate(index, lockedTarget: Owner);
        var recentAttacker = Evaluate(
            index,
            recentAttacker: Attacker,
            lockedTarget: Owner
        );

        Assert.Equal(Owner, ordinary.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.LockedTarget, ordinary.TargetSource);
        Assert.Equal(Attacker, recentAttacker.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.RecentAttacker, recentAttacker.TargetSource);
    }

    [Fact]
    public void Natural_ttl_does_not_use_birth_age_when_target_is_still_locked()
    {
        var result = Evaluate(
            Index(Player(Owner, "Farm", 20, 0)),
            currentMinute: 120,
            noTargetSince: null
        );

        Assert.False(result.NaturalTtlExpired);
        Assert.Equal(Owner, result.TargetPlayerKey);
    }

    [Fact]
    public void Natural_ttl_does_not_expire_when_no_player_is_on_the_shadow_map()
    {
        var result = Evaluate(
            Index(Player(Owner, "Town", 20, 0)),
            currentMinute: 120,
            noTargetSince: 0
        );

        Assert.False(result.NaturalTtlExpired);
        Assert.Equal(string.Empty, result.TargetPlayerKey);
    }

    [Fact]
    public void Natural_ttl_expiration_is_based_on_no_target_time_not_birth_time()
    {
        var result = Evaluate(
            Index(Player(Owner, "Farm", 5000, 0, isDangerActive: false)),
            currentMinute: 130,
            noTargetSince: 10
        );

        Assert.True(result.NaturalTtlExpired);
    }

    [Fact]
    public void Aggro_lock_targets_a_far_high_sanity_player_without_the_detection_limit()
    {
        var result = Evaluate(
            Index(
                Player(Owner, "Farm", 20, 0, isDangerActive: false),
                Player(Attacker, "Farm", 100_000, 0, isDangerActive: false)
            ),
            aggroLock: Attacker
        );

        Assert.Equal(Attacker, result.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.RecentAttacker, result.TargetSource);
        Assert.Equal(HostileShadowStateIds.Chase, result.StateId);
    }

    [Fact]
    public void Retreat_subject_prefers_the_active_aggro_lock_over_the_snapshot_target()
    {
        var subject = HostileShadowRetreatSubjectSelector.Select(
            activeAggroLockPlayerKey: Attacker,
            targetPlayerKey: Owner,
            ownerPlayerKey: Owner
        );

        Assert.Equal(Attacker, subject);
    }

    [Fact]
    public void Retreat_subject_is_empty_when_no_target_is_locked()
    {
        Assert.Equal(
            Owner,
            HostileShadowRetreatSubjectSelector.Select(
                activeAggroLockPlayerKey: string.Empty,
                targetPlayerKey: Owner,
                ownerPlayerKey: Owner
            )
        );
        Assert.Equal(
            string.Empty,
            HostileShadowRetreatSubjectSelector.Select(
                activeAggroLockPlayerKey: string.Empty,
                targetPlayerKey: string.Empty,
                ownerPlayerKey: Owner
            )
        );
    }

    [Fact]
    public void No_target_timer_starts_at_the_current_minute_after_target_loss()
    {
        var index = Index(Player(Owner, "Farm", 5000, 0, isDangerActive: false));

        var justLost = Evaluate(index, currentMinute: 50);
        var beforeExpiry = Evaluate(
            index,
            currentMinute: 169,
            noTargetSince: 50,
            noTargetElapsed: 119
        );
        var expired = Evaluate(
            index,
            currentMinute: 170,
            noTargetSince: 50,
            noTargetElapsed: 120
        );

        Assert.False(justLost.NaturalTtlExpired);
        Assert.False(beforeExpiry.NaturalTtlExpired);
        Assert.True(expired.NaturalTtlExpired);
    }

    [Fact]
    public void Natural_ttl_does_not_expire_from_birth_age_alone()
    {
        var result = Evaluate(
            Index(Player(Owner, "Farm", 20, 0)),
            currentMinute: 120
        );

        Assert.False(result.NaturalTtlExpired);
    }

    /*
    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    [InlineData(180, true)]
    public void Old_birth_age_ttl_contract(
        long currentMinute,
        bool expired
    )
    {
        var result = Evaluate(Index(), currentMinute: currentMinute);

        Assert.Equal(expired, result.NaturalTtlExpired);
    }
    */

    [Fact]
    public void Targeted_shadow_does_not_expire_from_birth_age_alone()
    {
        var result = Evaluate(
            Index(Player(Owner, "Farm", 20, 0)),
            currentMinute: 120
        );

        Assert.False(result.NaturalTtlExpired);
        Assert.Equal(Owner, result.TargetPlayerKey);
    }

    [Fact]
    public void Location_partitions_prevent_cross_location_targeting()
    {
        var index = Index(
            Player(Owner, "Town", 1, 0),
            Player(Observer, "Mine", 1, 0),
            Player(Attacker, "Farm", 30, 0)
        );

        var result = Evaluate(index);

        Assert.Equal(Attacker, result.TargetPlayerKey);
        Assert.Equal(
            HostileShadowTargetSource.NearestPlayer,
            result.TargetSource
        );
    }

    [Fact]
    public void Movement_is_normalized_direct_and_stops_at_range_without_pathfinding()
    {
        var moved = HostileShadowTargetingEngine.AdvancePosition(
            positionX: 0,
            positionY: 0,
            standingX: 0,
            standingY: 0,
            targetStandingX: 3,
            targetStandingY: 4,
            movementSpeed: 1,
            stopDistancePixels: 0,
            elapsedSeconds: 1d / 60d
        );
        var stopped = HostileShadowTargetingEngine.AdvancePosition(
            positionX: 0,
            positionY: 0,
            standingX: 0,
            standingY: 0,
            targetStandingX: 3,
            targetStandingY: 4,
            movementSpeed: 10,
            stopDistancePixels: 5,
            elapsedSeconds: 1d
        );

        Assert.True(moved.Valid);
        Assert.Equal(0.6d, moved.PositionX, 10);
        Assert.Equal(0.8d, moved.PositionY, 10);
        Assert.Equal(0d, stopped.PositionX);
        Assert.Equal(0d, stopped.PositionY);
    }

    [Fact]
    public void Player_index_fails_closed_at_its_bounded_capacity()
    {
        var players = Enumerable
            .Range(1, HostileShadowTargetingLimits.MaximumPlayers + 1)
            .Select(index =>
                Player(index.ToString(), "Farm", index, index)
            )
            .ToArray();
        var index = new HostileShadowLocationPlayerIndex();

        var result = index.Rebuild(players);

        Assert.False(result.Success);
        Assert.Equal(0, index.PlayerCount);
        Assert.Equal(0, index.LocationCount);
    }

    private static HostileShadowTargetingDecision Evaluate(
        HostileShadowLocationPlayerIndex index,
        string aggroLock = "",
        string recentAttacker = "",
        string lockedTarget = "",
        long currentMinute = 0,
        long? noTargetSince = null,
        long? noTargetElapsed = null
    )
    {
        return HostileShadowTargetingEngine.Evaluate(
            new HostileShadowTargetingInput
            {
                EntityId = 1,
                OwnerPlayerKey = Owner,
                LocationId = "Farm",
                AggroLockPlayerKey = aggroLock,
                RecentAttackerPlayerKey = recentAttacker,
                LockedTargetPlayerKey = lockedTarget,
                PositionX = 10,
                PositionY = 20,
                StandingX = 0,
                StandingY = 0,
                MovementSpeed = 2,
                DetectionRadiusPixels = 1280,
                StopDistancePixels = 64,
                SpawnGameMinute = 0,
                CurrentGameMinute = currentMinute,
                NaturalTtlMinutes = 120,
                NoTargetSinceGameMinute = noTargetSince,
                NoTargetElapsedGameMinutes = noTargetElapsed,
                ElapsedSeconds = 0,
            },
            index
        );
    }

    private static HostileShadowLocationPlayerIndex Index(
        params HostileShadowPlayerSample[] players
    )
    {
        var index = new HostileShadowLocationPlayerIndex();
        var result = index.Rebuild(players);
        Assert.True(result.Success, result.Reason);
        return index;
    }

    private static HostileShadowPlayerSample Player(
        string key,
        string location,
        double x,
        double y,
        bool isDangerActive = true
    )
    {
        return new HostileShadowPlayerSample(key, location, x, y, isDangerActive);
    }
}
