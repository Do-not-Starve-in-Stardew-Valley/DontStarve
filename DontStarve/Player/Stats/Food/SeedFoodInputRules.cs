#nullable enable

using System;

namespace DontStarve.Player.Stats.Food;

internal enum SeedFoodInputDevice
{
    Mouse,
    Gamepad,
    Touch,
}

internal enum SeedFoodGamepadButton
{
    A,
    X,
}

internal readonly record struct SeedFoodPoint(int X, int Y);

/// <summary>
/// Snapshot supplied by the SMAPI bridge. Keeping menu/world gates and the candidate identity in
/// this record makes the input contract testable without constructing Stardew's game objects.
/// </summary>
internal readonly record struct SeedFoodInputContext(
    bool Enabled,
    bool WorldReady,
    bool HasActiveMenu,
    bool HasActiveEvent,
    bool HasActiveMinigame,
    bool IsFading,
    bool IsEating,
    bool IsTargetSeed,
    int Slot,
    string? ItemId,
    int Stack
)
{
    internal bool IsEligible =>
        Enabled
        && WorldReady
        && !HasActiveMenu
        && !HasActiveEvent
        && !HasActiveMinigame
        && !IsFading
        && !IsEating
        && IsTargetSeed
        && Slot >= 0
        && !string.IsNullOrWhiteSpace(ItemId)
        && Stack > 0;
}

internal readonly record struct SeedFoodInputCandidate(
    SeedFoodInputDevice Device,
    int Slot,
    string ItemId,
    int Stack
)
{
    internal bool Matches(SeedFoodInputContext context)
    {
        return context.IsEligible
            && context.Slot == Slot
            && context.Stack == Stack
            && string.Equals(context.ItemId, ItemId, StringComparison.Ordinal);
    }
}

internal static class SeedFoodInputRules
{
    internal const long TouchLongPressMilliseconds = 1000;
    internal const int TouchMoveThresholdPixels = 16;

    internal static bool IsGamepadChord(
        bool aDown,
        bool xDown
    )
    {
        // The caller updates the pressed button before asking this predicate. A/X alone therefore
        // remain untouched, while the second edge of A+X is the sole trigger edge.
        return aDown && xDown;
    }

    internal static bool HasMoved(SeedFoodPoint origin, SeedFoodPoint current)
    {
        var dx = (long)current.X - origin.X;
        var dy = (long)current.Y - origin.Y;
        var threshold = TouchMoveThresholdPixels;
        return dx * dx + dy * dy >= (long)threshold * threshold;
    }

    internal static bool IsLongPress(long startedAt, long releasedAt)
    {
        return releasedAt >= startedAt
            && releasedAt - startedAt >= TouchLongPressMilliseconds;
    }
}

/// <summary>
/// A single-pointer touch gesture. It never emits a candidate before release, so a short press or
/// a drag cannot be mistaken for eating. Any invalidating snapshot cancels the gesture at once.
/// </summary>
internal sealed class SeedFoodTouchGestureState
{
    private bool active;
    private int pointerId;
    private SeedFoodPoint origin;
    private long startedAt;
    private SeedFoodInputCandidate candidate;

    internal bool IsActive => active;

    internal bool TryBegin(
        int pointerId,
        SeedFoodPoint position,
        long timestamp,
        SeedFoodInputContext context
    )
    {
        Reset();
        if (!context.IsEligible)
            return false;

        active = true;
        this.pointerId = pointerId;
        origin = position;
        startedAt = timestamp;
        candidate = new SeedFoodInputCandidate(
            SeedFoodInputDevice.Touch,
            context.Slot,
            context.ItemId!,
            context.Stack
        );
        return true;
    }

    internal void Observe(
        int pointerId,
        SeedFoodPoint position,
        SeedFoodInputContext context,
        bool multiplePointers
    )
    {
        if (!active)
            return;

        if (
            multiplePointers
            || pointerId != this.pointerId
            || SeedFoodInputRules.HasMoved(origin, position)
            || !candidate.Matches(context)
        )
        {
            Reset();
        }
    }

    internal SeedFoodInputCandidate? Release(
        int pointerId,
        SeedFoodPoint position,
        long timestamp,
        SeedFoodInputContext context
    )
    {
        if (!active)
            return null;

        var accepted =
            pointerId == this.pointerId
            && !SeedFoodInputRules.HasMoved(origin, position)
            && SeedFoodInputRules.IsLongPress(startedAt, timestamp)
            && candidate.Matches(context);
        var result = accepted ? candidate : (SeedFoodInputCandidate?)null;
        Reset();
        return result;
    }

    internal void Cancel()
    {
        Reset();
    }

    private void Reset()
    {
        active = false;
        pointerId = 0;
        origin = default;
        startedAt = 0;
        candidate = default;
    }
}

/// <summary>Tracks the A/X overlap edge while leaving both single-button edges observable by vanilla.</summary>
internal sealed class SeedFoodGamepadChordState
{
    private bool aDown;
    private bool xDown;
    private bool triggered;

    internal SeedFoodInputCandidate? Press(
        SeedFoodGamepadButton button,
        SeedFoodInputContext context
    )
    {
        if (button == SeedFoodGamepadButton.A)
            aDown = true;
        else
            xDown = true;

        if (triggered || !SeedFoodInputRules.IsGamepadChord(aDown, xDown))
            return null;

        triggered = true;
        if (!context.IsEligible)
            return null;

        return new SeedFoodInputCandidate(
            SeedFoodInputDevice.Gamepad,
            context.Slot,
            context.ItemId!,
            context.Stack
        );
    }

    internal void Release(SeedFoodGamepadButton button)
    {
        if (button == SeedFoodGamepadButton.A)
            aDown = false;
        else
            xDown = false;

        if (!aDown && !xDown)
            triggered = false;
    }

    internal void Reset()
    {
        aDown = false;
        xDown = false;
        triggered = false;
    }
}
