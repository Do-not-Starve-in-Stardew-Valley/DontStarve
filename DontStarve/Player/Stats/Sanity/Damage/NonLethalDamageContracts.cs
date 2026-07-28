#nullable enable

using System;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Player.Stats.Sanity.Damage;

internal enum NonLethalDamageOperation
{
    ApplyDamageUpToFloor = 1,
    ReduceToFloor = 2,
}

internal enum NonLethalDamagePurpose
{
    Unknown = 0,
    DarknessAttack = 1,
    SanityDarknessSpecialDeath = 2,
}

internal enum NonLethalDamageCapabilityStatus
{
    Available,
    Unavailable,
}

internal enum NonLethalDamageReceiptOutcome
{
    Applied,
    NoChange,
    Unavailable,
}

internal enum NonLethalDamageResultStatus
{
    Applied,
    NoChange,
    Duplicate,
    Invalid,
    RequiresHostAuthority,
    SessionMismatch,
    Unavailable,
    CorrelationConflict,
    CapacityExceeded,
}

/// <summary>
/// The authority envelope is intentionally value-only. Player identity, session and revision come
/// from task-family 01; this contract records them but never establishes a second authority.
/// </summary>
internal readonly record struct NonLethalDamageContext(
    string SessionId,
    string CorrelationId,
    string PlayerKey,
    NonLethalDamagePurpose Purpose,
    SanityAuthorityRole Authority,
    long AuthorityRevision
);

/// <summary>Physical damage intent. It is deliberately not interchangeable with ReduceToFloor.</summary>
internal readonly record struct ApplyDamageUpToFloorRequest(
    NonLethalDamageContext Context,
    int BeforeHealth,
    int MaximumHealth,
    int RequestedDamage
);

/// <summary>Direct floor target. It carries no physical-damage amount or defense semantics.</summary>
internal readonly record struct ReduceToFloorRequest(
    NonLethalDamageContext Context,
    int BeforeHealth,
    int MaximumHealth
);

internal readonly record struct NonLethalDamageCapability(
    NonLethalDamageOperation Operation,
    NonLethalDamageCapabilityStatus Status,
    string Reason
)
{
    internal bool IsAvailable => Status == NonLethalDamageCapabilityStatus.Available;
}

internal readonly record struct NonLethalDamageSessionResult(
    bool Accepted,
    string Reason
);

/// <summary>
/// Immutable evidence for one correlation. Planned damage and actual applied damage are separate
/// so a contract-only or rejected physical seam can never look like a health mutation.
/// </summary>
internal sealed class NonLethalDamageReceipt
{
    internal NonLethalDamageReceipt(
        NonLethalDamageOperation operation,
        NonLethalDamagePurpose purpose,
        string sessionId,
        string correlationId,
        string playerKey,
        int beforeHealth,
        int maximumHealth,
        int? requestedDamage,
        int? targetHealth,
        int floorHealth,
        int maximumAllowedDamage,
        int appliedDamage,
        int afterHealth,
        NonLethalDamageReceiptOutcome outcome,
        string reason,
        SanityAuthorityRole authority,
        long authorityRevision
    )
    {
        Operation = operation;
        Purpose = purpose;
        SessionId = sessionId;
        CorrelationId = correlationId;
        PlayerKey = playerKey;
        BeforeHealth = beforeHealth;
        MaximumHealth = maximumHealth;
        RequestedDamage = requestedDamage;
        TargetHealth = targetHealth;
        FloorHealth = floorHealth;
        MaximumAllowedDamage = maximumAllowedDamage;
        AppliedDamage = appliedDamage;
        AfterHealth = afterHealth;
        Outcome = outcome;
        Reason = reason;
        Authority = authority;
        AuthorityRevision = authorityRevision;
    }

    internal NonLethalDamageOperation Operation { get; }

    internal NonLethalDamagePurpose Purpose { get; }

    internal string SessionId { get; }

    internal string CorrelationId { get; }

    internal string PlayerKey { get; }

    internal int BeforeHealth { get; }

    internal int MaximumHealth { get; }

    internal int? RequestedDamage { get; }

    internal int? TargetHealth { get; }

    internal int FloorHealth { get; }

    internal int MaximumAllowedDamage { get; }

    internal int AppliedDamage { get; }

    internal int AfterHealth { get; }

    internal NonLethalDamageReceiptOutcome Outcome { get; }

    internal string Reason { get; }

    internal SanityAuthorityRole Authority { get; }

    internal long AuthorityRevision { get; }

    internal bool MatchesIntent(
        NonLethalDamageContext context,
        NonLethalDamageCalculation calculation
    )
    {
        return Operation == calculation.Operation
            && Purpose == context.Purpose
            && string.Equals(SessionId, context.SessionId, StringComparison.Ordinal)
            && string.Equals(CorrelationId, context.CorrelationId, StringComparison.Ordinal)
            && string.Equals(PlayerKey, context.PlayerKey, StringComparison.Ordinal)
            && BeforeHealth == calculation.BeforeHealth
            && MaximumHealth == calculation.MaximumHealth
            && RequestedDamage == calculation.RequestedDamage
            && TargetHealth == calculation.TargetHealth
            && FloorHealth == calculation.FloorHealth
            && MaximumAllowedDamage == calculation.MaximumAllowedDamage
            && Authority == context.Authority
            && AuthorityRevision == context.AuthorityRevision;
    }

    internal bool IsInvariantSatisfied(out string reason)
    {
        if (!SanityProtocol.IsValidSessionId(SessionId))
        {
            reason = "nonlethal.receipt.session-invalid";
            return false;
        }
        if (!NonLethalDamageContract.IsValidCorrelationId(CorrelationId))
        {
            reason = "nonlethal.receipt.correlation-invalid";
            return false;
        }
        if (!SanityPlayerKey.IsCanonical(PlayerKey))
        {
            reason = "nonlethal.receipt.player-invalid";
            return false;
        }
        if (Authority != SanityAuthorityRole.Host || AuthorityRevision < 0)
        {
            reason = "nonlethal.receipt.authority-invalid";
            return false;
        }
        if (!NonLethalDamageContract.IsPurposeCompatible(Operation, Purpose))
        {
            reason = "nonlethal.receipt.purpose-operation-mismatch";
            return false;
        }
        if (!Enum.IsDefined(typeof(NonLethalDamageReceiptOutcome), Outcome))
        {
            reason = "nonlethal.receipt.outcome-invalid";
            return false;
        }
        if (
            !NonLethalDamageCalculator.TryCalculateFloor(
                MaximumHealth,
                out var expectedFloor,
                out reason
            )
            || expectedFloor != FloorHealth
        )
        {
            reason = "nonlethal.receipt.floor-invalid";
            return false;
        }
        if (BeforeHealth < 0 || BeforeHealth > MaximumHealth)
        {
            reason = "nonlethal.receipt.before-health-invalid";
            return false;
        }

        var nonLethalLimit = Math.Max(0, BeforeHealth - FloorHealth);
        if (
            MaximumAllowedDamage < 0
            || MaximumAllowedDamage > nonLethalLimit
            || AppliedDamage < 0
            || AppliedDamage > MaximumAllowedDamage
            || AfterHealth != BeforeHealth - AppliedDamage
            || AfterHealth < 0
            || AfterHealth > MaximumHealth
            || AfterHealth < Math.Min(BeforeHealth, FloorHealth)
        )
        {
            reason = "nonlethal.receipt.health-transition-invalid";
            return false;
        }

        if (Operation == NonLethalDamageOperation.ApplyDamageUpToFloor)
        {
            if (
                RequestedDamage is null
                || RequestedDamage <= 0
                || TargetHealth is not null
                || MaximumAllowedDamage != Math.Min(RequestedDamage.Value, nonLethalLimit)
            )
            {
                reason = "nonlethal.receipt.physical-intent-invalid";
                return false;
            }
        }
        else if (Operation == NonLethalDamageOperation.ReduceToFloor)
        {
            if (
                RequestedDamage is not null
                || TargetHealth != FloorHealth
                || MaximumAllowedDamage != nonLethalLimit
                || (
                    Outcome == NonLethalDamageReceiptOutcome.Applied
                    && (
                        AppliedDamage != nonLethalLimit
                        || AfterHealth != FloorHealth
                    )
                )
            )
            {
                reason = "nonlethal.receipt.floor-target-invalid";
                return false;
            }
        }
        else
        {
            reason = "nonlethal.receipt.operation-invalid";
            return false;
        }

        if (
            Outcome == NonLethalDamageReceiptOutcome.Applied
                ? AppliedDamage <= 0
                : AppliedDamage != 0 || AfterHealth != BeforeHealth
        )
        {
            reason = "nonlethal.receipt.outcome-transition-mismatch";
            return false;
        }
        if (
            Outcome == NonLethalDamageReceiptOutcome.NoChange
            && MaximumAllowedDamage != 0
        )
        {
            reason = "nonlethal.receipt.no-change-has-applicable-damage";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Reason))
        {
            reason = "nonlethal.receipt.reason-missing";
            return false;
        }

        reason = "nonlethal.receipt.invariant-valid";
        return true;
    }
}

internal sealed class NonLethalDamageResult
{
    internal NonLethalDamageResult(
        NonLethalDamageResultStatus status,
        string reason,
        NonLethalDamageCapability capability,
        NonLethalDamageReceipt? receipt
    )
    {
        Status = status;
        Reason = reason;
        Capability = capability;
        Receipt = receipt;
    }

    internal NonLethalDamageResultStatus Status { get; }

    internal string Reason { get; }

    internal NonLethalDamageCapability Capability { get; }

    /// <summary>For Duplicate/CorrelationConflict this is the one original stored receipt.</summary>
    internal NonLethalDamageReceipt? Receipt { get; }

    internal bool MutationApplied =>
        Status == NonLethalDamageResultStatus.Applied
        && Receipt is { AppliedDamage: > 0 };
}

internal interface INonLethalDamageService
{
    NonLethalDamageSessionResult BeginSession(string sessionId);

    void ClearSession();

    NonLethalDamageCapability GetCapability(NonLethalDamageOperation operation);

    NonLethalDamageResult ApplyDamageUpToFloor(ApplyDamageUpToFloorRequest request);

    NonLethalDamageResult ReduceToFloor(ReduceToFloorRequest request);
}

internal static class NonLethalDamageContract
{
    internal static bool IsValidCorrelationId(string correlationId)
    {
        return !string.IsNullOrWhiteSpace(correlationId)
            && Guid.TryParseExact(correlationId, "N", out var parsed)
            && parsed != Guid.Empty;
    }

    internal static bool IsPurposeCompatible(
        NonLethalDamageOperation operation,
        NonLethalDamagePurpose purpose
    )
    {
        return (operation, purpose) switch
        {
            (
                NonLethalDamageOperation.ApplyDamageUpToFloor,
                NonLethalDamagePurpose.DarknessAttack
            ) => true,
            (
                NonLethalDamageOperation.ReduceToFloor,
                NonLethalDamagePurpose.SanityDarknessSpecialDeath
            ) => true,
            _ => false,
        };
    }
}
