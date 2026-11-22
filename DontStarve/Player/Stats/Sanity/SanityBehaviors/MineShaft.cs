#nullable enable

using DontStarve.Config;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据矿洞、火山和危险层级降低理智；只影响当前所在地点。
/// 「自然理智流失规则」选择 ByLight/Off 时不生效（2026-11-17）；配置缺失/损坏/值非法
/// 回落默认 ByLight（同 GMCM 默认），即只有玩家显式选 TimePlace 才走本行为。
/// </summary>
internal class MineShaft : IPlayerTimeRelatedBehavior
{
    private readonly TypedConfigResolver? configResolver;

    internal MineShaft(TypedConfigResolver? configResolver = null)
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

        double value;
        if (location is MineShaftLocation mineShaft)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.MineShaft,
                    mineShaft.mineLevel,
                    mineShaft.isDarkArea(),
                    mineShaft.isQuarryArea,
                    mineShaft.isSlimeArea,
                    mineShaft.isMonsterArea,
                    mineShaft.isDinoArea,
                    mineShaft.GetAdditionalDifficulty()
                )
            );
        }
        else if (location is VolcanoDungeon)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.VolcanoDungeon,
                    0,
                    false,
                    false,
                    false,
                    false,
                    false,
                    0
                )
            );
        }
        else
        {
            value = 0;
        }

        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Mine);
    }

}
