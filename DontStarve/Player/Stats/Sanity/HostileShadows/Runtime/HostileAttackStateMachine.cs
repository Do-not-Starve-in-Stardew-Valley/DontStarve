#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Immutable per-entity tick input. This stays a value type so the 60 Hz world loop can capture
/// current authority/target geometry without allocating one managed object per entity.
/// </summary>
internal readonly struct HostileAttackStateInput
{
    private HostileAttackStateInput(
        string sessionId,
        long entityId,
        long proposedAttackRevision,
        string locationId,
        string targetPlayerKey,
        bool hasTarget,
        bool targetWithinAttackRange,
        double positionX,
        double positionY,
        double standingX,
        double standingY,
        double targetStandingX,
        double targetStandingY,
        double tileSizePixels,
        double attackIntervalSeconds
    )
    {
        SessionId = sessionId;
        EntityId = entityId;
        ProposedAttackRevision = proposedAttackRevision;
        LocationId = locationId;
        TargetPlayerKey = targetPlayerKey;
        HasTarget = hasTarget;
        TargetWithinAttackRange = targetWithinAttackRange;
        PositionX = positionX;
        PositionY = positionY;
        StandingX = standingX;
        StandingY = standingY;
        TargetStandingX = targetStandingX;
        TargetStandingY = targetStandingY;
        TileSizePixels = tileSizePixels;
        AttackIntervalSeconds = attackIntervalSeconds;
    }

    internal string SessionId { get; }
    internal long EntityId { get; }
    internal long ProposedAttackRevision { get; }
    internal string LocationId { get; }
    internal string TargetPlayerKey { get; }
    internal bool HasTarget { get; }
    internal bool TargetWithinAttackRange { get; }
    internal double PositionX { get; }
    internal double PositionY { get; }
    internal double StandingX { get; }
    internal double StandingY { get; }
    internal double TargetStandingX { get; }
    internal double TargetStandingY { get; }
    internal double TileSizePixels { get; }
    internal double AttackIntervalSeconds { get; }

    internal static HostileAttackStateInput Capture(
        string sessionId,
        long entityId,
        long proposedAttackRevision,
        string locationId,
        string targetPlayerKey,
        bool hasTarget,
        bool targetWithinAttackRange,
        double positionX,
        double positionY,
        double standingX,
        double standingY,
        double targetStandingX,
        double targetStandingY,
        double tileSizePixels,
        double attackIntervalSeconds
    )
    {
        return new HostileAttackStateInput(
            sessionId,
            entityId,
            proposedAttackRevision,
            locationId,
            targetPlayerKey,
            hasTarget,
            targetWithinAttackRange,
            positionX,
            positionY,
            standingX,
            standingY,
            targetStandingX,
            targetStandingY,
            tileSizePixels,
            attackIntervalSeconds
        );
    }
}

internal readonly record struct HostileAttackStateDecision(
    bool Valid,
    string StateId,
    double PositionX,
    double PositionY,
    string AttackInstanceId,
    long AttackInstanceRevision,
    int AttackFrameNumber,
    bool StateChanged,
    bool PositionChanged,
    bool AttackFrameChanged,
    string Reason
);

internal readonly record struct HostileAttackExternalTransitionDecision(
    bool Valid,
    string StateId,
    double PositionX,
    double PositionY,
    bool AttackInterrupted,
    bool StateChanged,
    string Reason
);

internal sealed class HostileAttackStateMachine
{
    private readonly HostileAttackRuntimeDefinition definition;
    private readonly IHostileAttackTransitionPolicy transitionPolicy;
    private string stateId = HostileShadowStateIds.Spawn;
    private double stateElapsedMilliseconds;
    private double cooldownRemainingMilliseconds;
    private double pendingDelayAfterTauntMilliseconds = -1d;
    private bool firstChaseDecisionMade;

    internal HostileAttackStateMachine(
        HostileAttackRuntimeDefinition definition,
        IHostileAttackTransitionPolicy? transitionPolicy = null,
        string initialStateId = HostileShadowStateIds.Spawn
    )
    {
        this.definition = definition
            ?? throw new ArgumentNullException(nameof(definition));
        this.transitionPolicy = transitionPolicy
            ?? HostileAttackDeferredTransitionPolicy.Instance;
        if (
            !string.Equals(initialStateId, HostileShadowStateIds.Spawn, StringComparison.Ordinal)
            && !string.Equals(initialStateId, HostileShadowStateIds.Taunt, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "Only Spawn and Taunt are valid initial hostile-shadow states.",
                nameof(initialStateId)
            );
        }

        stateId = initialStateId;
        // A conversion has already supplied the one-time arrival Taunt. Mark the first-chase
        // decision consumed so an owner who is initially out of range does not receive a second
        // Taunt later when it first enters detection range.
        firstChaseDecisionMade = string.Equals(
            initialStateId,
            HostileShadowStateIds.Taunt,
            StringComparison.Ordinal
        );
    }

    internal string StateId => stateId;
    internal HostileAttackInstance? CurrentInstance { get; private set; }

    /// <summary>
    /// Ends the current physical attack exactly once. The frozen origin is restored before the
    /// instance (and its per-player ledger) is discarded, so an old request can no longer resolve
    /// after HitTeleport, Dying, or Despawn begins.
    /// </summary>
    internal HostileAttackExternalTransitionDecision TransitionToExternalState(
        string nextStateId,
        double currentPositionX,
        double currentPositionY
    )
    {
        if (
            !IsExternalState(nextStateId)
            || !double.IsFinite(currentPositionX)
            || !double.IsFinite(currentPositionY)
        )
        {
            return new HostileAttackExternalTransitionDecision(
                false,
                stateId,
                currentPositionX,
                currentPositionY,
                false,
                false,
                "hostile-shadow.attack-external-transition-invalid"
            );
        }

        var beforeState = stateId;
        var attackInterrupted = string.Equals(
                stateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && CurrentInstance is not null;
        var positionX = CurrentInstance?.OriginPositionX ?? currentPositionX;
        var positionY = CurrentInstance?.OriginPositionY ?? currentPositionY;
        CurrentInstance = null;
        stateElapsedMilliseconds = 0d;
        pendingDelayAfterTauntMilliseconds = -1d;
        stateId = nextStateId;
        return new HostileAttackExternalTransitionDecision(
            true,
            stateId,
            positionX,
            positionY,
            attackInterrupted,
            !string.Equals(beforeState, stateId, StringComparison.Ordinal),
            attackInterrupted
                ? "hostile-shadow.attack-interrupted-and-origin-restored"
                : "hostile-shadow.attack-external-state-entered"
        );
    }

    internal HostileAttackExternalTransitionDecision CompleteHitTeleport(
        bool hasTarget,
        double positionX,
        double positionY,
        bool forceIdle = false
    )
    {
        if (
            !string.Equals(
                stateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            )
            || !double.IsFinite(positionX)
            || !double.IsFinite(positionY)
        )
        {
            return new HostileAttackExternalTransitionDecision(
                false,
                stateId,
                positionX,
                positionY,
                false,
                false,
                "hostile-shadow.hit-teleport-completion-invalid"
            );
        }

        stateElapsedMilliseconds = 0d;
        pendingDelayAfterTauntMilliseconds = -1d;
        stateId = !forceIdle && hasTarget
            ? HostileShadowStateIds.Chase
            : HostileShadowStateIds.Idle;
        return new HostileAttackExternalTransitionDecision(
            true,
            stateId,
            positionX,
            positionY,
            false,
            true,
            forceIdle
                ? "hostile-shadow.hit-teleport-no-legal-point-idle"
                : "hostile-shadow.hit-teleport-completed"
        );
    }

    internal HostileAttackStateDecision Advance(
        in HostileAttackStateInput input,
        double elapsedMilliseconds
    )
    {
        if (!IsValid(input, elapsedMilliseconds))
            return Invalid(input, "hostile-shadow.attack-state-input-invalid");

        var beforeState = stateId;
        var beforeFrame = CurrentInstance?.FrameNumber ?? 0;
        var beforeX = input.PositionX;
        var beforeY = input.PositionY;
        var positionX = beforeX;
        var positionY = beforeY;
        cooldownRemainingMilliseconds = Math.Max(
            0d,
            cooldownRemainingMilliseconds - elapsedMilliseconds
        );

        if (string.Equals(stateId, HostileShadowStateIds.Attack, StringComparison.Ordinal))
        {
            if (CurrentInstance is null)
                return Invalid(input, "hostile-shadow.attack-instance-missing");
            stateElapsedMilliseconds += elapsedMilliseconds;
            var frame = 1 + (int)Math.Floor(
                stateElapsedMilliseconds / definition.AttackFrameDurationMilliseconds
            );
            if (frame > definition.AttackFrameCount)
            {
                positionX = CurrentInstance.OriginPositionX;
                positionY = CurrentInstance.OriginPositionY;
                var transition = new HostileAttackTransitionContext(
                    input.EntityId,
                    CurrentInstance.TargetPlayerKey,
                    CurrentInstance.Revision
                );
                if (
                    !transitionPolicy.TryResolvePostAttackTransition(
                        transition,
                        input.AttackIntervalSeconds,
                        out var postAttack
                    )
                    || !double.IsFinite(postAttack.NextAttackDelaySeconds)
                    || postAttack.NextAttackDelaySeconds < 0d
                )
                {
                    return Invalid(
                        input,
                        "hostile-shadow.post-attack-transition-policy-invalid"
                    );
                }
                CurrentInstance = null;
                stateElapsedMilliseconds = 0d;
                if (postAttack.EnterTaunt)
                {
                    // The 0.5s branch starts only after Taunt has fully completed; counting it in
                    // parallel with Taunt would shorten the species contract under a long frame.
                    cooldownRemainingMilliseconds = 0d;
                    pendingDelayAfterTauntMilliseconds =
                        postAttack.NextAttackDelaySeconds * 1000d;
                    stateId = HostileShadowStateIds.Taunt;
                }
                else
                {
                    cooldownRemainingMilliseconds =
                        postAttack.NextAttackDelaySeconds * 1000d;
                    pendingDelayAfterTauntMilliseconds = -1d;
                    stateId = input.HasTarget
                        ? HostileShadowStateIds.Chase
                        : HostileShadowStateIds.Idle;
                }
            }
            else
            {
                CurrentInstance.FrameNumber = frame;
                if (
                    !definition.Motion.TryGetCumulativeWorldAdvance(
                        frame,
                        input.TileSizePixels,
                        out var advance
                    )
                )
                {
                    return Invalid(input, "hostile-shadow.attack-motion-evaluation-failed");
                }
                positionX = CurrentInstance.OriginPositionX
                    + CurrentInstance.DirectionX * advance;
                positionY = CurrentInstance.OriginPositionY
                    + CurrentInstance.DirectionY * advance;
            }
        }
        else
        {
            stateElapsedMilliseconds += elapsedMilliseconds;
            AdvanceNonAttack(input, ref positionX, ref positionY);
        }

        var currentFrame = CurrentInstance?.FrameNumber ?? 0;
        return new HostileAttackStateDecision(
            true,
            stateId,
            positionX,
            positionY,
            CurrentInstance?.InstanceId ?? string.Empty,
            CurrentInstance?.Revision ?? 0,
            currentFrame,
            !string.Equals(beforeState, stateId, StringComparison.Ordinal),
            !Same(beforeX, positionX) || !Same(beforeY, positionY),
            beforeFrame != currentFrame,
            "hostile-shadow.attack-state-advanced"
        );
    }

    private void AdvanceNonAttack(
        in HostileAttackStateInput input,
        ref double positionX,
        ref double positionY
    )
    {
        if (
            string.Equals(stateId, HostileShadowStateIds.Spawn, StringComparison.Ordinal)
            && stateElapsedMilliseconds >= definition.SpawnDurationMilliseconds
        )
        {
            stateElapsedMilliseconds = 0d;
            var context = new HostileAttackTransitionContext(
                input.EntityId,
                input.TargetPlayerKey,
                input.ProposedAttackRevision
            );
            stateId = input.HasTarget
                ? ResolveFirstChase(context)
                    ? HostileShadowStateIds.Taunt
                    : HostileShadowStateIds.Chase
                : HostileShadowStateIds.Idle;
        }

        if (
            string.Equals(stateId, HostileShadowStateIds.Taunt, StringComparison.Ordinal)
            && stateElapsedMilliseconds >= definition.TauntDurationMilliseconds
        )
        {
            stateElapsedMilliseconds = 0d;
            if (pendingDelayAfterTauntMilliseconds >= 0d)
            {
                cooldownRemainingMilliseconds = pendingDelayAfterTauntMilliseconds;
                pendingDelayAfterTauntMilliseconds = -1d;
            }
            stateId = input.HasTarget
                ? HostileShadowStateIds.Chase
                : HostileShadowStateIds.Idle;
        }

        if (
            string.Equals(stateId, HostileShadowStateIds.Idle, StringComparison.Ordinal)
            && input.HasTarget
        )
        {
            stateElapsedMilliseconds = 0d;
            var context = new HostileAttackTransitionContext(
                input.EntityId,
                input.TargetPlayerKey,
                input.ProposedAttackRevision
            );
            stateId = ResolveFirstChase(context)
                ? HostileShadowStateIds.Taunt
                : HostileShadowStateIds.Chase;
        }
        else if (
            string.Equals(stateId, HostileShadowStateIds.Chase, StringComparison.Ordinal)
            && !input.HasTarget
        )
        {
            stateElapsedMilliseconds = 0d;
            stateId = HostileShadowStateIds.Idle;
        }

        if (
            string.Equals(stateId, HostileShadowStateIds.Chase, StringComparison.Ordinal)
            && input.HasTarget
            && input.TargetWithinAttackRange
            && cooldownRemainingMilliseconds <= 0d
        )
        {
            StartAttack(input, ref positionX, ref positionY);
        }
    }

    private void StartAttack(
        in HostileAttackStateInput input,
        ref double positionX,
        ref double positionY
    )
    {
        pendingDelayAfterTauntMilliseconds = -1d;
        var x = input.TargetStandingX - input.StandingX;
        var y = input.TargetStandingY - input.StandingY;
        var length = Math.Sqrt((x * x) + (y * y));
        if (!double.IsFinite(length) || length <= 0d)
        {
            x = 0d;
            y = 1d;
        }
        else
        {
            x /= length;
            y /= length;
        }
        var instanceId = string.Concat(
            input.SessionId,
            ":",
            input.EntityId.ToString(CultureInfo.InvariantCulture),
            ":",
            input.ProposedAttackRevision.ToString(CultureInfo.InvariantCulture)
        );
        CurrentInstance = new HostileAttackInstance(
            instanceId,
            input.EntityId,
            input.ProposedAttackRevision,
            input.LocationId,
            input.TargetPlayerKey,
            input.PositionX,
            input.PositionY,
            x,
            y,
            HostileAttackCollisionResolver.ResolveFacing(
                input.StandingX,
                input.StandingY,
                input.TargetStandingX,
                input.TargetStandingY
            )
        );
        stateElapsedMilliseconds = 0d;
        stateId = HostileShadowStateIds.Attack;
        if (
            definition.Motion.TryGetCumulativeWorldAdvance(
                1,
                input.TileSizePixels,
                out var advance
            )
        )
        {
            positionX = input.PositionX + x * advance;
            positionY = input.PositionY + y * advance;
        }
    }

    private bool ResolveFirstChase(HostileAttackTransitionContext context)
    {
        if (firstChaseDecisionMade)
            return false;
        firstChaseDecisionMade = true;
        return transitionPolicy.ShouldTauntBeforeFirstChase(context);
    }

    private static bool IsValid(
        in HostileAttackStateInput input,
        double elapsedMilliseconds
    )
    {
        return SanityProtocol.IsValidSessionId(input.SessionId)
            && input.EntityId > 0
            && input.ProposedAttackRevision > 0
            && !string.IsNullOrWhiteSpace(input.LocationId)
            && double.IsFinite(elapsedMilliseconds)
            && elapsedMilliseconds >= 0d
            && double.IsFinite(input.PositionX)
            && double.IsFinite(input.PositionY)
            && double.IsFinite(input.StandingX)
            && double.IsFinite(input.StandingY)
            && double.IsFinite(input.TileSizePixels)
            && input.TileSizePixels > 0d
            && double.IsFinite(input.AttackIntervalSeconds)
            && input.AttackIntervalSeconds >= 0d
            && (
                !input.HasTarget
                || (
                    SanityPlayerKey.IsCanonical(input.TargetPlayerKey)
                    && double.IsFinite(input.TargetStandingX)
                    && double.IsFinite(input.TargetStandingY)
                )
            );
    }

    private static bool IsExternalState(string stateId)
    {
        return string.Equals(
                stateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            )
            || string.Equals(
                stateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
            || string.Equals(
                stateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            );
    }

    private HostileAttackStateDecision Invalid(
        in HostileAttackStateInput input,
        string reason
    )
    {
        return new HostileAttackStateDecision(
            false,
            stateId,
            input.PositionX,
            input.PositionY,
            CurrentInstance?.InstanceId ?? string.Empty,
            CurrentInstance?.Revision ?? 0,
            CurrentInstance?.FrameNumber ?? 0,
            false,
            false,
            false,
            reason
        );
    }

    private static bool Same(double left, double right)
    {
        return BitConverter.DoubleToInt64Bits(left)
            == BitConverter.DoubleToInt64Bits(right);
    }
}
