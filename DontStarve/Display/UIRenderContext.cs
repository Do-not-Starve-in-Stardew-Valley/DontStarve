using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Display;

internal readonly struct UIRenderContext
{
    private const int HudOffsetWithHealth = 171;
    private const int HudOffsetWithoutHealth = 116;

    public IModHelper Helper { get; }
    public RenderingHudEventArgs EventArgs { get; }
    public Vector2 ViewportSize { get; }
    public bool ShowingHealth { get; }

    public UIRenderContext(IModHelper helper, RenderingHudEventArgs e)
    {
        Helper = helper;
        EventArgs = e;
        ViewportSize = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
        ShowingHealth = Game1.showingHealth;
    }

    public Vector2 ViewportBottomRightAnchor =>
        new Vector2(
            ViewportSize.X - (ShowingHealth ? HudOffsetWithHealth : HudOffsetWithoutHealth),
            ViewportSize.Y
        );
}
