#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal readonly record struct SpecialPassOutSanityOverride(
    double Sanity,
    SanityChangeSource Source,
    bool DisableSleepRecovery,
    string Reason
);

/// <summary>
/// Session-scoped handoff from a completed NonLethal darkness event to the sleep settlement.
/// It owns no Farmer and intentionally exposes a decision rather than writing Sanity itself.
/// </summary>
internal sealed class SpecialPassOutSanitySettlement
{
    internal const int MaximumEntries = 16;

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal int Count => entries.Count;

    internal bool RegisterNonLethal(
        string sessionId,
        string playerKey,
        int gameDay,
        out string reason
    )
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "passout.sanity-settlement.session-invalid";
            return false;
        }
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "passout.sanity-settlement.player-invalid";
            return false;
        }
        if (gameDay < 0)
        {
            reason = "passout.sanity-settlement.day-invalid";
            return false;
        }

        if (entries.TryGetValue(playerKey, out var existing))
        {
            if (
                string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
                && existing.GameDay == gameDay
            )
            {
                reason = "passout.sanity-settlement.duplicate";
                return false;
            }

            // A new session/day supersedes stale same-player runtime state. This is safe because
            // the old game session can no longer reach the current DayEnding callback.
            entries.Remove(playerKey);
        }
        else if (entries.Count >= MaximumEntries)
        {
            reason = "passout.sanity-settlement.capacity-exceeded";
            return false;
        }

        entries.Add(playerKey, new Entry(sessionId, gameDay));
        reason = "passout.sanity-settlement.registered";
        return true;
    }

    internal bool TryResolve(
        string sessionId,
        string playerKey,
        int gameDay,
        double currentSanity,
        out SpecialPassOutSanityOverride result
    )
    {
        if (
            !double.IsFinite(currentSanity)
            || !entries.TryGetValue(playerKey, out var entry)
            || !string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
            || entry.GameDay != gameDay
        )
        {
            result = default;
            return false;
        }

        result = new SpecialPassOutSanityOverride(
            0d,
            SanityChangeSource.SanityDarknessSpecialDeath,
            DisableSleepRecovery: true,
            "passout.sanity-settlement.nonlethal-night-zero"
        );
        return true;
    }

    internal void ClearSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            entries.Clear();
            return;
        }

        var remove = new List<string>();
        foreach (var pair in entries)
        {
            if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
                remove.Add(pair.Key);
        }
        foreach (var playerKey in remove)
            entries.Remove(playerKey);
    }

    private readonly record struct Entry(string SessionId, int GameDay);
}
