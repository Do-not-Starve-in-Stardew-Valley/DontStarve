#nullable enable

using System;

namespace DontStarve.Player.Stats.Food;

internal static class StarfruitFoodRules
{
    internal const string ItemId = "434";

    internal static bool IsStarfruit(string? itemId)
    {
        return string.Equals(itemId, ItemId, StringComparison.Ordinal);
    }

    internal static double GetFillDelta(double current, double maximum)
    {
        if (!double.IsFinite(current) || !double.IsFinite(maximum) || maximum <= 0)
            return 0;

        var delta = maximum - current;
        return delta > 0 ? delta : 0;
    }
}
