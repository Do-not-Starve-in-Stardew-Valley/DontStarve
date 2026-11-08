#nullable enable

using System;

namespace DontStarve.Music;

/// <summary>
/// Provides the Stardew-side equivalent of DST's FMOD intensity ramp. Stardew receives a raw
/// SoundEffectInstance, so the runtime advances its Volume explicitly instead of setting an FMOD
/// parameter. The durations are intentionally short UI/audio transitions, not activity lifetimes.
/// </summary>
internal static class ActivityMusicFadePolicy
{
    // DST's Work/Farming intensity parameter reaches the scripted target in about five seconds
    // in the installed FMOD event runtime. DST's Combat lane has no general intensity parameter;
    // dynamicmusic.lua starts it directly and ends it with KillSound("danger"). The installed
    // FMOD event's non-immediate stop/release path leaves an approximately one-second tail.
    internal const float WorkFarmingFadeInSeconds = 5f;
    internal const float WorkFarmingFadeOutSeconds = 5f;
    internal const float CombatFadeInSeconds = 0f;
    internal const float CombatFadeOutSeconds = 1f;
    internal const float SilentThreshold = 0.001f;

    internal static float GetFadeInSeconds(ActivityMusicKind kind)
    {
        return kind is ActivityMusicKind.Work or ActivityMusicKind.Farming
            ? WorkFarmingFadeInSeconds
            : CombatFadeInSeconds;
    }

    internal static float GetFadeOutSeconds(ActivityMusicKind kind)
    {
        return kind is ActivityMusicKind.Work or ActivityMusicKind.Farming
            ? WorkFarmingFadeOutSeconds
            : CombatFadeOutSeconds;
    }

    internal static bool IsImmediateCombatPreemption(
        ActivityMusicKind incoming,
        ActivityMusicKind current
    )
    {
        return incoming == ActivityMusicKind.Combat
            && current is ActivityMusicKind.Work or ActivityMusicKind.Farming;
    }

    internal static bool ShouldAdvanceDuringDawnDusk(ActivityMusicKind kind)
    {
        return kind is ActivityMusicKind.Work or ActivityMusicKind.Farming;
    }

    internal static float Advance(
        float current,
        float target,
        double elapsedSeconds,
        float durationSeconds
    )
    {
        current = Math.Clamp(current, 0f, 1f);
        target = Math.Clamp(target, 0f, 1f);
        if (Math.Abs(target - current) <= float.Epsilon)
            return target;
        if (elapsedSeconds <= 0)
            return current;
        if (durationSeconds <= 0)
            return target;

        var step = (float)Math.Clamp(elapsedSeconds / durationSeconds, 0d, 1d);
        return target > current
            ? Math.Min(target, current + step)
            : Math.Max(target, current - step);
    }
}
