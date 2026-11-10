#nullable enable

using System.Collections.Generic;

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

internal readonly record struct HudBarLayoutRequest(
    string Id,
    int Order,
    int Width,
    int Height
);

/// <summary>按可见状态条的顺序和尺寸，从原版 HUD 锚点计算屏幕 bounds。</summary>
internal static class HudDisplayRules
{
    // 原版状态条：右边距 8、底边距 16、尺寸 48x224。
    internal const int VanillaStatusBarWidth = 48;
    internal const int VanillaStatusBarHeight = 224;
    internal const int VanillaStatusBarRightInset = 8;
    internal const int VanillaStatusBarBottomInset = 16;
    internal const int VanillaStatusBarGap = 8;
    internal const int DontStarveBarGap = 12;

    internal static VanillaHudLayout CreateVanillaLayout(
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

    internal static bool TryGetBarBounds(
        VanillaHudLayout vanillaLayout,
        IReadOnlyList<HudBarLayoutRequest> visibleBars,
        string id,
        out HudBounds bounds,
        int customBarGap = DontStarveBarGap
    )
    {
        bounds = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var targetIndex = -1;
        for (var i = 0; i < visibleBars.Count; i++)
        {
            if (!string.Equals(visibleBars[i].Id, id, System.StringComparison.Ordinal))
                continue;

            if (targetIndex >= 0)
                return false;

            targetIndex = i;
        }

        if (targetIndex < 0)
            return false;

        var target = visibleBars[targetIndex];
        if (!IsValid(target))
            return false;

        var anchor = vanillaLayout.AnchorBounds;
        var left = anchor.X;
        for (var i = 0; i < visibleBars.Count; i++)
        {
            var bar = visibleBars[i];
            if (i == targetIndex || !IsValid(bar))
                continue;

            if (CompareOrder(bar, target) < 0)
                left -= customBarGap + bar.Width;
        }

        left -= customBarGap + target.Width;
        bounds = new HudBounds(
            left,
            anchor.Bottom - target.Height,
            target.Width,
            target.Height
        );
        return true;
    }

    private static bool IsValid(HudBarLayoutRequest bar)
    {
        return !string.IsNullOrWhiteSpace(bar.Id) && bar.Width > 0 && bar.Height > 0;
    }

    private static int CompareOrder(HudBarLayoutRequest left, HudBarLayoutRequest right)
    {
        var order = left.Order.CompareTo(right.Order);
        return order != 0
            ? order
            : System.StringComparer.Ordinal.Compare(left.Id, right.Id);
    }
}
