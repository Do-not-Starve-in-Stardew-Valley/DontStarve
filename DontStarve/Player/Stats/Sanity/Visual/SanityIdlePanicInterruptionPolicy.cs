#nullable enable

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Decides whether cancelling the panic loop may reset the player's current animation.
/// Vanilla has already taken ownership of the sprite for these action signals; calling
/// Farmer.completelyStopAnimatingOrDoingAction after them would cancel the action itself.
/// </summary>
internal static class SanityIdlePanicInterruptionPolicy
{
    internal const string UsingToolReason = "using-tool";
    internal const string UsingSlingshotReason = "using-slingshot";
    internal const string EatingReason = "eating";

    internal static bool ShouldReleasePlayerAnimation(string reason)
    {
        // 未知原因默认释放自有动画，避免新边界把 PauseForSingleAnimation 留在玩家身上；
        // 新增会接管精灵的原版动作时，必须在这里登记其原因码并补对应测试。
        return reason != UsingToolReason
            && reason != UsingSlingshotReason
            && reason != EatingReason;
    }
}
