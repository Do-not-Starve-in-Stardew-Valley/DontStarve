#nullable enable

using System;

namespace DontStarve.Display;

internal readonly record struct HudPoint(int X, int Y);

internal readonly record struct HudBounds(int X, int Y, int Width, int Height)
{
    internal bool Contains(int x, int y)
    {
        return x >= X && x < X + Width && y >= Y && y < Y + Height;
    }
}

internal readonly record struct SanityHudFrame(
    HudBounds ContainerBounds,
    HudPoint FillerPosition,
    int FillerWidth,
    int FillerHeight,
    double Current,
    double Maximum
);

internal readonly record struct SanityDisplayVisibility(
    bool ShowBar,
    bool ShowHeldFoodTooltip
);

/// <summary>
/// 与 Stardew/XNA 解耦的 HUD 定位和 Sanity 数值门，供绘制层与确定性测试共用。
/// </summary>
internal static class HudDisplayRules
{
    private const int HudOffsetWithHealth = 171;
    private const int HudOffsetWithoutHealth = 116;
    private const int ContainerBottomOffset = 240;
    private const int FillerXOffset = 36;
    private const int FillerBottomOffset = 25;
    private const int FillerMaximumHeight = 168;

    internal static SanityDisplayVisibility ResolveSanityVisibility(
        bool systemEnabled
    )
    {
        // 总开关隐藏状态 HUD，但物品数据提示仍帮助玩家理解食物属性。
        return new SanityDisplayVisibility(systemEnabled, true);
    }

    internal static HudPoint GetBottomRightAnchor(
        int viewportWidth,
        int viewportHeight,
        bool showingHealth
    )
    {
        return new HudPoint(
            viewportWidth
                - (showingHealth ? HudOffsetWithHealth : HudOffsetWithoutHealth),
            viewportHeight
        );
    }

    internal static bool TryCreateSanityFrame(
        HudPoint anchor,
        int containerWidth,
        int containerHeight,
        int fillerWidth,
        double current,
        double maximum,
        out SanityHudFrame frame,
        out string reason
    )
    {
        frame = default;
        if (containerWidth <= 0 || containerHeight <= 0 || fillerWidth <= 0)
        {
            reason = "sanity-hud-layout-dimensions-are-invalid";
            return false;
        }
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

        var ratio = current / maximum;
        var fillerHeight = Math.Clamp(
            (int)(ratio * FillerMaximumHeight),
            0,
            FillerMaximumHeight
        );
        var containerBounds = new HudBounds(
            anchor.X,
            anchor.Y - ContainerBottomOffset,
            containerWidth,
            containerHeight
        );
        frame = new SanityHudFrame(
            containerBounds,
            new HudPoint(anchor.X + FillerXOffset, anchor.Y - FillerBottomOffset),
            fillerWidth,
            fillerHeight,
            current,
            maximum
        );
        reason = "sanity-hud-frame-ready";
        return true;
    }
}
