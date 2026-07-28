#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal readonly record struct SanityIdleObservation(
    bool WorldReady,
    bool IsLocalOwner,
    bool CanMove,
    bool IsBusy,
    bool IsMoving,
    bool HasMovementDirections,
    bool IsUsingTool,
    bool IsInHitRecovery,
    bool PauseForSingleAnimation,
    bool IsPlayingBasicAnimation,
    bool IsMenuOpen,
    bool IsEventOrCutsceneActive,
    bool IsWarping,
    bool OwnPresentationActive
);

internal readonly record struct SanityIdleSnapshot(
    TimeSpan EligibleElapsed,
    bool ThresholdReached,
    string Reason
);

/// <summary>
/// Pure, real-elapsed-time idle predicate. It observes the frozen Farmer/FarmerSprite contract
/// only; it never selects, starts, pauses, or rewrites an animation.
/// </summary>
internal sealed class SanityIdleDetector
{
    internal static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(1);

    private TimeSpan eligibleElapsed;
    private string reason = "idle.not-observed";

    internal SanityIdleSnapshot Snapshot =>
        new(eligibleElapsed, eligibleElapsed >= IdleThreshold, reason);

    internal SanityIdleSnapshot Observe(
        TimeSpan elapsed,
        SanityIdleObservation observation
    )
    {
        if (elapsed < TimeSpan.Zero)
        {
            Reset("idle.elapsed-invalid");
            return Snapshot;
        }

        var interruption = GetInterruptionReason(observation);
        if (interruption is not null)
        {
            Reset(interruption);
            return Snapshot;
        }

        reason = "idle.eligible";
        if (eligibleElapsed >= IdleThreshold)
            return Snapshot;

        var remaining = IdleThreshold - eligibleElapsed;
        eligibleElapsed = elapsed >= remaining
            ? IdleThreshold
            : eligibleElapsed + elapsed;
        return Snapshot;
    }

    internal void Reset(string resetReason)
    {
        eligibleElapsed = TimeSpan.Zero;
        reason = string.IsNullOrWhiteSpace(resetReason)
            ? "idle.reset"
            : resetReason;
    }

    private static string? GetInterruptionReason(
        SanityIdleObservation observation
    )
    {
        if (!observation.WorldReady)
            return "idle.interrupted.world-not-ready";
        if (!observation.IsLocalOwner)
            return "idle.interrupted.owner-not-local";
        if (!observation.CanMove)
            return "idle.interrupted.cannot-move";
        if (observation.IsBusy)
            return "idle.interrupted.busy";
        if (observation.IsMoving)
            return "idle.interrupted.moving";
        if (observation.HasMovementDirections)
            return "idle.interrupted.movement-directions";
        if (observation.IsUsingTool)
            return "idle.interrupted.using-tool";
        if (observation.IsInHitRecovery)
            return "idle.interrupted.hit-recovery";
        if (
            observation.PauseForSingleAnimation
            && !observation.OwnPresentationActive
        )
            return "idle.interrupted.single-animation";
        if (
            !observation.IsPlayingBasicAnimation
            && !observation.OwnPresentationActive
        )
            return "idle.interrupted.non-basic-animation";
        if (observation.IsMenuOpen)
            return "idle.interrupted.menu";
        if (observation.IsEventOrCutsceneActive)
            return "idle.interrupted.event-or-cutscene";
        if (observation.IsWarping)
            return "idle.interrupted.warp";
        return null;
    }
}
