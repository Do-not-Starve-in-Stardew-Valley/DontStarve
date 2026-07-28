#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

internal enum ShadowRevisionApplyStatus
{
    Applied,
    IgnoredDuplicate,
    IgnoredStale,
    FullSnapshotRequired,
    Rejected,
}

internal readonly record struct ShadowRevisionApplyResult(
    ShadowRevisionApplyStatus Status,
    string Reason
)
{
    internal bool RequiresFullSnapshot =>
        Status == ShadowRevisionApplyStatus.FullSnapshotRequired;
}

/// <summary>
/// Client-side, session-only mirror of the host table. It has no spawn/config/game-object seam:
/// clients can only replace it with a valid full snapshot or advance it by one host delta.
/// </summary>
internal sealed class ShadowStateRevisionStore
{
    private readonly Dictionary<long, ShadowStateSnapshot> entities = new();

    internal string SessionId { get; private set; } = string.Empty;

    internal long Revision { get; private set; }

    internal string SubscriptionLocationId { get; private set; } = string.Empty;

    internal ShadowSnapshotTrigger SubscriptionTrigger { get; private set; }

    internal bool AwaitingFullSnapshot { get; private set; }

    internal int Count => entities.Count;

    internal IReadOnlyDictionary<long, ShadowStateSnapshot> GetSnapshot()
    {
        var copy = new Dictionary<long, ShadowStateSnapshot>(entities.Count);
        foreach (var pair in entities)
            copy.Add(pair.Key, pair.Value.Clone());
        return new ReadOnlyDictionary<long, ShadowStateSnapshot>(copy);
    }

    internal bool TryGet(long entityId, out ShadowStateSnapshot? state)
    {
        state = null;
        if (!entities.TryGetValue(entityId, out var existing))
            return false;
        state = existing.Clone();
        return true;
    }

    /// <summary>
    /// Immediately retires the prior-location mirror while preserving the last global revision as
    /// a resync clue. No delta is applied until the host confirms the new scoped full snapshot.
    /// </summary>
    internal bool BeginSubscription(
        string locationId,
        ShadowSnapshotTrigger trigger,
        out string reason
    )
    {
        if (
            !HostileShadowProtocol.IsValidLocationId(locationId)
            || !HostileShadowProtocol.IsValidSnapshotTrigger(trigger)
        )
        {
            reason = "hostile-shadow.subscription-local-invalid";
            return false;
        }
        entities.Clear();
        SubscriptionLocationId = locationId;
        SubscriptionTrigger = trigger;
        AwaitingFullSnapshot = true;
        reason = "hostile-shadow.subscription-local-started";
        return true;
    }

    internal ShadowRevisionApplyResult ApplyFull(
        ShadowStateSnapshotMessage? message
    )
    {
        if (!HostileShadowProtocol.IsValidSnapshotMessage(message, out var reason))
            return Rejected(reason);

        if (
            !string.IsNullOrEmpty(SubscriptionLocationId)
            && !string.Equals(
                SubscriptionLocationId,
                message!.LocationId,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected("hostile-shadow.snapshot-subscription-location-mismatch");
        }

        if (
            string.Equals(SessionId, message!.SessionId, StringComparison.Ordinal)
            && message.Revision < Revision
        )
        {
            return Result(
                ShadowRevisionApplyStatus.IgnoredStale,
                "hostile-shadow.snapshot-stale"
            );
        }
        if (
            string.Equals(SessionId, message.SessionId, StringComparison.Ordinal)
            && message.Revision == Revision
            && !AwaitingFullSnapshot
        )
        {
            return HostileShadowProtocol.StateSetsEqual(entities, message.Entities)
                ? Result(
                    ShadowRevisionApplyStatus.IgnoredDuplicate,
                    "hostile-shadow.snapshot-duplicate"
                )
                : Result(
                    ShadowRevisionApplyStatus.FullSnapshotRequired,
                    "hostile-shadow.snapshot-revision-conflict"
                );
        }

        entities.Clear();
        foreach (var state in message.Entities)
            entities.Add(state.EntityId, state.Clone());
        SessionId = message.SessionId;
        SubscriptionLocationId = message.LocationId;
        SubscriptionTrigger = message.Trigger;
        Revision = message.Revision;
        AwaitingFullSnapshot = false;
        return Result(
            ShadowRevisionApplyStatus.Applied,
            "hostile-shadow.snapshot-applied"
        );
    }

    internal ShadowRevisionApplyResult ApplyDelta(
        ShadowStateDeltaMessage? message
    )
    {
        if (!HostileShadowProtocol.IsValidDeltaMessage(message, out var reason))
            return Rejected(reason);

        if (AwaitingFullSnapshot || string.IsNullOrEmpty(SubscriptionLocationId))
        {
            return Result(
                ShadowRevisionApplyStatus.FullSnapshotRequired,
                "hostile-shadow.delta-subscription-awaiting-full"
            );
        }

        if (
            string.IsNullOrEmpty(SessionId)
            || !string.Equals(SessionId, message!.SessionId, StringComparison.Ordinal)
        )
        {
            return Result(
                ShadowRevisionApplyStatus.FullSnapshotRequired,
                "hostile-shadow.delta-session-unknown"
            );
        }
        if (message.Revision <= Revision)
        {
            return Result(
                ShadowRevisionApplyStatus.IgnoredStale,
                "hostile-shadow.delta-stale"
            );
        }
        if (
            message.BaseRevision != Revision
            || message.Revision != Revision + 1
        )
        {
            return Result(
                ShadowRevisionApplyStatus.FullSnapshotRequired,
                "hostile-shadow.delta-revision-gap"
            );
        }

        var change = message.Change;
        switch (change.Kind)
        {
            case ShadowStateDeltaKind.Spawned:
                if (!IsInSubscription(change.State!))
                {
                    entities.Remove(change.EntityId);
                    break;
                }
                if (entities.ContainsKey(change.EntityId))
                {
                    return Result(
                        ShadowRevisionApplyStatus.FullSnapshotRequired,
                        "hostile-shadow.delta-spawn-entity-known"
                    );
                }
                entities.Add(change.EntityId, change.State!.Clone());
                break;
            case ShadowStateDeltaKind.Updated:
                if (!IsInSubscription(change.State!))
                {
                    entities.Remove(change.EntityId);
                    break;
                }
                if (!entities.ContainsKey(change.EntityId))
                {
                    return Result(
                        ShadowRevisionApplyStatus.FullSnapshotRequired,
                        "hostile-shadow.delta-update-entity-unknown"
                    );
                }
                entities[change.EntityId] = change.State!.Clone();
                break;
            case ShadowStateDeltaKind.Removed:
                // A location-scoped mirror legitimately doesn't contain entities removed elsewhere;
                // the global revision still advances so later in-scope deltas remain contiguous.
                entities.Remove(change.EntityId);
                break;
            default:
                return Rejected("hostile-shadow.delta-kind-invalid");
        }

        Revision = message.Revision;
        return Result(
            ShadowRevisionApplyStatus.Applied,
            "hostile-shadow.delta-applied"
        );
    }

    internal void Reset()
    {
        entities.Clear();
        SessionId = string.Empty;
        SubscriptionLocationId = string.Empty;
        SubscriptionTrigger = default;
        Revision = 0;
        AwaitingFullSnapshot = false;
    }

    private bool IsInSubscription(ShadowStateSnapshot state)
    {
        return string.Equals(
            state.LocationId,
            SubscriptionLocationId,
            StringComparison.Ordinal
        );
    }

    private static ShadowRevisionApplyResult Rejected(string reason)
    {
        return Result(ShadowRevisionApplyStatus.Rejected, reason);
    }

    private static ShadowRevisionApplyResult Result(
        ShadowRevisionApplyStatus status,
        string reason
    )
    {
        return new ShadowRevisionApplyResult(status, reason);
    }
}
