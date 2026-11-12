#nullable enable

using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// 变更来源 → discrete/continuous 的唯一分类表（纯规则、可测试）。
/// 语义对齐饥荒原版：只有 not-overtime 的离散增减才播 HUD 音效，持续增减静音。
/// </summary>
/// <remarks>
/// 分类默认值是 open（未知/新增来源 → Discrete，自动播音）。这是有意为之的硬约定：
/// 以后新增一次性增减效果不需要在本文件接线就会响。因此任何"持续生效"的新来源
/// 必须显式加入下方 continuous 清单，否则会被误播。
/// 系统重置类来源（存档迁移、调试命令、死亡复活重置）与持续来源同列静音：
/// 它们不是玩家可感知的离散增减事件，而是把数值设成指定恢复值的系统行为。
/// </remarks>
internal static class StatusFeedbackTriggerPolicy
{
    /// <summary>
    /// Sanity 变更来源分类。continuous 清单逐条对应：
    /// Equipment=Wearing 每分钟装备影响；Npc/Junimo=NearNpc 每分钟邻近影响；
    /// Monster=NearMonster 每分钟邻近恐惧；Night=每分钟黑暗侵蚀；Mine=矿洞每分钟影响；
    /// Sleep=普通睡眠结算（DST 睡觉=overtime 语义）；Buff=Buff 周期脉冲；
    /// VoluntarySleep=自愿睡眠结算；BathHouse=澡堂每分钟恢复；
    /// Migration=存档迁移批量写入；Administration=调试/管理命令；
    /// HealthDeath=真死亡复活重置（PassOutReasonPolicy.ResolveHealthDeathRecovery 把
    /// 理智 Set 为上限 50%，是系统重置而非离散增减；2AM 特殊死亡 DefaultDeath
    /// 分支共享同一复活边界）。
    /// 其余（Food/Garbage/FlowerCollection/DarknessAttack/HostileShadowKill/
    /// TimeLimitPassOut/ExhaustionPassOut/SanityDarknessSpecialDeath 及未来新增）
    /// 均为一次性事件 → Discrete。
    /// </summary>
    internal static StatusFeedbackTriggerKind Classify(SanityChangeSource source)
    {
        return source switch
        {
            // —— 明确持续来源（静音）——
            SanityChangeSource.Equipment
                or SanityChangeSource.Npc
                or SanityChangeSource.Junimo
                or SanityChangeSource.Monster
                or SanityChangeSource.Night
                or SanityChangeSource.Mine
                or SanityChangeSource.Sleep
                or SanityChangeSource.Buff
                or SanityChangeSource.VoluntarySleep
                or SanityChangeSource.BathHouse
                or SanityChangeSource.Migration
                or SanityChangeSource.Administration

                // —— 系统重置（静音，与 Migration/Administration 同类）——
                or SanityChangeSource.HealthDeath
                => StatusFeedbackTriggerKind.Continuous,

            // —— 明确离散来源（播音）——
            SanityChangeSource.Food
                or SanityChangeSource.Garbage
                or SanityChangeSource.FlowerCollection
                or SanityChangeSource.DarknessAttack
                or SanityChangeSource.HostileShadowKill
                or SanityChangeSource.TimeLimitPassOut
                or SanityChangeSource.ExhaustionPassOut
                or SanityChangeSource.SanityDarknessSpecialDeath
                => StatusFeedbackTriggerKind.Discrete,

            // 未知/未来新增来源默认按离散处理（open 默认，见类型注释）。
            _ => StatusFeedbackTriggerKind.Discrete,
        };
    }

    /// <summary>
    /// Hunger 结算分类：只有进食是离散事件；每分钟衰减、睡眠期间扣饥饿、调试写入
    /// 与死亡复活重置（Revive，ApplyHealthDeathRecovery 把饥饿 Set 为复活值——系统
    /// 重置而非离散增减）都属于静音侧。Unknown 走 open 默认（Discrete）。
    /// </summary>
    internal static StatusFeedbackTriggerKind Classify(HungerChangeKind kind)
    {
        return kind switch
        {
            HungerChangeKind.Overtime
                or HungerChangeKind.Debug
                or HungerChangeKind.Revive
                => StatusFeedbackTriggerKind.Continuous,

            HungerChangeKind.Food
                => StatusFeedbackTriggerKind.Discrete,

            _ => StatusFeedbackTriggerKind.Discrete,
        };
    }
}
