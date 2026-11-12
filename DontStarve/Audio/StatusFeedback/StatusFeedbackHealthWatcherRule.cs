#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>健康差分 watcher 的一次观察结论。</summary>
internal enum StatusFeedbackHealthWatchAction
{
    None,
    Play,
    Suppressed,
}

/// <summary>健康差分 watcher 的一次观察结果（纯规则）。</summary>
internal readonly record struct StatusFeedbackHealthWatchDecision(
    StatusFeedbackHealthWatchAction Action,
    StatusFeedbackDirection? Direction
);

/// <summary>
/// 单个本地玩家的健康差分状态（纯规则、可测试）。
/// </summary>
/// <remarks>
/// 契约（对齐 DST 原版语义）：
/// - 健康变化 ≠ 0 的差分默认按方向播音（未知来源 open 默认：怪物袭击、黑暗非致命地板、
///   原版食物回血等都属于离散增减，自动获得音效）。
/// - 本 mod 自己的持续扣血/回血写入（睡眠结算、饥饿空腹伤害等 overtime 语义）在应用前
///   调用 <see cref="SuppressNextIfHealthChanges"/>，当次差分不播音（overtime 扣血完全无声；
///   overtime 回血在 DST 中也只闪不播音，这里同样静音）。
/// - 抑制令牌按"写入前后值确实不同"才发放，避免写入无实际变化时令牌滞留、
///   误吞下一次真实的离散音效。
/// - 死亡复活重置静音（纯规则，不依赖抑制令牌）：真实死亡会先经历持续多刻的
///   ≤0 死亡屏（原版 killScreen：pauseTime 3000ms，复活时把 health 直接设为
///   10/上限 50%），"持续处于 ≤0（连续 ≥<see cref="DeathStateMinimumTicks"/> 刻）
///   之后恢复为正值"的跳变视为复活重置，不播音。瞬时触碰 0（未形成持续死亡态）
///   之后的普通治疗仍按离散变化播音；黑暗袭击致死的那一下（正值→0）是攻击伤害，
///   照常播 down。
/// - 每次观察至多消费一个令牌；同一观察窗内如果同时发生持续写入与离散事件，
///   净差分会被令牌吞掉（罕见边界，接受并在功能文档中说明）。
/// </remarks>
internal sealed class StatusFeedbackHealthWatchState
{
    // 防御性上限：即便调用方泄漏多次抑制调用，也不会吞掉超过该数量的离散音效。
    internal const int MaxPendingSuppressions = 4;

    /// <summary>
    /// 判定"持续死亡态"所需的连续 ≤0 观测刻数。原版死亡屏持续约 3 秒（≥180 刻），
    /// 取 2 即可把真实复活与"单刻瞬时触碰 0 后被治疗"区分开：后者不属于死亡态，
    /// 恢复正值仍按离散变化播音。
    /// </summary>
    internal const int DeathStateMinimumTicks = 2;

    private double? lastObservedHealth;
    private int pendingSuppressions;
    private int consecutiveLowTicks;

    internal int PendingSuppressions => pendingSuppressions;

    /// <summary>当前连续处于 ≤0 的观测刻数（诊断视图）。</summary>
    internal int ConsecutiveLowTicks => consecutiveLowTicks;

    /// <summary>
    /// 持续健康写入方在写值前调用：仅当计划值与当前值确实不同时才发放抑制令牌。
    /// </summary>
    internal void SuppressNextIfHealthChanges(double currentHealth, double plannedHealth)
    {
        if (
            !double.IsFinite(currentHealth)
            || !double.IsFinite(plannedHealth)
            || currentHealth.Equals(plannedHealth)
        )
        {
            return;
        }

        if (pendingSuppressions >= MaxPendingSuppressions)
            return;
        pendingSuppressions++;
    }

    /// <summary>每刻观察一次本地玩家健康值；返回该次差分是否播音。</summary>
    internal StatusFeedbackHealthWatchDecision Observe(double currentHealth)
    {
        if (!double.IsFinite(currentHealth))
        {
            // 非法读数不作为基线，避免 NaN 污染下一次方向判定。
            return new StatusFeedbackHealthWatchDecision(
                StatusFeedbackHealthWatchAction.None,
                null
            );
        }

        var previous = lastObservedHealth;
        lastObservedHealth = currentHealth;
        if (previous is null || previous.Value.Equals(currentHealth))
        {
            if (currentHealth <= 0)
            {
                consecutiveLowTicks++;
            }
            return new StatusFeedbackHealthWatchDecision(
                StatusFeedbackHealthWatchAction.None,
                null
            );
        }

        var direction = currentHealth > previous.Value
            ? StatusFeedbackDirection.Up
            : StatusFeedbackDirection.Down;
        if (currentHealth <= 0)
        {
            // 进入/维持死亡态：这一刻本身就是离散伤害（播 down），随后累计死亡态计数。
            consecutiveLowTicks++;
            return ObserveAfterSuppressionCheck(direction);
        }

        // 恢复正值：先重置死亡态计数，再判定是否属于复活重置跳变。
        var wasSustainedDeath = consecutiveLowTicks >= DeathStateMinimumTicks;
        consecutiveLowTicks = 0;
        if (previous.Value <= 0 && wasSustainedDeath)
        {
            // 死亡复活重置（原版 killScreen 复活把 health 直接设为 10/上限 50%）：
            // 系统重置而非离散治疗，静音；不消耗抑制令牌（规则内建）。
            return new StatusFeedbackHealthWatchDecision(
                StatusFeedbackHealthWatchAction.Suppressed,
                direction
            );
        }

        return ObserveAfterSuppressionCheck(direction);
    }

    private StatusFeedbackHealthWatchDecision ObserveAfterSuppressionCheck(
        StatusFeedbackDirection direction
    )
    {
        if (pendingSuppressions > 0)
        {
            pendingSuppressions--;
            return new StatusFeedbackHealthWatchDecision(
                StatusFeedbackHealthWatchAction.Suppressed,
                direction
            );
        }

        return new StatusFeedbackHealthWatchDecision(
            StatusFeedbackHealthWatchAction.Play,
            direction
        );
    }

    /// <summary>世界卸载/回标题/换存档时重置；首个观察只建立基线，不播音。</summary>
    internal void Reset()
    {
        lastObservedHealth = null;
        pendingSuppressions = 0;
        consecutiveLowTicks = 0;
    }
}

/// <summary>
/// 全部本地玩家的健康差分与抑制状态表（纯规则）。键为玩家 UniqueMultiplayerID，
/// 兼容分屏：每个本机屏幕在各自 tick 内观察各自的 Game1.player。
/// </summary>
internal sealed class StatusFeedbackHealthWatchStore
{
    private readonly Dictionary<long, StatusFeedbackHealthWatchState> states =
        new();

    internal StatusFeedbackHealthWatchState GetOrCreate(long playerId)
    {
        if (!states.TryGetValue(playerId, out var state))
        {
            state = new StatusFeedbackHealthWatchState();
            states.Add(playerId, state);
        }
        return state;
    }

    internal bool TryGetState(long playerId, out StatusFeedbackHealthWatchState? state)
    {
        return states.TryGetValue(playerId, out state);
    }

    internal void Remove(long playerId)
    {
        states.Remove(playerId);
    }

    internal void Clear()
    {
        states.Clear();
    }
}
