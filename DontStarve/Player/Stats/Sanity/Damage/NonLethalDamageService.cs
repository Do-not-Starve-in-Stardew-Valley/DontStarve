#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Player.Stats.Sanity.Damage;

/// <summary>
/// Bounded, session-scoped receipt window. It never evicts a live-session receipt because doing so
/// would turn a late retry into a second mutation; capacity exhaustion therefore fails closed.
/// </summary>
internal sealed class NonLethalDamageReceiptRegistry
{
    internal const int MaximumReceipts = 256;

    private readonly Dictionary<string, NonLethalDamageReceipt> receipts =
        new(StringComparer.Ordinal);

    internal int Count => receipts.Count;

    internal bool HasCapacity => receipts.Count < MaximumReceipts;

    internal bool TryGet(string correlationId, out NonLethalDamageReceipt receipt)
    {
        return receipts.TryGetValue(correlationId, out receipt!);
    }

    internal bool TryAdd(NonLethalDamageReceipt receipt)
    {
        if (receipts.Count >= MaximumReceipts)
            return false;

        receipts.Add(receipt.CorrelationId, receipt);
        return true;
    }

    internal void Clear()
    {
        receipts.Clear();
    }
}

/// <summary>
/// Validates intent, calculates the non-lethal bound and owns the single receipt window. Each
/// operation has its own injectable executor so their physical and direct-floor semantics cannot
/// be substituted for one another.
/// </summary>
internal sealed class NonLethalDamageService : INonLethalDamageService
{
    private static readonly NonLethalDamageCapability UnavailableApplyCapability = new(
        NonLethalDamageOperation.ApplyDamageUpToFloor,
        NonLethalDamageCapabilityStatus.Unavailable,
        "nonlethal.apply-damage-up-to-floor.physical-seam-unavailable"
    );

    private static readonly NonLethalDamageCapability AvailableApplyCapability = new(
        NonLethalDamageOperation.ApplyDamageUpToFloor,
        NonLethalDamageCapabilityStatus.Available,
        "nonlethal.apply-damage-up-to-floor.controlled-defense-floor-available"
    );

    private static readonly NonLethalDamageCapability UnavailableReduceCapability = new(
        NonLethalDamageOperation.ReduceToFloor,
        NonLethalDamageCapabilityStatus.Unavailable,
        "nonlethal.reduce-to-floor.health-mutation-seam-deferred"
    );

    private static readonly NonLethalDamageCapability AvailableReduceCapability = new(
        NonLethalDamageOperation.ReduceToFloor,
        NonLethalDamageCapabilityStatus.Available,
        "nonlethal.reduce-to-floor.direct-health-floor-available"
    );

    private readonly NonLethalDamageReceiptRegistry receipts = new();
    private readonly IApplyDamageUpToFloorExecutor? applyExecutor;
    private readonly IReduceToFloorExecutor? reduceExecutor;
    private string activeSessionId = string.Empty;

    internal NonLethalDamageService(
        IApplyDamageUpToFloorExecutor? applyExecutor = null,
        IReduceToFloorExecutor? reduceExecutor = null
    )
    {
        this.applyExecutor = applyExecutor;
        this.reduceExecutor = reduceExecutor;
    }

    internal string ActiveSessionId => activeSessionId;

    internal int ReceiptCount => receipts.Count;

    public NonLethalDamageSessionResult BeginSession(string sessionId)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            return new NonLethalDamageSessionResult(
                false,
                "nonlethal.session-id-invalid"
            );
        }
        if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            return new NonLethalDamageSessionResult(
                true,
                "nonlethal.session-already-active"
            );
        }

        var replaced = activeSessionId.Length > 0;
        receipts.Clear();
        activeSessionId = sessionId;
        return new NonLethalDamageSessionResult(
            true,
            replaced ? "nonlethal.session-replaced" : "nonlethal.session-started"
        );
    }

    public void ClearSession()
    {
        receipts.Clear();
        activeSessionId = string.Empty;
    }

    public NonLethalDamageCapability GetCapability(
        NonLethalDamageOperation operation
    )
    {
        return operation switch
        {
            NonLethalDamageOperation.ApplyDamageUpToFloor => applyExecutor is null
                ? UnavailableApplyCapability
                : AvailableApplyCapability,
            NonLethalDamageOperation.ReduceToFloor => reduceExecutor is null
                ? UnavailableReduceCapability
                : AvailableReduceCapability,
            _ => new NonLethalDamageCapability(
                operation,
                NonLethalDamageCapabilityStatus.Unavailable,
                "nonlethal.operation-invalid"
            ),
        };
    }

    public NonLethalDamageResult ApplyDamageUpToFloor(
        ApplyDamageUpToFloorRequest request
    )
    {
        var capability = GetCapability(
            NonLethalDamageOperation.ApplyDamageUpToFloor
        );
        return Evaluate(
            request.Context,
            NonLethalDamageCalculator.Calculate(request),
            capability
        );
    }

    public NonLethalDamageResult ReduceToFloor(ReduceToFloorRequest request)
    {
        var capability = GetCapability(NonLethalDamageOperation.ReduceToFloor);
        return Evaluate(
            request.Context,
            NonLethalDamageCalculator.Calculate(request),
            capability
        );
    }

    private NonLethalDamageResult Evaluate(
        NonLethalDamageContext context,
        NonLethalDamageCalculationResult calculationResult,
        NonLethalDamageCapability capability
    )
    {
        var contextFailure = ValidateContext(context, capability);
        if (contextFailure is not null)
            return contextFailure;
        if (!calculationResult.Success || calculationResult.Calculation is null)
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                calculationResult.Reason,
                capability,
                null
            );
        }

        var calculation = calculationResult.Calculation.Value;
        if (receipts.TryGet(context.CorrelationId, out var original))
        {
            return original.MatchesIntent(context, calculation)
                ? Result(
                    NonLethalDamageResultStatus.Duplicate,
                    "nonlethal.correlation-duplicate",
                    capability,
                    original
                )
                : Result(
                    NonLethalDamageResultStatus.CorrelationConflict,
                    "nonlethal.correlation-conflict",
                    capability,
                    original
                );
        }

        if (calculation.MaximumAllowedDamage == 0)
        {
            return Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.NoChange,
                NonLethalDamageReceiptOutcome.NoChange,
                "nonlethal.already-at-or-below-floor"
            );
        }
        if (!capability.IsAvailable)
        {
            return Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.Unavailable,
                NonLethalDamageReceiptOutcome.Unavailable,
                capability.Reason
            );
        }

        if (
            calculation.Operation == NonLethalDamageOperation.ApplyDamageUpToFloor
            && applyExecutor is not null
        )
        {
            // Capacity is checked before mutation. A full live-session registry must never apply
            // damage that it cannot remember for an exactly-once retry.
            if (!receipts.HasCapacity)
            {
                return Result(
                    NonLethalDamageResultStatus.CapacityExceeded,
                    "nonlethal.receipt-capacity-exceeded",
                    capability,
                    null
                );
            }

            return ExecuteApply(context, calculation, capability, applyExecutor);
        }
        if (
            calculation.Operation == NonLethalDamageOperation.ReduceToFloor
            && reduceExecutor is not null
        )
        {
            // Direct health mutation shares the same exactly-once receipt window. Refuse before
            // writing when the live session can no longer remember a new correlation.
            if (!receipts.HasCapacity)
            {
                return Result(
                    NonLethalDamageResultStatus.CapacityExceeded,
                    "nonlethal.receipt-capacity-exceeded",
                    capability,
                    null
                );
            }

            return ExecuteReduce(context, calculation, capability, reduceExecutor);
        }

        var failClosed = new NonLethalDamageCapability(
            capability.Operation,
            NonLethalDamageCapabilityStatus.Unavailable,
            "nonlethal.operation-executor-unavailable"
        );
        return Record(
            context,
            calculation,
            failClosed,
            NonLethalDamageResultStatus.Unavailable,
            NonLethalDamageReceiptOutcome.Unavailable,
            failClosed.Reason
        );
    }

    private NonLethalDamageResult ExecuteApply(
        NonLethalDamageContext context,
        NonLethalDamageCalculation calculation,
        NonLethalDamageCapability capability,
        IApplyDamageUpToFloorExecutor executor
    )
    {
        var executionRequest = new ApplyDamageUpToFloorExecutionRequest(
            context,
            calculation.BeforeHealth,
            calculation.MaximumHealth,
            calculation.RequestedDamage!.Value,
            calculation.FloorHealth,
            calculation.MaximumAllowedDamage
        );

        ApplyDamageUpToFloorExecutionResult execution;
        try
        {
            execution = executor.Execute(executionRequest);
        }
        catch (Exception)
        {
            execution = ApplyDamageUpToFloorExecutionResult.Unavailable(
                calculation.BeforeHealth,
                "nonlethal.apply-damage-up-to-floor.executor-failed"
            );
        }

        if (!IsExecutionResultValid(calculation, execution))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.apply-damage-up-to-floor.executor-result-invalid",
                capability,
                null
            );
        }

        return execution.Applied
            ? Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.Applied,
                NonLethalDamageReceiptOutcome.Applied,
                execution.Reason,
                execution.AppliedDamage,
                execution.AfterHealth
            )
            : Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.Unavailable,
                NonLethalDamageReceiptOutcome.Unavailable,
                execution.Reason
            );
    }

    private static bool IsExecutionResultValid(
        NonLethalDamageCalculation calculation,
        ApplyDamageUpToFloorExecutionResult execution
    )
    {
        if (string.IsNullOrWhiteSpace(execution.Reason))
            return false;

        return execution.Applied
            ? execution.AppliedDamage > 0
                && execution.AppliedDamage <= calculation.MaximumAllowedDamage
                && execution.AfterHealth
                    == calculation.BeforeHealth - execution.AppliedDamage
                && execution.AfterHealth >= calculation.FloorHealth
            : execution.AppliedDamage == 0
                && execution.AfterHealth == calculation.BeforeHealth;
    }

    private NonLethalDamageResult ExecuteReduce(
        NonLethalDamageContext context,
        NonLethalDamageCalculation calculation,
        NonLethalDamageCapability capability,
        IReduceToFloorExecutor executor
    )
    {
        var executionRequest = new ReduceToFloorExecutionRequest(
            context,
            calculation.BeforeHealth,
            calculation.MaximumHealth,
            calculation.FloorHealth,
            calculation.MaximumAllowedDamage
        );

        ReduceToFloorExecutionResult execution;
        try
        {
            execution = executor.Execute(executionRequest);
        }
        catch (Exception)
        {
            execution = ReduceToFloorExecutionResult.Unavailable(
                calculation.BeforeHealth,
                "nonlethal.reduce-to-floor.executor-failed"
            );
        }

        if (!IsReduceExecutionResultValid(calculation, execution))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.reduce-to-floor.executor-result-invalid",
                capability,
                null
            );
        }

        return execution.Applied
            ? Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.Applied,
                NonLethalDamageReceiptOutcome.Applied,
                execution.Reason,
                execution.AppliedDamage,
                execution.AfterHealth
            )
            : Record(
                context,
                calculation,
                capability,
                NonLethalDamageResultStatus.Unavailable,
                NonLethalDamageReceiptOutcome.Unavailable,
                execution.Reason
            );
    }

    private static bool IsReduceExecutionResultValid(
        NonLethalDamageCalculation calculation,
        ReduceToFloorExecutionResult execution
    )
    {
        if (string.IsNullOrWhiteSpace(execution.Reason))
            return false;

        return execution.Applied
            ? execution.AppliedDamage == calculation.MaximumAllowedDamage
                && execution.AppliedDamage > 0
                && execution.AfterHealth == calculation.FloorHealth
            : execution.AppliedDamage == 0
                && execution.AfterHealth == calculation.BeforeHealth;
    }

    private NonLethalDamageResult? ValidateContext(
        NonLethalDamageContext context,
        NonLethalDamageCapability capability
    )
    {
        var operation = capability.Operation;
        if (!SanityProtocol.IsValidSessionId(context.SessionId))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.session-id-invalid",
                capability,
                null
            );
        }
        if (!NonLethalDamageContract.IsValidCorrelationId(context.CorrelationId))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.correlation-id-invalid",
                capability,
                null
            );
        }
        if (!SanityPlayerKey.IsCanonical(context.PlayerKey))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.player-key-invalid",
                capability,
                null
            );
        }
        if (!NonLethalDamageContract.IsPurposeCompatible(operation, context.Purpose))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.purpose-operation-mismatch",
                capability,
                null
            );
        }
        if (context.Authority != SanityAuthorityRole.Host)
        {
            return Result(
                NonLethalDamageResultStatus.RequiresHostAuthority,
                "nonlethal.host-mutating-authority-required",
                capability,
                null
            );
        }
        if (context.AuthorityRevision < 0)
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                "nonlethal.authority-revision-invalid",
                capability,
                null
            );
        }
        if (
            activeSessionId.Length == 0
            || !string.Equals(
                activeSessionId,
                context.SessionId,
                StringComparison.Ordinal
            )
        )
        {
            return Result(
                NonLethalDamageResultStatus.SessionMismatch,
                activeSessionId.Length == 0
                    ? "nonlethal.session-not-active"
                    : "nonlethal.session-mismatch",
                capability,
                null
            );
        }

        return null;
    }

    private NonLethalDamageResult Record(
        NonLethalDamageContext context,
        NonLethalDamageCalculation calculation,
        NonLethalDamageCapability capability,
        NonLethalDamageResultStatus status,
        NonLethalDamageReceiptOutcome outcome,
        string reason,
        int appliedDamage = 0,
        int? afterHealth = null
    )
    {
        var receipt = new NonLethalDamageReceipt(
            calculation.Operation,
            context.Purpose,
            context.SessionId,
            context.CorrelationId,
            context.PlayerKey,
            calculation.BeforeHealth,
            calculation.MaximumHealth,
            calculation.RequestedDamage,
            calculation.TargetHealth,
            calculation.FloorHealth,
            calculation.MaximumAllowedDamage,
            appliedDamage,
            afterHealth ?? calculation.BeforeHealth,
            outcome,
            reason,
            context.Authority,
            context.AuthorityRevision
        );
        if (!receipt.IsInvariantSatisfied(out var invariantReason))
        {
            return Result(
                NonLethalDamageResultStatus.Invalid,
                invariantReason,
                capability,
                null
            );
        }
        if (!receipts.TryAdd(receipt))
        {
            return Result(
                NonLethalDamageResultStatus.CapacityExceeded,
                "nonlethal.receipt-capacity-exceeded",
                capability,
                null
            );
        }

        return Result(status, reason, capability, receipt);
    }

    private static NonLethalDamageResult Result(
        NonLethalDamageResultStatus status,
        string reason,
        NonLethalDamageCapability capability,
        NonLethalDamageReceipt? receipt
    )
    {
        return new NonLethalDamageResult(status, reason, capability, receipt);
    }
}
