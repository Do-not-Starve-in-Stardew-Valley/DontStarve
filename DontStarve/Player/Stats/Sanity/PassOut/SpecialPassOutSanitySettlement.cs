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

/// <summary>特殊昏迷睡眠记账的两种来源。两者共用同一张登记表与同一个血量写回缝，但
/// Sanity 语义不同：非致命昏迷把 Sanity 压 0（TryResolve），黑暗死亡不清 Sanity——其
/// 清晨恢复由 passout.reason 的 checkForEvents postfix（SetToMaximumFraction）负责。</summary>
internal enum SpecialPassOutSettlementKind
{
    NonLethal,
    PendingDarknessWakeHealth,
}

/// <summary>
/// Session-scoped handoff from a completed special darkness outcome to the sleep settlement.
/// It owns no Farmer and intentionally exposes a decision rather than writing Sanity itself.
/// <para>
/// 本结算按 <see cref="SpecialPassOutSettlementKind"/> 承载两类特殊昏迷的记账项，共用同一张
/// 登记表与同一个"dayupdate 之后、存档序列化之前"的血量写回缝：
/// ① NonLethal（RegisterNonLethal）：Sanity=0（TryResolve，DayEnding 写回即可幸存，vanilla
///   dayupdate 不碰 Sanity）+ 醒来血量上限 min(昏迷捕获血量, ceil(上限×20%))。
/// ② PendingDarknessWakeHealth（RegisterPendingDarknessWakeHealth）：Default 黑暗死亡的复活血量
///   （原版 revive=10），不清 Sanity（清晨恢复走 passout.reason 的 checkForEvents postfix）。
/// 血量必须写回在 dayupdate 之后：vanilla dayupdate 在换日时把 health 满血回填
/// （1.6.15 Farmer.dayupdate 的 health=maxHealth），夜里写入的值会被覆盖。写回点为
/// dayupdate postfix / SaveFarmhand prefix / OnSaving，全部位于 SaveGame.Save 序列化之前，
/// 存档自然携带最终值，无需额外存档标记。
/// </para>
/// <para>
/// TryResolveHealth 有意不做 gameDay 匹配：登记发生在换日之前（day N），而全部消费点都在
/// N→N+1 换日协程内部或其后的 OnSaving（此时日历已滚到 N+1），按天匹配永远失配。消费窗口
/// 由 ClearSession 闭环约束——DayStarted / SaveLoaded / ReturnedToTitle 都会按 SessionId
/// 清空，条目不可能跨夜存活到下一次登记。
/// </para>
/// <para>
/// 线程边界：登记走主线程（状态机 ApplyActions），TryResolveHealth 会被换日后台任务
/// （_newDayTask 内的 dayupdate postfix / saveFarmhands）调用，与 SleepOvernightSettlement
/// 同样需要同步。
/// </para>
/// </summary>
internal sealed class SpecialPassOutSanitySettlement
{
    internal const int MaximumEntries = 16;

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    internal bool RegisterNonLethal(
        string sessionId,
        string playerKey,
        int gameDay,
        int wakeHealthCap,
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
        if (gameDay < 0 || wakeHealthCap < 0)
        {
            reason = "passout.sanity-settlement.day-invalid";
            return false;
        }

        lock (gate)
        {
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

            entries.Add(
                playerKey,
                new Entry(
                    sessionId,
                    gameDay,
                    wakeHealthCap,
                    SpecialPassOutSettlementKind.NonLethal
                )
            );
        }
        reason = "passout.sanity-settlement.registered";
        return true;
    }

    /// <summary>
    /// Pending-darkness（Default 黑暗死亡）的复活血量记账：把原版复活健康值
    /// （checkForEvents killScreen 块的 health=10，GameLocation.cs:15614）登记到与非致命
    /// cap 相同的"dayupdate 之后、存档序列化之前"最终值缝。用户决议（2026-11-14）：
    /// 复活生命值的设定时机必须与饥饿的保存前最终值一致，清晨 vanilla 的再次赋值成为
    /// 无害幂等重复。与 RegisterNonLethal 不同，本登记不携带 Sanity=0 语义。
    /// </summary>
    internal bool RegisterPendingDarknessWakeHealth(
        string sessionId,
        string playerKey,
        int gameDay,
        int reviveHealth,
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
        if (gameDay < 0 || reviveHealth < 0)
        {
            reason = "passout.sanity-settlement.day-invalid";
            return false;
        }

        lock (gate)
        {
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

                entries.Remove(playerKey);
            }
            else if (entries.Count >= MaximumEntries)
            {
                reason = "passout.sanity-settlement.capacity-exceeded";
                return false;
            }

            entries.Add(
                playerKey,
                new Entry(
                    sessionId,
                    gameDay,
                    reviveHealth,
                    SpecialPassOutSettlementKind.PendingDarknessWakeHealth
                )
            );
        }
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
            || !TryGetEntry(sessionId, playerKey, gameDay, out var entry)
            || entry.Kind != SpecialPassOutSettlementKind.NonLethal
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

    /// <summary>
    /// 非致命血量上限记账项的读取入口（dayupdate 之后的保存前最终值缝专用）。
    /// 只有登记过且 WakeHealthCap &gt; 0 的条目才暴露；cap&lt;=0 属退化边界（血 0 双死守卫
    /// 语义下不应出现），不暴露即由调用方保持原版晨间值，不写回 0 血。
    /// </summary>
    internal bool TryResolveHealth(
        string sessionId,
        string playerKey,
        out int wakeHealthCap
    )
    {
        lock (gate)
        {
            if (
                entries.TryGetValue(playerKey, out var entry)
                && string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
                && entry.WakeHealthCap > 0
            )
            {
                wakeHealthCap = entry.WakeHealthCap;
                return true;
            }
        }
        wakeHealthCap = 0;
        return false;
    }

    internal void ClearSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            lock (gate)
                entries.Clear();
            return;
        }

        lock (gate)
        {
            var remove = new List<string>();
            foreach (var pair in entries)
            {
                if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
                    remove.Add(pair.Key);
            }
            foreach (var playerKey in remove)
                entries.Remove(playerKey);
        }
    }

    private bool TryGetEntry(
        string sessionId,
        string playerKey,
        int gameDay,
        out Entry entry
    )
    {
        lock (gate)
        {
            if (
                entries.TryGetValue(playerKey, out entry)
                && string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
                && entry.GameDay == gameDay
            )
                return true;
        }
        entry = default;
        return false;
    }

    private readonly record struct Entry(
        string SessionId,
        int GameDay,
        int WakeHealthCap,
        SpecialPassOutSettlementKind Kind
    );
}
