#nullable enable

using System;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 按季节日落时间计算夜间理智损耗；午夜到清晨户外损耗更高。
/// </summary>
internal class Night : IPlayerTimeRelatedBehavior
{
    public void Update(Farmer player, string playerKey, long time)
    {
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
