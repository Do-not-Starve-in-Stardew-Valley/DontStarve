namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Keeps the single-player menu pause separate from multiplayer and game-time pause semantics.
/// Runtime adapters supply the live Stardew signals; this policy stays deterministic for tests.
/// </summary>
internal static class HostileShadowGameplayPausePolicy
{
    internal static bool IsHardPaused(bool menuOpen, bool isMultiplayer)
    {
        return menuOpen && !isMultiplayer;
    }

    internal static bool IsBehaviorFrozen(
        bool menuOpen,
        bool isMultiplayer,
        bool gamePaused,
        bool gameActive
    )
    {
        return gamePaused
            || !gameActive
            || IsHardPaused(menuOpen, isMultiplayer);
    }
}
