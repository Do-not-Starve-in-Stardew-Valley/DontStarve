#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Music;

/// <summary>
/// Keeps the two short-lived DST activity-music suppression windows separate per owner. The store
/// owns no audio instances and never changes logical activity priority.
/// </summary>
internal sealed class ActivityMusicSuppressionStateStore
{
    private sealed class OwnerState
    {
        internal double DawnSeconds;
        internal double DuskSeconds;
        internal double InsaneSeconds;
    }

    private readonly Dictionary<long, OwnerState> states = new();

    internal bool StartDawn(long ownerId, double seconds) =>
        Start(ownerId, seconds, SuppressionKind.Dawn);

    internal bool StartDusk(long ownerId, double seconds) =>
        Start(ownerId, seconds, SuppressionKind.Dusk);

    internal bool StartInsane(long ownerId, double seconds) =>
        Start(ownerId, seconds, SuppressionKind.Insane);

    internal bool IsFarmingBlocked(long ownerId) =>
        states.TryGetValue(ownerId, out var state)
        && (state.DawnSeconds > 0d || state.DuskSeconds > 0d);

    internal bool IsInsaneCooldownActive(long ownerId) =>
        states.TryGetValue(ownerId, out var state) && state.InsaneSeconds > 0d;

    internal void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0d)
            return;

        List<long>? expired = null;
        foreach (var pair in states)
        {
            pair.Value.DawnSeconds = Math.Max(0d, pair.Value.DawnSeconds - elapsedSeconds);
            pair.Value.DuskSeconds = Math.Max(0d, pair.Value.DuskSeconds - elapsedSeconds);
            pair.Value.InsaneSeconds = Math.Max(0d, pair.Value.InsaneSeconds - elapsedSeconds);
            if (
                pair.Value.DawnSeconds <= 0d
                && pair.Value.DuskSeconds <= 0d
                && pair.Value.InsaneSeconds <= 0d
            )
            {
                expired ??= new List<long>();
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
            return;
        foreach (var ownerId in expired)
            states.Remove(ownerId);
    }

    internal void ClearOwner(long ownerId) => states.Remove(ownerId);

    internal void Clear() => states.Clear();

    private enum SuppressionKind
    {
        Dawn,
        Dusk,
        Insane,
    }

    private bool Start(long ownerId, double seconds, SuppressionKind kind)
    {
        if (ownerId == 0 || !double.IsFinite(seconds) || seconds <= 0d)
            return false;

        if (!states.TryGetValue(ownerId, out var state))
        {
            state = new OwnerState();
            states.Add(ownerId, state);
        }

        var active = kind switch
        {
            SuppressionKind.Dawn => state.DawnSeconds,
            SuppressionKind.Dusk => state.DuskSeconds,
            SuppressionKind.Insane => state.InsaneSeconds,
            _ => 0d,
        };
        if (active > 0d)
            return false;

        switch (kind)
        {
            case SuppressionKind.Dawn:
                state.DawnSeconds = seconds;
                break;
            case SuppressionKind.Dusk:
                state.DuskSeconds = seconds;
                break;
            case SuppressionKind.Insane:
                state.InsaneSeconds = seconds;
                break;
        }
        return true;
    }
}
