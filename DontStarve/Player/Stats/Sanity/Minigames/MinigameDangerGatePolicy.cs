#nullable enable

namespace DontStarve.Player.Stats.Sanity.Minigames;

internal enum MinigameDangerReason
{
    None,
    NearbyMonster,
    HostileShadowTargeting,
    MultiplayerDanger,
    MultiplayerStateUnavailable,
}

internal readonly record struct MinigameDangerDecision(
    bool Allowed,
    MinigameDangerReason Reason
);

/// <summary>
/// Pure precedence for the dangerous-minigame gate. The story exception is evaluated before the
/// config and world checks because Abigail's event must never be interrupted by this mod.
/// </summary>
internal static class MinigameDangerGatePolicy
{
    internal static MinigameDangerDecision Evaluate(
        bool blockingEnabled,
        bool storyException,
        bool nearbyMonster,
        bool hostileShadowTargeting,
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
