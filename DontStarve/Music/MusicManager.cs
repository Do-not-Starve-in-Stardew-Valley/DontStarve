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
    private static bool _bossMusicOverrideActive;

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
            DawnMusicService.SetBossMusicOverrideActive(_bossMusicOverrideActive);
            DuskMusicService.SetBossMusicOverrideActive(_bossMusicOverrideActive);
            return;
        }

        DawnMusicService.Disable(_helper);
        DuskMusicService.Disable(_helper);
    }

    /// <summary>
    /// Reserves the highest-priority music lane for a future boss battle. The boss owner can
    /// interrupt either independent dawn/dusk one-shot, but releasing the override never
    /// resumes an interrupted cue.
    /// </summary>
    internal static void SetBossMusicOverrideActive(bool active)
    {
        if (_bossMusicOverrideActive == active)
            return;

        _bossMusicOverrideActive = active;
        if (!_initialized || !_enabled)
            return;

        DawnMusicService.SetBossMusicOverrideActive(active);
        DuskMusicService.SetBossMusicOverrideActive(active);
    }
}
