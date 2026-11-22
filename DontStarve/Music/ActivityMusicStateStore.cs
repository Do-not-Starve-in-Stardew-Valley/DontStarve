#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Music;

/// <summary>
/// Stores only logical activity state. Physical SoundEffectInstance ownership is deliberately
/// left to the runtime audio stage so a state update can never accidentally share a player or
/// playback position between farmers.
/// </summary>
internal sealed class ActivityMusicStateStore
{
    private readonly Dictionary<long, ActivityMusicState> states = new();
    private long nextGeneration;

    internal int Count => states.Count;

    internal ActivityMusicState Set(
        long ownerId,
        ActivityMusicKind kind,
        ActivityMusicLocationContext location,
        string trackKey,
        int remainingSeconds
    )
    {
        var hasCurrent = states.TryGetValue(ownerId, out var current);
        var sameTrack = hasCurrent
            && current is not null
            && IsSameTrack(current, location, trackKey);

        if (hasCurrent
            && current is not null
            && ActivityMusicArbitration.ResolveTransition(
                current.Kind,
                kind,
                sameTrack
            ) == ActivityMusicStateTransition.IgnoredLowerPriority)
        {
            return current;
        }

        if (hasCurrent
            && current is not null
            && ActivityMusicArbitration.ResolveTransition(
                current.Kind,
                kind,
                sameTrack
            ) == ActivityMusicStateTransition.Refreshed)
        {
            current.Refresh(remainingSeconds);
            return current;
        }

        var state = new ActivityMusicState(
            ownerId,
            kind,
            location,
            trackKey,
            ++nextGeneration,
            remainingSeconds
        );
        states[ownerId] = state;
        return state;
    }

    internal bool TryGet(long ownerId, out ActivityMusicState? state)
    {
        return states.TryGetValue(ownerId, out state);
    }

    internal bool Remove(long ownerId)
    {
        return states.Remove(ownerId);
    }

    internal bool RemoveWorkAndFarming(long ownerId)
    {
        if (!states.TryGetValue(ownerId, out var state))
            return false;
        if (state.Kind is not (ActivityMusicKind.Work or ActivityMusicKind.Farming))
            return false;

        return states.Remove(ownerId);
    }

    /// <summary>
    /// EnableFarmingMusic 关闭瞬间使用：一次性移除所有 owner 的耕种逻辑态，使后续耕种触发
    /// （已被降级为工作触发）不被旧 Farming 态的更高车道优先级吞掉。工作/战斗态不动。
    /// </summary>
    internal void RemoveFarmingAll()
    {
        List<long>? farmingOwners = null;
        foreach (var pair in states)
        {
            if (pair.Value.Kind != ActivityMusicKind.Farming)
                continue;

            farmingOwners ??= new List<long>();
            farmingOwners.Add(pair.Key);
        }

        if (farmingOwners is null)
            return;

        foreach (var ownerId in farmingOwners)
            states.Remove(ownerId);
    }

    internal IReadOnlyList<long> Advance(int seconds)
    {
        if (seconds <= 0 || states.Count == 0)
            return Array.Empty<long>();

        var expired = new List<long>();
        foreach (var pair in states)
        {
            if (pair.Value.Advance(seconds))
                expired.Add(pair.Key);
        }

        foreach (var ownerId in expired)
            states.Remove(ownerId);

        return expired;
    }

    internal void Clear()
    {
        states.Clear();
    }

    private static bool IsSameTrack(
        ActivityMusicState current,
        ActivityMusicLocationContext location,
        string trackKey
    )
    {
        return current.TrackKey == trackKey
            && current.Location.Family == location.Family
            && current.Location.ValleySeason == location.ValleySeason
            && current.Location.GingerSeason == location.GingerSeason
            && string.Equals(
                current.Location.LocationContextId,
                location.LocationContextId,
                System.StringComparison.Ordinal
            );
    }
}
