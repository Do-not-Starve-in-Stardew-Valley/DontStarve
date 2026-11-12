#nullable enable

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Session-local behavioral evidence that another mod owns the late-night clock transition.
/// Identity compatibility remains a fast path, but a same-session clock rollback also suppresses
/// the conflicting special chain when a fork changed its manifest or used no known UID.
/// </summary>
internal sealed class LateNightBehaviorEvidence
{
    internal const string ClockRollbackReason =
        "passout.compatibility.late-night.behavior-clock-rollback";

    private int previousTimeOfDay;
    private bool initialized;

    internal bool IsObserved { get; private set; }

    internal string StableReason => IsObserved ? ClockRollbackReason : string.Empty;

    internal void Reset(int currentTimeOfDay)
    {
        previousTimeOfDay = currentTimeOfDay;
        initialized = true;
        IsObserved = false;
    }

    internal void Clear()
    {
        previousTimeOfDay = 0;
        initialized = false;
        IsObserved = false;
    }

    internal bool Observe(int currentTimeOfDay, bool worldReady, bool newDay)
    {
        if (!initialized)
        {
            previousTimeOfDay = currentTimeOfDay;
            initialized = true;
            return IsObserved;
        }

        var rolledBack = previousTimeOfDay >= 2400
            && currentTimeOfDay <= 300
            && worldReady
            && !newDay;
        previousTimeOfDay = currentTimeOfDay;
        if (rolledBack)
            IsObserved = true;
        return IsObserved;
    }

    internal bool ObserveTimeChanged(
        int eventNewTime,
        int currentTimeOfDay,
        bool worldReady,
        bool newDay
    )
    {
        if (
            worldReady
            && !newDay
            && eventNewTime >= 2400
            && currentTimeOfDay <= 300
        )
        {
            IsObserved = true;
        }

        return Observe(currentTimeOfDay, worldReady, newDay);
    }
}
