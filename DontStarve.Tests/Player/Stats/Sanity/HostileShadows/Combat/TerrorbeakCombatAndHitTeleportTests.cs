using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class TerrorbeakCombatAndHitTeleportTests
{
    [Fact]
    public void Shipped_profile_binds_exact_health_damage_range_and_immunity()
    {
        var profile = RuntimeProfile();

        Assert.Equal(400, profile.MaxHealth);
        Assert.Equal(50, profile.BaseDamage);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);
        Assert.Equal(new[] { "Knockback", "Frozen" }, profile.ImmunityTags);
        Assert.True(
            TerrorbeakCombatImmunityPolicy.TryCreate(
                profile,
                out var immunity,
                out var reason
            ),
            reason
        );
        Assert.True(immunity!.BlocksKnockback);
        Assert.True(immunity.BlocksFrozen);
        Assert.False(TerrorbeakCombatImmunityPolicy.IsFrozenStun(50));
        Assert.True(TerrorbeakCombatImmunityPolicy.IsFrozenStun(51));
        Assert.True(TerrorbeakCombatImmunityPolicy.IsFrozenStun(2000));
    }

    [Theory]
    [InlineData("missing-knockback", "terrorbeak.combat-immunity-required-tag-missing")]
    [InlineData("missing-frozen", "terrorbeak.combat-immunity-required-tag-missing")]
    [InlineData("duplicate", "terrorbeak.combat-immunity-tag-duplicate")]
    [InlineData("unknown", "terrorbeak.combat-immunity-tag-unsupported")]
    [InlineData("wrong-binding", "terrorbeak.combat-immunity-profile-invalid")]
    public void Immunity_profile_drift_fails_closed(
        string mutation,
        string expectedReason
    )
    {
        var original = RuntimeProfile();
        var tags = mutation switch
        {
            "missing-knockback" => new[] { "Frozen" },
            "missing-frozen" => new[] { "Knockback" },
            "duplicate" => new[] { "Knockback", "Frozen", "Frozen" },
            "unknown" => new[] { "Knockback", "Frozen", "Poison" },
            _ => new[] { "Knockback", "Frozen" },
        };
        var profile = CopyProfile(
            original,
            mutation == "wrong-binding"
                ? ShadowMonsterAssetBindingIds.CreeperFear
                : original.AssetBindingId,
            tags
        );

        Assert.False(
            TerrorbeakCombatImmunityPolicy.TryCreate(
                profile,
                out var policy,
                out var reason
            )
        );
        Assert.Null(policy);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Smapi_materialization_binds_both_profiles_to_one_public_immunity_seam()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority"
        );
        var monster = File.ReadAllText(Path.Combine(root, "HostileShadowMonster.cs"));
        var runtime = File.ReadAllText(
            Path.Combine(root, "SmapiHostileShadowWorldRuntime.cs")
        );
        var immunity = File.ReadAllText(
            Path.Combine(root, "CreeperFearCombatImmunityPolicy.cs")
        );

        Assert.Contains("HostileShadowCombatImmunityPolicy policy", monster);
        Assert.Contains("public override void setTrajectory(Vector2 trajectory)", monster);
        Assert.Contains("Slipperiness = -1;", monster);
        Assert.Contains("HostileShadowCombatImmunityPolicy.IsFrozenStun", monster);
        Assert.Contains("stunTime.Value = 0;", monster);
        Assert.Contains(
            "HostileShadowStateIds.HitTeleport => HitResponseVisualStateId",
            monster
        );
        Assert.Contains("CreeperFearCombatImmunityPolicy.TryCreate", runtime);
        Assert.Contains("TerrorbeakCombatImmunityPolicy.TryCreate", runtime);
        Assert.Contains("ShadowMonsterAssetBindingIds.Terrorbeak", runtime);
        Assert.Contains("monster.ApplyCombatImmunity(combatImmunity);", runtime);
        Assert.Contains("TryValidateProfile", immunity);

        var ordinarySources = string.Join("\n", monster, runtime, immunity);
        Assert.DoesNotContain(
            "INonLethalDamageService",
            ordinarySources,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ApplyDamageUpToFloor",
            ordinarySources,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Incoming_hit_interrupts_high_speed_attack_from_frozen_origin()
    {
        var started = StartAttack();
        var active = started.Machine.Advance(
            started.Input,
            started.Definition.AttackFrameDurationMilliseconds * 2d
        );
        Assert.Equal(164d, active.PositionX, precision: 8);
        Assert.Equal(200d, active.PositionY, precision: 8);
        var controller = new HostileShadowHitResponseController(started.Machine);

        var hit = controller.HandleHit(
            Input(
                active.PositionX,
                active.PositionY,
                health: 350,
                revision: 20,
                new OpenTeleportMap(),
                new FixedTeleportRandom(2468, distance: 6, direction: 0)
            )
        );

        Assert.True(hit.Valid, hit.Reason);
        Assert.True(hit.AttackInterrupted);
        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);
        Assert.Equal(100d, hit.PositionX, precision: 8);
        Assert.Equal(200d, hit.PositionY, precision: 8);
        Assert.True(hit.PositionChanged);
        Assert.Null(started.Machine.CurrentInstance);

        var completed = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Chase, completed.StateId);
        Assert.Equal(100d + 6d * 64d, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
        Assert.NotEqual(active.PositionX + 6d * 64d, completed.PositionX);
    }

    [Theory]
    [InlineData(4, 356d)]
    [InlineData(8, 612d)]
    public void Hit_teleport_keeps_exact_four_and_eight_tile_boundaries(
        int distance,
        double expectedPositionX
    )
    {
        var started = StartAttack();
        var controller = new HostileShadowHitResponseController(started.Machine);
        var hit = controller.HandleHit(
            Input(
                132d,
                200d,
                health: 399,
                revision: 30 + distance,
                new OpenTeleportMap(),
                new FixedTeleportRandom(3000 + distance, distance, direction: 0)
            )
        );

        var completed = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: false
        );

        Assert.Equal(HostileShadowStateIds.Idle, completed.StateId);
        Assert.Equal(expectedPositionX, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
        Assert.Equal(
            HostileShadowLifecycleTransitionKind.HitTeleport,
            hit.Receipt!.Value.Kind
        );
    }

    [Fact]
    public void No_legal_point_stops_after_sixteen_candidates_without_movement()
    {
        var started = StartAttack();
        var map = new ClosedTeleportMap();
        var random = new FixedTeleportRandom(4000, distance: 4, direction: 0);
        var controller = new HostileShadowHitResponseController(started.Machine);

        var hit = controller.HandleHit(
            Input(132d, 200d, 399, 40, map, random)
        );

        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);
        Assert.Equal("hostile-shadow.hit-teleport-no-legal-point", hit.Reason);
        Assert.Equal(16, map.CandidateChecks);
        Assert.Equal(32, random.CallCount);
        Assert.Equal(100d, hit.PositionX, precision: 8);
        Assert.Equal(200d, hit.PositionY, precision: 8);

        var completed = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            elapsed: 0d,
            hasTarget: false
        );
        Assert.Equal(HostileShadowStateIds.Idle, completed.StateId);
        Assert.False(completed.PositionChanged);
        Assert.Equal(100d, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
    }

    [Fact]
    public void Repeated_hit_never_rerolls_restarts_or_accumulates_motion()
    {
        var started = StartAttack();
        var controller = new HostileShadowHitResponseController(started.Machine);
        var first = controller.HandleHit(
            Input(
                132d,
                200d,
                399,
                50,
                new OpenTeleportMap(),
                new FixedTeleportRandom(5000, distance: 8, direction: 0)
            )
        );
        var active = controller.Advance(100d, 200d, elapsed: 150d, hasTarget: true);
        Assert.Equal(HostileShadowHitResponseDecisionStatus.Advanced, active.Status);

        var duplicate = controller.HandleHit(
            Input(
                100d,
                200d,
                350,
                51,
                new OpenTeleportMap(),
                new ThrowingTeleportRandom(5001)
            )
        );

        Assert.Equal(HostileShadowHitResponseDecisionStatus.Duplicate, duplicate.Status);
        Assert.Equal("hostile-shadow.hit-teleport-hit-duplicate", duplicate.Reason);
        Assert.Equal(first.Receipt!.Value.CorrelationId, duplicate.Receipt!.Value.CorrelationId);
        Assert.Equal(150d, controller.ElapsedMilliseconds, precision: 8);
        var stillActive = controller.Advance(100d, 200d, 249d, hasTarget: true);
        Assert.Equal(HostileShadowStateIds.HitTeleport, stillActive.StateId);
        var completed = controller.Advance(100d, 200d, 1d, hasTarget: true);
        Assert.Equal(612d, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
    }

    [Fact]
    public void True_zero_during_hit_teleport_enters_dying_and_never_uses_target()
    {
        var started = StartAttack();
        var controller = new HostileShadowHitResponseController(started.Machine);
        var hit = controller.HandleHit(
            Input(
                132d,
                200d,
                399,
                60,
                new OpenTeleportMap(),
                new FixedTeleportRandom(6000, distance: 8, direction: 0)
            )
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);

        var dying = controller.HandleHit(
            Input(100d, 200d, health: 0, revision: 61, map: null, random: null)
        );

        Assert.Equal(HostileShadowStateIds.Dying, dying.StateId);
        Assert.Equal(
            HostileShadowLifecycleTransitionKind.Dying,
            dying.Receipt!.Value.Kind
        );
        Assert.Equal(100d, dying.PositionX, precision: 8);
        Assert.Equal(200d, dying.PositionY, precision: 8);
        var active = controller.Advance(100d, 200d, 399d, hasTarget: true);
        Assert.False(active.RemovalRequested);
        var removal = controller.Advance(100d, 200d, 1d, hasTarget: true);
        Assert.True(removal.RemovalRequested);
        Assert.Equal(100d, removal.PositionX, precision: 8);
        Assert.NotEqual(612d, removal.PositionX);
        var replay = controller.Advance(100d, 200d, 1d, hasTarget: true);
        Assert.Equal(HostileShadowHitResponseDecisionStatus.Duplicate, replay.Status);
        Assert.True(replay.RemovalRequested);
    }

    [Theory]
    [MemberData(nameof(CleanupReasons))]
    public void Cleanup_reasons_remain_positive_health_despawn(string reason)
    {
        Assert.True(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Dying, 0));
        Assert.False(
            HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Dying, 400)
        );
        Assert.True(
            HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Despawn, 400)
        );
        Assert.False(
            HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Despawn, 0)
        );
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                revision: 70,
                HostileShadowLifecycleTransitionKind.Despawn,
                reason,
                teleportRandomSeed: null,
                attributedPlayerKey: string.Empty,
                out var receipt
            )
        );
        Assert.Equal(HostileShadowLifecycleTransitionKind.Despawn, receipt.Kind);
        Assert.Equal(reason, receipt.Reason);
        Assert.Null(receipt.TeleportRandomSeed);
        Assert.Empty(receipt.AttributedPlayerKey);
    }

    [Fact]
    public void Dying_and_despawn_receipts_replay_exactly_and_conflict_by_kind()
    {
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                revision: 80,
                HostileShadowLifecycleTransitionKind.Dying,
                HostileShadowSettlementReasonIds.DyingHealthZero,
                teleportRandomSeed: null,
                HostileAttackTestFactory.PlayerOne,
                out var dying
            )
        );
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                revision: 81,
                HostileShadowLifecycleTransitionKind.Despawn,
                HostileShadowCleanupReasonIds.Natural,
                teleportRandomSeed: null,
                attributedPlayerKey: string.Empty,
                out var despawn
            )
        );
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                revision: 80,
                HostileShadowLifecycleTransitionKind.Despawn,
                HostileShadowCleanupReasonIds.WorldCleanup,
                teleportRandomSeed: null,
                attributedPlayerKey: string.Empty,
                out var conflictingKind
            )
        );

        var store = new HostileShadowLifecycleReceiptStore();
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            store.Record(dying).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Duplicate,
            store.Record(dying).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            store.Record(despawn).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Duplicate,
            store.Record(despawn).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Conflict,
            store.Record(conflictingKind).Status
        );
        Assert.Equal(2, store.Count);
    }

    public static IEnumerable<object[]> CleanupReasons()
    {
        yield return new object[] { HostileShadowCleanupReasonIds.Natural };
        yield return new object[] { HostileShadowCleanupReasonIds.DangerExited };
        yield return new object[] { HostileShadowCleanupReasonIds.SystemDisabled };
        yield return new object[] { HostileShadowCleanupReasonIds.ResourceInvalidated };
        yield return new object[] { HostileShadowCleanupReasonIds.WorldCleanup };
    }

    private static AttackStart StartAttack()
    {
        var profile = RuntimeProfile();
        var definition = HostileAttackTestFactory.Definition(profile);
        var machine = new HostileAttackStateMachine(
            definition,
            new TerrorbeakAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                new FixedTransitionRandom(0.25d)
            )
        );
        var input = HostileAttackTestFactory.Input(
            intervalSeconds: profile.AttackIntervalSeconds
        );
        var taunt = machine.Advance(input, definition.SpawnDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        var attack = machine.Advance(input, definition.TauntDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Attack, attack.StateId);
        return new AttackStart(machine, definition, input);
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile()
    {
        return HostileAttackTestFactory.ShippedRuntimeProfile(
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
    }

    private static HostileShadowHitResponseInput Input(
        double positionX,
        double positionY,
        int health,
        long revision,
        IHostileShadowTeleportMap? map,
        IHostileShadowTeleportRandom? random
    )
    {
        return new HostileShadowHitResponseInput
        {
            SessionId = HostileAttackTestFactory.SessionId,
            EntityId = HostileAttackTestFactory.EntityId,
            ProposedRevision = revision,
            LocationId = HostileAttackTestFactory.LocationId,
            PositionX = positionX,
            PositionY = positionY,
            TileSizePixels = 64d,
            Health = health,
            AttackerPlayerKey = HostileAttackTestFactory.PlayerOne,
            Map = map,
            Random = random,
        };
    }

    private static ShadowMonsterRuntimeProfile CopyProfile(
        ShadowMonsterRuntimeProfile source,
        string bindingId,
        IReadOnlyList<string> tags
    )
    {
        return new ShadowMonsterRuntimeProfile(
            source.DifficultyProfileId,
            bindingId,
            source.AdapterVersion,
            source.MaxHealth,
            source.BaseDamage,
            source.MovementSpeed,
            source.Defense,
            source.DetectionRadiusTiles,
            source.DetectionRadiusPixels,
            source.AttackRangeTiles,
            source.AttackRangePixels,
            source.AttackIntervalSeconds,
            source.NaturalDespawnGameHours,
            source.DisplayNameKey,
            source.WallTraversalMode,
            tags,
            source.DropTable,
            source.SanityReward,
            source.AnimationProfileId,
            source.CueSetId,
            source.AttackMotionPolicyId,
            source.PostAttackPolicyId,
            source.ExperienceValue,
            source.KillCounterId
        );
    }

    private sealed record AttackStart(
        HostileAttackStateMachine Machine,
        HostileAttackRuntimeDefinition Definition,
        HostileAttackStateInput Input
    );

    private sealed class FixedTransitionRandom : IHostileAttackTransitionRandom
    {
        private readonly double sample;

        internal FixedTransitionRandom(double sample)
        {
            this.sample = sample;
        }

        public double NextSample()
        {
            return sample;
        }
    }

    private sealed class FixedTeleportRandom : IHostileShadowTeleportRandom
    {
        private readonly int distance;
        private readonly int direction;

        internal FixedTeleportRandom(int seed, int distance, int direction)
        {
            Seed = seed;
            this.distance = distance;
            this.direction = direction;
        }

        public int Seed { get; }
        internal int CallCount { get; private set; }

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            CallCount++;
            var value = minimumInclusive == HostileShadowTeleportPointSelector.MinimumDistanceTiles
                ? distance
                : direction;
            Assert.InRange(value, minimumInclusive, maximumExclusive - 1);
            return value;
        }
    }

    private sealed class ThrowingTeleportRandom : IHostileShadowTeleportRandom
    {
        internal ThrowingTeleportRandom(int seed)
        {
            Seed = seed;
        }

        public int Seed { get; }

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            throw new InvalidOperationException("Duplicate hit rerolled teleport RNG.");
        }
    }

    private class OpenTeleportMap : IHostileShadowTeleportMap
    {
        public bool IsLocationValid(string expectedLocationId)
        {
            return string.Equals(
                expectedLocationId,
                HostileAttackTestFactory.LocationId,
                StringComparison.Ordinal
            );
        }

        public virtual bool IsTileOnMap(int tileX, int tileY)
        {
            return tileX >= 0 && tileY >= 0 && tileX < 100 && tileY < 100;
        }

        public bool IsTileLocationOpen(int tileX, int tileY)
        {
            return true;
        }

        public bool IsTilePassable(int tileX, int tileY)
        {
            return true;
        }
    }

    private sealed class ClosedTeleportMap : OpenTeleportMap
    {
        internal int CandidateChecks { get; private set; }

        public override bool IsTileOnMap(int tileX, int tileY)
        {
            CandidateChecks++;
            return false;
        }
    }
}
