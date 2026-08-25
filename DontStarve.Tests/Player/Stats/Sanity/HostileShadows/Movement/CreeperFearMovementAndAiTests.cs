using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Movement;

public sealed class CreeperFearMovementAndAiTests
{
    private const string Owner = "101";
    private const string LocationId = "Farm";

    [Fact]
    public void Shipped_profile_drives_stats_twenty_tile_boundary_idle_and_two_hour_no_target_ttl()
    {
        var profile = RuntimeProfile(ShadowMonsterAssetBindingIds.CreeperFear);

        Assert.Equal(300, profile.MaxHealth);
        Assert.Equal(20, profile.BaseDamage);
        Assert.Equal(2.5d, profile.MovementSpeed);
        Assert.Equal(20d, profile.DetectionRadiusTiles);
        Assert.Equal(1280d, profile.DetectionRadiusPixels);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);
        Assert.Equal(1.8d, profile.AttackIntervalSeconds);
        Assert.Equal(2d, profile.NaturalDespawnGameHours);
        Assert.Equal(
            ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
            profile.PostAttackPolicyId
        );

        var boundary = Evaluate(
            profile,
            currentMinute: 119,
            new HostileShadowPlayerSample(Owner, LocationId, 1280d, 0d)
        );
        var outside = Evaluate(
            profile,
            currentMinute: 119,
            new HostileShadowPlayerSample(Owner, LocationId, 1280.001d, 0d)
        );
        var expired = EvaluateWithNoTargetTimer(
            profile,
            currentMinute: 120,
            noTargetSinceGameMinute: 0,
            new HostileShadowPlayerSample(Owner, LocationId, 5000d, 0d, false)
        );

        Assert.Equal(HostileShadowStateIds.Chase, boundary.StateId);
        Assert.Equal(Owner, boundary.TargetPlayerKey);
        Assert.Equal(HostileShadowStateIds.Idle, outside.StateId);
        Assert.Equal(string.Empty, outside.TargetPlayerKey);
        Assert.True(expired.NaturalTtlExpired);
    }

    [Fact]
    public void First_target_discovery_taunts_then_chases_without_retaunting_on_reacquire()
    {
        var definition = HostileAttackTestFactory.Definition(intervalSeconds: 1d);
        var random = new SequenceTransitionRandom(0.999999d);
        var machine = new HostileAttackStateMachine(
            definition,
            new CreeperFearAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                random
            )
        );

        var idle = machine.Advance(
            HostileAttackTestFactory.Input(hasTarget: false, inRange: false),
            definition.SpawnDurationMilliseconds
        );
        Assert.Equal(HostileShadowStateIds.Idle, idle.StateId);

        var taunt = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            0d
        );
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        Assert.Equal(0, random.CallCount);

        var stillTaunting = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            definition.TauntDurationMilliseconds - 1d
        );
        var chase = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            1d
        );
        Assert.Equal(HostileShadowStateIds.Taunt, stillTaunting.StateId);
        Assert.Equal(HostileShadowStateIds.Chase, chase.StateId);

        var lost = machine.Advance(
            HostileAttackTestFactory.Input(hasTarget: false, inRange: false),
            0d
        );
        var reacquired = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            0d
        );
        Assert.Equal(HostileShadowStateIds.Idle, lost.StateId);
        Assert.Equal(HostileShadowStateIds.Chase, reacquired.StateId);
        Assert.Equal(0, random.CallCount);
    }

    [Theory]
    [InlineData(0d, 10d, HostileShadowFacingIds.Down)]
    [InlineData(10d, 1d, HostileShadowFacingIds.Right)]
    [InlineData(0d, -10d, HostileShadowFacingIds.Up)]
    [InlineData(-10d, 1d, HostileShadowFacingIds.Left)]
    public void Common_facing_resolver_selects_all_four_move_rows(
        double targetX,
        double targetY,
        string expectedFacing
    )
    {
        var presentation = new HostileShadowMovementPresentationState(4, 100);

        Assert.True(
            presentation.TryAdvance(
                isChasing: true,
                positionChanged: true,
                standingX: 0d,
                standingY: 0d,
                targetX,
                targetY,
                elapsedMilliseconds: 100d,
                out var changed
            )
        );

        Assert.True(changed);
        Assert.Equal(expectedFacing, presentation.FacingId);
        Assert.Equal(1, presentation.FrameIndex);
    }

    [Fact]
    public void Move_metadata_exposes_rows_zero_to_three_and_100ms_loop_cadence()
    {
        var metadata = HostileAttackTestFactory.MetadataCatalog();
        Assert.True(
            metadata.TryGet(
                ShadowMonsterAssetBindingIds.CreeperFear,
                out var definition
            )
        );
        var chase = Assert.IsType<
            DontStarve.Resource.Sanity.SanityHostileAnimationStateDefinition
        >(definition!.Chase);

        Assert.Equal(4, chase.FrameCount);
        Assert.Equal(100, chase.FrameDurationMilliseconds);
        AssertDirectionRow(chase, HostileShadowFacingIds.Down, 0);
        AssertDirectionRow(chase, HostileShadowFacingIds.Right, 1);
        AssertDirectionRow(chase, HostileShadowFacingIds.Up, 2);
        AssertDirectionRow(chase, HostileShadowFacingIds.Left, 3);

        var presentation = new HostileShadowMovementPresentationState(
            chase.FrameCount,
            chase.FrameDurationMilliseconds
        );
        Assert.True(
            presentation.TryAdvance(
                true,
                true,
                0d,
                0d,
                0d,
                10d,
                99d,
                out _
            )
        );
        Assert.Equal(0, presentation.FrameIndex);
        Assert.True(
            presentation.TryAdvance(
                true,
                true,
                0d,
                0d,
                0d,
                10d,
                1d,
                out _
            )
        );
        Assert.Equal(1, presentation.FrameIndex);
        Assert.True(
            presentation.TryAdvance(
                true,
                true,
                0d,
                0d,
                0d,
                10d,
                300d,
                out _
            )
        );
        Assert.Equal(0, presentation.FrameIndex);
        Assert.True(
            presentation.TryAdvance(
                false,
                false,
                0d,
                0d,
                0d,
                0d,
                0d,
                out _
            )
        );
        Assert.Equal(0, presentation.FrameIndex);
    }

    [Fact]
    public void Wandering_uses_half_speed_for_position_and_animation_clock()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");

        Assert.Contains("entry.Profile.MovementSpeed * 0.5d", world, StringComparison.Ordinal);
        Assert.Contains("FixedUpdateSeconds * 1000d * (isWanderingNow ? 0.5d : 1d)", world, StringComparison.Ordinal);
        Assert.Contains("|| isWanderingNow", world, StringComparison.Ordinal);
    }

    [Fact]
    public void Creeper_fear_render_offset_moves_up_one_tile_without_rebasing_collision_geometry()
    {
        var source = Contract("HostileShadowMonster.cs").Replace("\r\n", "\n", StringComparison.Ordinal);
        var spriteSectionStart = source.IndexOf("var spriteScreen", StringComparison.Ordinal);
        var drawColorStart = source.IndexOf("var drawColor", spriteSectionStart, StringComparison.Ordinal);

        Assert.True(spriteSectionStart >= 0);
        Assert.True(drawColorStart > spriteSectionStart);
        var spriteSection = source[spriteSectionStart..drawColorStart];
        Assert.Contains("? 0f", spriteSection, StringComparison.Ordinal);
        Assert.Contains(": -64f", spriteSection, StringComparison.Ordinal);
        Assert.DoesNotContain("? 64f", spriteSection, StringComparison.Ordinal);
        Assert.Contains("            screen,\n            pivot,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Creeper_fear_and_terrorbeak_share_the_three_to_five_second_wander_window_but_keep_attack_intervals()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var wanderStart = world.IndexOf("private bool TryAdvanceWander", StringComparison.Ordinal);
        var wanderEnd = world.IndexOf("private void ResolvePendingLethalDamage", wanderStart, StringComparison.Ordinal);

        Assert.True(wanderStart >= 0);
        Assert.True(wanderEnd > wanderStart);
        var wander = world[wanderStart..wanderEnd];
        Assert.Contains("3000d + (wanderRandom.NextDouble() * 2000d)", wander, StringComparison.Ordinal);
        Assert.DoesNotContain("CreeperFear", wander, StringComparison.Ordinal);
        Assert.DoesNotContain("Terrorbeak", wander, StringComparison.Ordinal);

        var creeper = RuntimeProfile(ShadowMonsterAssetBindingIds.CreeperFear);
        var terrorbeak = RuntimeProfile(ShadowMonsterAssetBindingIds.Terrorbeak);
        Assert.Equal(1.8d, creeper.AttackIntervalSeconds);
        Assert.Equal(1.2d, terrorbeak.AttackIntervalSeconds);
    }

    [Fact]
    public void Speed_2_5_moves_on_a_normalized_straight_line_without_map_or_path_input()
    {
        var moved = HostileShadowTargetingEngine.AdvancePosition(
            positionX: 0d,
            positionY: 0d,
            standingX: 0d,
            standingY: 0d,
            targetStandingX: 300d,
            targetStandingY: 400d,
            movementSpeed: 2.5d,
            stopDistancePixels: 0d,
            elapsedSeconds: 0.1d
        );

        Assert.True(moved.Valid, moved.Reason);
        Assert.Equal(9d, moved.PositionX, precision: 10);
        Assert.Equal(12d, moved.PositionY, precision: 10);

        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        Assert.DoesNotContain("PathFind", world, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AStar(", world, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HostileShadowTargetingEngine.AdvancePosition", world, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0d, true, 1.2d)]
    [InlineData(0.249999d, true, 1.2d)]
    [InlineData(0.25d, false, 1d)]
    [InlineData(0.999999d, false, 1d)]
    public void Injected_rng_boundary_is_exact_and_duplicate_revision_is_memoized(
        double sample,
        bool expectedTaunt,
        double expectedDelay
    )
    {
        var random = new SequenceTransitionRandom(sample, 0d);
        var policy = new CreeperFearAttackTransitionPolicy(
            HostileAttackTestFactory.EntityId,
            random
        );
        var context = new HostileAttackTransitionContext(
            HostileAttackTestFactory.EntityId,
            HostileAttackTestFactory.PlayerOne,
            HostileAttackTestFactory.AttackRevision
        );

        Assert.True(
            policy.TryResolvePostAttackTransition(
                context,
                configuredProfileIntervalSeconds: 1d,
                out var first
            )
        );
        Assert.True(
            policy.TryResolvePostAttackTransition(
                context,
                configuredProfileIntervalSeconds: 1d,
                out var replay
            )
        );

        Assert.Equal(expectedTaunt, first.EnterTaunt);
        Assert.Equal(expectedDelay, first.NextAttackDelaySeconds);
        Assert.Equal(first, replay);
        Assert.Equal(1, random.CallCount);
    }

    [Theory]
    [InlineData(0d, true, 1200d)]
    [InlineData(0.25d, false, 1000d)]
    public void Attack_completion_waits_after_taunt_or_directly_for_profile_interval(
        double sample,
        bool entersTaunt,
        double waitMilliseconds
    )
    {
        var random = new SequenceTransitionRandom(sample, 0.999999d);
        var (machine, definition) = StartCreeperAttack(random);

        for (var frame = 0; frame < definition.AttackFrameCount; frame++)
        {
            machine.Advance(
                HostileAttackTestFactory.Input(intervalSeconds: 1d),
                definition.AttackFrameDurationMilliseconds
            );
        }

        Assert.Equal(
            entersTaunt ? HostileShadowStateIds.Taunt : HostileShadowStateIds.Chase,
            machine.StateId
        );
        Assert.Equal(1, random.CallCount);

        var duplicate = machine.Advance(
            HostileAttackTestFactory.Input(intervalSeconds: 1d),
            0d
        );
        Assert.Equal(
            entersTaunt ? HostileShadowStateIds.Taunt : HostileShadowStateIds.Chase,
            duplicate.StateId
        );
        Assert.Equal(1, random.CallCount);

        if (entersTaunt)
        {
            var afterTaunt = machine.Advance(
                HostileAttackTestFactory.Input(intervalSeconds: 1d),
                definition.TauntDurationMilliseconds
            );
            Assert.Equal(HostileShadowStateIds.Chase, afterTaunt.StateId);
        }

        var waiting = machine.Advance(
            HostileAttackTestFactory.Input(intervalSeconds: 1d),
            waitMilliseconds - 1d
        );
        var nextAttack = machine.Advance(
            HostileAttackTestFactory.Input(intervalSeconds: 1d),
            1d
        );
        Assert.Equal(HostileShadowStateIds.Chase, waiting.StateId);
        Assert.Equal(HostileShadowStateIds.Attack, nextAttack.StateId);
        Assert.Equal(1, random.CallCount);
    }

    [Fact]
    public void Factory_binds_creeper_and_terrorbeak_and_fails_closed_on_wrong_policy_id()
    {
        var profile = RuntimeProfile(ShadowMonsterAssetBindingIds.CreeperFear);
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
        Assert.IsType<CreeperFearAttackTransitionPolicy>(policy);

        var metadata = HostileAttackTestFactory.MetadataCatalog();
        Assert.True(metadata.TryGet(profile.AssetBindingId, out var attackMetadata));
        var wrongPolicy = HostileAttackTestFactory.Profile(
            profile.AssetBindingId,
            attackMetadata!
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
            "hostile-shadow.creeper-fear-post-attack-policy-unsupported",
            reason
        );

        var terrorbeak = RuntimeProfile(ShadowMonsterAssetBindingIds.Terrorbeak);
        Assert.True(
            HostileAttackTransitionPolicyFactory.TryCreate(
                terrorbeak,
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                out policy,
                out reason
            ),
            reason
        );
        Assert.IsType<TerrorbeakAttackTransitionPolicy>(policy);
        Assert.Equal(
            "hostile-shadow.terrorbeak-transition-policy-ready",
            reason
        );
    }

    [Fact]
    public void Warp_adds_no_runtime_subscription_and_disabled_removes_entry_owned_state()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var policy = Contract("CreeperFearAttackTransitionPolicy.cs");
        var movement = Contract("HostileShadowMovementPresentation.cs");

        Assert.Contains("HostileAttackTransitionPolicyFactory.TryCreate", world, StringComparison.Ordinal);
        Assert.Contains("new HostileAttackStateMachine(", world, StringComparison.Ordinal);
        Assert.Contains("transitionPolicy", world, StringComparison.Ordinal);
        Assert.DoesNotContain("Player.Warped +=", world, StringComparison.Ordinal);
        Assert.Contains("SanityResourceReleaseReason.SystemDisabled", world, StringComparison.Ordinal);
        Assert.Contains("RemoveAllPhysical()", world, StringComparison.Ordinal);
        Assert.Contains("entries.Remove(entityId", world, StringComparison.Ordinal);
        Assert.DoesNotContain("static readonly Dictionary", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.random", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("AttackBox", policy + movement, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", policy + movement, StringComparison.Ordinal);
    }

    private static (
        HostileAttackStateMachine Machine,
        HostileAttackRuntimeDefinition Definition
    ) StartCreeperAttack(SequenceTransitionRandom random)
    {
        var definition = HostileAttackTestFactory.Definition(intervalSeconds: 1d);
        var machine = new HostileAttackStateMachine(
            definition,
            new CreeperFearAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                random
            )
        );
        var taunt = machine.Advance(
            HostileAttackTestFactory.Input(inRange: true, intervalSeconds: 1d),
            definition.SpawnDurationMilliseconds
        );
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        var attack = machine.Advance(
            HostileAttackTestFactory.Input(inRange: true, intervalSeconds: 1d),
            definition.TauntDurationMilliseconds
        );
        Assert.Equal(HostileShadowStateIds.Attack, attack.StateId);
        return (machine, definition);
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile(string bindingId)
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(
            catalog.TryGetProfile(
                ShadowMonsterDifficultyProfileIds.Compatible,
                out var difficulty
            )
        );
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
        var adapted = capability.Adapter!.Adapt(difficulty!, bindingId, 64);
        Assert.True(adapted.Success, adapted.Reason);
        return Assert.IsType<ShadowMonsterRuntimeProfile>(adapted.Profile);
    }

    private static HostileShadowTargetingDecision Evaluate(
        ShadowMonsterRuntimeProfile profile,
        long currentMinute,
        params HostileShadowPlayerSample[] players
    )
    {
        return EvaluateCore(
            profile,
            currentMinute,
            noTargetSinceGameMinute: null,
            players
        );
    }

    private static HostileShadowTargetingDecision EvaluateWithNoTargetTimer(
        ShadowMonsterRuntimeProfile profile,
        long currentMinute,
        long noTargetSinceGameMinute,
        params HostileShadowPlayerSample[] players
    )
    {
        return EvaluateCore(
            profile,
            currentMinute,
            noTargetSinceGameMinute,
            players
        );
    }

    private static HostileShadowTargetingDecision EvaluateCore(
        ShadowMonsterRuntimeProfile profile,
        long currentMinute,
        long? noTargetSinceGameMinute,
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
                CurrentGameMinute = currentMinute,
                NaturalTtlMinutes = (long)(profile.NaturalDespawnGameHours * 60d),
                NoTargetSinceGameMinute = noTargetSinceGameMinute,
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
                throw new InvalidOperationException("No injected RNG sample remains.");
            return samples[index++];
        }
    }
}
