#nullable enable

using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据矿洞、火山和危险层级降低理智；只影响当前所在地点。
/// </summary>
internal class MineShaft : IPlayerTimeRelatedBehavior
{
    public void Update(Farmer player, string playerKey, long time)
    {
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
