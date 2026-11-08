#nullable enable

using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats;

/// <summary>
/// Sanity 等需要对每位玩家分别执行的分钟行为。
/// ITimeAPI 仍只发布全局分钟；玩家维度在消费边界显式传入，避免依赖 Game1.player。
/// </summary>
internal interface IPlayerTimeRelatedBehavior
{
    void Init(IModHelper helper) { }

    void Update(Farmer player, long time);

    void Sync(string playerKey, long time, long delta);

    void Load(IModHelper helper);

    void Save(IModHelper helper);

    void Reset();
}

internal static class PlayerTimeRelatedBehaviorPlayer
{
    internal static bool TryGetPlayerKey(Farmer? player, out string playerKey)
    {
        if (player is null)
        {
            playerKey = string.Empty;
            return false;
        }

        playerKey = Sanity.SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        return Sanity.SanityPlayerKey.IsCanonical(playerKey);
    }
}
