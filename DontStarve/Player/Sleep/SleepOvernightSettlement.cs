#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Hunger;

namespace DontStarve.Player.Sleep;

/// <summary>
/// One real night, held only until the native save finishes. Final values are saved by the
/// ordinary stat stores; no session-bound delta is persisted or replayed when a save is loaded.
/// Native dayupdate/farmhand cloning can run on the overnight worker, so access is synchronized.
/// </summary>
internal sealed class SleepOvernightSettlement
{
    private readonly object gate = new();
    private readonly Dictionary<string, SleepSnapshot> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SleepProgress> results = new(StringComparer.Ordinal);

    internal bool TryPrepare(SleepSnapshot snapshot, out string reason)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(snapshot.PlayerKey)
                || !double.IsFinite(snapshot.Hunger) || !double.IsFinite(snapshot.Sanity)
                || !double.IsFinite(snapshot.Health) || snapshot.MaximumHealth <= 0
                || snapshot.BlackoutStartMinute < 0 || snapshot.TargetMinute < snapshot.BlackoutStartMinute
                || snapshot.TargetMinute - snapshot.BlackoutStartMinute > 1440)
            {
                reason = "sleep-night-input-is-invalid";
                return false;
            }
            if (inputs.ContainsKey(snapshot.PlayerKey))
            {
                reason = "sleep-night-already-prepared";
                return false;
            }
            inputs.Add(snapshot.PlayerKey, snapshot);
            reason = "sleep-night-prepared";
            return true;
        }
    }

    internal bool TryFinalize(string playerKey, int vanillaHealth, HungerDamageMode mode,
        out SleepProgress result)
    {
        lock (gate)
        {
            if (results.TryGetValue(playerKey, out result))
                return true;
            if (!inputs.TryGetValue(playerKey, out var input))
                return false;

            // Balanced sleep buys health from bedtime state. Disabled balance keeps native
            // full recovery, then applies the entire starvation interval to that recovered HP.
            if (!input.RecoveryBalance.Enabled || !input.RecoveryBalance.HungerEnabled
                || input.Mode != SleepMode.Voluntary)
                input = input with { Health = vanillaHealth };
            result = SleepRules.SettleBlackoutBatch(input, input.BlackoutStartMinute,
                input.TargetMinute, mode);
            results.Add(playerKey, result);
            return true;
        }
    }

    internal IReadOnlyList<SleepSnapshot> Snapshot()
    {
        lock (gate)
            return new List<SleepSnapshot>(inputs.Values);
    }

    internal void Clear()
    {
        lock (gate)
        {
            inputs.Clear();
            results.Clear();
        }
    }
}
