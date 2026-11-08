#nullable enable

namespace DontStarve.Player.Stats.Sanity.Audio;

internal enum SanityInsanityTransition
{
    NoChange,
    Entered,
    Exited,
}

/// <summary>
/// Pure DST-compatible Insane hysteresis. It models the edge that starts the activity-music
/// cooldown; it does not mutate the project's general Sanity tier state.
/// </summary>
internal static class SanityInsanityEdgePolicy
{
    internal const double EnterRatio = 0.15d;
    internal const double ExitRatio = 0.175d;
    internal const double SuppressionSeconds = 15d;

    internal static SanityInsanityTransition Observe(
        bool currentlyInsane,
        double ratio
    )
    {
        if (!double.IsFinite(ratio) || ratio < 0d || ratio > 1d)
            return SanityInsanityTransition.NoChange;

        if (!currentlyInsane && ratio <= EnterRatio)
            return SanityInsanityTransition.Entered;
        if (currentlyInsane && ratio >= ExitRatio)
            return SanityInsanityTransition.Exited;
        return SanityInsanityTransition.NoChange;
    }
}
