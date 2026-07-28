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

    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    [InlineData(180, true)]
    public void Natural_ttl_expires_at_two_game_hours(
        long currentMinute,
        bool expired
    )
    {
        var result = Evaluate(Index(), currentMinute: currentMinute);

        Assert.Equal(expired, result.NaturalTtlExpired);
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
        string recentAttacker = "",
        long currentMinute = 0
    )
    {
        return HostileShadowTargetingEngine.Evaluate(
            new HostileShadowTargetingInput
            {
                EntityId = 1,
                OwnerPlayerKey = Owner,
                LocationId = "Farm",
                RecentAttackerPlayerKey = recentAttacker,
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
        double y
    )
    {
        return new HostileShadowPlayerSample(key, location, x, y);
    }
}
