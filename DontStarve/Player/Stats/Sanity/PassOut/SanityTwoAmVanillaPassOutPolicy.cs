#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal static class SanityTwoAmVanillaPassOutPolicy
{
    internal static int ResolveNonLethalWakeHealth(
        int capturedHealth,
        int maximumHealth
    )
    {
        if (maximumHealth <= 0)
            return 0;
        var safeCaptured = Math.Clamp(capturedHealth, 0, maximumHealth);
        var floor = (int)Math.Ceiling(maximumHealth * 0.2d);
        return Math.Min(safeCaptured, floor);
    }
}
