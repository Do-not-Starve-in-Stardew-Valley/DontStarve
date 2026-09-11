#nullable enable

using System;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// Tracks restore pulses from one active Buff lifetime without depending on SMAPI or Stardew types.
/// Keeping this state transition pure makes the no-duplicate/no-backfill contract executable in tests.
/// </summary>
internal sealed class FixedRestoreBuffTiming
{
    private const int BASELINE_PULSE_MILLISECONDS = 7000;
    private const int RELEASE_LOOKAHEAD_MILLISECONDS = 1000;

    private string? activeBuffId;
    private int activeTotalMilliseconds;
    private int lastRemainingMilliseconds;
    private int appliedPulses;

    /// <summary>
    /// Observe one active Buff sample and return only pulses newly due at this sample.
    /// </summary>
    internal int Observe(string buffId, int totalMilliseconds, int remainingMilliseconds)
    {
        if (
            string.IsNullOrWhiteSpace(buffId)
            || totalMilliseconds <= 0
            || remainingMilliseconds <= 0
        )
        {
            Reset();
            return 0;
        }

        if (
            activeBuffId != buffId
            || activeTotalMilliseconds != totalMilliseconds
            || remainingMilliseconds > lastRemainingMilliseconds
        )
        {
            // A new id, duration, or backwards jump in elapsed time starts a new Buff lifetime.
            StartCycle(buffId, totalMilliseconds, remainingMilliseconds);
        }

        var targetPulses = GetTargetPulseCount(totalMilliseconds);
        var duePulses = GetDuePulseCount(
            totalMilliseconds,
            remainingMilliseconds,
            targetPulses
        );
        var pulsesToApply = duePulses - appliedPulses;
        if (pulsesToApply > 0)
            appliedPulses = duePulses;

        lastRemainingMilliseconds = remainingMilliseconds;
        return Math.Max(0, pulsesToApply);
    }

    internal void Reset()
    {
        activeBuffId = null;
        activeTotalMilliseconds = 0;
        lastRemainingMilliseconds = 0;
        appliedPulses = 0;
    }

    private void StartCycle(
        string buffId,
        int totalMilliseconds,
        int remainingMilliseconds
    )
    {
        activeBuffId = buffId;
        activeTotalMilliseconds = totalMilliseconds;
        lastRemainingMilliseconds = remainingMilliseconds;

        // Reopening a save aligns past elapsed time instead of replaying the whole missed segment.
        appliedPulses = GetDuePulseCount(
            totalMilliseconds,
            remainingMilliseconds,
            GetTargetPulseCount(totalMilliseconds)
        );
    }

    private static int GetTargetPulseCount(int totalMilliseconds)
    {
        return Math.Max(1, totalMilliseconds / BASELINE_PULSE_MILLISECONDS);
    }

    private static int GetDuePulseCount(
        int totalMilliseconds,
        int remainingMilliseconds,
        int targetPulses
    )
    {
        var effectiveRemaining = Math.Max(
            0,
            remainingMilliseconds - RELEASE_LOOKAHEAD_MILLISECONDS
        );
        var elapsedMilliseconds = Math.Clamp(
            totalMilliseconds - effectiveRemaining,
            0,
            totalMilliseconds
        );

        return Math.Min(
            targetPulses,
            (int)Math.Floor(
                targetPulses * elapsedMilliseconds / (double)totalMilliseconds
            )
        );
    }
}
