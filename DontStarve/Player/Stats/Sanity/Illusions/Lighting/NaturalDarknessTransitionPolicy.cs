#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Pure real-time transition policy for the natural outdoor darkness override. This deliberately
/// owns no Stardew state so the timing contract can be tested without a live game.
/// </summary>
internal static class NaturalDarknessTransitionPolicy
{
    internal const double NightTransitionDurationSeconds = 3d;
    internal const double MineDuskTransitionDurationSeconds = 2d;
    internal const double MineNightTransitionDurationSeconds = 1d;
    internal const double MineDuskLightFraction = 0.70d;
    internal const double MineDuskWhiteBlend = 1d - MineDuskLightFraction;

    internal static double Advance(
        double startingProgress,
        double targetProgress,
        double elapsedRealSeconds,
        double transitionDurationSeconds
    )
    {
        var start = ClampProgress(startingProgress);
        var target = ClampProgress(targetProgress);
        if (target <= start)
            return target;
        if (!double.IsFinite(elapsedRealSeconds) || elapsedRealSeconds <= 0d)
            return start;
        if (
            !double.IsFinite(transitionDurationSeconds)
            || transitionDurationSeconds <= 0d
        )
        {
            return target;
        }

        return ClampProgress(
            start
                + (target - start)
                    * Math.Clamp(
                        elapsedRealSeconds / transitionDurationSeconds,
                        0d,
                        1d
                    )
        );
    }

    internal static float GetLightmapWhiteBlend(double progress)
    {
        return (float)ClampProgress(progress);
    }

    internal static double GetWarpArrivalProgress(NaturalDarknessScenePlan plan)
    {
        return plan.IsActive ? ClampProgress(plan.TargetWhiteBlend) : 0d;
    }

    private static double ClampProgress(double value)
    {
        if (!double.IsFinite(value))
            return 0d;

        return Math.Clamp(value, 0d, 1d);
    }
}
