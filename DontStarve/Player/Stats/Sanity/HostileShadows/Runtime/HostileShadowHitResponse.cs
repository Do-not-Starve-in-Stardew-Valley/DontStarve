#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal readonly record struct HostileShadowIncomingDamageDecision(
    bool Valid,
    int AppliedDamage,
    int LogicalHealthAfter,
    int PhysicalHealthAfter,
    bool PendingDying,
    string Reason
);

internal static class HostileShadowIncomingDamagePolicy
{
    internal static HostileShadowIncomingDamageDecision Evaluate(
        int currentHealth,
        int requestedDamage,
        int defense
    )
    {
        if (currentHealth <= 0 || requestedDamage <= 0 || defense < 0)
        {
            return new HostileShadowIncomingDamageDecision(
                false,
                0,
                currentHealth,
                currentHealth,
                false,
                "hostile-shadow.incoming-damage-input-invalid"
            );
        }

        var appliedDamage = Math.Min(
            currentHealth,
            Math.Max(1, requestedDamage - defense)
        );
        var logicalHealthAfter = currentHealth - appliedDamage;
        var pendingDying = logicalHealthAfter == 0;
        return new HostileShadowIncomingDamageDecision(
            true,
            appliedDamage,
            logicalHealthAfter,
            pendingDying ? 1 : logicalHealthAfter,
            pendingDying,
            pendingDying
                ? "hostile-shadow.incoming-damage-lethal-pending-dying"
                : "hostile-shadow.incoming-damage-applied"
        );
    }
}

internal sealed class HostileShadowHitResponseInput
{
    internal string SessionId { get; init; } = string.Empty;
    internal long EntityId { get; init; }
    internal long ProposedRevision { get; init; }
    internal string LocationId { get; init; } = string.Empty;
    internal double PositionX { get; init; }
    internal double PositionY { get; init; }
    internal double TileSizePixels { get; init; }
    internal int Health { get; init; }
    internal string AttackerPlayerKey { get; init; } = string.Empty;
    internal IHostileShadowTeleportMap? Map { get; init; }
    internal IHostileShadowTeleportRandom? Random { get; init; }
}

internal enum HostileShadowHitResponseDecisionStatus
{
    Started,
    Duplicate,
    Advanced,
    Completed,
    RemovalRequested,
    Rejected,
}

internal readonly record struct HostileShadowHitResponseDecision(
    HostileShadowHitResponseDecisionStatus Status,
    string StateId,
    double PositionX,
    double PositionY,
    bool AttackInterrupted,
    bool StateChanged,
    bool PositionChanged,
    bool RemovalRequested,
    HostileShadowLifecycleReceipt? Receipt,
    string Reason
)
{
    internal bool Valid => Status != HostileShadowHitResponseDecisionStatus.Rejected;
}

/// <summary>
/// Owns only the semantic HitTeleport/Dying transition. Its fixed 400ms timing is deliberately
/// independent from optional stage-03 visual metadata, so a missing row never changes gameplay.
/// </summary>
internal sealed class HostileShadowHitResponseController
{
    internal const double TransitionDurationMilliseconds = 400d;

    private readonly HostileAttackStateMachine attackState;
    private readonly HostileShadowTeleportPointSelector selector;
    private double elapsedMilliseconds;
    private HostileShadowTeleportPoint teleportPoint;
    private bool hasTeleportPoint;
    private bool completeWithoutTeleport;
    private bool removalIssued;

    internal HostileShadowHitResponseController(
        HostileAttackStateMachine attackState,
        HostileShadowTeleportPointSelector? selector = null
    )
    {
        this.attackState = attackState
            ?? throw new ArgumentNullException(nameof(attackState));
        this.selector = selector ?? new HostileShadowTeleportPointSelector();
    }

    internal string StateId => attackState.StateId;
    internal HostileShadowLifecycleReceipt? ActiveReceipt { get; private set; }
    internal double ElapsedMilliseconds => elapsedMilliseconds;

    internal HostileShadowHitResponseDecision HandleHit(
        HostileShadowHitResponseInput? input
    )
    {
        if (!IsValid(input))
            return Rejected(input, "hostile-shadow.hit-response-input-invalid");

        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            )
        )
        {
            return Existing(
                input!,
                HostileShadowHitResponseDecisionStatus.Duplicate,
                removalRequested: true,
                "hostile-shadow.hit-response-despawn-already-terminal"
            );
        }
        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
        )
        {
            return Existing(
                input!,
                HostileShadowHitResponseDecisionStatus.Duplicate,
                removalIssued,
                "hostile-shadow.hit-response-dying-already-active"
            );
        }
        if (input!.Health == 0)
            return BeginDying(input);

        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            )
        )
        {
            // Damage/health may still advance, but the first hit owns the transition target,
            // seed, duration, and correlation. A repeat never restarts or rerolls it.
            return Existing(
                input,
                HostileShadowHitResponseDecisionStatus.Duplicate,
                removalRequested: false,
                "hostile-shadow.hit-teleport-hit-duplicate"
            );
        }

        var transition = attackState.TransitionToExternalState(
            HostileShadowStateIds.HitTeleport,
            input.PositionX,
            input.PositionY
        );
        if (!transition.Valid)
            return Rejected(input, transition.Reason);

        var selection = selector.Select(
            input.Map,
            input.LocationId,
            transition.PositionX,
            transition.PositionY,
            input.TileSizePixels,
            input.Random
        );
        elapsedMilliseconds = 0d;
        removalIssued = false;
        hasTeleportPoint = selection.Selected;
        completeWithoutTeleport = selection.Status
            == HostileShadowTeleportSelectionStatus.NoLegalPoint;
        teleportPoint = selection.Point;

        if (
            selection.Status == HostileShadowTeleportSelectionStatus.LocationInvalid
            || selection.Status == HostileShadowTeleportSelectionStatus.Rejected
        )
        {
            var despawn = attackState.TransitionToExternalState(
                HostileShadowStateIds.Despawn,
                transition.PositionX,
                transition.PositionY
            );
            if (
                !despawn.Valid
                || !TryReceipt(
                    input,
                    HostileShadowLifecycleTransitionKind.Despawn,
                    selection.Reason,
                    null,
                    out var despawnReceipt
                )
            )
            {
                return Rejected(
                    input,
                    "hostile-shadow.hit-response-despawn-transition-failed"
                );
            }
            ActiveReceipt = despawnReceipt;
            return new HostileShadowHitResponseDecision(
                HostileShadowHitResponseDecisionStatus.RemovalRequested,
                HostileShadowStateIds.Despawn,
                despawn.PositionX,
                despawn.PositionY,
                transition.AttackInterrupted,
                true,
                !Same(input.PositionX, despawn.PositionX)
                    || !Same(input.PositionY, despawn.PositionY),
                true,
                despawnReceipt,
                selection.Reason
            );
        }

        if (
            !TryReceipt(
                input,
                HostileShadowLifecycleTransitionKind.HitTeleport,
                selection.Reason,
                selection.RandomSeed,
                out var receipt
            )
        )
        {
            return Rejected(input, "hostile-shadow.hit-teleport-receipt-invalid");
        }
        ActiveReceipt = receipt;
        return new HostileShadowHitResponseDecision(
            HostileShadowHitResponseDecisionStatus.Started,
            HostileShadowStateIds.HitTeleport,
            transition.PositionX,
            transition.PositionY,
            transition.AttackInterrupted,
            true,
            !Same(input.PositionX, transition.PositionX)
                || !Same(input.PositionY, transition.PositionY),
            false,
            receipt,
            selection.Reason
        );
    }

    /// <summary>
    /// Starts the no-target natural disappearance animation without creating a death settlement
    /// receipt. Natural disappearance is not a kill, so it must use the same complete death visual
    /// timing while remaining ineligible for drops, sanity rewards, and kill settlement.
    /// </summary>
    internal HostileShadowHitResponseDecision BeginNaturalDying(
        double positionX,
        double positionY
    )
    {
        if (
            !double.IsFinite(positionX)
            || !double.IsFinite(positionY)
        )
        {
            return Rejected(
                null,
                "hostile-shadow.natural-dying-position-invalid"
            );
        }
        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
        )
        {
            return Existing(
                positionX,
                positionY,
                HostileShadowHitResponseDecisionStatus.Duplicate,
                false,
                "hostile-shadow.natural-dying-already-active"
            );
        }
        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(
                null,
                "hostile-shadow.natural-dying-after-despawn"
            );
        }

        var transition = attackState.TransitionToExternalState(
            HostileShadowStateIds.Dying,
            positionX,
            positionY
        );
        if (!transition.Valid)
            return Rejected(null, transition.Reason);

        elapsedMilliseconds = 0d;
        hasTeleportPoint = false;
        completeWithoutTeleport = false;
        removalIssued = false;
        ActiveReceipt = null;
        return new HostileShadowHitResponseDecision(
            HostileShadowHitResponseDecisionStatus.Started,
            HostileShadowStateIds.Dying,
            transition.PositionX,
            transition.PositionY,
            transition.AttackInterrupted,
            true,
            !Same(positionX, transition.PositionX)
                || !Same(positionY, transition.PositionY),
            false,
            null,
            "hostile-shadow.natural-dying-started"
        );
    }

    internal HostileShadowHitResponseDecision Advance(
        double positionX,
        double positionY,
        double elapsed,
        bool hasTarget
    )
    {
        if (
            !double.IsFinite(positionX)
            || !double.IsFinite(positionY)
            || !double.IsFinite(elapsed)
            || elapsed < 0d
        )
        {
            return Rejected(
                null,
                "hostile-shadow.hit-response-advance-input-invalid"
            );
        }

        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            )
        )
        {
            elapsedMilliseconds += elapsed;
            if (
                !completeWithoutTeleport
                && elapsedMilliseconds < TransitionDurationMilliseconds
            )
            {
                return Existing(
                    positionX,
                    positionY,
                    HostileShadowHitResponseDecisionStatus.Advanced,
                    false,
                    "hostile-shadow.hit-teleport-active"
                );
            }

            var destinationX = hasTeleportPoint
                ? teleportPoint.PositionX
                : positionX;
            var destinationY = hasTeleportPoint
                ? teleportPoint.PositionY
                : positionY;
            var completion = attackState.CompleteHitTeleport(
                hasTarget,
                destinationX,
                destinationY,
                completeWithoutTeleport
            );
            if (!completion.Valid)
                return Rejected(null, completion.Reason);
            hasTeleportPoint = false;
            completeWithoutTeleport = false;
            elapsedMilliseconds = 0d;
            return new HostileShadowHitResponseDecision(
                HostileShadowHitResponseDecisionStatus.Completed,
                completion.StateId,
                completion.PositionX,
                completion.PositionY,
                false,
                true,
                !Same(positionX, completion.PositionX)
                    || !Same(positionY, completion.PositionY),
                false,
                ActiveReceipt,
                completion.Reason
            );
        }

        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
        )
        {
            elapsedMilliseconds += elapsed;
            if (elapsedMilliseconds < TransitionDurationMilliseconds)
            {
                return Existing(
                    positionX,
                    positionY,
                    HostileShadowHitResponseDecisionStatus.Advanced,
                    false,
                    "hostile-shadow.dying-active"
                );
            }
            if (removalIssued)
            {
                return Existing(
                    positionX,
                    positionY,
                    HostileShadowHitResponseDecisionStatus.Duplicate,
                    true,
                    "hostile-shadow.dying-removal-duplicate"
                );
            }
            removalIssued = true;
            return Existing(
                positionX,
                positionY,
                HostileShadowHitResponseDecisionStatus.RemovalRequested,
                true,
                "hostile-shadow.dying-completed"
            );
        }

        if (
            string.Equals(
                StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            )
        )
        {
            return Existing(
                positionX,
                positionY,
                HostileShadowHitResponseDecisionStatus.RemovalRequested,
                true,
                "hostile-shadow.despawn-removal-requested"
            );
        }

        return Existing(
            positionX,
            positionY,
            HostileShadowHitResponseDecisionStatus.Advanced,
            false,
            "hostile-shadow.hit-response-inactive"
        );
    }

    private HostileShadowHitResponseDecision BeginDying(
        HostileShadowHitResponseInput input
    )
    {
        var transition = attackState.TransitionToExternalState(
            HostileShadowStateIds.Dying,
            input.PositionX,
            input.PositionY
        );
        if (
            !transition.Valid
            || !TryReceipt(
                input,
                HostileShadowLifecycleTransitionKind.Dying,
                HostileShadowSettlementReasonIds.DyingHealthZero,
                null,
                out var receipt
            )
        )
        {
            return Rejected(input, "hostile-shadow.dying-transition-failed");
        }

        elapsedMilliseconds = 0d;
        hasTeleportPoint = false;
        completeWithoutTeleport = false;
        removalIssued = false;
        ActiveReceipt = receipt;
        return new HostileShadowHitResponseDecision(
            HostileShadowHitResponseDecisionStatus.Started,
            HostileShadowStateIds.Dying,
            transition.PositionX,
            transition.PositionY,
            transition.AttackInterrupted,
            true,
            !Same(input.PositionX, transition.PositionX)
                || !Same(input.PositionY, transition.PositionY),
            false,
            receipt,
            receipt.Reason
        );
    }

    private static bool IsValid(HostileShadowHitResponseInput? input)
    {
        return input is not null
            && SanityProtocol.IsValidSessionId(input.SessionId)
            && input.EntityId > 0
            && input.ProposedRevision > 0
            && !string.IsNullOrWhiteSpace(input.LocationId)
            && double.IsFinite(input.PositionX)
            && double.IsFinite(input.PositionY)
            && double.IsFinite(input.TileSizePixels)
            && input.TileSizePixels > 0d
            && input.Health >= 0
            && input.AttackerPlayerKey is not null
            && (
                input.AttackerPlayerKey.Length == 0
                || SanityPlayerKey.IsCanonical(input.AttackerPlayerKey)
            )
            && (
                input.Health == 0
                || (input.Map is not null && input.Random is not null)
            );
    }

    private static bool TryReceipt(
        HostileShadowHitResponseInput input,
        HostileShadowLifecycleTransitionKind kind,
        string reason,
        int? seed,
        out HostileShadowLifecycleReceipt receipt
    )
    {
        return HostileShadowLifecycleReceipt.TryCreate(
            input.SessionId,
            input.EntityId,
            input.ProposedRevision,
            kind,
            reason,
            seed,
            kind == HostileShadowLifecycleTransitionKind.Despawn
                ? string.Empty
                : input.AttackerPlayerKey,
            out receipt
        );
    }

    private HostileShadowHitResponseDecision Existing(
        HostileShadowHitResponseInput input,
        HostileShadowHitResponseDecisionStatus status,
        bool removalRequested,
        string reason
    )
    {
        return Existing(
            input.PositionX,
            input.PositionY,
            status,
            removalRequested,
            reason
        );
    }

    private HostileShadowHitResponseDecision Existing(
        double positionX,
        double positionY,
        HostileShadowHitResponseDecisionStatus status,
        bool removalRequested,
        string reason
    )
    {
        return new HostileShadowHitResponseDecision(
            status,
            StateId,
            positionX,
            positionY,
            false,
            false,
            false,
            removalRequested,
            ActiveReceipt,
            reason
        );
    }

    private HostileShadowHitResponseDecision Rejected(
        HostileShadowHitResponseInput? input,
        string reason
    )
    {
        return new HostileShadowHitResponseDecision(
            HostileShadowHitResponseDecisionStatus.Rejected,
            StateId,
            input?.PositionX ?? 0d,
            input?.PositionY ?? 0d,
            false,
            false,
            false,
            false,
            ActiveReceipt,
            reason
        );
    }

    private static bool Same(double left, double right)
    {
        return BitConverter.DoubleToInt64Bits(left)
            == BitConverter.DoubleToInt64Bits(right);
    }
}
