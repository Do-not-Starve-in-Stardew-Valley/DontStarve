using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Display;

internal readonly struct UIRenderContext
{
    public IModHelper Helper { get; }
    public RenderingHudEventArgs EventArgs { get; }
    public Vector2 ViewportSize { get; }
    public bool ShowingHealth { get; }

    public UIRenderContext(IModHelper helper, RenderingHudEventArgs e)
    {
        Helper = helper;
        EventArgs = e;
        // 所有 HUD 定位使用 uiViewport，避免窗口缩放或 UI 缩放时与世界 viewport 混淆。
        ViewportSize = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
        ShowingHealth = Game1.showingHealth;
    }

    public Vector2 ViewportBottomRightAnchor
    {
        get
        {
            // Stardew 原版生命/体力 HUD 展开时会占用右下角更多宽度，DS 条需要跟着让位。
            var anchor = HudDisplayRules.GetBottomRightAnchor(
                (int)ViewportSize.X,
                (int)ViewportSize.Y,
                ShowingHealth
            );
            return new Vector2(anchor.X, anchor.Y);
        }
    }
}
