#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats;

/// <summary>
/// 按玩家隔离时间回退补偿。它只保存调度游标，不知道 SMAPI、地图或具体 Sanity/Hunger 公式。
/// </summary>
internal sealed class PlayerTimeBehaviorState
{
    private readonly Dictionary<string, long> waits =
        new(StringComparer.Ordinal);

    internal bool TryEnterMinute(string playerKey, bool isSleeping)
    {
        if (string.IsNullOrWhiteSpace(playerKey) || isSleeping)
            return false;

        if (!waits.TryGetValue(playerKey, out var wait) || wait <= 0)
            return true;

        if (wait == 1)
            waits.Remove(playerKey);
        else
            waits[playerKey] = wait - 1;
        return false;
    }

    internal void AddRewindWait(string playerKey, long delta)
    {
        if (string.IsNullOrWhiteSpace(playerKey) || delta >= 0)
            return;

        var rewindMinutes = delta == long.MinValue ? long.MaxValue : -delta;
        if (!waits.TryGetValue(playerKey, out var current) || current < 0)
            current = 0;

        waits[playerKey] =
            current > long.MaxValue - rewindMinutes
                ? long.MaxValue
                : current + rewindMinutes;
    }

    internal Dictionary<string, long> CaptureWaits()
    {
        var copy = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in waits)
        {
            if (pair.Value > 0)
                copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    internal void Clear(string playerKey)
    {
        if (!string.IsNullOrWhiteSpace(playerKey))
            waits.Remove(playerKey);
    }

    internal void Load(
        IReadOnlyDictionary<string, long>? savedWaits,
        string? legacyPlayerKey,
        long legacyWait
    )
    {
        waits.Clear();
        if (savedWaits is not null)
        {
            foreach (var pair in savedWaits)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                    waits[pair.Key] = pair.Value;
            }
        }

        // 旧版本只有一个全局 Wait；仅映射到主玩家，避免把旧回退账复制给每个 farmhand。
        if (
            waits.Count == 0
            && legacyWait > 0
            && !string.IsNullOrWhiteSpace(legacyPlayerKey)
        )
        {
            waits[legacyPlayerKey] = legacyWait;
        }
    }

    internal void Clear() => waits.Clear();
}
