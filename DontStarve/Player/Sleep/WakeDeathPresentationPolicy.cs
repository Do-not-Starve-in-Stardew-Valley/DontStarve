#nullable enable

namespace DontStarve.Player.Sleep;

internal enum WakeDeathPresentationMode
{
    Natural,
    HiddenHospital,
}

/// <summary>
/// Pure gates for deaths which are discovered at the wake boundary. Natural hunger deaths let
/// Farmer.Update own the vanilla bed-death presentation; hidden hospital deaths hold that native
/// branch until the settled morning can hand off to the vanilla hospital warp path.
/// </summary>
internal static class WakeDeathPresentationPolicy
{
    internal static bool ShouldHoldNativeDeath(
        WakeDeathPresentationMode mode,
        bool markerPresent,
        int health,
        bool killScreen
    )
    {
        return mode == WakeDeathPresentationMode.HiddenHospital
            && markerPresent
            && health <= 0
            && !killScreen;
    }

    internal static bool ShouldReleaseHiddenHospital(
        bool markerPresent,
        int health,
        bool killScreen,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool newDay,
        bool messageAfterPauseEmpty
    )
    {
        return markerPresent
            && health <= 0
            && !killScreen
            && worldReady
            && !isFading
            && !eventUp
            && !menuOpen
            && !newDay
            && messageAfterPauseEmpty;
    }

}
