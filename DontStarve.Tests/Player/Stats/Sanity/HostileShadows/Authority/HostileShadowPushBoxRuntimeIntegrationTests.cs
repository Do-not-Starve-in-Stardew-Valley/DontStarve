using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowPushBoxRuntimeIntegrationTests
{
    [Fact]
    public void Update_is_host_gated_and_uses_a_two_phase_crowd_pipeline()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var tick = Slice(
            world,
            "private void OnUpdateTicked(",
            "private void RebuildPlayerIndex()"
        );
        var advance = Slice(
            world,
            "private void AdvanceCachedTargets(bool snapshotCadence)",
            "private void ObserveShadowCreatureSfx("
        );

        Assert.Contains("!Game1.IsMasterGame", tick, StringComparison.Ordinal);
        Assert.True(
            tick.IndexOf("!Game1.IsMasterGame", StringComparison.Ordinal)
                < tick.IndexOf("ResolvePendingLethalDamage();", StringComparison.Ordinal)
        );
        Assert.Contains("crowdParticipants.Clear();", advance, StringComparison.Ordinal);
        Assert.Contains("crowdResolutions.Clear();", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Monster.Position =", advance, StringComparison.Ordinal);

        var firstLoop = advance.IndexOf(
            "for (var index = 0; index < entityCount; index++)",
            StringComparison.Ordinal
        );
        var resolve = advance.IndexOf("ResolveCrowdPlans();", StringComparison.Ordinal);
        var secondLoop = advance.IndexOf(
            "for (var index = 0; index < entityCount; index++)",
            firstLoop + 1,
            StringComparison.Ordinal
        );
        Assert.True(firstLoop >= 0);
        Assert.True(firstLoop < resolve && resolve < secondLoop);
        Assert.Contains(
            "ApplyCrowdPlan(entityId, entry, snapshotCadence)",
            advance,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Final_position_is_written_before_state_sync_and_attack_hit_resolution()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var apply = Slice(
            world,
            "private void ApplyCrowdPlan(",
            "private void ApplyNormalCrowdPlan("
        );
        var normal = Slice(
            world,
            "private void ApplyNormalCrowdPlan(",
            "private bool TryAlignAttackInstanceRevision("
        );

        var resolutionLookup = apply.IndexOf(
            "crowdResolutions.TryGetValue(",
            StringComparison.Ordinal
        );
        var originTranslation = apply.IndexOf(
            "TryTranslateCurrentAttackOrigin(",
            StringComparison.Ordinal
        );
        var positionWrite = apply.IndexOf(
            "entry.Monster.Position = new Vector2(",
            StringComparison.Ordinal
        );
        var normalApply = apply.IndexOf(
            "ApplyNormalCrowdPlan(",
            StringComparison.Ordinal
        );
        Assert.True(resolutionLookup >= 0);
        Assert.True(resolutionLookup < positionWrite);
        Assert.True(originTranslation > resolutionLookup && originTranslation < positionWrite);
        Assert.True(positionWrite < normalApply);

        var sync = normal.IndexOf("authority.TryUpdate(", StringComparison.Ordinal);
        var hit = normal.IndexOf(
            "attackCombat.ProcessCurrentHits(",
            StringComparison.Ordinal
        );
        Assert.True(sync >= 0 && hit > sync);
        Assert.Contains("entry.CrowdPlanFinalPositionX", normal, StringComparison.Ordinal);
        Assert.Contains("entry.CrowdPlanFinalPositionY", normal, StringComparison.Ordinal);
    }

    [Fact]
    public void All_live_hostile_states_participate_while_despawn_is_excluded()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var advance = Slice(
            world,
            "private void AdvanceCachedTargets(bool snapshotCadence)",
            "private void ObserveShadowCreatureSfx("
        );

        Assert.Contains("if (entry.IsRetreating)", advance, StringComparison.Ordinal);
        Assert.Contains("PrepareRetreatCrowdPlan(", advance, StringComparison.Ordinal);
        Assert.Contains("if (entry.IsBindingHidden)", advance, StringComparison.Ordinal);
        Assert.Contains("PrepareBindingCrowdPlan(", advance, StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.HitTeleport", advance, StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Dying", advance, StringComparison.Ordinal);
        Assert.Contains("PrepareHitResponseCrowdPlan(", advance, StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Spawn", Contract("HostileAttackStateMachine.cs"), StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Idle", Contract("HostileAttackStateMachine.cs"), StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Chase", Contract("HostileAttackStateMachine.cs"), StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Taunt", Contract("HostileAttackStateMachine.cs"), StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Attack", Contract("HostileAttackStateMachine.cs"), StringComparison.Ordinal);

        var despawnGuard = advance.IndexOf(
            "HostileShadowStateIds.Despawn",
            StringComparison.Ordinal
        );
        var hitResponseGuard = advance.IndexOf(
            "PrepareHitResponseCrowdPlan(",
            StringComparison.Ordinal
        );
        Assert.True(despawnGuard >= 0 && despawnGuard < hitResponseGuard);
        var excludedDespawnBlock = advance[despawnGuard..hitResponseGuard];
        Assert.Contains("continue;", excludedDespawnBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_uses_the_dangerous_entity_as_the_only_runtime_representative()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var entry = Slice(
            world,
            "private sealed class PhysicalEntry",
            "private readonly IModHelper helper;"
        );
        var update = Slice(
            world,
            "private void UpdateCrowdParticipant(",
            "private void ResolveCrowdPlans()"
        );
        var binding = Slice(
            world,
            "private void PrepareBindingCrowdPlan(",
            "private void PrepareHitResponseCrowdPlan("
        );

        Assert.Contains("CrowdParticipant = new HostileShadowCrowdParticipant(", entry, StringComparison.Ordinal);
        Assert.Contains("attackDefinition.PushBoxGroupId", entry, StringComparison.Ordinal);
        Assert.Contains("attackDefinition.PushForce", entry, StringComparison.Ordinal);
        Assert.Contains("isBindingRepresentative: isBinding", update, StringComparison.Ordinal);
        Assert.Contains("isBinding: true", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("SmapiHarmlessProjectionHost", world, StringComparison.Ordinal);
        Assert.DoesNotContain("ShadowCreatureHarmlessProjection", world, StringComparison.Ordinal);
    }

    [Fact]
    public void Crowd_plan_changes_position_intent_only_and_does_not_mutate_speed_or_collision_contracts()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var update = Slice(
            world,
            "private void UpdateCrowdParticipant(",
            "private void ResolveCrowdPlans()"
        );
        var advance = Slice(
            world,
            "private void AdvanceCachedTargets(bool snapshotCadence)",
            "private void ObserveShadowCreatureSfx("
        );

        Assert.Contains(
            "normalPositionX - currentPositionX",
            update,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "HostileShadowPushBoxGeometry.TryCreateWorldBox(",
            update,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("entry.Profile.MovementSpeed =", world, StringComparison.Ordinal);
        Assert.Contains("entry.Profile.MovementSpeed,", advance, StringComparison.Ordinal);
        Assert.Contains("HostileAttackStateInput.Capture(", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("isCollidingPosition", update, StringComparison.Ordinal);
        Assert.DoesNotContain("farmerPassesThrough", update, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.getOnlineFarmers", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_attack_pushes_follow_snapshot_cadence_but_attack_pushes_sync_immediately()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var normal = Slice(
            world,
            "private void ApplyNormalCrowdPlan(",
            "private bool TryAlignAttackInstanceRevision("
        );
        var retreat = Slice(
            world,
            "private void ApplyRetreatCrowdPlan(",
            "private void ApplyBindingCrowdPlan("
        );
        var binding = Slice(
            world,
            "private void ApplyBindingCrowdPlan(",
            "private bool TrySynchronizePositionOnly("
        );

        Assert.Contains("snapshotCadence", normal, StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Attack", normal, StringComparison.Ordinal);
        Assert.Contains("decision.StateChanged", normal, StringComparison.Ordinal);
        Assert.Contains("decision.AttackFrameChanged", normal, StringComparison.Ordinal);
        Assert.Contains("finalPositionChanged && snapshotCadence", retreat, StringComparison.Ordinal);
        Assert.Contains("shouldAlign", binding, StringComparison.Ordinal);
        Assert.Contains("finalPositionChanged && snapshotCadence", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void Attack_origin_translation_survives_the_next_state_machine_tick()
    {
        var baseline = HostileAttackTestFactory.StartAttack();
        var shifted = HostileAttackTestFactory.StartAttack();
        var instance = shifted.Machine.CurrentInstance;
        Assert.NotNull(instance);
        var originX = instance!.OriginPositionX;
        var originY = instance.OriginPositionY;

        Assert.True(
            shifted.Machine.TryTranslateCurrentAttackOrigin(7d, -3d, out var reason),
            reason
        );
        Assert.Equal(originX + 7d, instance.OriginPositionX, precision: 8);
        Assert.Equal(originY - 3d, instance.OriginPositionY, precision: 8);

        var baselineNext = baseline.Machine.Advance(
            HostileAttackTestFactory.Input(),
            baseline.Definition.AttackFrameDurationMilliseconds
        );
        var shiftedNext = shifted.Machine.Advance(
            HostileAttackTestFactory.Input(),
            shifted.Definition.AttackFrameDurationMilliseconds
        );
        Assert.Equal(baselineNext.PositionX + 7d, shiftedNext.PositionX, precision: 8);
        Assert.Equal(baselineNext.PositionY - 3d, shiftedNext.PositionY, precision: 8);
    }

    [Fact]
    public void Attack_instance_revision_alignment_preserves_continuation_and_new_attack_rules()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var alignment = Slice(
            world,
            "private bool TryAlignAttackInstanceRevision(",
            "private void ApplyHitResponseCrowdPlan("
        );
        var instance = Contract("HostileAttackInstance.cs");
        var machine = Contract("HostileAttackStateMachine.cs");

        Assert.Contains("current.AttackInstanceRevision", alignment, StringComparison.Ordinal);
        Assert.Contains("authority.Revision + 1", alignment, StringComparison.Ordinal);
        Assert.Contains("TrySetCurrentAttackRevision(", alignment, StringComparison.Ordinal);
        Assert.Contains("Revision { get; private set; }", instance, StringComparison.Ordinal);
        Assert.Contains("OriginPositionX { get; private set; }", instance, StringComparison.Ordinal);
        Assert.Contains("TryTranslateOrigin(", instance, StringComparison.Ordinal);
        Assert.Contains("TrySetRevision(", instance, StringComparison.Ordinal);
        Assert.Contains("TrySetCurrentAttackRevision(", machine, StringComparison.Ordinal);
    }

    [Fact]
    public void Hit_response_cleanup_stays_after_the_final_position_is_synchronized()
    {
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var hit = Slice(
            world,
            "private void ApplyHitResponseCrowdPlan(",
            "private void ApplyRetreatCrowdPlan("
        );

        var finalWrite = hit.IndexOf(
            "entry.CrowdPlanFinalPositionX",
            StringComparison.Ordinal
        );
        var sync = hit.IndexOf("TrySynchronizeHitResponse(", StringComparison.Ordinal);
        var cleanup = hit.IndexOf("authority.CleanupEntity(", StringComparison.Ordinal);
        Assert.True(finalWrite >= 0 && sync > finalWrite && cleanup > sync);
        Assert.Contains("response.RemovalRequested", hit, StringComparison.Ordinal);
        Assert.Contains("DeferHitResponseSynchronizationFailure", hit, StringComparison.Ordinal);
    }

    [Fact]
    public void Harmless_projection_intents_are_captured_by_owner_before_the_host_solve()
    {
        var projection = ProjectionContract("SmapiHarmlessProjectionHost.cs");
        var update = Slice(
            projection,
            "private void OnUpdateTicked(",
            "private void SynchronizeShadowTierFromCurrentTierState("
        );
        var index = ProjectionContract("ShadowCreatureHarmlessProjectionIndex.cs");

        var begin = update.IndexOf(
            "shadowCoordinator.BeginPushBoxIntentCapture(currentOwner);",
            StringComparison.Ordinal
        );
        var shadowUpdate = update.IndexOf(
            "var shadowUpdate = shadowCoordinator.UpdateOwner(",
            StringComparison.Ordinal
        );
        var capture = update.IndexOf(
            "shadowCoordinator.CapturePushBoxIntents(currentOwner);",
            StringComparison.Ordinal
        );
        var submit = update.IndexOf(
            "SubmitShadowPushBoxIntentBatch(currentOwner);",
            StringComparison.Ordinal
        );
        Assert.True(begin >= 0 && begin < shadowUpdate && shadowUpdate < capture && capture < submit);
        Assert.Contains("CopyPushBoxIntents(owner, pushBoxIntentBuffer)", projection, StringComparison.Ordinal);
        Assert.Contains("!instance.IsBindingProjection", index, StringComparison.Ordinal);
        Assert.Contains("MaximumPushBoxEntriesPerBatch", index, StringComparison.Ordinal);
        Assert.Contains("CopyPushBoxIntents(\n        HarmlessProjectionOwnerContext owner", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_projection_bridge_validates_batches_clears_vanished_entries_and_returns_authority()
    {
        var host = Contract("SmapiHostileShadowHost.cs");
        var coordinator = Contract("SmapiHostileShadowMultiplayerCoordinator.cs");

        Assert.Contains("HostileShadowProtocol.IsValidPushBoxIntentMessage(", host, StringComparison.Ordinal);
        Assert.Contains("RemoveRemotePushBoxStatesForOwner(", host, StringComparison.Ordinal);
        Assert.Contains("message.Entries.Count", host, StringComparison.Ordinal);
        Assert.Contains("HostileShadowProtocol.IsFreshPushBoxHostTick(", host, StringComparison.Ordinal);
        Assert.Contains("multiplayer.SendPushBoxResult(", host, StringComparison.Ordinal);
        Assert.Contains("HostTick = Game1.ticks", host, StringComparison.Ordinal);
        Assert.Contains("PushBoxIntentMessageType", coordinator, StringComparison.Ordinal);
        Assert.Contains("PushBoxResultMessageType", coordinator, StringComparison.Ordinal);
        Assert.Contains(
            "DispatchByDirection(e, hostReceives: true, HandlePushBoxIntent)",
            coordinator,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DispatchByDirection(e, hostReceives: false, HandlePushBoxResult)",
            coordinator,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Ds_boxes_off_gates_red_green_and_yellow_push_boxes_together()
    {
        var commands = DebugContract();
        var hostile = Contract("HostileShadowMonster.cs");
        var hostileBoxes = Slice(
            hostile,
            "private void DrawDebugCollisionBoxes(",
            "private static HostileAttackFacing? ResolveTauntFacing("
        );
        var projection = ProjectionContract("ShadowCreatureHarmlessProjectionRenderer.cs");

        Assert.Contains("case \"off\":", commands, StringComparison.Ordinal);
        Assert.Contains("boxesVisible = false", commands, StringComparison.Ordinal);
        var gate = hostileBoxes.IndexOf(
            "if (!Debug.DebugCommands.AreBoxesVisible)",
            StringComparison.Ordinal
        );
        Assert.True(gate >= 0);
        foreach (var color in new[]
        {
            "new Color(100, 255, 100)",
            "new Color(255, 240, 70)",
            "new Color(255, 80, 80)",
        })
        {
            var colorIndex = hostileBoxes.IndexOf(color, StringComparison.Ordinal);
            Assert.True(colorIndex > gate, $"Missing gated debug box color: {color}");
        }
        Assert.Contains("Debug.DebugCommands.AreBoxesVisible", projection, StringComparison.Ordinal);
        Assert.Contains("new Color(255, 240, 70)", projection, StringComparison.Ordinal);
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

    private static string DebugContract()
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "Debug",
                "DebugCommands.cs"
            )
        );
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing contract start: {start}");
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal
        );
        Assert.True(endIndex > startIndex, $"Missing contract end: {end}");
        return source[startIndex..endIndex];
    }
}
