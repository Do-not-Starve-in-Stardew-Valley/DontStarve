using DontStarve.Buff;
using DontStarve.Player.Hunger;
using DontStarve.Player.Sanity;
using StardewModdingAPI;

namespace DontStarve;

internal class ModEntry : Mod
{
    /// <summary>
    /// Main
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        TextureLoader.Initialize(helper.ModContent);
        BuffManager.Initialize(helper);
        Sanity.init(helper);
        Hunger.init(helper);

        helper.Events.Display.RenderingHud += (_, e) => Hud.OnRenderingHud(helper, e);
    }
}
