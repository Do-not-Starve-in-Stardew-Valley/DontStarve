using DontStarve.Player.Hunger;
using DontStarve.Player.Sanity;
using StardewModdingAPI;

namespace DontStarve;

internal class ModEntry : Mod
{
    /// <summary>
    /// SMAPI entry point.
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        TextureLoader.Initialize(helper.ModContent);
        Buff.Buff.init(helper);
        Sanity.init(helper);
        Hunger.init(helper);

        helper.Events.Display.RenderingHud += (_, e) => Hud.OnRenderingHud(helper, e);
    }
}
