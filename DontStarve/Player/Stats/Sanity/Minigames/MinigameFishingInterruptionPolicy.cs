#nullable enable

namespace DontStarve.Player.Stats.Sanity.Minigames;

/// <summary>
/// Pure gate for the multiplayer fishing interruption. Waiting for a bite has no BobberBar, and
/// a result which already entered vanilla's terminal fade must not be rewritten by a later hit.
/// </summary>
internal static class MinigameFishingInterruptionPolicy
{
    internal static bool ShouldInterrupt(
        bool isMultiplayer,
        bool blockingEnabled,
        bool hasBobberBar,
        bool bobberBarResultHandled,
        bool damageSourceIsMonster,
        int healthBefore,
        int healthAfter
    )
    {
        if (
            !isMultiplayer
            || !blockingEnabled
            || !hasBobberBar
            || bobberBarResultHandled
            || !damageSourceIsMonster
        )
        {
            return false;
        }

        return healthAfter < healthBefore;
    }
}
