#nullable enable

using StardewValley;

namespace DontStarve.Player.Stats;

/// <summary>
/// Sanity 内部按单个玩家计算的分钟规则。玩家分钟的去重与阶段由
/// <see cref="SurvivalStateTimeOwner{TPlayer}"/> 统一管理。
/// </summary>
internal interface IPlayerTimeRelatedBehavior
{
    void Init(StardewModdingAPI.IModHelper helper) { }

    void Update(Farmer player, string playerKey, long time);
}
