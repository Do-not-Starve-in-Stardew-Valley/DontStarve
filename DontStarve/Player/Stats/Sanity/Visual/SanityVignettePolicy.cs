#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal enum SanityVignetteMode
{
    Hidden,
    Basic,
    Insane,
}

/// <summary>
/// Keeps the two player-facing controls independent: the vignette toggle owns ordinary dark
/// corners, while the existing low-Sanity filter owns the animated insane state below 15%.
/// </summary>
internal static class SanityVignettePolicy
{
    internal const double InsaneThreshold = 0.15d;

    internal static SanityVignetteMode Resolve(
        bool lowSanityFilterEnabled,
        bool vignetteEnabled,
        double sanityRatio
    )
    {
        if (
            !double.IsFinite(sanityRatio)
            || sanityRatio < 0d
            || sanityRatio > 1d
        )
        {
            return SanityVignetteMode.Hidden;
        }

        if (lowSanityFilterEnabled && sanityRatio <= InsaneThreshold)
            return SanityVignetteMode.Insane;

        return vignetteEnabled
            ? SanityVignetteMode.Basic
            : SanityVignetteMode.Hidden;
    }
}
