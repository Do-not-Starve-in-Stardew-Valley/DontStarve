#nullable enable

namespace DontStarve.Player.Stats.Sanity.Minigames;

internal enum MinigameDangerReason
{
    None,
    NearbyMonster,
    HostileShadowTargeting,
    MultiplayerDanger,
    MultiplayerStateUnavailable,
    // 枚举值被策略测试按数值断言，新增危险来源只能追加在末尾，不能插入中间。
    Starving,
}

internal readonly record struct MinigameDangerDecision(
    bool Allowed,
    MinigameDangerReason Reason
);

/// <summary>
/// Pure precedence for the dangerous-minigame gate. The story exception is evaluated before the
/// config and world checks because Abigail's event must never be interrupted by this mod.
/// Danger sources: nearby vanilla monster, hostile shadow targeting, starvation (hunger 0 while
/// the Hunger system is enabled — the caller owns that enabled-boundary), then multiplayer
/// fail-closed state.
/// </summary>
internal static class MinigameDangerGatePolicy
{
    internal static MinigameDangerDecision Evaluate(
        bool blockingEnabled,
        bool storyException,
        bool nearbyMonster,
        bool hostileShadowTargeting,
        bool starving,
        bool isMultiplayer,
        bool multiplayerDanger,
        bool multiplayerStateAvailable
    )
    {
        if (storyException || !blockingEnabled)
            return new MinigameDangerDecision(true, MinigameDangerReason.None);
        if (nearbyMonster)
            return new MinigameDangerDecision(false, MinigameDangerReason.NearbyMonster);
        if (hostileShadowTargeting)
        {
            return new MinigameDangerDecision(
                false,
                MinigameDangerReason.HostileShadowTargeting
            );
        }
        // 饥饿 0 是持续掉血的危险期；不拦截的话濒死玩家可以用小游戏挂机规避伤害。
        if (starving)
            return new MinigameDangerDecision(false, MinigameDangerReason.Starving);
        if (isMultiplayer && !multiplayerStateAvailable)
        {
            return new MinigameDangerDecision(
                false,
                MinigameDangerReason.MultiplayerStateUnavailable
            );
        }
        if (isMultiplayer && multiplayerDanger)
        {
            return new MinigameDangerDecision(false, MinigameDangerReason.MultiplayerDanger);
        }
        return new MinigameDangerDecision(true, MinigameDangerReason.None);
    }
}
