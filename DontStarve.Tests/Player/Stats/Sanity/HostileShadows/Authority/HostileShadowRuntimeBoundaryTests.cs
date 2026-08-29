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
    public void Debug_and_interval_spawn_commands_share_the_same_physical_materialization_path()
    {
        var source = Contract("SmapiHostileShadowHost.cs");
        Assert.Contains("HostileShadowSpawnOrigin.DebugCommand", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowSpawnOrigin.Interval", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowSpawnOrigin origin", source, StringComparison.Ordinal);
        Assert.True(Count(source, "world.TryMaterialize(") >= 2);
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
}
