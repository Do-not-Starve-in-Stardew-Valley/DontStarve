#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Sleep;

/// <summary>
/// Keeps the post-save Hunger-death markers bounded and short-lived. These markers only carry
/// death-dialogue context; they are not numeric sleep state and must never grow with save age.
/// </summary>
internal static class SleepWakeEffectsPolicy
{
    internal const int MaxMarkers = 128;
    internal const int RetentionDays = 1;

    internal static Dictionary<string, int> Normalize(
        IEnumerable<string>? playerKeys,
        IReadOnlyDictionary<string, int>? markerDays,
        int currentDay
    )
    {
        currentDay = Math.Max(0, currentDay);
        var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
        if (playerKeys is null)
            return candidates;

        foreach (var playerKey in playerKeys)
        {
            if (!SleepLifecycleProtocol.IsValidPlayerKey(playerKey))
                continue;

            var markerDay = currentDay;
            if (
                markerDays is not null
                && markerDays.TryGetValue(playerKey, out var storedDay)
                && storedDay >= 0
                && storedDay <= currentDay
            )
            {
                markerDay = storedDay;
            }

            if (markerDay < currentDay - RetentionDays)
                continue;

            if (
                !candidates.TryGetValue(playerKey, out var existingDay)
                || markerDay > existingDay
            )
            {
                candidates[playerKey] = markerDay;
            }
        }

        var ordered = new List<KeyValuePair<string, int>>(candidates);
        ordered.Sort(
            (left, right) =>
            {
                var byDay = right.Value.CompareTo(left.Value);
                return byDay != 0
                    ? byDay
                    : StringComparer.Ordinal.Compare(left.Key, right.Key);
            }
        );

        if (ordered.Count > MaxMarkers)
            ordered.RemoveRange(MaxMarkers, ordered.Count - MaxMarkers);

        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in ordered)
            normalized[pair.Key] = pair.Value;
        return normalized;
    }
}
