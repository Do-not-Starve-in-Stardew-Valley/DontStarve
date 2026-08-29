#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Bounded mod-private index for the two shared-pool harmless species. Unlike the ordinary index,
/// one owner may hold multiple instances of the same species, so occupancy can represent every cap
/// frozen by task family 02 without creating a world object.
/// </summary>
internal sealed class ShadowCreatureHarmlessProjectionIndex
{
    private readonly Dictionary<
        OwnerContextKey,
        Dictionary<string, ShadowCreatureHarmlessProjectionInstance>
    > byContext = new();
    private readonly Dictionary<OwnerScreenKey, OwnerContextKey> contextByOwnerScreen =
        new();
    private readonly Dictionary<string, ShadowCreatureHarmlessProjectionInstance>
        byCorrelation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> countsByOwner =
        new(StringComparer.Ordinal);

    internal int Count => byCorrelation.Count;

    internal bool TryAdd(
        ShadowCreatureHarmlessProjectionInstance instance,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.IsCleanedUp)
        {
            reason = "shadow-index.instance-already-cleaned";
            return false;
        }
        if (byCorrelation.ContainsKey(instance.CorrelationId))
        {
            reason = "shadow-index.correlation-duplicate";
            return false;
        }

        var contextKey = new OwnerContextKey(instance.Owner);
        var ownerScreen = new OwnerScreenKey(
            instance.Owner.PlayerKey,
            instance.Owner.ScreenId
        );
        if (
            contextByOwnerScreen.TryGetValue(ownerScreen, out var existingContext)
            && !existingContext.Matches(instance.Owner)
        )
        {
            reason = "shadow-index.owner-screen-location-mismatch";
            return false;
        }
        if (!byContext.TryGetValue(contextKey, out var instances))
        {
            instances = new Dictionary<
                string,
                ShadowCreatureHarmlessProjectionInstance
            >(StringComparer.Ordinal);
            byContext.Add(contextKey, instances);
            contextByOwnerScreen[ownerScreen] = contextKey;
        }

        instances.Add(instance.CorrelationId, instance);
        byCorrelation.Add(instance.CorrelationId, instance);
        countsByOwner.TryGetValue(instance.Owner.PlayerKey, out var ownerCount);
        countsByOwner[instance.Owner.PlayerKey] = ownerCount + 1;
        reason = "shadow-index.added";
        return true;
    }

    internal int CountForOwner(string playerKey)
    {
        return countsByOwner.TryGetValue(playerKey, out var count) ? count : 0;
    }

    internal bool ContainsCorrelation(string correlationId)
    {
        return byCorrelation.ContainsKey(correlationId);
    }

    internal bool TryRemove(
        string correlationId,
        HarmlessProjectionCleanupReason reason,
        out ShadowCreatureHarmlessProjectionInstance? removed
    )
    {
        if (!byCorrelation.TryGetValue(correlationId, out var instance))
        {
            removed = null;
            return false;
        }

        return TryRemoveCore(
            new OwnerContextKey(instance.Owner),
            correlationId,
            reason,
            out removed
        );
    }

    internal int CleanupOwner(
        string playerKey,
        HarmlessProjectionCleanupReason reason
    )
    {
        return CleanupWhere(
            key => string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal),
            reason,
            removed: null
        );
    }

    /// <summary>
    /// DIAG-20260809: 高理智/远离等场景不立即清除，标记该玩家全部实例进入淡出，
    /// 由行为 tick 在透明度降到 0 后完成移除（保留实例以便淡出动画播放）。
    /// </summary>
    internal int MarkAllFadingOut(
        string playerKey,
        int durationMilliseconds,
        ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionFadeOutKind kind =
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionFadeOutKind.HighSan
    )
    {
        var marked = 0;
        foreach (var byId in byContext.Values)
        {
            foreach (var instance in byId.Values)
            {
                if (
                    !instance.IsCleanedUp
                    && string.Equals(
                        instance.Owner.PlayerKey,
                        playerKey,
                        StringComparison.Ordinal
                    )
                    && (
                        instance.BehaviorState
                            != ShadowCreatureHarmlessProjectionInstance
                                .ShadowCreatureProjectionBehaviorState.FadingOut
                        || instance.FadeOutKind != kind
                    )
                )
                {
                    instance.BeginFadeOut(durationMilliseconds, kind);
                    marked++;
                }
            }
        }
        return marked;
    }

    internal IReadOnlyList<ShadowCreatureHarmlessProjectionInstance>
        CleanupOwnerWithSnapshot(
            string playerKey,
            HarmlessProjectionCleanupReason reason,
            Func<ShadowCreatureHarmlessProjectionInstance, bool>? shouldRemove = null
        )
    {
        var removed = new List<ShadowCreatureHarmlessProjectionInstance>();
        CleanupWhere(
            key => string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal),
            reason,
            removed,
            shouldRemove
        );
        return removed.AsReadOnly();
    }

    internal int CleanupContext(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionCleanupReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        return CleanupWhere(key => key.Matches(owner), reason, removed: null);
    }

    internal int CleanupMismatchedLocation(
        HarmlessProjectionOwnerContext current,
        HarmlessProjectionCleanupReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(current);
        var ownerScreen = new OwnerScreenKey(current.PlayerKey, current.ScreenId);
        if (
            !contextByOwnerScreen.TryGetValue(ownerScreen, out var contextKey)
            || contextKey.Matches(current)
        )
        {
            return 0;
        }

        return CleanupWhere(key => key.Equals(contextKey), reason, removed: null);
    }

    internal int CleanupInvalidScreens(
        Func<int, bool> isScreenValid,
        HarmlessProjectionCleanupReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(isScreenValid);
        return CleanupWhere(
            key => !isScreenValid(key.ScreenId),
            reason,
            removed: null
        );
    }

    internal int CleanupAll(HarmlessProjectionCleanupReason reason)
    {
        return CleanupWhere(_ => true, reason, removed: null);
    }

    /// <summary>
    /// Returns the bounded dictionary view so the current owner update/render path allocates no
    /// snapshot. Callers collect IDs before removing entries.
    /// </summary>
    internal bool TryGetContextInstances(
        HarmlessProjectionOwnerContext owner,
        out Dictionary<
            string,
            ShadowCreatureHarmlessProjectionInstance
        >.ValueCollection? instances
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (
            byContext.TryGetValue(new OwnerContextKey(owner), out var byId)
            && contextByOwnerScreen.TryGetValue(
                new OwnerScreenKey(owner.PlayerKey, owner.ScreenId),
                out var contextKey
            )
            && contextKey.Matches(owner)
        )
        {
            instances = byId.Values;
            return true;
        }

        instances = null;
        return false;
    }

    private int CleanupWhere(
        Func<OwnerContextKey, bool> predicate,
        HarmlessProjectionCleanupReason reason,
        List<ShadowCreatureHarmlessProjectionInstance>? removed,
        Func<ShadowCreatureHarmlessProjectionInstance, bool>? shouldRemove = null
    )
    {
        var contextKeys = new List<OwnerContextKey>();
        foreach (var key in byContext.Keys)
        {
            if (predicate(key))
                contextKeys.Add(key);
        }

        var removedCount = 0;
        foreach (var contextKey in contextKeys)
        {
            if (!byContext.TryGetValue(contextKey, out var instances))
                continue;
            var correlationIds = new List<string>(instances.Keys);
            foreach (var correlationId in correlationIds)
            {
                if (
                    shouldRemove is not null
                    && instances.TryGetValue(correlationId, out var candidate)
                    && !shouldRemove(candidate)
                )
                {
                    continue;
                }
                if (
                    TryRemoveCore(
                        contextKey,
                        correlationId,
                        reason,
                        out var instance
                    )
                )
                {
                    removedCount++;
                    if (instance is not null)
                        removed?.Add(instance);
                }
            }
        }
        return removedCount;
    }

    private bool TryRemoveCore(
        OwnerContextKey contextKey,
        string correlationId,
        HarmlessProjectionCleanupReason reason,
        out ShadowCreatureHarmlessProjectionInstance? removed
    )
    {
        if (
            !byContext.TryGetValue(contextKey, out var instances)
            || !instances.Remove(correlationId, out removed)
        )
        {
            removed = null;
            return false;
        }

        if (instances.Count == 0)
        {
            byContext.Remove(contextKey);
            contextByOwnerScreen.Remove(
                new OwnerScreenKey(contextKey.PlayerKey, contextKey.ScreenId)
            );
        }

        byCorrelation.Remove(correlationId);
        if (countsByOwner.TryGetValue(contextKey.PlayerKey, out var count))
        {
            if (count <= 1)
                countsByOwner.Remove(contextKey.PlayerKey);
            else
                countsByOwner[contextKey.PlayerKey] = count - 1;
        }
        removed.TryMarkCleaned(reason);
        return true;
    }

    private readonly struct OwnerContextKey : IEquatable<OwnerContextKey>
    {
        internal OwnerContextKey(HarmlessProjectionOwnerContext owner)
        {
            PlayerKey = owner.PlayerKey;
            ScreenId = owner.ScreenId;
            LocationReference = owner.LocationReference;
            LocationNameOrUniqueName = owner.LocationNameOrUniqueName;
        }

        internal string PlayerKey { get; }

        internal int ScreenId { get; }

        private object LocationReference { get; }

        private string LocationNameOrUniqueName { get; }

        internal bool Matches(HarmlessProjectionOwnerContext owner)
        {
            return string.Equals(PlayerKey, owner.PlayerKey, StringComparison.Ordinal)
                && ScreenId == owner.ScreenId
                && ReferenceEquals(LocationReference, owner.LocationReference)
                && string.Equals(
                    LocationNameOrUniqueName,
                    owner.LocationNameOrUniqueName,
                    StringComparison.Ordinal
                );
        }

        public bool Equals(OwnerContextKey other)
        {
            return string.Equals(PlayerKey, other.PlayerKey, StringComparison.Ordinal)
                && ScreenId == other.ScreenId
                && ReferenceEquals(LocationReference, other.LocationReference);
        }

        public override bool Equals(object? obj)
        {
            return obj is OwnerContextKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(PlayerKey),
                ScreenId,
                RuntimeHelpers.GetHashCode(LocationReference)
            );
        }
    }

    private readonly struct OwnerScreenKey : IEquatable<OwnerScreenKey>
    {
        internal OwnerScreenKey(string playerKey, int screenId)
        {
            PlayerKey = playerKey;
            ScreenId = screenId;
        }

        private string PlayerKey { get; }

        private int ScreenId { get; }

        public bool Equals(OwnerScreenKey other)
        {
            return string.Equals(PlayerKey, other.PlayerKey, StringComparison.Ordinal)
                && ScreenId == other.ScreenId;
        }

        public override bool Equals(object? obj)
        {
            return obj is OwnerScreenKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(PlayerKey),
                ScreenId
            );
        }
    }
}
