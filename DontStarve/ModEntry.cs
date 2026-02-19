using DontStarve.Buff;
using DontStarve.Display;
using DontStarve.Player;
using DontStarve.Resource;
using StardewModdingAPI;

namespace DontStarve;

internal class ModEntry : Mod
{
    /// <summary>
    /// Main
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        TextureLoader.Initialize(helper);
        BuffManager.Initialize(helper);
        StatManager.Initialize(helper);
        DisplayManager.Initialize(helper);
    }
}
