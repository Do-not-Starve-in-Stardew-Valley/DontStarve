using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Movement;

public sealed class TerrorbeakMovementAndAiTests
{
    private const string Owner = "101";
    private const string LocationId = "Farm";

    [Fact]
    public void Shipped_profile_drives_terrorbeak_stats_and_twenty_tile_target_boundary()
    {
        var profile = RuntimeProfile();

        Assert.Equal(400, profile.MaxHealth);
        Assert.Equal(50, profile.BaseDamage);
        Assert.Equal(6d, profile.MovementSpeed);
        Assert.Equal(20d, profile.DetectionRadiusTiles);
        Assert.Equal(1280d, profile.DetectionRadiusPixels);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);
        Assert.Equal(1.2d, profile.AttackIntervalSeconds);
        Assert.Equal(2d, profile.NaturalDespawnGameHours);
        Assert.Equal(
            ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
            profile.PostAttackPolicyId
        );

        var boundary = Evaluate(
            profile,
            new HostileShadowPlayerSample(Owner, LocationId, 1280d, 0d)
        );
        var outside = Evaluate(
            profile,
            new HostileShadowPlayerSample(Owner, LocationId, 1280.001d, 0d)
        );

        Assert.Equal(HostileShadowStateIds.Chase, boundary.StateId);
        Assert.Equal(Owner, boundary.TargetPlayerKey);
        Assert.Equal(HostileShadowStateIds.Idle, outside.StateId);
        Assert.Equal(string.Empty, outside.TargetPlayerKey);
    }

    [Fact]
    public void Speed_six_moves_directly_and_caps_a_large_elapsed_input()
    {
        var normal = HostileShadowTargetingEngine.AdvancePosition(
            0d,
            0d,
            0d,
            0d,
            300d,
            400d,
            movementSpeed: 6d,
            stopDistancePixels: 0d,
            elapsedSeconds: 0.1d
        );
        var lagSized = HostileShadowTargetingEngine.AdvancePosition(
            0d,
            0d,
            0d,
            0d,
            3000d,
            4000d,
            movementSpeed: 6d,
            stopDistancePixels: 0d,
            elapsedSeconds: 1d
        );

        Assert.True(normal.Valid, normal.Reason);
        Assert.Equal(21.6d, normal.PositionX, precision: 10);
        Assert.Equal(28.8d, normal.PositionY, precision: 10);
        Assert.True(lagSized.Valid, lagSized.Reason);
        Assert.Equal(54d, lagSized.PositionX, precision: 10);
        Assert.Equal(72d, lagSized.PositionY, precision: 10);
    }

    [Fact]
    public void First_target_discovery_taunts_once_then_reacquire_goes_directly_to_chase()
    {
        var profile = RuntimeProfile();
        var definition = HostileAttackTestFactory.Definition(profile);
        var random = new SequenceTransitionRandom(0.999999d);
        var machine = new HostileAttackStateMachine(
            definition,
            new TerrorbeakAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                random
            )
        );
        var noTarget = HostileAttackTestFactory.Input(
            hasTarget: false,
            inRange: false,
            intervalSeconds: profile.AttackIntervalSeconds
        );
        var target = HostileAttackTestFactory.Input(
            inRange: false,
            intervalSeconds: profile.AttackIntervalSeconds
        );

        Assert.Equal(
            HostileShadowStateIds.Idle,
            machine.Advance(noTarget, definition.SpawnDurationMilliseconds).StateId
        );
        Assert.Equal(HostileShadowStateIds.Taunt, machine.Advance(target, 0d).StateId);
        Assert.Equal(
            HostileShadowStateIds.Chase,
            machine.Advance(target, definition.TauntDurationMilliseconds).StateId
        );
        Assert.Equal(HostileShadowStateIds.Idle, machine.Advance(noTarget, 0d).StateId);
        Assert.Equal(HostileShadowStateIds.Chase, machine.Advance(target, 0d).StateId);
        Assert.Equal(0, random.CallCount);
    }

    [Theory]
    [InlineData(0d, true, 0.6d)]
    [InlineData(0.249999d, true, 0.6d)]
    [InlineData(0.25d, false, 1.2d)]
    [InlineData(0.999999d, false, 1.2d)]
    public void Injected_rng_boundary_is_exact_and_same_revision_is_memoized(
        double sample,
        bool expectedTaunt,
        double expectedDelay
    )
    {
        var random = new SequenceTransitionRandom(sample, 0d);
        var policy = new TerrorbeakAttackTransitionPolicy(
            HostileAttackTestFactory.EntityId,
            random
        );
        var context = new HostileAttackTransitionContext(
            HostileAttackTestFactory.EntityId,
            HostileAttackTestFactory.PlayerOne,
            HostileAttackTestFactory.AttackRevision
        );

        Assert.True(
            policy.TryResolvePostAttackTransition(context, 1.2d, out var first)
        );
        Assert.True(
            policy.TryResolvePostAttackTransition(context, 1.2d, out var replay)
        );
        Assert.Equal(expectedTaunt, first.EnterTaunt);
        Assert.Equal(expectedDelay, first.NextAttackDelaySeconds);
        Assert.Equal(first, replay);
        Assert.Equal(1, random.CallCount);
    }

    [Fact]
    public void Metadata_factory_and_runtime_bind_four_way_100ms_terrorbeak_movement()
    {
        var profile = RuntimeProfile();
        var metadata = HostileAttackTestFactory.MetadataCatalog();
        Assert.True(metadata.TryGet(profile.AssetBindingId, out var definition));
        Assert.NotNull(definition);
        Assert.Equal(4, definition!.Chase.FrameCount);
        Assert.Equal(100, definition.Chase.FrameDurationMilliseconds);
        AssertDirectionRow(definition.Chase, HostileShadowFacingIds.Down, 0);
        AssertDirectionRow(definition.Chase, HostileShadowFacingIds.Right, 1);
        AssertDirectionRow(definition.Chase, HostileShadowFacingIds.Up, 2);
        AssertDirectionRow(definition.Chase, HostileShadowFacingIds.Left, 3);

        Assert.True(
            HostileAttackTransitionPolicyFactory.TryCreate(
                profile,
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                out var policy,
                out var reason
            ),
            reason
        );
        Assert.IsType<TerrorbeakAttackTransitionPolicy>(policy);
        Assert.Equal("hostile-shadow.terrorbeak-transition-policy-ready", reason);

        var wrongPolicy = HostileAttackTestFactory.Profile(
            profile.AssetBindingId,
            definition,
            damage: 50,
            intervalSeconds: 0.7d
        );
        Assert.False(
            HostileAttackTransitionPolicyFactory.TryCreate(
                wrongPolicy,
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                out policy,
                out reason
            )
        );
        Assert.Null(policy);
        Assert.Equal(
            "hostile-shadow.terrorbeak-post-attack-policy-unsupported",
            reason
        );
    }

    [Fact]
    public void Runtime_reuses_bounded_targeting_and_shared_movement_presentation()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var renderer = Contract("HostileShadowMonster.cs");
        var movement = Contract("HostileShadowMovementPresentation.cs");
        var policy = Contract("CreeperFearAttackTransitionPolicy.cs");

        Assert.Contains("HostileShadowMovementPresentationBindings.Supports", world);
        Assert.Contains("HostileShadowMovementPresentationBindings.Supports", renderer);
        Assert.Contains("ShadowMonsterAssetBindingIds.Terrorbeak", movement);
        Assert.Contains("HostileAttackTransitionPolicyFactory.TryCreate", world);
        Assert.DoesNotContain("PathFind", world, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AStar(", world, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Game1.random", policy, StringComparison.Ordinal);
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile()
    {
        return HostileAttackTestFactory.ShippedRuntimeProfile(
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
    }

    private static HostileShadowTargetingDecision Evaluate(
        ShadowMonsterRuntimeProfile profile,
        params HostileShadowPlayerSample[] players
    )
    {
        var index = new HostileShadowLocationPlayerIndex();
        var rebuild = index.Rebuild(players);
        Assert.True(rebuild.Success, rebuild.Reason);
        return HostileShadowTargetingEngine.Evaluate(
            new HostileShadowTargetingInput
            {
                EntityId = HostileAttackTestFactory.EntityId,
                OwnerPlayerKey = Owner,
                LocationId = LocationId,
                PositionX = 0d,
                PositionY = 0d,
                StandingX = 0d,
                StandingY = 0d,
                MovementSpeed = profile.MovementSpeed,
                DetectionRadiusPixels = profile.DetectionRadiusPixels,
                StopDistancePixels = profile.AttackRangePixels,
                SpawnGameMinute = 0,
                CurrentGameMinute = 119,
                NaturalTtlMinutes = 120,
                ElapsedSeconds = 0d,
            },
            index
        );
    }

    private static void AssertDirectionRow(
        DontStarve.Resource.Sanity.SanityHostileAnimationStateDefinition state,
        string direction,
        int expectedRow
    )
    {
        Assert.True(state.TryGetDirectionRow(direction, out var row));
        Assert.Equal(expectedRow, row);
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                fileName
            )
        );
    }

    private sealed class SequenceTransitionRandom : IHostileAttackTransitionRandom
    {
        private readonly double[] samples;
        private int index;

        internal SequenceTransitionRandom(params double[] samples)
        {
            this.samples = samples;
        }

        internal int CallCount { get; private set; }

        public double NextSample()
        {
            CallCount++;
            if (index >= samples.Length)
                throw new InvalidOperationException("No RNG sample remains.");
            return samples[index++];
        }
    }
}
