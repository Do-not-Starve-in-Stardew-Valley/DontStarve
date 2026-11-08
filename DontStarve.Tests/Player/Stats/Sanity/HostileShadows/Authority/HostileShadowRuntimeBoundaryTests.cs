using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowRuntimeBoundaryTests
{
    [Fact]
    public void Host_registers_and_unsubscribes_every_intersecting_lifecycle_boundary()
    {
        var source = Contract("SmapiHostileShadowHost.cs");
        var multiplayer = Contract("SmapiHostileShadowMultiplayerCoordinator.cs");
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var peerGate = Contract("HostileShadowPhysicalCapabilityGate.cs");

        AssertPair(source, "GameLoop.SaveLoaded", "OnSaveLoaded");
        AssertPair(source, "GameLoop.DayEnding", "OnDayEnding");
        AssertPair(source, "StateEventPublished", "OnStateEventPublished");
        AssertPair(source, "EventOwnerCoverageChanged", "OnEventOwnerCoverageChanged");
        AssertPair(source, "WorldBoundaryStarting", "OnWorldBoundaryStarting");
        AssertPair(source, "SessionClearing", "OnSessionClearing");
        Assert.Contains("timeApi.OnUpdate.Add(OnMinuteUpdate)", source, StringComparison.Ordinal);
        Assert.Contains("timeApi.OnUpdate.Remove(OnMinuteUpdate)", source, StringComparison.Ordinal);
        Assert.Contains("timeApi.OnSync.Add(OnTimeSynchronized)", source, StringComparison.Ordinal);
        Assert.Contains("timeApi.OnSync.Remove(OnTimeSynchronized)", source, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit += OnProcessExit", source, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit -= OnProcessExit", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowCleanupReasonIds.OwnerDisconnected", multiplayer, StringComparison.Ordinal);
        Assert.Contains("HostileShadowCleanupReasonIds.ReturnedToTitle", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowCleanupReasonIds.DayEnding", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowCleanupReasonIds.Disposed", source, StringComparison.Ordinal);
        Assert.Contains("world.OnSessionStarted()", source, StringComparison.Ordinal);
        Assert.Contains("world.BeginSettlementSession(lifecycle.SessionId", source, StringComparison.Ordinal);
        Assert.Contains("sessionLifecycleCoordinator.BeginSession(", source, StringComparison.Ordinal);
        Assert.Contains("multiplayer.ClearSession()", source, StringComparison.Ordinal);
        Assert.Contains("sessionLifecycleCoordinator.ClearSession()", source, StringComparison.Ordinal);
        Assert.Contains("world.ClearSession()", source, StringComparison.Ordinal);
        AssertPair(world, "GameLoop.UpdateTicked", "OnUpdateTicked");
        AssertPair(peerGate, "Multiplayer.PeerContextReceived", "OnPeerContextReceived");
        AssertPair(peerGate, "Multiplayer.PeerDisconnected", "OnPeerDisconnected");
        AssertPair(world, "WorldResourcesReleasing", "OnWorldResourcesReleasing");
        Assert.Contains("HostileAttackStateMachine AttackState", world, StringComparison.Ordinal);
        Assert.Contains("HostileShadowHitResponseController HitResponse", world, StringComparison.Ordinal);
        Assert.Contains("HostileShadowMonsterHitBridge.Configure", world, StringComparison.Ordinal);
        Assert.Contains("HostileShadowMonsterHitBridge.Clear", world, StringComparison.Ordinal);
        Assert.Contains("lifecycleReceipts.Clear()", world, StringComparison.Ordinal);
        Assert.Contains("settlements.ClearSession()", world, StringComparison.Ordinal);
        Assert.True(
            world.IndexOf("ResolvePendingLethalDamage();", StringComparison.Ordinal)
                < world.IndexOf("RefreshTargetsAndSnapshots();", StringComparison.Ordinal)
        );
        Assert.Contains("entries.Remove(entityId", world, StringComparison.Ordinal);
        Assert.DoesNotContain("Player.Warped +=", world, StringComparison.Ordinal);
    }

    [Fact]
    public void Shadow_damage_uses_vanilla_timer_seam_without_relaxing_guarded_entities()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var monster = Contract("HostileShadowMonster.cs");

        Assert.DoesNotContain(
            "monster.invincibleCountdown = 1000",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "450 / 2",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "HostileShadowMonster.update",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "public override bool isInvincible()",
            monster,
            StringComparison.Ordinal
        );
        var invincibility = Slice(
            monster,
            "public override bool isInvincible()",
            "public override void setTrajectory"
        );
        Assert.Contains("BindingHiddenModDataKey", invincibility, StringComparison.Ordinal);
        Assert.Contains("RetreatingModDataKey", invincibility, StringComparison.Ordinal);
        Assert.Contains("return base.isInvincible();", invincibility, StringComparison.Ordinal);
        Assert.Contains("stopGlowing();", monster, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduled_shadow_rolls_wait_for_the_final_tick_and_rebase_after_non_unit_sync()
    {
        var host = Contract("SmapiHostileShadowHost.cs");
        var minute = Slice(
            host,
            "private void OnMinuteUpdate(long gameMinute)",
            "private void OnTimeSynchronized(long gameMinute, long delta)"
        );
        var sync = Slice(
            host,
            "private void OnTimeSynchronized(long gameMinute, long delta)",
            "private void AdvanceNaturalIntervalSpawns(int elapsedMilliseconds)"
        );
        var tick = Slice(
            host,
            "private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)",
            "private bool CanRetreatForCurrentTarget"
        );

        Assert.Contains("pendingScheduledShadowCheckMinute = gameMinute", minute, StringComparison.Ordinal);
        Assert.DoesNotContain("TrimOverCap(gameMinute)", minute, StringComparison.Ordinal);
        Assert.Contains("pendingScheduledShadowCheckMinute = -1", sync, StringComparison.Ordinal);
        Assert.Contains("RebaseShadowCheckSchedules(gameMinute)", sync, StringComparison.Ordinal);
        Assert.Contains("var scheduledCheckMinute = pendingScheduledShadowCheckMinute", tick, StringComparison.Ordinal);
        Assert.Contains("TrimOverCap(scheduledCheckMinute)", tick, StringComparison.Ordinal);
    }

    [Fact]
    public void Tick_loop_reuses_sorted_entity_order_and_value_attack_input()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var attackState = Contract("HostileAttackStateMachine.cs");
        var refresh = Slice(
            world,
            "private void RefreshTargetsAndSnapshots()",
            "private void AdvanceCachedTargets(bool snapshotCadence)"
        );
        var advance = Slice(
            world,
            "private void AdvanceCachedTargets(bool snapshotCadence)",
            "private static void ApplyMonsterState(PhysicalEntry entry)"
        );

        Assert.Contains(
            "new(\n        HostileShadowAuthority.MaximumEntities\n    )",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new long[\n        HostileShadowAuthority.MaximumEntities\n    ]",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains("orderedEntityIds.BinarySearch(entityId)", world, StringComparison.Ordinal);
        Assert.Contains("orderedEntityIds.Insert(~insertionIndex, entityId)", world, StringComparison.Ordinal);
        Assert.Contains("orderedEntityIds.RemoveAt(orderIndex)", world, StringComparison.Ordinal);
        Assert.Contains(
            "orderedEntityIds.CopyTo(entityIterationBuffer, 0)",
            world,
            StringComparison.Ordinal
        );

        AssertNoTemporaryEntityCollection(refresh);
        AssertNoTemporaryEntityCollection(advance);
        Assert.Contains(
            "var attackInput = HostileAttackStateInput.Capture(",
            advance,
            StringComparison.Ordinal
        );
        Assert.Contains("in attackInput", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("new HostileAttackStateInput", advance, StringComparison.Ordinal);
        Assert.Contains(
            "internal readonly struct HostileAttackStateInput",
            attackState,
            StringComparison.Ordinal
        );
        Assert.True(typeof(HostileAttackStateInput).IsValueType);
        Assert.False(typeof(HostileAttackStateInput).IsClass);
        var advanceMethod = typeof(HostileAttackStateMachine).GetMethod(
            "Advance",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
        );
        Assert.NotNull(advanceMethod);
        var inputParameter = advanceMethod!.GetParameters()[0];
        Assert.True(inputParameter.ParameterType.IsByRef);
        Assert.Equal(
            typeof(HostileAttackStateInput),
            inputParameter.ParameterType.GetElementType()
        );
        Assert.DoesNotContain(
            "class HostileAttackStateInput",
            attackState,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "HostileAttackStateInput?",
            attackState,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "in HostileAttackStateInput input",
            attackState,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Steady_tick_mod_data_sync_formats_only_after_raw_value_changes()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var advance = Slice(
            world,
            "private void AdvanceCachedTargets(bool snapshotCadence)",
            "private static void ApplyMonsterState(PhysicalEntry entry)"
        );
        var localApply = Slice(
            world,
            "private static void ApplyMonsterState(PhysicalEntry entry)",
            "private static void ApplyAttackModDataIfChanged("
        );
        var attackApply = Slice(
            world,
            "private static void ApplyAttackModDataIfChanged(",
            "private static string SerializeMovementFrame(int frameIndex)"
        );

        Assert.Contains(
            "AppliedStateId { get; set; } = HostileShadowStateIds.Spawn",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AppliedAttackInstanceRevision { get; set; }",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AppliedAttackFrameNumber { get; set; }",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "AppliedMovementFrameIndex = movementPresentation is null ? -1 : 0",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "TargetPlayerKeyIsCanonical = SanityPlayerKey.IsCanonical(value)",
            world,
            StringComparison.Ordinal
        );
        Assert.True(
            world.IndexOf(
                "if (string.Equals(targetPlayerKey, value, StringComparison.Ordinal))",
                StringComparison.Ordinal
            )
                < world.IndexOf(
                    "TargetPlayerKeyIsCanonical = SanityPlayerKey.IsCanonical(value)",
                    StringComparison.Ordinal
                )
        );
        Assert.Contains(
            "var hasTarget = entry.TargetPlayerKeyIsCanonical",
            advance,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "SanityPlayerKey.IsCanonical(entry.TargetPlayerKey)",
            advance,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(".ToString(", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Concat", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Monster.modData[", localApply, StringComparison.Ordinal);
        Assert.True(
            localApply.IndexOf(
                "entry.AppliedMovementFacingId",
                StringComparison.Ordinal
            )
                < localApply.IndexOf(
                    "HostileShadowMonster.MovementFacingModDataKey",
                    StringComparison.Ordinal
                )
        );
        Assert.True(
            localApply.IndexOf(
                "entry.AppliedMovementFrameIndex != presentation.FrameIndex",
                StringComparison.Ordinal
            )
                < localApply.IndexOf(
                    "SerializeMovementFrame(presentation.FrameIndex)",
                    StringComparison.Ordinal
                )
        );

        var revisionGuard = attackApply.IndexOf(
            "if (entry.AppliedAttackInstanceRevision != attackInstanceRevision)",
            StringComparison.Ordinal
        );
        var revisionFormat = attackApply.IndexOf(
            "attackInstanceRevision.ToString(CultureInfo.InvariantCulture)",
            StringComparison.Ordinal
        );
        var frameGuard = attackApply.IndexOf(
            "if (entry.AppliedAttackFrameNumber != attackFrameNumber)",
            StringComparison.Ordinal
        );
        var frameFormat = attackApply.IndexOf(
            "attackFrameNumber.ToString(CultureInfo.InvariantCulture)",
            StringComparison.Ordinal
        );
        Assert.True(revisionGuard >= 0 && revisionGuard < revisionFormat);
        Assert.True(frameGuard >= 0 && frameGuard < frameFormat);
        Assert.Equal(2, Count(attackApply, ".ToString("));
        Assert.DoesNotContain("entry.Monster.modData[", attackApply, StringComparison.Ordinal);
        Assert.Contains(
            "SetModDataIfChanged(\n                entry.Monster,\n                HostileShadowMonster.StateModDataKey",
            attackApply,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SetModDataIfChanged(\n                entry.Monster,\n                HostileShadowMonster.AttackInstanceModDataKey",
            attackApply,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ApplyAttackModDataIfChanged(\n            entry,\n            state.StateId",
            world,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Collision_preflight_defers_geometry_and_protocol_work_until_the_target_is_eligible()
    {
        var combat = Contract("SmapiHostileAttackCombatService.cs");
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var geometry = Contract("HostileAttackGeometry.cs");
        var instance = Contract("HostileAttackInstance.cs");
        var damage = Contract("HostileAttackDamage.cs");
        var process = Slice(
            combat,
            "internal void ProcessCurrentHits(",
            "internal bool TryProcessHit("
        );
        var playerKeyOffset = process.IndexOf(
            "var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(",
            StringComparison.Ordinal
        );
        var targetKeyOffset = process.IndexOf(
            "!string.Equals(playerKey, state.TargetPlayerKey",
            StringComparison.Ordinal
        );
        var settledOffset = process.IndexOf(
            "instance.HasSettledPlayer(playerId)",
            StringComparison.Ordinal
        );
        var targetBoundsOffset = process.IndexOf(
            "var farmerBoundingBox = farmer.GetBoundingBox()",
            StringComparison.Ordinal
        );
        Assert.True(playerKeyOffset > 0);
        Assert.True(targetKeyOffset > playerKeyOffset);
        Assert.True(settledOffset > targetKeyOffset);
        Assert.True(targetBoundsOffset > settledOffset);
        var preflight = process[..targetBoundsOffset];

        Assert.Contains("Game1.getOnlineFarmers()", preflight, StringComparison.Ordinal);
        Assert.True(
            preflight.IndexOf("farmer.currentLocation", StringComparison.Ordinal)
                < playerKeyOffset
        );
        Assert.True(
            targetKeyOffset < settledOffset
        );
        Assert.Contains(
            "if (MissDiagnosticsEnabled && loggedMissReasons.Add(\"attack-hit.player-location-mismatch\"))",
            preflight,
            StringComparison.Ordinal
        );
        AssertNoManagedCollisionConstruction(preflight);
        Assert.DoesNotContain("IEnumerable<Farmer>", process, StringComparison.Ordinal);
        Assert.DoesNotContain(".Cast<Farmer>", process, StringComparison.Ordinal);
        Assert.DoesNotContain(".AsEnumerable(", process, StringComparison.Ordinal);
        Assert.DoesNotContain("Rejected(", process, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HostileAttackCollisionResolver.IsWithinRange(",
            process,
            StringComparison.Ordinal
        );
        Assert.True(
            targetBoundsOffset
                < process.IndexOf("attackBox.Intersects(targetBox)", StringComparison.Ordinal)
        );
        Assert.True(
            process.IndexOf("new ShadowAttackHitRequest", StringComparison.Ordinal)
                < process.IndexOf("!TryProcessHit(", StringComparison.Ordinal)
        );

        Assert.DoesNotContain("using System.Linq;", geometry, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", geometry, StringComparison.Ordinal);
        Assert.DoesNotContain("point =>", geometry, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.", geometry, StringComparison.Ordinal);
        Assert.Contains("Math.Min(", geometry, StringComparison.Ordinal);
        Assert.Contains("Math.Max(", geometry, StringComparison.Ordinal);
        Assert.Contains("private readonly HashSet<long> hitPlayerIds", instance, StringComparison.Ordinal);
        Assert.Contains("internal bool HasSettledPlayer(long playerId)", instance, StringComparison.Ordinal);
        Assert.Contains("context.TargetPlayerId", damage, StringComparison.Ordinal);
        Assert.Contains(
            "TargetPlayerId = farmer.UniqueMultiplayerID",
            combat,
            StringComparison.Ordinal
        );
        Assert.Contains("if (playerId == 0)", combat, StringComparison.Ordinal);
        Assert.DoesNotContain("playerId < 0", combat, StringComparison.Ordinal);
        Assert.Contains(
            "return playerId != 0 && hitPlayerIds.Contains(playerId);",
            instance,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SanityPlayerKey.TryParseCanonicalPlayerId",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new HostileAttackHitContext",
            combat,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new SmapiHostileAttackDamageAdapter",
            combat,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Render_frame_lookup_uses_a_value_key_without_per_draw_formatting()
    {
        var monster = Contract("HostileShadowMonster.cs");
        var draw = Slice(
            monster,
            "internal void Draw(SpriteBatch spriteBatch, HostileShadowMonster monster)",
            "private bool TryGetOrLoad("
        );

        Assert.Contains(
            "(string BindingId, string StateId, int FrameIndex)",
            monster,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "var visualResourceState = visualState;",
            draw,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "(bindingId, visualResourceState, frameIndex)",
            draw,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("Key(", draw, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToString(", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Concat", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", draw, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(", draw, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray(", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("yield return", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("(object)", draw, StringComparison.Ordinal);
        Assert.DoesNotContain(" as object", draw, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private static string Key(",
            monster,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Runtime_is_session_only_and_uses_a_probed_real_network_monster()
    {
        var source = Contract("SmapiHostileShadowHost.cs");
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var monster = Contract("HostileShadowMonster.cs");
        var peerGate = Contract("HostileShadowPhysicalCapabilityGate.cs");

        Assert.DoesNotContain("WriteSaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadSaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SanitySaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Critter", source, StringComparison.Ordinal);
        Assert.Contains("location.characters.Add(monster)", world, StringComparison.Ordinal);
        Assert.Contains("NetCollection<NPC>", peerGate, StringComparison.Ordinal);
        Assert.Contains("source.WriteFull(writer)", peerGate, StringComparison.Ordinal);
        Assert.Contains("copy.ReadFull(reader, default)", peerGate, StringComparison.Ordinal);
        Assert.Contains("peer.GetMod(modId)", peerGate, StringComparison.Ordinal);
        Assert.Contains("public sealed class HostileShadowMonster : Monster", monster, StringComparison.Ordinal);
        Assert.Contains("public HostileShadowMonster()", monster, StringComparison.Ordinal);
        Assert.Contains("Character.modData", monster, StringComparison.Ordinal);
        Assert.DoesNotContain("initNetFields", monster, StringComparison.Ordinal);
        Assert.Contains(
            "Physical spawning remains fail-closed",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Multiplayer_uses_scoped_v4_transport_and_diagnostic_hash_without_config_sync()
    {
        var source = Contract("SmapiHostileShadowMultiplayerCoordinator.cs");

        Assert.Contains("DeltaFlushCadenceTicks = 15", source, StringComparison.Ordinal);
        Assert.Contains("MaximumQueuedDeltas = 64", source, StringComparison.Ordinal);
        Assert.Contains("e.IsMultipleOf(DeltaFlushCadenceTicks)", source, StringComparison.Ordinal);
        Assert.Contains("StateSnapshot.v4", source, StringComparison.Ordinal);
        Assert.Contains("StateDelta.v4", source, StringComparison.Ordinal);
        Assert.Contains("StateSnapshotRequest.v4", source, StringComparison.Ordinal);
        Assert.Contains("ConversionRequest.v4", source, StringComparison.Ordinal);
        Assert.Contains("AggroHint.v1", source, StringComparison.Ordinal);
        Assert.Contains("PhysicalCapability.v1", source, StringComparison.Ordinal);
        Assert.Contains("AttackHitRequest.v1", source, StringComparison.Ordinal);
        Assert.Contains("ConfigFingerprint.v1", source, StringComparison.Ordinal);
        Assert.Contains("DarkHand.LeaseRequest.v1", source, StringComparison.Ordinal);
        Assert.Contains("DarkHand.Lease.v1", source, StringComparison.Ordinal);
        Assert.Contains("KnownEntityRevision", source, StringComparison.Ordinal);
        Assert.Contains("TryCreateScopedSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("ShadowSnapshotTrigger.Resync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastFullSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("new[] { playerId }", source, StringComparison.Ordinal);
        Assert.Contains("new[] { e.FromPlayerID }", source, StringComparison.Ordinal);
        Assert.Contains("\"protocol=\"", source, StringComparison.Ordinal);
        Assert.Contains("\" type=\"", source, StringComparison.Ordinal);
        Assert.Contains("\" sender=\"", source, StringComparison.Ordinal);
        Assert.Contains("\" reason=\"", source, StringComparison.Ordinal);
        Assert.Contains("ConfigSchemaVersion", source, StringComparison.Ordinal);
        Assert.Contains("Hash = fingerprint.Hash", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigKeys.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TypedConfigResolver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MonsterDifficultyProfile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SanityMonsterIntensity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fire", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_07_routes_true_death_to_custom_settlement_and_keeps_vanilla_kill_disabled()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var monster = Contract("HostileShadowMonster.cs");
        var attack = Contract("SmapiHostileAttackCombatService.cs");
        var settlement = Contract("HostileShadowSettlementService.cs");
        var effects = Contract("SmapiHostileShadowSettlementEffects.cs");

        Assert.Contains("HostileShadowMonsterHitBridge.HandleHit", monster, StringComparison.Ordinal);
        Assert.Contains("HandleIncomingHit", world, StringComparison.Ordinal);
        Assert.Contains("monster.Health = damageDecision.PhysicalHealthAfter", world, StringComparison.Ordinal);
        Assert.Contains("pendingDying ? 1 : logicalHealthAfter", Contract("HostileShadowHitResponse.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("base.takeDamage", monster, StringComparison.Ordinal);
        Assert.DoesNotContain("onMonsterKilled", monster, StringComparison.Ordinal);
        Assert.DoesNotContain("TemporaryAnimatedSprite", world, StringComparison.Ordinal);
        Assert.DoesNotContain("PathFind", world, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AStar(", world, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createItemDebris", world, StringComparison.Ordinal);
        Assert.Contains("SettleDying(entityId, entry)", world, StringComparison.Ordinal);
        Assert.Contains("lifecycleReceipts.TryGetDying", world, StringComparison.Ordinal);
        Assert.Contains("request.Authority != SanityAuthorityRole.Host", settlement, StringComparison.Ordinal);
        Assert.True(
            settlement.IndexOf("request.Authority != SanityAuthorityRole.Host", StringComparison.Ordinal)
                < settlement.IndexOf("random.NextBonusRoll10000(seed)", StringComparison.Ordinal)
        );
        Assert.Contains("MaximumReceipts = 256", settlement, StringComparison.Ordinal);
        Assert.DoesNotContain("receipts.Remove", settlement, StringComparison.Ordinal);
        Assert.Contains("VoidEssenceQualifiedItemId = \"(O)769\"", effects, StringComparison.Ordinal);
        Assert.Contains("ItemRegistry.Create", effects, StringComparison.Ordinal);
        Assert.Contains("Game1.createItemDebris", effects, StringComparison.Ordinal);
        Assert.Contains("SanityChangeSource.HostileShadowKill", effects, StringComparison.Ordinal);
        Assert.Contains("Game1.getOnlineFarmers()", effects, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(player.currentLocation, location)", effects, StringComparison.Ordinal);
        Assert.Equal(2, Count(world, "SettleDying("));
        Assert.DoesNotContain("Game1.random", settlement, StringComparison.Ordinal);
        Assert.DoesNotContain("Random.Shared", settlement, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.player", effects, StringComparison.Ordinal);
        Assert.DoesNotContain("Experience", world, StringComparison.Ordinal);
        Assert.DoesNotContain("INonLethalDamageService", world, StringComparison.Ordinal);
        Assert.Contains("HostileAttackHitProcessor.TryProcess", attack, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_03_bridges_nonlethal_hits_after_sync_and_force_cleans_world_audio()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var modEntry = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ShadowProjection", "ModEntry.cs")
        );
        var hit = Slice(
            world,
            "private int HandleIncomingHit(",
            "    private void PropagateAggroToNearby("
        );
        var sync = hit.IndexOf("TrySynchronizeHitResponse(", StringComparison.Ordinal);
        var notify = hit.IndexOf("NotifyHostileHit", sync, StringComparison.Ordinal);

        Assert.True(sync >= 0);
        Assert.True(notify > sync);
        Assert.Contains("damageDecision.PendingDying", hit, StringComparison.Ordinal);
        Assert.Contains("ClassifyHitSource(attacker)", hit, StringComparison.Ordinal);
        Assert.Contains("ResolvePendingLethalDamage", world, StringComparison.Ordinal);
        Assert.Contains("ConfirmHostileDeath", world, StringComparison.Ordinal);
        Assert.Contains("RemoveOwner(", world, StringComparison.Ordinal);
        Assert.Contains("NotifyHostileHit(", modEntry, StringComparison.Ordinal);
        Assert.Contains("ForceRemoveAll(", modEntry, StringComparison.Ordinal);
        Assert.Contains("coordinator.ForceRemoveAll", modEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void External_health_drift_is_repaired_before_the_hit_is_priced()
    {
        // DIAG-20260922 regression: a third-party mod inflates the physical monster health, so the
        // ledger and the live monster disagree. The old guard logged
        // "hostile-shadow.hit-guard-health-mismatch" and returned 0, which is the reported
        // "damage number 0, shadow takes no damage". The guard must now adopt the live value into
        // the ledger first and only then price the hit.
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var hit = Slice(
            world,
            "private int HandleIncomingHit(",
            "    private void PropagateAggroToNearby("
        );
        var mismatch = hit.IndexOf(
            "if (state.Health != monster.Health)",
            StringComparison.Ordinal
        );
        var repair = hit.IndexOf(
            "hostile-shadow.hit-guard-health-repair\"",
            StringComparison.Ordinal
        );
        var pricing = hit.IndexOf(
            "HostileShadowIncomingDamagePolicy.Evaluate(",
            StringComparison.Ordinal
        );

        Assert.True(mismatch >= 0, "health mismatch guard missing");
        Assert.True(repair > mismatch, "repair must run after the mismatch is observed");
        Assert.True(pricing > repair, "the hit must be priced only after the ledger is repaired");
        // The old hard refusal must be gone: a mismatch alone no longer rejects the hit.
        Assert.DoesNotContain(
            "hostile-shadow.hit-guard-health-mismatch (entity=",
            hit,
            StringComparison.Ordinal
        );
        // The repair still fails closed when the ledger genuinely cannot adopt the value.
        Assert.Contains(
            "hostile-shadow.hit-guard-health-repair-failed",
            hit,
            StringComparison.Ordinal
        );
        // Exactly one canonical reason owns this observation point. Stage 03's synonym
        // "hostile-shadow.hit-guard-health-mismatch-adopted" was removed: it was passed through the
        // single-argument LogOnce overload, which keys the dedup set on the whole message, so with
        // entity baked in it produced one dedup entry per monster.
        var drift = Slice(
            hit,
            "if (state.Health != monster.Health)",
            "var attackerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId("
        );
        // Comments are removed first so this source file may freely quote the removed name while
        // documenting why it is gone; only executable code counts as a live diagnostic.
        var driftCode = CodeOnly(drift);
        const string canonical = "\"hostile-shadow.ledger-adopted-physical-health\"";
        Assert.Contains(canonical, driftCode, StringComparison.Ordinal);
        // Recorded exactly once: a second call here would double-record one observed event, which
        // stage 06 hard requirement 6 forbids.
        Assert.Equal(
            1,
            CodeLines(driftCode).Count(line => line.Contains(canonical, StringComparison.Ordinal))
        );
        Assert.DoesNotContain(
            "\"hostile-shadow.hit-guard-health-mismatch-adopted",
            driftCode,
            StringComparison.Ordinal
        );
        // The adoption line must follow the repair it reports: logging it first would claim the
        // ledger followed even when the write then fails.
        Assert.True(
            driftCode.IndexOf(canonical, StringComparison.Ordinal)
                > driftCode.IndexOf(
                    "\"hostile-shadow.hit-guard-health-repair\"",
                    StringComparison.Ordinal
                ),
            "the adoption diagnostic must be logged after the repair succeeds, not before it"
        );
        Assert.True(
            driftCode.IndexOf(canonical, StringComparison.Ordinal)
                > driftCode.IndexOf(
                    "authority.TryGetEntity(entityId, out state)",
                    StringComparison.Ordinal
                ),
            "the adoption diagnostic must read the post-repair ledger value, not the stale clone"
        );
    }

    [Fact]
    public void External_health_diagnostics_are_stable_and_bounded_to_one_sample_per_reason()
    {
        // DIAG-20260922: three diagnostics exist so the next fan log alone can identify the mod
        // that rewrites our health. They must be deduplicated by a fixed reason key (one sample per
        // reason, never per entity) so a per-monster detail body cannot exhaust the log budget.
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");

        foreach (
            var reason in new[]
            {
                "hostile-shadow.external-health-mutation-observed",
                "hostile-shadow.max-health-ratcheted",
                "hostile-shadow.ledger-adopted-physical-health",
            }
        )
        {
            Assert.Contains(reason, world, StringComparison.Ordinal);
        }

        // Every one of the three new reasons must go through the detail-carrying overload, which
        // keys the dedup set on the reason alone: that is what bounds each new reason to a single
        // sample. Routing a reason through the single-argument overload would fold the per-observation
        // body into the key (one entry per monster), which is exactly the exhaustion this stage
        // forbids. Whitespace is squashed so the check does not depend on argument line wrapping.
        var squashed = new string(
            CodeOnly(world).Where(character => !char.IsWhiteSpace(character)).ToArray()
        );
        foreach (
            var reason in new[]
            {
                "hostile-shadow.external-health-mutation-observed",
                "hostile-shadow.max-health-ratcheted",
                "hostile-shadow.ledger-adopted-physical-health",
            }
        )
        {
            Assert.Contains(
                $"LogOnce(\"{reason}\",string.Concat(",
                squashed,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                $"LogOnce(\"{reason}\",LogLevel.",
                squashed,
                StringComparison.Ordinal
            );
            // The reason literal must never be concatenated together with the observation body,
            // otherwise the body becomes part of the dedup key.
            Assert.DoesNotContain($"\"{reason} (", squashed, StringComparison.Ordinal);
        }

        // The detail-carrying overload keys the dedup set on the reason, not on the whole message.
        var overload = Slice(
            world,
            "private void LogOnce(string reason, string detail, LogLevel level)",
            "    private void LogOnce(string reason, LogLevel level)"
        );
        Assert.Contains("loggedReasons.Add(reason)", overload, StringComparison.Ordinal);
        Assert.Contains("MaximumLoggedReasons", overload, StringComparison.Ordinal);
        Assert.DoesNotContain("Add(detail", overload, StringComparison.Ordinal);

        // The ratchet diagnostic must be driven by the authority event rather than by polling.
        Assert.Contains(
            "authority.MaxHealthRatcheted += OnMaxHealthRatcheted;",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "authority.MaxHealthRatcheted -= OnMaxHealthRatcheted;",
            world,
            StringComparison.Ordinal
        );

        // The hot per-frame sync path must only build the detail string when a drift is observed.
        var sync = Slice(
            world,
            "hostile-shadow.routine-sync-rejected (",
            "    private static void AdvanceNoTargetClock("
        );
        Assert.Contains(
            "else if (state.Health != entry.Monster.Health)",
            sync,
            StringComparison.Ordinal
        );

        // Diagnostics must never be escalated to errors, and lenient reasons must stay bounded.
        foreach (var reason in new[] { "external-health-mutation-observed", "max-health-ratcheted" })
        {
            var index = world.IndexOf(reason, StringComparison.Ordinal);
            Assert.True(index >= 0);
        }
        Assert.DoesNotContain(
            "hostile-shadow.external-health-mutation-observed\", LogLevel.Error",
            world,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Refresh_occupancy_ignores_the_live_target_in_both_authority_and_world()
    {
        // DIAG-20260922: the two occupancy counters (authority + world) must agree, otherwise the
        // permit path and the cap path keep contradicting each other. Both must key on ownership
        // and location only, and neither may consult the live aggro target.
        // HostileShadowAuthority.cs is compiled into the test assembly directly (no Contracts
        // copy), so it is read from the mod source tree by static repository-relative path.
        var authority = Source("DontStarve/Player/Stats/Sanity/HostileShadows/Runtime/HostileShadowAuthority.cs");
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var authorityCount = Slice(
            authority,
            "private int GetOwnerOccupancyOnCurrentMap(",
            "internal bool TryGetConversionEpochRevision("
        );
        var worldCount = Slice(
            world,
            "internal int CountOwnerEntitiesOnCurrentMap(",
            "internal int CountEntitiesForOwnerAtLocation("
        );

        foreach (var slice in new[] { authorityCount, worldCount })
        {
            Assert.Contains("LocationId", slice, StringComparison.Ordinal);
            Assert.Contains("Despawn", slice, StringComparison.Ordinal);
            foreach (var line in CodeLines(slice))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                // Ownership is asserted via the owner index in the authority and via
                // OwnerPlayerKey in the world; neither may read TargetPlayerKey.
                Assert.DoesNotContain(
                    "state.TargetPlayerKey",
                    line,
                    StringComparison.Ordinal
                );
            }
        }

        // The authority computes occupancy from its owner index, so ownership is implicit there.
        Assert.Contains("entityIdsByOwner", authorityCount, StringComparison.Ordinal);
        Assert.Contains("state.OwnerPlayerKey", worldCount, StringComparison.Ordinal);

        // The host must call the world counter through the renamed method.
        var host = Contract("SmapiHostileShadowHost.cs");
        Assert.Contains(
            "world.CountOwnerEntitiesOnCurrentMap(",
            host,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "CountLockedEntitiesForOwnerAtLocation",
            host,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Dying_publish_value_never_reads_the_live_physical_health()
    {
        // DIAG-20260922: settlement broke for the same root cause as the zero damage. If the
        // published Dying health were read back from monster.Health, an external mod re-inflating
        // the monster after our write would publish Dying + positive health, violating
        // IsHealthValid(Dying, health == 0) and killing the whole settlement chain (no kill, no
        // drop). The publish value must therefore be resolved by our own policy.
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var publish = Slice(
            world,
            "private static int ResolvePublishedHealth(",
            "    private bool TrySynchronizeHitResponse("
        );
        var settle = Slice(
            world,
            "private void SettleDying(long entityId, PhysicalEntry entry)",
            "    private static int ResolvePublishedHealth("
        );

        Assert.Contains(
            "HostileShadowStateIds.Dying",
            publish,
            StringComparison.Ordinal
        );
        Assert.Contains("? 0", publish, StringComparison.Ordinal);
        Assert.Contains(
            "ResolvePublishedHealth(decision.StateId, entry.Monster.Health)",
            world,
            StringComparison.Ordinal
        );
        // Settlement qualification reads the ledger snapshot only, never the live monster.
        Assert.Contains("IsTrueDyingForSettlement", settle, StringComparison.Ordinal);
        // Only the explanatory comment may mention the physical health; no code may read it.
        foreach (var line in CodeLines(settle))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            Assert.DoesNotContain(
                "entry.Monster.Health",
                line,
                StringComparison.Ordinal
            );
        }
        Assert.Contains(
            "lifecycleReceipts.TryGetDying(",
            settle,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "hostile-shadow.settlement-confirmed-dying-receipt-missing",
            settle,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Repair_then_hit_keeps_every_pre_existing_hit_guard()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var hit = Slice(
            world,
            "private int HandleIncomingHit(",
            "    private void PropagateAggroToNearby("
        );

        foreach (
            var guard in new[]
            {
                "hostile-shadow.hit-guard-disposed",
                "hostile-shadow.hit-guard-binding-hidden",
                "hostile-shadow.hit-guard-retreating",
                "hostile-shadow.hit-guard-not-master",
                "hostile-shadow.hit-guard-session-inactive",
                "hostile-shadow.hit-guard-damage-nonpositive",
                "hostile-shadow.hit-guard-attacker-invalid",
                "hostile-shadow.hit-guard-entity-id-missing",
                "hostile-shadow.hit-guard-entry-missing",
                "hostile-shadow.hit-guard-monster-identity-mismatch",
                "hostile-shadow.hit-guard-location-mismatch",
                "hostile-shadow.hit-guard-not-in-characters",
                "hostile-shadow.hit-guard-pending-lethal",
                "hostile-shadow.hit-guard-entity-missing",
                "hostile-shadow.hit-guard-monster-health-nonpositive",
                "hostile-shadow.hit-guard-terminal-state",
            }
        )
        {
            Assert.Contains(guard, hit, StringComparison.Ordinal);
        }

        // The lethal sentinel and its rollback must survive untouched: vanilla otherwise calls
        // onMonsterKilled while the custom Dying pipeline is still mid-frame.
        Assert.Contains("var previousHealth = monster.Health;", hit, StringComparison.Ordinal);
        Assert.Contains(
            "monster.Health = damageDecision.PhysicalHealthAfter;",
            hit,
            StringComparison.Ordinal
        );
        Assert.Contains("monster.Health = previousHealth;", hit, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggro_propagation_reuses_the_target_reacquisition_taunt_flow()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var aggro = Slice(
            world,
            "private void PropagateAggroToNearby(",
            "    private bool TryAdvanceWander("
        );

        Assert.Contains("BeginTargetReacquisition", aggro, StringComparison.Ordinal);
        Assert.Contains("ApplyMonsterState(other)", aggro, StringComparison.Ordinal);
        Assert.Contains(
            "other.AttackState.StateId",
            aggro,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "替换 TargetPlayerKey 后继续沿用旧 Chase 状态",
            aggro,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Hit_entity_uses_direct_handoff_for_a_new_attacker()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var hitPath = Slice(
            world,
            "var damageDecision = HostileShadowIncomingDamagePolicy.Evaluate(",
            "        // DIAG-20260809: 受击拉仇恨传播"
        );

        Assert.Contains(
            "RequestTargetHandoffAfterHit",
            hitPath,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "entry.TargetPlayerKey = attackerPlayerKey",
            hitPath,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "!damageDecision.PendingDying",
            hitPath,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Debug_and_interval_spawn_commands_share_the_same_physical_materialization_path()
    {
        var source = Contract("SmapiHostileShadowHost.cs");
        Assert.Contains("HostileShadowSpawnOrigin.DebugCommand", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowSpawnOrigin.Interval", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowSpawnOrigin origin", source, StringComparison.Ordinal);
        Assert.True(Count(source, "world.TryMaterialize(") >= 2);
    }

    [Fact]
    public void Materialized_shadow_names_use_profile_i18n_keys_for_lookup()
    {
        var source = Contract("SmapiHostileShadowWorldRuntime.cs");

        Assert.Contains(
            "monster.Name = helper.Translation.Get(profile.DisplayNameKey).ToString();",
            source,
            StringComparison.Ordinal
        );
    }

    private static void AssertPair(string source, string eventName, string handler)
    {
        Assert.Contains($"{eventName} += {handler}", source, StringComparison.Ordinal);
        Assert.Contains($"{eventName} -= {handler}", source, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static void AssertNoTemporaryEntityCollection(string source)
    {
        Assert.Contains("entityIterationBuffer[index]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("entries.Keys", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Dictionary<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new HashSet<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new long[", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Sort(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("yield return", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnumerator(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(object)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" as object", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Over_cap_cleanup_reuses_retreat_and_fade_instead_of_hard_removal()
    {
        var host = Contract("SmapiHostileShadowHost.cs");
        var trim = Slice(
            host,
            "private void TrimOverCap(long gameMinute)",
            "private void QueueFastSpawnsForLocation("
        );

        Assert.Contains("BeginRetreat(entityId, state)", trim, StringComparison.Ordinal);
        Assert.Contains("BeginBindingFadeOut(", trim, StringComparison.Ordinal);
        Assert.Contains(
            "BeginOverCapShadowProjectionFade",
            trim,
            StringComparison.Ordinal
        );
        Assert.Contains("pendingOverCapRetreats", trim, StringComparison.Ordinal);
        Assert.Contains("pendingOverCapBindingFades", trim, StringComparison.Ordinal);
        Assert.Contains("overCapCheckSchedule.TryConsumeIfDue", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("lastOverCapTrimMinute", host, StringComparison.Ordinal);
        Assert.DoesNotContain("lastBindingRollMinuteByEntity", host, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "authority.CleanupEntity(\n                    entityId,\n                    \"hostile-shadow.cleanup.over-cap\"",
            trim,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "OverCapFadeOutMilliseconds = 1000",
            host,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Over_cap_cleanup_is_limited_to_the_two_shadow_species_and_keeps_binding_as_one()
    {
        var host = Contract("SmapiHostileShadowHost.cs");
        var projection = ProjectionContract("SmapiHarmlessProjectionHost.cs");
        var trim = Slice(
            host,
            "private void TrimOverCap(long gameMinute)",
            "private void QueueFastSpawnsForLocation("
        );

        Assert.Contains("IsOverCapAssetBinding", trim, StringComparison.Ordinal);
        Assert.Contains("boundPairs++", trim, StringComparison.Ordinal);
        Assert.Contains(
            "CountOverCapShadowProjectionsForOwnerAtLocation",
            trim,
            StringComparison.Ordinal
        );
        Assert.Contains("instance.IsBindingProjection", projection, StringComparison.Ordinal);
        Assert.Contains("IsOverCapShadowSpecies(instance.SpeciesId)", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("DarkHand", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("MrSkitts", trim, StringComparison.Ordinal);
    }

    [Fact]
    public void Danger_projection_update_keeps_existing_instances_moving()
    {
        var coordinator = ProjectionContract("ShadowCreatureHarmlessProjectionCoordinator.cs");
        var danger = Slice(
            coordinator,
            "if (phase.DangerTierActive || phase.RequiresFutureReverseResolution)",
            "return UpdateResult("
        );

        Assert.Contains("advanceMovement,", danger, StringComparison.Ordinal);
        Assert.Contains("bindingOnly: false", danger, StringComparison.Ordinal);
        Assert.DoesNotContain("advanceMovement: false", danger, StringComparison.Ordinal);
        Assert.DoesNotContain("bindingOnly: true", danger, StringComparison.Ordinal);
        Assert.Contains("UpdateLocalInstances(", danger, StringComparison.Ordinal);
    }

    private static void AssertNoManagedCollisionConstruction(string source)
    {
        Assert.DoesNotContain("new ShadowAttackHitRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new HostileAttackHitContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SmapiHostileAttackDamageAdapter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new HostileAttackReceipt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new HostileAttackResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Dictionary<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new HashSet<", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToString(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("yield return", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(object)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" as object", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips // line comments and /* */ block comments so a diagnostic contract can be asserted
    /// against executable code only. Without this, documenting a removed reason name inside a
    /// comment would look like a live diagnostic to a plain substring check.
    /// </summary>
    private static string CodeOnly(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    result.Append(current);
                }
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                else if (current == '\n')
                {
                    result.Append(current);
                }
                continue;
            }
            if (inString)
            {
                result.Append(current);
                if (current == '\\')
                {
                    if (next != '\0')
                    {
                        result.Append(next);
                        index++;
                    }
                    continue;
                }
                if (current == '"')
                    inString = false;
                continue;
            }
            if (inChar)
            {
                result.Append(current);
                if (current == '\\')
                {
                    if (next != '\0')
                    {
                        result.Append(next);
                        index++;
                    }
                    continue;
                }
                if (current == '\'')
                    inChar = false;
                continue;
            }
            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '"')
                inString = true;
            else if (current == '\'')
                inChar = true;
            result.Append(current);
        }
        return result.ToString();
    }

    private static string Source(string repositoryRelativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, repositoryRelativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(repositoryRelativePath);
    }

    private static IEnumerable<string> CodeLines(string source)
    {
        return source.Replace("\r\n", "\n").Split('\n');
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
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

    private static string ProjectionContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "ShadowProjection",
                fileName
            )
        );
    }
}
