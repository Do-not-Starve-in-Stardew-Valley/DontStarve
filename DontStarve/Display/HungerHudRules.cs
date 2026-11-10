#nullable enable

using System;

namespace DontStarve.Display;

internal static class HungerHudRules
{
    internal static bool TryCreateValues(
        double current,
        double maximum,
        out HudBarValues values,
        out string reason
    )
    {
        values = default;
        if (!double.IsFinite(current))
        {
            reason = "hunger-hud-current-is-not-finite";
            return false;
        }
        if (!double.IsFinite(maximum))
        {
            reason = "hunger-hud-maximum-is-not-finite";
            return false;
        }
        if (maximum <= 0)
        {
            reason = "hunger-hud-maximum-is-not-positive";
            return false;
        }

        // Hunger 运行时可能短暂低于零；仅 HUD 显示值钳制到零。
        var boundedCurrent = Math.Max(0d, current);
        if (boundedCurrent > maximum)
        {
            reason = "hunger-hud-current-is-out-of-range";
            return false;
        }

        values = new HudBarValues(
            boundedCurrent,
            maximum,
            boundedCurrent / maximum
        );
        reason = "hunger-hud-values-ready";
        return true;
    }
}
