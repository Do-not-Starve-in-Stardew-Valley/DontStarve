using DontStarve.Resource;
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
    public bool ShowingSanity { get; }
    public HudDisplayLayout HudLayout { get; }

    public UIRenderContext(
        IModHelper helper,
        RenderingHudEventArgs e,
        bool showingSanity
    )
    {
        Helper = helper;
        EventArgs = e;
        // 所有 HUD 定位使用 uiViewport，避免窗口缩放或 UI 缩放时与世界 viewport 混淆。
        ViewportSize = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
        ShowingHealth = Game1.showingHealth;
        ShowingSanity = showingSanity;

        var player = Game1.player;
        HudLayout = HudDisplayRules.CreateLayout(
            (int)ViewportSize.X,
            (int)ViewportSize.Y,
            ShowingHealth,
            ShowingSanity,
            player.maxHealth,
            player.MaxStamina,
            TextureLoader.HungerContainerScaledWidth,
            TextureLoader.HungerContainerScaledHeight
        );
    }
}
