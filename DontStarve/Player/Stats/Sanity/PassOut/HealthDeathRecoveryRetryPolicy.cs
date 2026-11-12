#nullable enable

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Bounds the one-shot health-death recovery retry to the load transition that was observed
/// before SMAPI reported the world ready. It never creates a new death or changes vanilla health.
/// </summary>
internal static class HealthDeathRecoveryRetryPolicy
{
    internal static bool ShouldQueue(
        bool wasKillScreen,
        bool isKillScreen,
        int health,
        bool worldReady,
        bool isMainPlayer,
        bool isHost,
        bool sessionValid,
        bool farmerAvailable
    )
    {
        return wasKillScreen
            && !isKillScreen
            && health > 0
            && !worldReady
            && isMainPlayer
            && isHost
            && sessionValid
            && farmerAvailable;
    }

    internal static bool ShouldAttempt(
        bool pending,
        int health,
        bool isKillScreen,
        bool worldReady,
        bool isMainPlayer,
        bool isHost,
        bool sessionValid
    )
    {
        return pending
            && health > 0
            && !isKillScreen
            && worldReady
            && isMainPlayer
            && isHost
            && sessionValid;
    }
}
