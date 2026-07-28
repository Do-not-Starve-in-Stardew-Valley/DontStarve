#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal enum EnvironmentLightInvalidationReason
{
    OwnerWarped,
    DayStarted,
    DayEnding,
    EventOverride,
    ConfigDisabled,
    OwnerInvalidated,
    ScreenInvalid,
    LocationInvalid,
    ReturnedToTitle,
    WorldCleanup,
}

internal readonly record struct EnvironmentLightCacheKey(
    string PlayerKey,
    int ScreenId,
    string LocationNameOrUniqueName,
    long LocationInstanceId
);

internal sealed record EnvironmentLightCacheEntry(
    long LightRevision,
    long CapturedAtTick,
    EnvironmentLightResult Result,
    EnvironmentLightDiagnostic Diagnostic,
    long StoredSequence
);

/// <summary>
/// Small owner/screen/location cache. Fifteen update ticks is approximately 0.25 seconds at the
/// game's 60 Hz update rate, within the frozen 0.2–0.5 second sampling window.
/// </summary>
internal sealed class EnvironmentLightCache
{
    internal const long SampleCadenceTicks = 15;
    internal const int MaximumEntries = 16;

    private readonly Dictionary<EnvironmentLightCacheKey, EnvironmentLightCacheEntry>
        entries = new();
    private long sequence;

    internal int Count => entries.Count;

    internal bool TryGetFresh(
        EnvironmentLightCacheKey key,
        long currentTick,
        out EnvironmentLightCacheEntry entry
    )
    {
        entry = null!;
        if (currentTick == long.MinValue)
            return false;
        if (!entries.TryGetValue(key, out var found) || found is null)
            return false;
        entry = found;
        if (entry.CapturedAtTick == long.MinValue)
            return false;
        if (currentTick < entry.CapturedAtTick)
            return false;
        return currentTick - entry.CapturedAtTick < SampleCadenceTicks;
    }

    internal bool TryGetLatest(
        EnvironmentLightCacheKey key,
        out EnvironmentLightCacheEntry entry
    )
    {
        if (entries.TryGetValue(key, out var found) && found is not null)
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    internal void Store(
        EnvironmentLightCacheKey key,
        long lightRevision,
        long capturedAtTick,
        EnvironmentLightResult result,
        EnvironmentLightDiagnostic diagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (!entries.ContainsKey(key) && entries.Count >= MaximumEntries)
            RemoveOldest();
        entries[key] = new EnvironmentLightCacheEntry(
            lightRevision,
            capturedAtTick,
            result,
            diagnostic,
            ++sequence
        );
    }

    internal bool TryGetDiagnostic(
        string playerKey,
        int screenId,
        out EnvironmentLightDiagnostic diagnostic
    )
    {
        EnvironmentLightCacheEntry? newest = null;
        foreach (var pair in entries)
        {
            if (
                pair.Key.ScreenId != screenId
                || !string.Equals(
                    pair.Key.PlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            if (newest is null || pair.Value.StoredSequence > newest.StoredSequence)
                newest = pair.Value;
        }

        if (newest is null)
        {
            diagnostic = null!;
            return false;
        }
        diagnostic = newest.Diagnostic;
        return true;
    }

    internal int Invalidate(string playerKey, int screenId)
    {
        List<EnvironmentLightCacheKey>? matches = null;
        foreach (var key in entries.Keys)
        {
            if (
                key.ScreenId == screenId
                && string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                matches ??= new List<EnvironmentLightCacheKey>();
                matches.Add(key);
            }
        }
        if (matches is null)
            return 0;
        foreach (var key in matches)
            entries.Remove(key);
        return matches.Count;
    }

    internal void Clear()
    {
        entries.Clear();
    }

    private void RemoveOldest()
    {
        var found = false;
        var oldestKey = default(EnvironmentLightCacheKey);
        var oldestSequence = long.MaxValue;
        foreach (var pair in entries)
        {
            if (pair.Value.StoredSequence >= oldestSequence)
                continue;
            found = true;
            oldestKey = pair.Key;
            oldestSequence = pair.Value.StoredSequence;
        }
        if (found)
            entries.Remove(oldestKey);
    }
}
