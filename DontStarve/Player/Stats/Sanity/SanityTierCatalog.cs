#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

internal static class SanityTierIds
{
    internal const string MrSkitts = "mr-skitts";
    internal const string DarkHand = "dark-hand";
    internal const string DarkWatcher = "dark-watcher";
    internal const string Eyes = "eyes";
    internal const string ShadowCreatures = "shadow-creatures";
    internal const string Whispers = "whispers";
    internal const string BeardRabbit = "beard-rabbit";
    internal const string Danger = "danger";
    internal const string Terrorbeak = "terrorbeak";
}

internal static class SanityStateEventIds
{
    internal const string SystemEnabled = "sanity/system/enabled";
    internal const string SystemDisabled = "sanity/system/disabled";
    internal const string OwnerInvalidated = "sanity/owner/invalidated";
    internal const string WorldCleanup = "sanity/world/cleanup";

    internal static string TierEntered(string tierId)
    {
        return string.Concat("sanity/tier/", tierId, "/entered");
    }

    internal static string TierExited(string tierId)
    {
        return string.Concat("sanity/tier/", tierId, "/exited");
    }
}

internal sealed class SanityTierRule
{
    /// <summary>
    /// 边界口径（对齐 DST 本体"卡线归更疯一侧"的习惯）：
    /// enterInclusive=true 表示恰好等于进入线也算进入；exitInclusive=true 表示恰好等于
    /// 恢复线即退出（仅滞回 tier 使用）。默认对称 tier：进入含边界、退出严格大于
    /// （活跃带为 [0, exit]，恰好等于阈值时保持活跃），与 DST 影怪种群、暗影之手一致。
    /// </summary>
    internal SanityTierRule(
        string id,
        double enterRatio,
        double exitRatio,
        bool enterInclusive = true,
        bool exitInclusive = false
    )
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Tier IDs must be non-empty.", nameof(id));
        if (
            !double.IsFinite(enterRatio)
            || !double.IsFinite(exitRatio)
            || enterRatio < 0
            || enterRatio > 1
            || exitRatio < enterRatio
            || exitRatio > 1
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(enterRatio),
                "Tier ratios must satisfy 0 <= enter <= exit <= 1."
            );
        }

        Id = id;
        EnterRatio = enterRatio;
        ExitRatio = exitRatio;
        EnterInclusive = enterInclusive;
        ExitInclusive = exitInclusive;
        EnteredEventId = SanityStateEventIds.TierEntered(id);
        ExitedEventId = SanityStateEventIds.TierExited(id);
    }

    internal string Id { get; }

    internal double EnterRatio { get; }

    internal double ExitRatio { get; }

    internal bool EnterInclusive { get; }

    internal bool ExitInclusive { get; }

    internal string EnteredEventId { get; }

    internal string ExitedEventId { get; }
}

/// <summary>
/// 4.0 唯一百分比阈值表。ID 与事件名不包含数值，后续平衡调整只能改规则数据。
/// 边界口径对齐 DST 本体：卡线归更疯一侧（进入含边界），danger 的恢复线含边界，
/// terrorbeak 是唯一严格进入的例外（DST 恐怖喙概率本身是严格 <10%）。
/// </summary>
internal static class SanityTierCatalog
{
    private static readonly IReadOnlyList<SanityTierRule> FrozenRules =
        Array.AsReadOnly(
            new[]
            {
                new SanityTierRule(SanityTierIds.MrSkitts, 0.835d, 0.835d),
                new SanityTierRule(SanityTierIds.DarkHand, 0.75d, 0.75d),
                new SanityTierRule(SanityTierIds.DarkWatcher, 0.65d, 0.65d),
                new SanityTierRule(SanityTierIds.Eyes, 0.6d, 0.6d),
                new SanityTierRule(SanityTierIds.ShadowCreatures, 0.5d, 0.5d),
                new SanityTierRule(SanityTierIds.Whispers, 0.45d, 0.45d),
                new SanityTierRule(SanityTierIds.BeardRabbit, 0.4d, 0.4d),
                // danger 是唯一有数值滞回的 tier：≤15% 进入，≥17.5% 才退出（DST 疯狂标志口径）。
                new SanityTierRule(
                    SanityTierIds.Danger,
                    0.15d,
                    0.175d,
                    enterInclusive: true,
                    exitInclusive: true
                ),
                // terrorbeak 唯一严格进入：恰好 10% 不出恐怖喙（DST SpawnLandShadowCreature 同款严格 <）。
                new SanityTierRule(
                    SanityTierIds.Terrorbeak,
                    0.1d,
                    0.1d,
                    enterInclusive: false,
                    exitInclusive: false
                ),
            }
        );

    internal static IReadOnlyList<SanityTierRule> Rules => FrozenRules;
}
