using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileShadowHitResponseTests
{
    [Theory]
    [InlineData(100, 20, 0, 20, 80, 80, false)]
    [InlineData(100, 20, 5, 15, 85, 85, false)]
    [InlineData(7, 20, 0, 7, 0, 1, true)]
    [InlineData(1, 1, 10, 1, 0, 1, true)]
    public void Incoming_damage_keeps_a_one_hp_physical_sentinel_until_true_dying(
        int health,
        int damage,
        int defense,
        int expectedDamage,
        int expectedLogicalHealth,
        int expectedPhysicalHealth,
        bool pendingDying
    )
    {
        var decision = HostileShadowIncomingDamagePolicy.Evaluate(
            health,
            damage,
            defense
        );

        Assert.True(decision.Valid, decision.Reason);
        Assert.Equal(expectedDamage, decision.AppliedDamage);
        Assert.Equal(expectedLogicalHealth, decision.LogicalHealthAfter);
        Assert.Equal(expectedPhysicalHealth, decision.PhysicalHealthAfter);
        Assert.Equal(pendingDying, decision.PendingDying);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Selector_accepts_only_legal_four_to_eight_tile_points(int distance)
    {
        var selector = new HostileShadowTeleportPointSelector();
        var map = new FakeMap(width: 40, height: 40);
        var random = new SequenceRandom(2468, distance, 0);

        var result = selector.Select(
            map,
            HostileAttackTestFactory.LocationId,
            10d * 64d,
            10d * 64d,
            64d,
            random
        );

        Assert.True(result.Selected, result.Reason);
        Assert.Equal(distance, result.Point.TileOffsetX);
        Assert.Equal(0, result.Point.TileOffsetY);
        Assert.InRange(
            Math.Sqrt(
                result.Point.TileOffsetX * result.Point.TileOffsetX
                    + result.Point.TileOffsetY * result.Point.TileOffsetY
            ),
            HostileShadowTeleportPointSelector.MinimumDistanceTiles,
            HostileShadowTeleportPointSelector.MaximumDistanceTiles
        );
        Assert.Equal(2468, result.RandomSeed);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void Selector_is_bounded_and_rejects_edge_or_invalidated_location()
    {
        var selector = new HostileShadowTeleportPointSelector();
        var edge = new FakeMap(width: 5, height: 5);
        var alwaysLeft = new SequenceRandom(10, 4, 4);

        var noPoint = selector.Select(
            edge,
            HostileAttackTestFactory.LocationId,
            1d * 64d,
            2d * 64d,
            64d,
            alwaysLeft
        );

        Assert.Equal(
            HostileShadowTeleportSelectionStatus.NoLegalPoint,
            noPoint.Status
        );
        Assert.Equal(HostileShadowTeleportPointSelector.MaximumAttempts, noPoint.Attempts);
        Assert.Equal("hostile-shadow.hit-teleport-no-legal-point", noPoint.Reason);

        var invalidated = new FakeMap(width: 40, height: 40);
        invalidated.Validity.Enqueue(true);
        invalidated.Validity.Enqueue(false);
        var race = selector.Select(
            invalidated,
            HostileAttackTestFactory.LocationId,
            10d * 64d,
            10d * 64d,
            64d,
            new SequenceRandom(11, 4, 0)
        );
        Assert.Equal(
            HostileShadowTeleportSelectionStatus.LocationInvalid,
            race.Status
        );
        Assert.Equal("hostile-shadow.hit-teleport-location-invalid", race.Reason);
    }

    [Fact]
    public void Hit_interrupts_attack_once_clears_ledger_resets_origin_and_keeps_one_seed()
    {
        var (machine, _, attack) = HostileAttackTestFactory.StartAttack();
        var oldInstance = Assert.IsType<HostileAttackInstance>(machine.CurrentInstance);
        Assert.True(
            oldInstance.TryClaimHit(
                "claimed-before-hit",
                HostileAttackTestFactory.PlayerTwo,
                out _
            )
        );
        var random = new SequenceRandom(12345, 4, 0);
        var controller = new HostileShadowHitResponseController(machine);

        var first = controller.HandleHit(
            Input(12, health: 80, attack.PositionX, attack.PositionY, random)
        );

        Assert.True(first.Valid, first.Reason);
        Assert.Equal(HostileShadowStateIds.HitTeleport, first.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Hit,
            controller.HitTeleportVisualPhase
        );
        Assert.True(first.AttackInterrupted);
        Assert.Equal(100d, first.PositionX, precision: 8);
        Assert.Equal(200d, first.PositionY, precision: 8);
        Assert.Null(machine.CurrentInstance);
        var firstReceipt = Assert.IsType<HostileShadowLifecycleReceipt>(first.Receipt);
        Assert.Equal(HostileShadowLifecycleTransitionKind.HitTeleport, firstReceipt.Kind);
        Assert.Equal(12345, firstReceipt.TeleportRandomSeed);
        Assert.False(firstReceipt.SettlementEligible);
        Assert.False(firstReceipt.DropEligible);
        Assert.False(firstReceipt.RewardEligible);
        var callsAfterFirst = random.Calls;

        var repeated = controller.HandleHit(
            Input(13, health: 60, first.PositionX, first.PositionY, random)
        );
        Assert.Equal(HostileShadowHitResponseDecisionStatus.Duplicate, repeated.Status);
        Assert.False(repeated.AttackInterrupted);
        Assert.Equal(firstReceipt.CorrelationId, repeated.Receipt!.Value.CorrelationId);
        Assert.Equal(callsAfterFirst, random.Calls);

        var active = controller.Advance(
            first.PositionX,
            first.PositionY,
            399d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, active.StateId);
        var arrival = controller.Advance(
            active.PositionX,
            active.PositionY,
            1d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, arrival.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Spawn,
            controller.HitTeleportVisualPhase
        );
        Assert.Equal(100d + 4d * 64d, arrival.PositionX, precision: 8);
        Assert.Equal(200d, arrival.PositionY, precision: 8);

        var spawnActive = controller.Advance(
            arrival.PositionX,
            arrival.PositionY,
            399d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, spawnActive.StateId);
        var completed = controller.Advance(
            spawnActive.PositionX,
            spawnActive.PositionY,
            1d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Chase, completed.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.None,
            controller.HitTeleportVisualPhase
        );
        Assert.Equal(100d + 4d * 64d, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
    }

    [Fact]
    public void Spawn_phase_hit_restarts_hurt_animation_and_selects_one_new_destination()
    {
        var machine = HostileAttackTestFactory.StartAttack().Machine;
        var random = new SequenceRandom(12345, 4, 0, 8, 2);
        var controller = new HostileShadowHitResponseController(machine);

        var first = controller.HandleHit(
            Input(12, health: 80, 100d, 200d, random)
        );
        var arrival = controller.Advance(
            first.PositionX,
            first.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: true
        );

        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Spawn,
            controller.HitTeleportVisualPhase
        );
        Assert.Equal(2, random.Calls);
        var interrupted = controller.HandleHit(
            Input(
                13,
                health: 60,
                arrival.PositionX,
                arrival.PositionY,
                random
            )
        );

        Assert.True(interrupted.Valid, interrupted.Reason);
        Assert.Equal(
            HostileShadowHitResponseDecisionStatus.Advanced,
            interrupted.Status
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, interrupted.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Hit,
            controller.HitTeleportVisualPhase
        );
        Assert.True(interrupted.StateChanged);
        Assert.False(interrupted.PositionChanged);
        Assert.Equal(0d, controller.ElapsedMilliseconds, precision: 8);
        Assert.Equal(
            first.Receipt!.Value.CorrelationId,
            interrupted.Receipt!.Value.CorrelationId
        );
        Assert.Equal(4, random.Calls);

        var hurtActive = controller.Advance(
            interrupted.PositionX,
            interrupted.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds - 1d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, hurtActive.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Hit,
            controller.HitTeleportVisualPhase
        );

        var secondArrival = controller.Advance(
            hurtActive.PositionX,
            hurtActive.PositionY,
            1d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, secondArrival.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Spawn,
            controller.HitTeleportVisualPhase
        );
        Assert.Equal(arrival.PositionX, secondArrival.PositionX, precision: 8);
        Assert.Equal(arrival.PositionY + 8d * 64d, secondArrival.PositionY, precision: 8);

        var completed = controller.Advance(
            secondArrival.PositionX,
            secondArrival.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Chase, completed.StateId);
        Assert.Equal(secondArrival.PositionX, completed.PositionX, precision: 8);
        Assert.Equal(secondArrival.PositionY, completed.PositionY, precision: 8);
    }

    [Fact]
    public void Spawn_phase_hit_with_no_legal_point_keeps_existing_no_teleport_completion()
    {
        var machine = HostileAttackTestFactory.StartAttack().Machine;
        var map = new FakeMap(width: 40, height: 40);
        var random = new SequenceRandom(12345, 4, 0);
        var controller = new HostileShadowHitResponseController(machine);

        var first = controller.HandleHit(
            Input(12, health: 80, 100d, 200d, random, map)
        );
        var arrival = controller.Advance(
            first.PositionX,
            first.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: true
        );
        map.Open = false;

        var interrupted = controller.HandleHit(
            Input(
                13,
                health: 60,
                arrival.PositionX,
                arrival.PositionY,
                random,
                map
            )
        );
        Assert.True(interrupted.Valid, interrupted.Reason);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.Hit,
            controller.HitTeleportVisualPhase
        );

        var completed = controller.Advance(
            interrupted.PositionX,
            interrupted.PositionY,
            0d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Idle, completed.StateId);
        Assert.Equal(
            HostileShadowHitTeleportVisualPhaseIds.None,
            controller.HitTeleportVisualPhase
        );
        Assert.Equal(interrupted.PositionX, completed.PositionX, precision: 8);
        Assert.Equal(interrupted.PositionY, completed.PositionY, precision: 8);
    }

    [Fact]
    public void Pending_target_reacquisition_survives_hit_teleport_and_prevents_direct_chase()
    {
        var machine = HostileAttackTestFactory.StartAttack().Machine;
        Assert.True(
            machine.RequestTargetReacquisition(out var requestReason),
            requestReason
        );
        var controller = new HostileShadowHitResponseController(machine);
        var hit = controller.HandleHit(
            Input(
                12,
                health: 80,
                100d,
                200d,
                new SequenceRandom(12345, 4, 0)
            )
        );

        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);
        var activeHit = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            400d,
            hasTarget: true
        );
        var completed = controller.Advance(
            activeHit.PositionX,
            activeHit.PositionY,
            400d,
            hasTarget: true
        );

        Assert.Equal(
            HostileShadowHitResponseDecisionStatus.Completed,
            completed.Status
        );
        Assert.Equal(HostileShadowStateIds.Idle, completed.StateId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Different_attacker_handoff_after_hit_teleport_resumes_chase_without_taunt(
        bool wasChasing
    )
    {
        var (machine, definition, _) = HostileAttackTestFactory.StartAttack();
        if (wasChasing)
        {
            var chase = machine.Advance(
                HostileAttackTestFactory.Input(),
                definition.AttackFrameDurationMilliseconds
                    * definition.AttackFrameCount
            );
            Assert.Equal(HostileShadowStateIds.Chase, chase.StateId);
        }

        Assert.True(
            machine.RequestTargetHandoffAfterHit(out var handoffReason),
            handoffReason
        );
        var controller = new HostileShadowHitResponseController(machine);
        var hit = controller.HandleHit(
            Input(
                12,
                health: 80,
                100d,
                200d,
                new SequenceRandom(12345, 4, 0),
                attackerPlayerKey: HostileAttackTestFactory.PlayerTwo
            )
        );

        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);
        var arrival = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            400d,
            hasTarget: true
        );
        var completed = controller.Advance(
            arrival.PositionX,
            arrival.PositionY,
            400d,
            hasTarget: true
        );

        Assert.Equal(
            HostileShadowHitResponseDecisionStatus.Completed,
            completed.Status
        );
        Assert.Equal(HostileShadowStateIds.Chase, completed.StateId);
        Assert.Equal(
            HostileShadowStateIds.Chase,
            machine.Advance(
                HostileAttackTestFactory.Input(
                    inRange: false,
                    targetPlayerKey: HostileAttackTestFactory.PlayerTwo
                ),
                0d
            ).StateId
        );
    }

    [Fact]
    public void Old_attack_request_is_rejected_after_hit_teleport_interrupt()
    {
        var (machine, definition, attack) = HostileAttackTestFactory.StartAttack();
        var oldInstance = Assert.IsType<HostileAttackInstance>(machine.CurrentInstance);
        oldInstance.FrameNumber = 3;
        var controller = new HostileShadowHitResponseController(machine);
        var hit = controller.HandleHit(
            Input(
                12,
                health: 80,
                attack.PositionX,
                attack.PositionY,
                new SequenceRandom(99, 4, 0)
            )
        );
        var pipeline = new CountingPipeline(100);
        var request = new ShadowAttackHitRequest
        {
            SessionId = HostileAttackTestFactory.SessionId,
            Nonce = "old-after-teleport",
            EntityId = HostileAttackTestFactory.EntityId,
            TargetPlayerKey = HostileAttackTestFactory.PlayerOne,
            LocationId = HostileAttackTestFactory.LocationId,
            AttackInstanceId = oldInstance.InstanceId,
            ObservedEntityRevision = HostileAttackTestFactory.AttackRevision,
            ObservedAttackInstanceRevision = oldInstance.Revision,
            ObservedFrameNumber = 3,
        };
        var context = new HostileAttackHitContext
        {
            SessionId = HostileAttackTestFactory.SessionId,
            EntityId = HostileAttackTestFactory.EntityId,
            CurrentEntityRevision = HostileAttackTestFactory.AttackRevision,
            LocationId = HostileAttackTestFactory.LocationId,
            StateId = hit.StateId,
            CurrentFrameNumber = 0,
            Instance = machine.CurrentInstance,
            Definition = definition,
            AttackBox = new HostileAttackRectangle(0, 0, 32, 32),
            TargetBox = new HostileAttackRectangle(0, 0, 32, 32),
            AttackerStanding = new HostileAttackPoint(0, 0),
            TargetStanding = new HostileAttackPoint(0, 0),
            MaximumRangePixels = 100,
            Damage = 20,
        };

        Assert.False(
            HostileAttackHitProcessor.TryProcess(
                request,
                HostileAttackTestFactory.PlayerOne,
                context,
                pipeline,
                out var receipt
            )
        );
        Assert.Equal("hostile-shadow.attack-hit-host-context-invalid", receipt.Result.Reason);
        Assert.Equal(0, pipeline.Calls);
        Assert.Equal(100, pipeline.CurrentHealth);
    }

    [Fact]
    public void No_legal_point_returns_idle_while_invalid_location_requests_despawn()
    {
        var noPointMachine = HostileAttackTestFactory.StartAttack().Machine;
        var noPointController = new HostileShadowHitResponseController(noPointMachine);
        var blockedMap = new FakeMap(width: 40, height: 40) { Open = false };
        var noPoint = noPointController.HandleHit(
            Input(
                12,
                health: 80,
                132d,
                200d,
                new SequenceRandom(7, 4, 0),
                blockedMap
            )
        );
        Assert.Equal(HostileShadowStateIds.HitTeleport, noPoint.StateId);
        Assert.Equal("hostile-shadow.hit-teleport-no-legal-point", noPoint.Reason);
        var idle = noPointController.Advance(
            noPoint.PositionX,
            noPoint.PositionY,
            0d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Idle, idle.StateId);
        Assert.Equal(noPoint.PositionX, idle.PositionX);
        Assert.Equal(noPoint.PositionY, idle.PositionY);

        var invalidMachine = HostileAttackTestFactory.StartAttack().Machine;
        var invalidController = new HostileShadowHitResponseController(invalidMachine);
        var invalidMap = new FakeMap(width: 40, height: 40) { Valid = false };
        var despawn = invalidController.HandleHit(
            Input(
                20,
                health: 80,
                132d,
                200d,
                new SequenceRandom(8, 4, 0),
                invalidMap
            )
        );
        Assert.Equal(HostileShadowStateIds.Despawn, despawn.StateId);
        Assert.True(despawn.RemovalRequested);
        Assert.Equal(
            HostileShadowLifecycleTransitionKind.Despawn,
            despawn.Receipt!.Value.Kind
        );
        Assert.Equal(string.Empty, despawn.Receipt.Value.AttributedPlayerKey);
    }

    [Fact]
    public void Death_during_teleport_is_distinct_and_never_applies_teleport_target()
    {
        var (machine, _, attack) = HostileAttackTestFactory.StartAttack();
        var controller = new HostileShadowHitResponseController(machine);
        var hit = controller.HandleHit(
            Input(
                12,
                health: 1,
                attack.PositionX,
                attack.PositionY,
                new SequenceRandom(5, 8, 0)
            )
        );
        var hitReceipt = Assert.IsType<HostileShadowLifecycleReceipt>(hit.Receipt);

        var dying = controller.HandleHit(
            Input(
                13,
                health: 0,
                hit.PositionX,
                hit.PositionY,
                random: null,
                map: null
            )
        );

        Assert.Equal(HostileShadowStateIds.Dying, dying.StateId);
        var deathReceipt = Assert.IsType<HostileShadowLifecycleReceipt>(dying.Receipt);
        Assert.Equal(HostileShadowLifecycleTransitionKind.Dying, deathReceipt.Kind);
        Assert.Equal(13, deathReceipt.DeathRevision);
        Assert.NotEqual(hitReceipt.CorrelationId, deathReceipt.CorrelationId);
        Assert.True(deathReceipt.SettlementEligible);
        Assert.True(deathReceipt.DropEligible);
        Assert.True(deathReceipt.RewardEligible);

        var active = controller.Advance(
            dying.PositionX,
            dying.PositionY,
            399d,
            hasTarget: true
        );
        Assert.False(active.RemovalRequested);
        var remove = controller.Advance(
            active.PositionX,
            active.PositionY,
            1d,
            hasTarget: true
        );
        Assert.True(remove.RemovalRequested);
        Assert.Equal(100d, remove.PositionX, precision: 8);
        Assert.Equal(200d, remove.PositionY, precision: 8);
        var replay = controller.Advance(
            remove.PositionX,
            remove.PositionY,
            1d,
            hasTarget: true
        );
        Assert.Equal(HostileShadowHitResponseDecisionStatus.Duplicate, replay.Status);
    }

    [Fact]
    public void Natural_no_target_dying_has_no_settlement_and_removes_only_after_the_full_animation()
    {
        var machine = HostileAttackTestFactory.StartAttack().Machine;
        var controller = new HostileShadowHitResponseController(machine);

        var dying = controller.BeginNaturalDying(100d, 200d);

        Assert.True(dying.Valid, dying.Reason);
        Assert.Equal(HostileShadowStateIds.Dying, dying.StateId);
        Assert.Null(dying.Receipt);

        var active = controller.Advance(
            dying.PositionX,
            dying.PositionY,
            399d,
            hasTarget: false
        );
        Assert.False(active.RemovalRequested);

        var removed = controller.Advance(
            active.PositionX,
            active.PositionY,
            1d,
            hasTarget: false
        );
        Assert.True(removed.RemovalRequested);
        Assert.Null(removed.Receipt);
    }

    [Fact]
    public void Lifecycle_receipts_dedupe_exact_replay_and_reject_kind_confusion()
    {
        var store = new HostileShadowLifecycleReceiptStore();
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                HostileShadowLifecycleTransitionKind.Dying,
                "hostile-shadow.dying-health-zero",
                null,
                HostileAttackTestFactory.PlayerOne,
                out var dying
            )
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            store.Record(dying).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Duplicate,
            store.Record(dying).Status
        );
        Assert.True(
            store.TryGetDying(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                out var downstream
            )
        );
        Assert.Equal(30, downstream.DeathRevision);
        Assert.Equal(HostileAttackTestFactory.PlayerOne, downstream.AttributedPlayerKey);
        Assert.True(downstream.SettlementEligible);
        Assert.True(downstream.DropEligible);
        Assert.True(downstream.RewardEligible);

        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                HostileShadowLifecycleTransitionKind.Dying,
                "hostile-shadow.dying-conflicting-reason",
                null,
                HostileAttackTestFactory.PlayerOne,
                out var changed
            )
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Conflict,
            store.Record(changed).Status
        );

        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                HostileShadowLifecycleTransitionKind.Despawn,
                "hostile-shadow.cleanup.natural",
                null,
                string.Empty,
                out var confused
            )
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Conflict,
            store.Record(confused).Status
        );
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Public_state_health_contract_separates_true_death_and_cleanup()
    {
        Assert.True(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Dying, 0));
        Assert.False(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Dying, 1));
        Assert.False(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Despawn, 0));
        Assert.True(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.Despawn, 1));
        Assert.False(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.HitTeleport, 0));
        Assert.True(HostileShadowStateIds.IsHealthValid(HostileShadowStateIds.HitTeleport, 1));
    }

    private static HostileShadowHitResponseInput Input(
        long revision,
        int health,
        double positionX,
        double positionY,
        IHostileShadowTeleportRandom? random,
        IHostileShadowTeleportMap? map = null,
        string attackerPlayerKey = HostileAttackTestFactory.PlayerOne
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
            AttackerPlayerKey = attackerPlayerKey,
            Map = health == 0 ? null : map ?? new FakeMap(40, 40),
            Random = health == 0 ? null : random,
        };
    }

    private sealed class SequenceRandom : IHostileShadowTeleportRandom
    {
        private readonly Queue<int> values;

        internal SequenceRandom(int seed, params int[] values)
        {
            Seed = seed;
            this.values = new Queue<int>(values);
        }

        public int Seed { get; }
        internal int Calls { get; private set; }

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            Calls++;
            var value = values.Count > 0
                ? values.Dequeue()
                : minimumInclusive == HostileShadowTeleportPointSelector.MinimumDistanceTiles
                    ? HostileShadowTeleportPointSelector.MinimumDistanceTiles
                    : 4;
            if (value < minimumInclusive || value >= maximumExclusive)
                throw new ArgumentOutOfRangeException(nameof(value));
            return value;
        }
    }

    private sealed class FakeMap : IHostileShadowTeleportMap
    {
        private readonly int width;
        private readonly int height;

        internal FakeMap(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        internal bool Valid { get; set; } = true;
        internal bool Open { get; set; } = true;
        internal Queue<bool> Validity { get; } = new();

        public bool IsLocationValid(string expectedLocationId)
        {
            return string.Equals(
                    expectedLocationId,
                    HostileAttackTestFactory.LocationId,
                    StringComparison.Ordinal
                )
                && (Validity.Count > 0 ? Validity.Dequeue() : Valid);
        }

        public bool IsTileOnMap(int tileX, int tileY)
        {
            return tileX >= 0 && tileY >= 0 && tileX < width && tileY < height;
        }

        public bool IsTileLocationOpen(int tileX, int tileY)
        {
            return Open;
        }

        public bool IsTilePassable(int tileX, int tileY)
        {
            return Open;
        }
    }

    private sealed class CountingPipeline : IHostileAttackLethalDamagePipeline
    {
        internal CountingPipeline(int health)
        {
            CurrentHealth = health;
        }

        public int CurrentHealth { get; private set; }
        internal int Calls { get; private set; }

        public void ApplyOrdinaryDamage(int damage)
        {
            Calls++;
            CurrentHealth = Math.Max(0, CurrentHealth - damage);
        }
    }
}
