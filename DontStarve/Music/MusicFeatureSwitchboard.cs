using System;

namespace DontStarve.Music;

/// <summary>
/// Keeps the independently configurable Dawn/Dusk and activity-music lanes from becoming a
/// shared master toggle again. The callbacks are the runtime boundary owned by MusicManager.
/// </summary>
internal sealed class MusicFeatureSwitchboard
{
    private readonly Action<bool> setDawnDuskEnabled;
    private readonly Action<bool> setActivityMusicEnabled;
    private bool initialized;

    internal MusicFeatureSwitchboard(
        Action<bool> setDawnDuskEnabled,
        Action<bool> setActivityMusicEnabled
    )
    {
        this.setDawnDuskEnabled = setDawnDuskEnabled
            ?? throw new ArgumentNullException(nameof(setDawnDuskEnabled));
        this.setActivityMusicEnabled = setActivityMusicEnabled
            ?? throw new ArgumentNullException(nameof(setActivityMusicEnabled));
    }

    internal bool DawnDuskEnabled { get; private set; }

    internal bool ActivityMusicEnabled { get; private set; }

    internal void Initialize(bool dawnDuskEnabled, bool activityMusicEnabled)
    {
        if (initialized)
        {
            SetDawnDuskEnabled(dawnDuskEnabled);
            SetActivityMusicEnabled(activityMusicEnabled);
            return;
        }

        initialized = true;
        DawnDuskEnabled = dawnDuskEnabled;
        ActivityMusicEnabled = activityMusicEnabled;
        setDawnDuskEnabled(dawnDuskEnabled);
        setActivityMusicEnabled(activityMusicEnabled);
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
}
