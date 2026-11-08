#nullable enable

namespace DontStarve.Music;

/// <summary>
/// The activity lanes and their frozen precedence. Keeping this in a game-assembly-free file lets
/// the arbitration contract be tested without loading SMAPI or Stardew Valley.
/// </summary>
internal enum ActivityMusicKind
{
    None = 0,
    Work = 1,
    Farming = 2,
    Combat = 3,
    Boss = 4
}

internal enum ActivityMusicStateTransition
{
    IgnoredLowerPriority = 0,
    Refreshed = 1,
    Replaced = 2
}

internal static class ActivityMusicArbitration
{
    internal const int WorkDurationSeconds = 15;
    internal const int FarmingDurationSeconds = 15;
    internal const int CombatDurationSeconds = 10;

    internal static int GetDurationSeconds(ActivityMusicKind kind)
    {
        return kind switch
        {
            ActivityMusicKind.Work => WorkDurationSeconds,
            ActivityMusicKind.Farming => FarmingDurationSeconds,
            ActivityMusicKind.Combat => CombatDurationSeconds,
            // Boss is only a future input. Keeping a duration here prevents a future caller from
            // silently borrowing the work/farming duration when the lane is eventually enabled.
            ActivityMusicKind.Boss => CombatDurationSeconds,
            _ => 0
        };
    }

    internal static ActivityMusicStateTransition ResolveTransition(
        ActivityMusicKind currentKind,
        ActivityMusicKind incomingKind,
        bool sameTrack
    )
    {
        if (incomingKind < currentKind)
            return ActivityMusicStateTransition.IgnoredLowerPriority;

        if (incomingKind == currentKind && sameTrack)
            return ActivityMusicStateTransition.Refreshed;

        return ActivityMusicStateTransition.Replaced;
    }
}
