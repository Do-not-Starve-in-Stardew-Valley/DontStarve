#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Keeps one independent next-check time per shadow. A consumed check is scheduled from the
/// time that was actually observed, rather than repeatedly catching up an old schedule. This is
/// the small state machine used by both the high-Sanity retreat roll and over-cap cleanup.
/// </summary>
internal sealed class HostileShadowCheckSchedule<TKey>
    where TKey : notnull
{
    private readonly long intervalMinutes;
    private readonly Dictionary<TKey, long> nextCheckByKey;

    internal HostileShadowCheckSchedule(
        long intervalMinutes,
        IEqualityComparer<TKey>? comparer = null
    )
    {
        if (intervalMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));

        this.intervalMinutes = intervalMinutes;
        nextCheckByKey = new Dictionary<TKey, long>(comparer);
    }

    internal int Count => nextCheckByKey.Count;

    internal void Register(TKey key, long spawnedAtMinute)
    {
        if (spawnedAtMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(spawnedAtMinute));

        nextCheckByKey.TryAdd(key, AddInterval(spawnedAtMinute));
    }

    internal bool TryConsumeIfDue(
        TKey key,
        long currentMinute,
        long spawnedAtMinute
    )
    {
        if (currentMinute < 0)
            return false;

        Register(key, spawnedAtMinute);
        if (currentMinute < nextCheckByKey[key])
            return false;

        // A large time jump consumes at most this one check. The next check starts from the
        // current clock, so the old timeline can never make this shadow roll several times.
        nextCheckByKey[key] = AddInterval(currentMinute);
        return true;
    }

    internal void Rebase(IEnumerable<TKey> activeKeys, long currentMinute)
    {
        if (currentMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(currentMinute));

        var nextMinute = AddInterval(currentMinute);
        foreach (var key in activeKeys)
            nextCheckByKey[key] = nextMinute;
    }

    internal void RebaseKey(TKey key, long currentMinute)
    {
        if (currentMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(currentMinute));

        nextCheckByKey[key] = AddInterval(currentMinute);
    }

    internal bool TryGetNextCheck(TKey key, out long nextMinute)
    {
        return nextCheckByKey.TryGetValue(key, out nextMinute);
    }

    internal void Remove(TKey key)
    {
        nextCheckByKey.Remove(key);
    }

    internal void Clear()
    {
        nextCheckByKey.Clear();
    }

    private long AddInterval(long minute)
    {
        return minute > long.MaxValue - intervalMinutes
            ? long.MaxValue
            : minute + intervalMinutes;
    }
}
