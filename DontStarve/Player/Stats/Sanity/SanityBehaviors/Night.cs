#nullable enable

using System;
using DontStarve.Config;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 按季节日落时间计算夜间理智损耗；午夜到清晨户外损耗更高。
/// 「自然理智流失规则」选择 ByLight/Off 时不生效（2026-11-17）；配置缺失/损坏/值非法
/// 回落默认 ByLight（同 GMCM 默认），即只有玩家显式选 TimePlace 才走本行为。
/// </summary>
internal class Night : IPlayerTimeRelatedBehavior
{
    private readonly TypedConfigResolver? configResolver;

    internal Night(TypedConfigResolver? configResolver = null)
    {
        this.configResolver = configResolver;
    }

    public void Update(Farmer player, string playerKey, long time)
    {
        var ruleResolution = configResolver?.GetEnum(
            ConfigKeys.SanityNaturalDrainRule
        );
        var ruleText = ruleResolution is { HasValue: true }
            ? ruleResolution.Value.Value
            : null;
        if (
            SanityNaturalDrainRulePolicy.ResolveOrDefault(ruleText)
            != SanityNaturalDrainRule.TimePlace
        )
        {
            return;
        }

        var location = player.currentLocation;
        if (location == null)
            return;

        var locationKind = location switch
        {
            MineShaftLocation => MineSanityLocationKind.MineShaft,
            VolcanoDungeon => MineSanityLocationKind.VolcanoDungeon,
            _ => MineSanityLocationKind.Other,
        };
        if (!SanityBehaviorRules.ShouldApplyNightLoss(locationKind))
            return;

        // Stardew 不同季节天黑时间不同；这里按 1 分钟粒度拆分“日落后”和“午夜后”两段损耗。
        var season = Game1.season switch
        {
            Season.Spring => SanitySeason.Spring,
            Season.Summer => SanitySeason.Summer,
            Season.Fall => SanitySeason.Fall,
            Season.Winter => SanitySeason.Winter,
            _ => throw new InvalidOperationException("unknown-season"),
        };
        var nightfallStart = SanityBehaviorRules.GetNightfallStartMinutes(season);

        var value = SanityBehaviorRules.CalculateNightLossForMinute(
            time,
            nightfallStart,
            location.IsOutdoors
        );
        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Night);
    }

}
