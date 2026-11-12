#nullable enable

namespace DontStarve.Player.Sleep;

/// <summary>
/// Pure timing rules for replaying a wake-time death after a save load. The gate keeps the
/// persisted zero health intact while Stardew's load fade is still active; it never changes the
/// native kill-screen or recovery contract.
/// </summary>
internal static class WakeDeathLoadGatePolicy
{
    internal static bool ShouldEarlyArm(
        bool hasLoadedGame,
        bool worldReady,
        bool localPlayer,
        int health,
        bool killScreen,
        int timeOfDay
    )
    {
        return hasLoadedGame
            && !worldReady
            && localPlayer
            && health <= 0
            && !killScreen
            && timeOfDay < 2600;
    }

    internal static bool ShouldArm(
        bool markerPresent,
        int health,
        bool killScreen
    ) => markerPresent && health <= 0 && !killScreen;

    internal static bool ShouldRelease(
        bool armed,
        int health,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool killScreen
    )
    {
        if (!armed || killScreen || health > 0)
            return false;

        return worldReady && !isFading && !eventUp && !menuOpen;
    }

    internal static bool ShouldClear(
        bool armed,
        int health,
        bool killScreen
    ) => armed && (health > 0 || killScreen);
}
