#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Player.Sleep;

internal enum SleepMode
{
    Voluntary,
    ExhaustionPassOut,
    TimeLimitPassOut,
}

internal enum SleepPhase
{
    Realtime,
    BlackoutBatch,
    Completed,
    Cancelled,
    Interrupted,
}

/// <summary>
/// 睡眠结算所需的纯数据快照。自然 Hunger 速率是输入而非第二时钟；日历字段只记录事实，不改时长公式。
/// </summary>
internal readonly record struct SleepRecoveryBalance(
    bool Enabled,
    double NaturalHungerPerMinute,
    double BaseSanityRecoveryPerMinute,
    double BedSanityMultiplier,
    double HungerCostPerSanity,
    bool HungerEnabled = true,
    bool SanityEnabled = true,
    bool SanityRecoveryEnabled = true,
    SanityChangeSource SanitySettlementSource = SanityChangeSource.VoluntarySleep
)
{
    internal static SleepRecoveryBalance Default => new(
        Enabled: true,
        NaturalHungerPerMinute: 0.052d,
        BaseSanityRecoveryPerMinute: 0.3d,
        BedSanityMultiplier: 1d,
        HungerCostPerSanity: 1d
    );

    internal static SleepRecoveryBalance Disabled => Default with { Enabled = false };
}

internal readonly record struct SleepSnapshot(
    string PlayerKey,
    string SessionId,
    string SettlementId,
    SleepMode Mode,
    long SleepStartMinute,
    long BlackoutStartMinute,
    long TargetMinute,
    string BedId,
    double Hunger,
    double MaximumHunger,
    double Sanity,
    double MaximumSanity,
    double Health,
    int MaximumHealth,
    SleepRecoveryBalance RecoveryBalance,
    int CalendarYear = 0,
    string CalendarSeason = "",
    int CalendarDay = 0
);

internal readonly record struct SleepContext(
    string PlayerKey,
    string SessionId,
    string SettlementId,
    SleepMode Mode,
    SleepPhase Phase,
    long SleepStartMinute,
    long BlackoutStartMinute,
    long CurrentMinute,
    long TargetMinute,
    string BedId,
    SleepRecoveryBalance RecoveryBalance
)
{
    internal static SleepContext Begin(SleepSnapshot snapshot)
    {
        return new SleepContext(
            snapshot.PlayerKey,
            snapshot.SessionId,
            snapshot.SettlementId,
            snapshot.Mode,
            SleepPhase.Realtime,
            snapshot.SleepStartMinute,
            snapshot.BlackoutStartMinute,
            snapshot.SleepStartMinute,
            snapshot.TargetMinute,
            snapshot.BedId,
            snapshot.RecoveryBalance
        );
    }

    internal SleepContext EnterBlackout(long blackoutStartMinute)
    {
        return this with
        {
            Phase = SleepPhase.BlackoutBatch,
            BlackoutStartMinute = blackoutStartMinute,
            CurrentMinute = blackoutStartMinute,
        };
    }

    internal SleepContext Complete(long completedMinute)
    {
        return this with
        {
            Phase = SleepPhase.Completed,
            CurrentMinute = completedMinute,
        };
    }

    internal SleepContext Cancel()
    {
        return this with { Phase = SleepPhase.Cancelled };
    }

    internal SleepContext Interrupt()
    {
        return this with { Phase = SleepPhase.Interrupted };
    }
}

internal readonly record struct SleepProgress(
    int ProcessedMinutes,
    long LastProcessedMinute,
    double Hunger,
    double Sanity,
    double Health,
    double NaturalHungerConsumed,
    double HungerPaidForRecovery,
    double SanityRecovered,
    int HungerDamage,
    SleepPhase Phase
)
{
    internal static SleepProgress Start(SleepSnapshot snapshot, long cursor)
    {
        return new SleepProgress(
            ProcessedMinutes: 0,
            LastProcessedMinute: cursor,
            Hunger: snapshot.Hunger,
            Sanity: snapshot.Sanity,
            Health: snapshot.Health,
            NaturalHungerConsumed: 0d,
            HungerPaidForRecovery: 0d,
            SanityRecovered: 0d,
            HungerDamage: 0,
            Phase: SleepPhase.Realtime
        );
    }
}

internal sealed class SleepRuntimeState
{
    private readonly Dictionary<string, SleepContext> contexts =
        new(StringComparer.Ordinal);

    internal int Count => contexts.Count;

    internal bool TryBegin(SleepContext context, out string reason)
    {
        if (string.IsNullOrWhiteSpace(context.PlayerKey))
        {
            reason = "sleep-player-key-is-empty";
            return false;
        }
        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            reason = "sleep-session-id-is-empty";
            return false;
        }
        if (string.IsNullOrWhiteSpace(context.SettlementId))
        {
            reason = "sleep-settlement-id-is-empty";
            return false;
        }
        if (contexts.ContainsKey(context.PlayerKey))
        {
            reason = "sleep-context-already-active";
            return false;
        }

        contexts.Add(context.PlayerKey, context);
        reason = "sleep-context-started";
        return true;
    }

    internal bool TryGet(string playerKey, out SleepContext context)
    {
        return contexts.TryGetValue(playerKey, out context);
    }

    internal bool TryInterrupt(
        string playerKey,
        string sessionId,
        out SleepContext interrupted
    )
    {
        if (
            !contexts.TryGetValue(playerKey, out var context)
            || !string.Equals(context.SessionId, sessionId, StringComparison.Ordinal)
        )
        {
            interrupted = default;
            return false;
        }

        interrupted = context.Interrupt();
        contexts.Remove(playerKey);
        return true;
    }

    internal void ClearSession(string sessionId)
    {
        var remove = new List<string>();
        foreach (var pair in contexts)
        {
            if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
                remove.Add(pair.Key);
        }

        foreach (var playerKey in remove)
            contexts.Remove(playerKey);
    }

    internal void Clear()
    {
        contexts.Clear();
    }
}
