using StardewModdingAPI;

namespace DontStarve.Music;

internal static class MusicManager
{
    // GMCM 分开控制晨昏与活动音乐；此 switchboard 保证两条 lane 不互相覆盖。
    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static string _manifestId;
    private static bool _initialized;
    private static MusicFeatureSwitchboard _switchboard;
    private static bool _bossMusicOverrideActive;

    internal static void Initialize(
        IModHelper helper,
        IMonitor monitor,
        string manifestId,
        bool dawnDuskEnabled,
        bool activityMusicEnabled,
        bool farmingMusicEnabled
    )
    {
        _helper = helper;
        _monitor = monitor;
        _manifestId = manifestId;
        _initialized = true;
        ActivityMusicService.Initialize(helper, monitor, manifestId);

        _switchboard = new MusicFeatureSwitchboard(
            ApplyDawnDuskEnabled,
            ApplyActivityMusicEnabled,
            ApplyFarmingMusicEnabled
        );
        _switchboard.Initialize(dawnDuskEnabled, activityMusicEnabled, farmingMusicEnabled);
    }

    internal static void SetDawnDuskEnabled(bool enabled)
    {
        _switchboard?.SetDawnDuskEnabled(enabled);
    }

    internal static void SetActivityMusicEnabled(bool enabled)
    {
        _switchboard?.SetActivityMusicEnabled(enabled);
    }

    internal static void SetFarmingMusicEnabled(bool enabled)
    {
        _switchboard?.SetFarmingMusicEnabled(enabled);
    }

    internal static void NotifySanityInsaneEntered(string playerKey)
    {
        if (!_initialized)
            return;

        ActivityMusicService.StartInsaneCooldown(playerKey, 15d);
    }

    internal static void DisableAll()
    {
        SetDawnDuskEnabled(false);
        SetActivityMusicEnabled(false);
    }

    private static void ApplyDawnDuskEnabled(bool enabled)
    {
        if (!_initialized)
            return;
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

    private static void ApplyActivityMusicEnabled(bool enabled)
    {
        if (!_initialized)
            return;

        if (enabled)
        {
            ActivityMusicService.Enable();
            return;
        }

        ActivityMusicService.Disable();
    }

    private static void ApplyFarmingMusicEnabled(bool enabled)
    {
        if (!_initialized)
            return;

        ActivityMusicService.SetFarmingMusicEnabled(enabled);
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
        if (!_initialized || _switchboard?.DawnDuskEnabled != true)
            return;

        DawnMusicService.SetBossMusicOverrideActive(active);
        DuskMusicService.SetBossMusicOverrideActive(active);
    }
}
