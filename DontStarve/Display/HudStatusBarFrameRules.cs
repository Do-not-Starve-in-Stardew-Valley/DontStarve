#nullable enable

using System;

namespace DontStarve.Display;

internal readonly record struct HudBarValues(
    double Current,
    double Maximum,
    double FillRatio
);

internal readonly record struct HudStatusBarFrame(
    HudBounds ContainerBounds,
    HudBounds HitBounds,
    HudPoint FillerPosition,
    int FillerWidth,
    int FillerHeight
);

/// <summary>提供者先校验并规范各自数值；此规则只处理通用填充几何和命中边界。</summary>
internal static class HudStatusBarFrameRules
{
    internal const int FillerXOffset = 36;
    internal const int FillerBottomOffset = 9;
    internal const int FillerMaximumHeight = 168;

    internal static bool TryCreateFrame(
        HudBounds containerBounds,
        int fillerWidth,
        double fillRatio,
        out HudStatusBarFrame frame,
        out string reason
    )
    {
        frame = default;
        if (
            containerBounds.Width <= 0
            || containerBounds.Height <= 0
            || fillerWidth <= 0
        )
        {
            reason = "hud-frame-layout-dimensions-are-invalid";
            return false;
        }
        if (!double.IsFinite(fillRatio) || fillRatio < 0 || fillRatio > 1)
        {
            reason = "hud-frame-fill-ratio-is-invalid";
            return false;
        }

        frame = new HudStatusBarFrame(
            containerBounds,
            containerBounds,
            new HudPoint(
                containerBounds.X + FillerXOffset,
                containerBounds.Bottom - FillerBottomOffset
            ),
            fillerWidth,
            Math.Clamp(
                (int)(fillRatio * FillerMaximumHeight),
                0,
                FillerMaximumHeight
            )
        );
        reason = "hud-frame-ready";
        return true;
    }
}
