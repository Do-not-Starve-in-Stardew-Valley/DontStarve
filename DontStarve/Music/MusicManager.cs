using StardewModdingAPI;

namespace DontStarve.Music;

internal static class MusicManager
{
    // 音乐开关可能被 GMCM 多次调用，Manager 保存初始化上下文并保证 Enable/Disable 幂等。
    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static string _manifestId;
    private static bool _initialized;
    private static bool _enabled;

    internal static void Initialize(
        IModHelper helper,
        IMonitor monitor,
        string manifestId,
        bool enabled)
    {
        _helper = helper;
        _monitor = monitor;
        _manifestId = manifestId;
        _initialized = true;

        SetEnabled(enabled);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!_initialized || _enabled == enabled)
            return;

        _enabled = enabled;

        if (enabled)
        {
            DawnMusicService.Enable(_helper, _monitor, _manifestId);
            DuskMusicService.Enable(_helper, _monitor);
            return;
        }

        DawnMusicService.Disable(_helper);
        DuskMusicService.Disable(_helper);
    }
}
