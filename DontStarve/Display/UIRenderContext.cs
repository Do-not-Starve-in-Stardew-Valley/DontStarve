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
    public HudBounds? StatusBarBounds { get; }

    public UIRenderContext(
        IModHelper helper,
        RenderingHudEventArgs e,
        HudBounds? statusBarBounds
    )
    {
        Helper = helper;
        EventArgs = e;
        // 所有 HUD 定位使用 uiViewport，避免窗口缩放或 UI 缩放时与世界 viewport 混淆。
        ViewportSize = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
        StatusBarBounds = statusBarBounds;
    }
}
