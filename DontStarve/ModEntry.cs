using DontStarve.Buff;
using DontStarve.Display;
using DontStarve.Music;
using DontStarve.Player;
using DontStarve.Resource;
using DontStarve.Time;
using StardewModdingAPI;

namespace DontStarve;

internal class ModEntry : Mod
{
    private readonly TimeApi _timeApi = new();

    /// <summary>
    /// Main
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        _timeApi.Initialize(helper);
        TextureLoader.Initialize(helper);
        MusicManager.Initialize(helper, Monitor, ModManifest.UniqueID);
        BuffManager.Initialize(helper, _timeApi);
        StatManager.Initialize(helper, _timeApi);
        DisplayManager.Initialize(helper, _timeApi);
    }
}
