#nullable enable

using System;

namespace DontStarve.Player.Stats.Hunger;

internal readonly record struct HungerWarningTransition(
    bool IsLow,
    bool ShouldWarn
);

internal static class HungerWarningRules
{
    internal static HungerWarningTransition Evaluate(
        double current,
        double maximum,
        bool wasInLowValueRegion
    )
    {
        if (!double.IsFinite(current) || !double.IsFinite(maximum) || maximum <= 0d)
            return new(false, false);

        var isLow = current < maximum / 3d;
        return new(isLow, isLow && !wasInLowValueRegion);
    }
}
