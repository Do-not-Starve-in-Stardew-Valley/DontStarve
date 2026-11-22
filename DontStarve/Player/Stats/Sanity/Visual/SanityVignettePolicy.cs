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
/// corners, while the existing low-Sanity filter owns the animated insane state entered below
/// 15%. Once the authoritative Danger tier has entered, it keeps that state through the tier's
/// 17.5% exit line.
/// </summary>
internal static class SanityVignettePolicy
{
    internal const double InsaneThreshold = 0.15d;

    internal static SanityVignetteMode Resolve(
        bool lowSanityFilterEnabled,
        bool vignetteEnabled,
        double sanityRatio,
        bool dangerTierActive = false
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

        // Do not recompute Danger's exit line here. The tier state machine owns the
        // ≤15% entry / ≥17.5% exit hysteresis; this policy only consumes its active receipt.
        // The ratio fallback is inclusive at 15% so the exact boundary behaves like DST's
        // insane flag even without a tier receipt.
        if (lowSanityFilterEnabled && (dangerTierActive || sanityRatio <= InsaneThreshold))
            return SanityVignetteMode.Insane;

        return vignetteEnabled
            ? SanityVignetteMode.Basic
            : SanityVignetteMode.Hidden;
    }
}
