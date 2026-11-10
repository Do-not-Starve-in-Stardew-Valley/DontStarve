#nullable enable

using DontStarve.Player.Stats;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 在原版温泉水中按内部分钟时间服务的每个刻度恢复理智。
/// </summary>
internal sealed class BathHouse : IPlayerTimeRelatedBehavior
{
    public void Update(Farmer player, string playerKey, long time)
    {
        var recovery = SanityBehaviorRules.CalculateBathHouseRecovery(
            player.currentLocation is BathHousePool,
            player.swimming.Value
        );
        if (recovery <= 0d)
        {
            return;
        }

        player.ChangeSanity(recovery, SanityChangeSource.BathHouse);
    }
}
