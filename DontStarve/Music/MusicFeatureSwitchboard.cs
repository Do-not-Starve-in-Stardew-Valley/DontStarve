using System;

namespace DontStarve.Music;

/// <summary>
/// Keeps the independently configurable Dawn/Dusk and activity-music lanes from becoming a
/// shared master toggle again. The farming-music switch is deliberately routed through the same
/// switchboard: it is a sub-switch of the activity lane (total silence still wins), but its
/// on/off transitions must not cross-talk with the two master lanes. The callbacks are the
/// runtime boundary owned by MusicManager.
/// </summary>
internal sealed class MusicFeatureSwitchboard
{
    private readonly Action<bool> setDawnDuskEnabled;
    private readonly Action<bool> setActivityMusicEnabled;
    private readonly Action<bool> setFarmingMusicEnabled;
    private bool initialized;

    internal MusicFeatureSwitchboard(
        Action<bool> setDawnDuskEnabled,
        Action<bool> setActivityMusicEnabled,
        Action<bool> setFarmingMusicEnabled
    )
    {
        this.setDawnDuskEnabled = setDawnDuskEnabled
            ?? throw new ArgumentNullException(nameof(setDawnDuskEnabled));
        this.setActivityMusicEnabled = setActivityMusicEnabled
            ?? throw new ArgumentNullException(nameof(setActivityMusicEnabled));
        this.setFarmingMusicEnabled = setFarmingMusicEnabled
            ?? throw new ArgumentNullException(nameof(setFarmingMusicEnabled));
    }

    internal bool DawnDuskEnabled { get; private set; }

    internal bool ActivityMusicEnabled { get; private set; }

    internal bool FarmingMusicEnabled { get; private set; }

    internal void Initialize(
        bool dawnDuskEnabled,
        bool activityMusicEnabled,
        bool farmingMusicEnabled
    )
    {
        if (initialized)
        {
            SetDawnDuskEnabled(dawnDuskEnabled);
            SetActivityMusicEnabled(activityMusicEnabled);
            SetFarmingMusicEnabled(farmingMusicEnabled);
            return;
        }

        initialized = true;
        DawnDuskEnabled = dawnDuskEnabled;
        ActivityMusicEnabled = activityMusicEnabled;
        FarmingMusicEnabled = farmingMusicEnabled;
        setDawnDuskEnabled(dawnDuskEnabled);
        setActivityMusicEnabled(activityMusicEnabled);
        setFarmingMusicEnabled(farmingMusicEnabled);
    }

    internal void SetDawnDuskEnabled(bool enabled)
    {
        if (!initialized || DawnDuskEnabled == enabled)
            return;

        DawnDuskEnabled = enabled;
        setDawnDuskEnabled(enabled);
    }

    internal void SetActivityMusicEnabled(bool enabled)
    {
        if (!initialized || ActivityMusicEnabled == enabled)
            return;

        ActivityMusicEnabled = enabled;
        setActivityMusicEnabled(enabled);
    }

    internal void SetFarmingMusicEnabled(bool enabled)
    {
        if (!initialized || FarmingMusicEnabled == enabled)
            return;

        FarmingMusicEnabled = enabled;
        setFarmingMusicEnabled(enabled);
    }
}
