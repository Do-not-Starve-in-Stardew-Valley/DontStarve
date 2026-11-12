#nullable enable

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Pure rule for the starving fishing-cast gate. A starving local player cannot start a new
/// cast; casts already in flight (bobber, bite, BobberBar) are never interrupted by hunger —
/// that boundary lives in SmapiHungerDamageExecutor (direct health writes) and must stay there.
/// </summary>
internal static class HungerFishingCastGatePolicy
{
    internal static bool ShouldBlock(
        bool isLocalPlayer,
        bool eventUp,
        bool hungerEnabled,
        bool starving
    )
    {
        // 剧情事件脚本可能要求玩家做动作，事件内永远放行避免软锁。
        if (!isLocalPlayer || eventUp)
            return false;
        // 远端玩家的挥杆在自己机器上由同一规则拦截；本机回放不拦，避免双端表现分叉。
        return hungerEnabled && starving;
    }
}
