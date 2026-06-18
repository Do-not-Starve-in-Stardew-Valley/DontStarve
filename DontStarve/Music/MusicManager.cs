using StardewModdingAPI;

namespace DontStarve.Music;

internal static class MusicManager
{
    internal static void Initialize(IModHelper helper, IMonitor monitor, string manifestId)
    {
        DawnMusicService.Initialize(helper, monitor, manifestId);
        DuskMusicService.Initialize(helper, monitor);
    }
}
