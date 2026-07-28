using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class ShadowStateRevisionStoreTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";
    private const string Other = "223456789";

    [Fact]
    public void Full_snapshot_establishes_session_and_strict_delta_advances_once()
    {
        var store = new ShadowStateRevisionStore();
        var full = Full(SessionA, revision: 1, State(entityId: 10, revision: 1));
        var delta = Delta(
            SessionA,
            baseRevision: 1,
            revision: 2,
            ShadowStateDeltaKind.Updated,
            entityId: 10,
            State(entityId: 10, revision: 2, health: 80)
        );

        var fullResult = store.ApplyFull(full);
        var deltaResult = store.ApplyDelta(delta);

        Assert.Equal(ShadowRevisionApplyStatus.Applied, fullResult.Status);
        Assert.Equal(ShadowRevisionApplyStatus.Applied, deltaResult.Status);
        Assert.Equal(SessionA, store.SessionId);
        Assert.Equal(2, store.Revision);
        Assert.True(store.TryGet(10, out var current));
        Assert.Equal(80, current!.Health);
    }

    [Fact]
    public void Target_and_location_visibility_survive_full_and_delta_cloning()
    {
        var store = new ShadowStateRevisionStore();
        var initial = State(10, revision: 1);
        initial.TargetPlayerKey = Owner;
        Assert.Equal(
            ShadowRevisionApplyStatus.Applied,
            store.ApplyFull(Full(SessionA, 1, initial)).Status
        );
        var retargeted = State(10, revision: 2);
        retargeted.TargetPlayerKey = Other;
        retargeted.StateId = "hostile-shadow.state.chase";

        var applied = store.ApplyDelta(
            Delta(
                SessionA,
                1,
                2,
                ShadowStateDeltaKind.Updated,
                10,
                retargeted
            )
        );

        Assert.Equal(ShadowRevisionApplyStatus.Applied, applied.Status);
        Assert.True(store.TryGet(10, out var current));
        Assert.Equal("Farm", current!.LocationId);
        Assert.Equal(Other, current.TargetPlayerKey);
        Assert.Equal("hostile-shadow.state.chase", current.StateId);
    }

    [Fact]
    public void Old_and_duplicate_revisions_are_ignored_without_mutation()
    {
        var store = new ShadowStateRevisionStore();
        var full = Full(SessionA, revision: 1, State(10, revision: 1));
        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyFull(full).Status);
        var duplicateFull = store.ApplyFull(full);
        var delta = Delta(
            SessionA,
            1,
            2,
            ShadowStateDeltaKind.Updated,
            10,
            State(10, revision: 2, health: 90)
        );
        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyDelta(delta).Status);

        var stale = store.ApplyDelta(delta);

        Assert.Equal(ShadowRevisionApplyStatus.IgnoredDuplicate, duplicateFull.Status);
        Assert.Equal(ShadowRevisionApplyStatus.IgnoredStale, stale.Status);
        Assert.Equal(2, store.Revision);
        Assert.True(store.TryGet(10, out var current));
        Assert.Equal(90, current!.Health);
    }

    [Fact]
    public void Gap_or_unknown_in_scope_update_requests_full_but_unknown_remove_advances()
    {
        var gapStore = StoreAtRevisionOne();
        var gap = gapStore.ApplyDelta(
            Delta(
                SessionA,
                baseRevision: 2,
                revision: 3,
                ShadowStateDeltaKind.Updated,
                entityId: 10,
                State(10, revision: 3, health: 70)
            )
        );

        var updateStore = StoreAtRevisionOne();
        var unknownUpdate = updateStore.ApplyDelta(
            Delta(
                SessionA,
                1,
                2,
                ShadowStateDeltaKind.Updated,
                entityId: 99,
                State(99, revision: 2)
            )
        );

        var removeStore = StoreAtRevisionOne();
        var unknownRemove = removeStore.ApplyDelta(
            Delta(
                SessionA,
                1,
                2,
                ShadowStateDeltaKind.Removed,
                entityId: 99,
                state: null
            )
        );

        Assert.True(gap.RequiresFullSnapshot);
        Assert.Equal("hostile-shadow.delta-revision-gap", gap.Reason);
        Assert.True(unknownUpdate.RequiresFullSnapshot);
        Assert.Equal(
            "hostile-shadow.delta-update-entity-unknown",
            unknownUpdate.Reason
        );
        Assert.Equal(ShadowRevisionApplyStatus.Applied, unknownRemove.Status);
        Assert.Equal("hostile-shadow.delta-applied", unknownRemove.Reason);
        Assert.Equal(1, gapStore.Revision);
        Assert.Equal(1, updateStore.Revision);
        Assert.Equal(2, removeStore.Revision);
    }

    [Fact]
    public void Same_revision_conflict_requires_full_but_new_session_full_can_rebuild()
    {
        var store = StoreAtRevisionOne();
        var conflict = store.ApplyFull(
            Full(SessionA, 1, State(10, revision: 1, health: 50))
        );
        var newSession = store.ApplyFull(
            Full(SessionB, 0)
        );

        Assert.True(conflict.RequiresFullSnapshot);
        Assert.Equal(
            "hostile-shadow.snapshot-revision-conflict",
            conflict.Reason
        );
        Assert.Equal(ShadowRevisionApplyStatus.Applied, newSession.Status);
        Assert.Equal(SessionB, store.SessionId);
        Assert.Equal(0, store.Revision);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Client_without_full_state_cannot_promote_a_spawn_delta()
    {
        var store = new ShadowStateRevisionStore();
        var result = store.ApplyDelta(
            Delta(
                SessionA,
                0,
                1,
                ShadowStateDeltaKind.Spawned,
                10,
                State(10, revision: 1)
            )
        );

        Assert.True(result.RequiresFullSnapshot);
        Assert.Equal(
            "hostile-shadow.delta-subscription-awaiting-full",
            result.Reason
        );
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Warp_retires_old_mirror_and_same_revision_new_location_full_rebuilds()
    {
        var store = StoreAtRevisionOne();
        Assert.True(store.TryGet(10, out _));

        Assert.True(
            store.BeginSubscription(
                "Mine",
                ShadowSnapshotTrigger.Warp,
                out var subscriptionReason
            ),
            subscriptionReason
        );
        Assert.True(store.AwaitingFullSnapshot);
        Assert.Equal("Mine", store.SubscriptionLocationId);
        Assert.Equal(0, store.Count);
        Assert.Equal(1, store.Revision);

        var whileWaiting = store.ApplyDelta(
            Delta(
                SessionA,
                1,
                2,
                ShadowStateDeltaKind.Updated,
                10,
                State(10, revision: 2, location: "Mine")
            )
        );
        Assert.True(whileWaiting.RequiresFullSnapshot);
        Assert.Equal(1, store.Revision);

        var mineState = State(20, revision: 1, location: "Mine");
        var rebuilt = store.ApplyFull(
            FullAt(
                "Mine",
                ShadowSnapshotTrigger.Warp,
                SessionA,
                revision: 1,
                mineState
            )
        );
        Assert.Equal(ShadowRevisionApplyStatus.Applied, rebuilt.Status);
        Assert.False(store.AwaitingFullSnapshot);
        Assert.False(store.TryGet(10, out _));
        Assert.True(store.TryGet(20, out _));
    }

    [Fact]
    public void Out_of_scope_deltas_advance_global_revision_without_polluting_mirror()
    {
        var store = StoreAtRevisionOne();
        var mineSpawn = store.ApplyDelta(
            Delta(
                SessionA,
                1,
                2,
                ShadowStateDeltaKind.Spawned,
                20,
                State(20, revision: 2, location: "Mine")
            )
        );
        var ownerMovedOffMap = store.ApplyDelta(
            Delta(
                SessionA,
                2,
                3,
                ShadowStateDeltaKind.Updated,
                10,
                State(10, revision: 3, location: "Mine")
            )
        );
        var unrelatedRemove = store.ApplyDelta(
            Delta(
                SessionA,
                3,
                4,
                ShadowStateDeltaKind.Removed,
                99,
                null
            )
        );

        Assert.Equal(ShadowRevisionApplyStatus.Applied, mineSpawn.Status);
        Assert.Equal(ShadowRevisionApplyStatus.Applied, ownerMovedOffMap.Status);
        Assert.Equal(ShadowRevisionApplyStatus.Applied, unrelatedRemove.Status);
        Assert.Equal(4, store.Revision);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Revision_gap_can_resync_and_old_session_delta_stays_rejected()
    {
        var store = StoreAtRevisionOne();
        var gap = store.ApplyDelta(
            Delta(
                SessionA,
                2,
                3,
                ShadowStateDeltaKind.Updated,
                10,
                State(10, revision: 3)
            )
        );
        Assert.True(gap.RequiresFullSnapshot);
        Assert.True(
            store.BeginSubscription(
                "Farm",
                ShadowSnapshotTrigger.Resync,
                out var reason
            ),
            reason
        );
        var resynced = store.ApplyFull(
            FullAt(
                "Farm",
                ShadowSnapshotTrigger.Resync,
                SessionA,
                3,
                State(10, revision: 3, health: 70)
            )
        );
        Assert.Equal(ShadowRevisionApplyStatus.Applied, resynced.Status);
        Assert.Equal(3, store.Revision);

        var oldSession = store.ApplyDelta(
            Delta(
                SessionB,
                3,
                4,
                ShadowStateDeltaKind.Updated,
                10,
                State(10, revision: 4)
            )
        );
        Assert.True(oldSession.RequiresFullSnapshot);
        Assert.Equal("hostile-shadow.delta-session-unknown", oldSession.Reason);
        Assert.Equal(3, store.Revision);
    }

    [Fact]
    public void Snapshot_rejects_entity_from_another_location_scope()
    {
        var snapshot = FullAt(
            "Farm",
            ShadowSnapshotTrigger.Join,
            SessionA,
            1,
            State(10, revision: 1, location: "Mine")
        );

        Assert.False(
            HostileShadowProtocol.IsValidSnapshotMessage(snapshot, out var reason)
        );
        Assert.Equal("hostile-shadow.snapshot-location-scope-conflict", reason);
    }

    [Fact]
    public void Host_table_copy_is_cloned_and_filtered_before_transport()
    {
        var source = new ShadowStateSnapshotMessage
        {
            SessionId = SessionA,
            Revision = 2,
            Entities = new List<ShadowStateSnapshot>
            {
                State(10, revision: 1, location: "Farm"),
                State(20, revision: 2, location: "Mine"),
            },
        };

        Assert.True(
            HostileShadowProtocol.TryCreateScopedSnapshot(
                source,
                "Mine",
                ShadowSnapshotTrigger.Join,
                out var scoped,
                out var reason
            ),
            reason
        );
        var state = Assert.Single(scoped!.Entities);
        Assert.Equal(20, state.EntityId);
        Assert.Equal("Mine", scoped.LocationId);
        Assert.Equal(ShadowSnapshotTrigger.Join, scoped.Trigger);
        Assert.True(
            HostileShadowProtocol.IsValidSnapshotMessage(scoped, out reason),
            reason
        );

        state.Health = 1;
        Assert.Equal(100, source.Entities[1].Health);
    }

    [Fact]
    public void Snapshot_and_conversion_requests_bind_sender_session_and_host_time()
    {
        var snapshotRequest = new ShadowStateSnapshotRequest
        {
            SessionId = SessionA,
            PlayerKey = Owner,
            LocationId = "Farm",
            Trigger = ShadowSnapshotTrigger.Join,
            KnownRevision = 1,
        };
        var validSnapshot = HostileShadowProtocol.IsValidSnapshotRequest(
            snapshotRequest,
            Owner,
            "Farm",
            SessionA,
            out var snapshotReason
        );
        var conversion = new ShadowProjectionConversionRequest
        {
            SessionId = SessionA,
            CorrelationId = "conversion-a",
            PlayerKey = Owner,
            SpeciesId = "sanity.projection.creeper-fear",
            RequestedAtMinute = 101,
            DangerRevision = 7,
        };
        var futureConversion = HostileShadowProtocol.IsValidConversionRequest(
            conversion,
            Owner,
            SessionA,
            hostGameMinute: 100,
            expectedDangerRevision: 7,
            out var conversionReason
        );

        Assert.True(validSnapshot, snapshotReason);
        Assert.False(futureConversion);
        Assert.Equal("hostile-shadow.conversion-request-invalid", conversionReason);
    }

    [Fact]
    public void Conversion_request_from_an_old_danger_epoch_is_rejected()
    {
        var request = new ShadowProjectionConversionRequest
        {
            SessionId = SessionA,
            CorrelationId = "old-danger",
            PlayerKey = Owner,
            SpeciesId = "sanity.projection.creeper-fear",
            RequestedAtMinute = 100,
            DangerRevision = 6,
        };

        var valid = HostileShadowProtocol.IsValidConversionRequest(
            request,
            Owner,
            SessionA,
            hostGameMinute: 100,
            expectedDangerRevision: 7,
            out var reason
        );

        Assert.False(valid);
        Assert.Equal("hostile-shadow.conversion-request-invalid", reason);
    }

    [Fact]
    public void Aggro_and_physical_capability_requests_bind_exact_sender_and_session()
    {
        var aggro = new ShadowAggroHintRequest
        {
            SessionId = SessionA,
            EntityId = 10,
            AttackerPlayerKey = Owner,
            LocationId = "Farm",
            KnownEntityRevision = 3,
        };
        var capability = new ShadowPhysicalCapabilityReport
        {
            SessionId = SessionA,
            PlayerKey = Owner,
            Available = true,
            Reason = "hostile-shadow.local-shared-visibility-capability-ready",
        };

        Assert.True(
            HostileShadowProtocol.IsValidAggroHintRequest(
                aggro,
                Owner,
                SessionA,
                out var aggroReason
            ),
            aggroReason
        );
        Assert.False(
            HostileShadowProtocol.IsValidAggroHintRequest(
                aggro,
                Other,
                SessionA,
                out _
            )
        );
        Assert.True(
            HostileShadowProtocol.IsValidPhysicalCapabilityReport(
                capability,
                Owner,
                SessionA,
                out var capabilityReason
            ),
            capabilityReason
        );
        capability.SessionId = SessionB;
        Assert.False(
            HostileShadowProtocol.IsValidPhysicalCapabilityReport(
                capability,
                Owner,
                SessionA,
                out _
            )
        );
    }

    [Fact]
    public void Protocol_rejects_settlement_and_invalid_owner_state()
    {
        var settlement = Delta(
            SessionA,
            0,
            1,
            ShadowStateDeltaKind.Spawned,
            10,
            State(10, revision: 1)
        );
        settlement.Change.SettlementEligible = true;
        var badOwner = Full(
            SessionA,
            1,
            State(10, revision: 1, owner: "01")
        );

        Assert.False(
            HostileShadowProtocol.IsValidDeltaMessage(settlement, out var settlementReason)
        );
        Assert.Equal(
            "hostile-shadow.delta-settlement-forbidden",
            settlementReason
        );
        Assert.False(
            HostileShadowProtocol.IsValidSnapshotMessage(badOwner, out var ownerReason)
        );
        Assert.Equal("hostile-shadow.state-invalid", ownerReason);
    }

    private static ShadowStateRevisionStore StoreAtRevisionOne()
    {
        var store = new ShadowStateRevisionStore();
        Assert.Equal(
            ShadowRevisionApplyStatus.Applied,
            store.ApplyFull(Full(SessionA, 1, State(10, revision: 1))).Status
        );
        return store;
    }

    private static ShadowStateSnapshotMessage Full(
        string session,
        long revision,
        params ShadowStateSnapshot[] states
    )
    {
        return FullAt(
            "Farm",
            ShadowSnapshotTrigger.Resync,
            session,
            revision,
            states
        );
    }

    private static ShadowStateSnapshotMessage FullAt(
        string location,
        ShadowSnapshotTrigger trigger,
        string session,
        long revision,
        params ShadowStateSnapshot[] states
    )
    {
        return new ShadowStateSnapshotMessage
        {
            SessionId = session,
            LocationId = location,
            Trigger = trigger,
            Revision = revision,
            Entities = states.ToList(),
        };
    }

    private static ShadowStateDeltaMessage Delta(
        string session,
        long baseRevision,
        long revision,
        ShadowStateDeltaKind kind,
        long entityId,
        ShadowStateSnapshot? state
    )
    {
        return new ShadowStateDeltaMessage
        {
            SessionId = session,
            BaseRevision = baseRevision,
            Revision = revision,
            Change = new ShadowStateDelta
            {
                Kind = kind,
                EntityId = entityId,
                State = state,
                Reason = "test-change",
                SettlementEligible = false,
            },
        };
    }

    private static ShadowStateSnapshot State(
        long entityId,
        long revision,
        int health = 100,
        string owner = Owner,
        string location = "Farm"
    )
    {
        return new ShadowStateSnapshot
        {
            EntityId = entityId,
            OwnerPlayerKey = owner,
            LocationId = location,
            DifficultyProfileId = "Compatible",
            AssetBindingId = "sanity.binding.creeper-fear",
            StateId = "hostile-shadow.state.spawn",
            PositionX = 128d,
            PositionY = 256d,
            Health = health,
            MaxHealth = 100,
            Revision = revision,
        };
    }
}
