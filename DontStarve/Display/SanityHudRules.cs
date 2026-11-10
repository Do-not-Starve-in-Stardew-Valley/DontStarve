#nullable enable

using System;

namespace DontStarve.Display;

internal readonly record struct SanityDisplayVisibility(
    bool ShowBar,
    bool ShowHeldFoodTooltip
);

internal static class SanityHudRules
{
    internal static SanityDisplayVisibility ResolveVisibility(bool systemEnabled)
    {
        // 总开关只隐藏理智条；食物提示中的理智数据独立控制。
        return new SanityDisplayVisibility(systemEnabled, true);
    }

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
            reason = "sanity-hud-current-is-not-finite";
            return false;
        }
        if (!double.IsFinite(maximum))
        {
            reason = "sanity-hud-maximum-is-not-finite";
            return false;
        }
        if (maximum <= 0)
        {
            reason = "sanity-hud-maximum-is-not-positive";
            return false;
        }
        if (current < 0 || current > maximum)
        {
            reason = "sanity-hud-current-is-out-of-range";
            return false;
        }

        values = new HudBarValues(current, maximum, current / maximum);
        reason = "sanity-hud-values-ready";
        return true;
    }
}
