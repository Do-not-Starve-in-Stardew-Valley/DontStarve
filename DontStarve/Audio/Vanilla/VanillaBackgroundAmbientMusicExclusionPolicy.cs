#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Audio.Vanilla;

/// <summary>
/// Identifies the narrow set of vanilla background ambience that music owners must not destroy.
/// This policy is intentionally pure so Sanity, Dawn, Dusk, and ActivityMusic share one boundary.
/// </summary>
internal static class VanillaBackgroundAmbientMusicExclusionPolicy
{
    private const string RainCueName = "rain";
    private static readonly HashSet<string> ExplicitBackgroundAmbientCueNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            RainCueName,
            "spring_day_ambient",
            "summer_day_ambient",
            "fall_day_ambient",
            "winter_day_ambient",
            "spring_night_ambient",
            "jungle_ambience",
            "pool_ambient",
            "movieScreenAmbience",
            "Upper_Ambient",
            "Frost_Ambient",
            "Lava_Ambient",
            "Volcano_Ambient",
            "Hospital_Ambient",
            "tropical_island_day_ambient",
        };

    internal static bool ShouldPreserve(
        string? cueName,
        bool isPlayingOutdoorsAmbience,
        bool isPlayingNightAmbience
    )
    {
        if (
            string.IsNullOrWhiteSpace(cueName)
            || string.Equals(cueName, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cueName, "silence", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return isPlayingOutdoorsAmbience
            || isPlayingNightAmbience
            || string.Equals(cueName, RainCueName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps vanilla's normal selector available only while it is selecting protected ambience.
    /// Ordinary music and a cleared request remain blocked by the owning suppression system.
    /// </summary>
    internal static bool ShouldAllowSuppressedUpdate(
        string? currentCueName,
        string? requestedCueName,
        bool isPlayingOutdoorsAmbience,
        bool isPlayingNightAmbience
    )
    {
        if (!ShouldPreserve(requestedCueName, isPlayingOutdoorsAmbience, isPlayingNightAmbience))
            return false;

        if (currentCueName is null)
            return IsExplicitBackgroundAmbientCueName(requestedCueName);

        // When both names are known background tracks, allow vanilla to perform its normal
        // ambience transition. Unknown names fail closed here so stale ambience flags can never
        // turn an ordinary music request into a suppression bypass.
        return IsExplicitBackgroundAmbientCueName(currentCueName)
            && IsExplicitBackgroundAmbientCueName(requestedCueName);
    }

    private static bool IsExplicitBackgroundAmbientCueName(string? cueName)
    {
        if (string.IsNullOrWhiteSpace(cueName))
            return false;

        return ExplicitBackgroundAmbientCueNames.Contains(cueName);
    }
}
