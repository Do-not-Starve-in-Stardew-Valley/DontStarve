#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Mod-private owner-local active index. Location identity uses reference equality intentionally;
/// no entry is mirrored into GameLocation critters/characters/temporarySprites or a network snapshot.
/// </summary>
internal sealed class HarmlessProjectionIndex
{
    private readonly Dictionary<OwnerContextKey, Dictionary<string, HarmlessProjectionInstance>>
        byContext = new();
    private readonly Dictionary<OwnerScreenKey, OwnerContextKey> contextByOwnerScreen =
        new();
    private readonly Dictionary<OwnerSpeciesKey, int> counts = new();
    private readonly Dictionary<OwnerSpeciesKey, HarmlessProjectionInstance>
        byOwnerSpecies = new();

    internal int Count { get; private set; }

    internal bool TryAdd(HarmlessProjectionInstance instance, out string reason)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.IsCleanedUp)
        {
            reason = "index.instance-already-cleaned";
            return false;
        }

        var ownerSpecies = new OwnerSpeciesKey(
            instance.Owner.PlayerKey,
            instance.SpeciesId
        );
        counts.TryGetValue(ownerSpecies, out var activeCount);
        if (activeCount >= instance.Policy.ActiveCap)
        {
            reason = "index.active-cap-reached";
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
            reason = "index.owner-screen-location-mismatch";
            return false;
        }
        if (!byContext.TryGetValue(contextKey, out var species))
        {
            species = new Dictionary<string, HarmlessProjectionInstance>(
                StringComparer.Ordinal
            );
            byContext.Add(contextKey, species);
            contextByOwnerScreen[ownerScreen] = contextKey;
        }
        if (species.ContainsKey(instance.SpeciesId))
        {
            reason = "index.context-species-already-active";
            return false;
        }

        species.Add(instance.SpeciesId, instance);
        counts[ownerSpecies] = activeCount + 1;
        byOwnerSpecies.Add(ownerSpecies, instance);
        Count++;
        reason = "index.added";
        return true;
    }

    internal bool TryGet(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        out HarmlessProjectionInstance? instance
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (
            byContext.TryGetValue(new OwnerContextKey(owner), out var species)
            && species.TryGetValue(speciesId, out instance)
            && instance.Owner.Matches(owner)
        )
        {
            return true;
        }

        instance = null;
        return false;
    }

    internal bool TryGetForOwnerSpecies(
        string playerKey,
        string speciesId,
        out HarmlessProjectionInstance? instance
    )
    {
        return byOwnerSpecies.TryGetValue(
            new OwnerSpeciesKey(playerKey, speciesId),
            out instance
        );
    }

    internal int CountForOwnerSpecies(string playerKey, string speciesId)
    {
        return counts.TryGetValue(new OwnerSpeciesKey(playerKey, speciesId), out var count)
            ? count
            : 0;
    }

    internal bool TryRemove(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        HarmlessProjectionCleanupReason reason,
        out HarmlessProjectionInstance? removed
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        return TryRemoveCore(new OwnerContextKey(owner), speciesId, reason, out removed);
    }

    internal bool TryRemoveForOwnerSpecies(
        string playerKey,
        string speciesId,
        HarmlessProjectionCleanupReason reason,
        out HarmlessProjectionInstance? removed
    )
    {
        var ownerSpecies = new OwnerSpeciesKey(playerKey, speciesId);
        if (byOwnerSpecies.TryGetValue(ownerSpecies, out var instance))
        {
            return TryRemoveCore(
                new OwnerContextKey(instance.Owner),
                speciesId,
                reason,
                out removed
            );
        }

        removed = null;
        return false;
    }

    internal int CleanupOwner(
        string playerKey,
        HarmlessProjectionCleanupReason reason
    )
    {
        return CleanupWhere(
            key => string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal),
            reason
        );
    }

    internal int CleanupOwnerSpecies(
        string playerKey,
        string speciesId,
        HarmlessProjectionCleanupReason reason
    )
    {
        return TryRemoveForOwnerSpecies(playerKey, speciesId, reason, out _) ? 1 : 0;
    }

    internal int CleanupContext(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionCleanupReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        return CleanupWhere(key => key.Matches(owner), reason);
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
            || !byContext.TryGetValue(contextKey, out var species)
        )
        {
            return 0;
        }

        var speciesIds = new List<string>(species.Keys);
        var removed = 0;
        foreach (var speciesId in speciesIds)
        {
            if (TryRemoveCore(contextKey, speciesId, reason, out _))
                removed++;
        }
        return removed;
    }

    internal int CleanupInvalidScreens(
        Func<int, bool> isScreenValid,
        HarmlessProjectionCleanupReason reason
    )
    {
        ArgumentNullException.ThrowIfNull(isScreenValid);
        return CleanupWhere(key => !isScreenValid(key.ScreenId), reason);
    }

    internal int CleanupAll(HarmlessProjectionCleanupReason reason)
    {
        return CleanupWhere(_ => true, reason);
    }

    internal IReadOnlyList<HarmlessProjectionInstance> SnapshotForContext(
        HarmlessProjectionOwnerContext owner
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!byContext.TryGetValue(new OwnerContextKey(owner), out var species))
            return Array.Empty<HarmlessProjectionInstance>();

        var snapshot = new List<HarmlessProjectionInstance>(species.Count);
        foreach (var instance in species.Values)
        {
            if (instance.Owner.Matches(owner))
                snapshot.Add(instance);
        }
        return snapshot.AsReadOnly();
    }

    /// <summary>
    /// Returns the current dictionary value view so RenderedWorld can enumerate the bounded
    /// owner-local set without allocating a per-frame snapshot.
    /// </summary>
    internal bool TryGetContextInstances(
        HarmlessProjectionOwnerContext owner,
        out Dictionary<string, HarmlessProjectionInstance>.ValueCollection? instances
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (
            byContext.TryGetValue(new OwnerContextKey(owner), out var species)
            && contextByOwnerScreen.TryGetValue(
                new OwnerScreenKey(owner.PlayerKey, owner.ScreenId),
                out var contextKey
            )
            && contextKey.Matches(owner)
        )
        {
            instances = species.Values;
            return true;
        }

        instances = null;
        return false;
    }

    private int CleanupWhere(
        Func<OwnerContextKey, bool> predicate,
        HarmlessProjectionCleanupReason reason
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
            if (!byContext.TryGetValue(contextKey, out var species))
                continue;
            var speciesIds = new List<string>(species.Keys);
            foreach (var speciesId in speciesIds)
            {
                if (TryRemoveCore(contextKey, speciesId, reason, out _))
                    removedCount++;
            }
        }
        return removedCount;
    }

    private bool TryRemoveCore(
        OwnerContextKey contextKey,
        string speciesId,
        HarmlessProjectionCleanupReason reason,
        out HarmlessProjectionInstance? removed
    )
    {
        if (
            !byContext.TryGetValue(contextKey, out var species)
            || !species.Remove(speciesId, out removed)
        )
        {
            removed = null;
            return false;
        }

        if (species.Count == 0)
        {
            byContext.Remove(contextKey);
            contextByOwnerScreen.Remove(
                new OwnerScreenKey(contextKey.PlayerKey, contextKey.ScreenId)
            );
        }

        var ownerSpecies = new OwnerSpeciesKey(contextKey.PlayerKey, speciesId);
        byOwnerSpecies.Remove(ownerSpecies);
        if (counts.TryGetValue(ownerSpecies, out var count))
        {
            if (count <= 1)
                counts.Remove(ownerSpecies);
            else
                counts[ownerSpecies] = count - 1;
        }

        Count--;
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

        internal object LocationReference { get; }

        internal string LocationNameOrUniqueName { get; }

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

    private readonly struct OwnerSpeciesKey : IEquatable<OwnerSpeciesKey>
    {
        internal OwnerSpeciesKey(string playerKey, string speciesId)
        {
            PlayerKey = playerKey;
            SpeciesId = speciesId;
        }

        private string PlayerKey { get; }

        private string SpeciesId { get; }

        public bool Equals(OwnerSpeciesKey other)
        {
            return string.Equals(PlayerKey, other.PlayerKey, StringComparison.Ordinal)
                && string.Equals(SpeciesId, other.SpeciesId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is OwnerSpeciesKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(PlayerKey),
                StringComparer.Ordinal.GetHashCode(SpeciesId)
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
