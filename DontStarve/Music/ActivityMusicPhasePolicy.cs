#nullable enable

namespace DontStarve.Music;

/// <summary>
/// Pure DST-style phase gates for activity music. Runtime location and clock facts are supplied by
/// ActivityMusicService; this policy only decides boundary behavior.
/// </summary>
internal static class ActivityMusicPhasePolicy
{
    internal const int DawnSuppressionSeconds = 15;
    internal const int DuskSuppressionSeconds = 15;

    internal static bool IsDawnCrossing(int oldTime, int newTime, int dawnTime)
    {
        return dawnTime >= 0
            && oldTime < newTime
            && oldTime < dawnTime
            && newTime >= dawnTime;
    }

    internal static bool IsDuskCrossing(int oldTime, int newTime, int startDarkTime)
    {
        return startDarkTime >= 0
            && oldTime < newTime
            && oldTime < startDarkTime
            && newTime >= startDarkTime;
    }

    internal static bool IsWorkBlocked(
        bool isCaveLike,
        int timeOfDay,
        int startDarkTime
    )
    {
        return !isCaveLike
            && startDarkTime >= 0
            && timeOfDay >= startDarkTime;
    }
}
