#nullable enable

using System;

namespace DontStarve.Display;

internal readonly record struct HudPoint(int X, int Y);

internal readonly record struct HudBounds(int X, int Y, int Width, int Height)
{
    internal int Right => X + Width;
    internal int Bottom => Y + Height;

    internal bool Contains(int x, int y)
    {
        return x >= X && x < Right && y >= Y && y < Bottom;
    }
}

internal readonly record struct VanillaHudLayout(
    HudBounds EnergyBounds,
    HudBounds HealthBounds,
    bool ShowingHealth
)
{
    internal HudBounds AnchorBounds => ShowingHealth ? HealthBounds : EnergyBounds;
}

internal readonly record struct HudDisplayLayout(
    VanillaHudLayout Vanilla,
    HudBounds HungerBounds,
    HudBounds SanityBounds,
    bool ShowingSanity
)
{
    internal HudBounds AnchorBounds => Vanilla.AnchorBounds;
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
    // 这些尺寸来自 Stardew 1.6.15 drawHUD 的状态条：右边距 8、底边距 16、条宽 48、条高 224。
    // DS 容器也保持 48x224；布局只计算相对原版条的位置，不保存屏幕坐标。
    internal const int VanillaStatusBarWidth = 48;
    internal const int VanillaStatusBarHeight = 224;
    internal const int VanillaStatusBarRightInset = 8;
    internal const int VanillaStatusBarBottomInset = 16;
    internal const int VanillaStatusBarGap = 8;
    internal const int DontStarveBarGap = 12;

    internal const int FillerXOffset = 36;
    internal const int FillerBottomOffset = 9;
    internal const int FillerMaximumHeight = 168;

    internal static SanityDisplayVisibility ResolveSanityVisibility(
        bool systemEnabled
    )
    {
        // 总开关只隐藏 Sanity 自己的 HUD；物品数据提示仍由 formatter 单独控制。
        return new SanityDisplayVisibility(systemEnabled, true);
    }

    internal static HudDisplayLayout CreateLayout(
        int viewportWidth,
        int viewportHeight,
        bool showingHealth,
        bool showingSanity,
        int maxHealth,
        int maxStamina,
        int customBarWidth,
        int customBarHeight,
        int customBarGap = DontStarveBarGap
    )
    {
        var vanilla = CreateVanillaLayout(
            viewportWidth,
            viewportHeight,
            showingHealth,
            maxHealth,
            maxStamina
        );
        var anchor = vanilla.AnchorBounds;
        // Keep the existing slot geometry: right-to-left is vanilla Energy, vanilla Health,
        // Sanity, then Hunger. When Sanity is hidden, Hunger occupies the first custom slot.
        var sanityBounds = PlaceCustomBar(
            anchor,
            customBarWidth,
            customBarHeight,
            customBarGap
        );
        var hungerBounds = showingSanity
            ? PlaceCustomBar(
                sanityBounds,
                customBarWidth,
                customBarHeight,
                customBarGap
            )
            : sanityBounds;

        return new HudDisplayLayout(
            vanilla,
            hungerBounds,
            sanityBounds,
            showingSanity
        );
    }

    internal static HudPoint GetFillerPosition(HudBounds containerBounds)
    {
        return new HudPoint(
            containerBounds.X + FillerXOffset,
            containerBounds.Bottom - FillerBottomOffset
        );
    }

    internal static int GetFillerHeight(double current, double maximum)
    {
        if (!double.IsFinite(current) || !double.IsFinite(maximum) || maximum <= 0)
            return 0;

        var ratio = Math.Clamp(current / maximum, 0d, 1d);
        return Math.Clamp(
            (int)(ratio * FillerMaximumHeight),
            0,
            FillerMaximumHeight
        );
    }

    internal static bool TryCreateSanityFrame(
        HudBounds containerBounds,
        int fillerWidth,
        double current,
        double maximum,
        out SanityHudFrame frame,
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

        frame = new SanityHudFrame(
            containerBounds,
            GetFillerPosition(containerBounds),
            fillerWidth,
            GetFillerHeight(current, maximum),
            current,
            maximum
        );
        reason = "sanity-hud-frame-ready";
        return true;
    }

    private static VanillaHudLayout CreateVanillaLayout(
        int viewportWidth,
        int viewportHeight,
        bool showingHealth,
        int maxHealth,
        int maxStamina
    )
    {
        var bottom = viewportHeight - VanillaStatusBarBottomInset;
        var energyHeightExtension = (int)((maxStamina - 270) * 0.625f);
        var energyHeight = VanillaStatusBarHeight + energyHeightExtension;
        var energyBounds = new HudBounds(
            viewportWidth - VanillaStatusBarRightInset - VanillaStatusBarWidth,
            bottom - energyHeight,
            VanillaStatusBarWidth,
            energyHeight
        );

        var healthHeight = VanillaStatusBarHeight + (maxHealth - 100);
        var healthBounds = new HudBounds(
            energyBounds.X - VanillaStatusBarWidth - VanillaStatusBarGap,
            bottom - healthHeight,
            VanillaStatusBarWidth,
            healthHeight
        );

        return new VanillaHudLayout(energyBounds, healthBounds, showingHealth);
    }

    private static HudBounds PlaceCustomBar(
        HudBounds anchor,
        int width,
        int height,
        int gap
    )
    {
        return new HudBounds(
            anchor.X - gap - width,
            anchor.Bottom - height,
            width,
            height
        );
    }
}
