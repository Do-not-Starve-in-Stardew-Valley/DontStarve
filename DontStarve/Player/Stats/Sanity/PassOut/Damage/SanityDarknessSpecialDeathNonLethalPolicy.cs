#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.Damage;

namespace DontStarve.Player.Stats.Sanity.PassOut.Damage;

internal enum SanityDarknessSpecialDeathNonLethalStatus
{
    Applied,
    NoChange,
    Duplicate,
    Rejected,
}

/// <summary>
/// Value-only handoff from the future pass-out classifier. Health remains integer-valued because
/// that is the task-family-06 and Farmer runtime contract; the shared calculator owns the ceil-20%
/// conversion for maximum-health values whose 20% is fractional.
/// </summary>
internal readonly record struct SanityDarknessSpecialDeathNonLethalRequest(
    string SessionId,
    string CorrelationId,
    string PlayerKey,
    SanityAuthorityRole Authority,
    long AuthorityRevision,
    int CurrentHealth,
    int MaximumHealth
);

/// <summary>
/// The receipt is the original task-family-06 receipt, never a pass-out-specific copy. Feedback is
/// only a one-shot directive for a newly applied mutation; this stage deliberately does not play
/// presentation effects.
/// </summary>
internal sealed class SanityDarknessSpecialDeathNonLethalResult
{
    internal SanityDarknessSpecialDeathNonLethalResult(
        SanityDarknessSpecialDeathNonLethalStatus status,
        string reason,
        NonLethalDamageResultStatus? nonLethalStatus,
        NonLethalDamageReceipt? receipt
    )
    {
        Status = status;
        Reason = reason;
        NonLethalStatus = nonLethalStatus;
        Receipt = receipt;
    }

    internal SanityDarknessSpecialDeathNonLethalStatus Status { get; }

    internal string Reason { get; }

    internal NonLethalDamageResultStatus? NonLethalStatus { get; }

    internal NonLethalDamageReceipt? Receipt { get; }

    internal bool ShouldEmitDamageFeedback =>
        Status == SanityDarknessSpecialDeathNonLethalStatus.Applied;

    internal static SanityDarknessSpecialDeathNonLethalResult Rejected(
        string reason,
        NonLethalDamageResultStatus? nonLethalStatus = null,
        NonLethalDamageReceipt? receipt = null
    )
    {
        return new SanityDarknessSpecialDeathNonLethalResult(
            SanityDarknessSpecialDeathNonLethalStatus.Rejected,
            reason,
            nonLethalStatus,
            receipt
        );
    }
}

internal interface ISanityDarknessSpecialDeathNonLethalPolicy
{
    SanityDarknessSpecialDeathNonLethalResult ReduceToFloor(
        SanityDarknessSpecialDeathNonLethalRequest request
    );
}

/// <summary>
/// Narrows the shared non-lethal authority to the one operation and purpose permitted for the
/// Sanity darkness special death. Session ownership and runtime authority are validated by the
/// integration seam before this policy is called.
/// </summary>
internal sealed class SanityDarknessSpecialDeathNonLethalPolicy
    : ISanityDarknessSpecialDeathNonLethalPolicy
{
    private readonly INonLethalDamageService nonLethalDamage;

    internal SanityDarknessSpecialDeathNonLethalPolicy(
        INonLethalDamageService nonLethalDamage
    )
    {
        this.nonLethalDamage = nonLethalDamage
            ?? throw new ArgumentNullException(nameof(nonLethalDamage));
    }

    public SanityDarknessSpecialDeathNonLethalResult ReduceToFloor(
        SanityDarknessSpecialDeathNonLethalRequest request
    )
    {
        var result = nonLethalDamage.ReduceToFloor(
            new ReduceToFloorRequest(
                new NonLethalDamageContext(
                    request.SessionId,
                    request.CorrelationId,
                    request.PlayerKey,
                    NonLethalDamagePurpose.SanityDarknessSpecialDeath,
                    request.Authority,
                    request.AuthorityRevision
                ),
                request.CurrentHealth,
                request.MaximumHealth
            )
        );

        return MapResult(request, result);
    }

    private static SanityDarknessSpecialDeathNonLethalResult MapResult(
        SanityDarknessSpecialDeathNonLethalRequest request,
        NonLethalDamageResult result
    )
    {
        var status = result.Status switch
        {
            NonLethalDamageResultStatus.Applied =>
                SanityDarknessSpecialDeathNonLethalStatus.Applied,
            NonLethalDamageResultStatus.NoChange =>
                SanityDarknessSpecialDeathNonLethalStatus.NoChange,
            NonLethalDamageResultStatus.Duplicate =>
                SanityDarknessSpecialDeathNonLethalStatus.Duplicate,
            _ => SanityDarknessSpecialDeathNonLethalStatus.Rejected,
        };

        if (status == SanityDarknessSpecialDeathNonLethalStatus.Rejected)
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                result.Reason,
                result.Status,
                result.Receipt
            );
        }

        if (result.Receipt is not { } receipt)
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.ReceiptMissing,
                result.Status
            );
        }
        if (!receipt.IsInvariantSatisfied(out _))
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.ReceiptInvalid,
                result.Status,
                receipt
            );
        }
        if (!MatchesRequest(receipt, request))
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.ReceiptMismatch,
                result.Status,
                receipt
            );
        }
        if (
            (status == SanityDarknessSpecialDeathNonLethalStatus.Applied
                && receipt.Outcome != NonLethalDamageReceiptOutcome.Applied)
            || (status == SanityDarknessSpecialDeathNonLethalStatus.NoChange
                && receipt.Outcome != NonLethalDamageReceiptOutcome.NoChange)
        )
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.ReceiptOutcomeMismatch,
                result.Status,
                receipt
            );
        }

        return new SanityDarknessSpecialDeathNonLethalResult(
            status,
            result.Reason,
            result.Status,
            receipt
        );
    }

    private static bool MatchesRequest(
        NonLethalDamageReceipt receipt,
        SanityDarknessSpecialDeathNonLethalRequest request
    )
    {
        return receipt.Operation == NonLethalDamageOperation.ReduceToFloor
            && receipt.Purpose
                == NonLethalDamagePurpose.SanityDarknessSpecialDeath
            && string.Equals(receipt.SessionId, request.SessionId, StringComparison.Ordinal)
            && string.Equals(
                receipt.CorrelationId,
                request.CorrelationId,
                StringComparison.Ordinal
            )
            && string.Equals(receipt.PlayerKey, request.PlayerKey, StringComparison.Ordinal)
            && receipt.Authority == request.Authority
            && receipt.AuthorityRevision == request.AuthorityRevision
            && receipt.BeforeHealth == request.CurrentHealth
            && receipt.MaximumHealth == request.MaximumHealth;
    }
}

internal static class SanityDarknessSpecialDeathNonLethalReasonIds
{
    internal const string RuntimeDisposed =
        "passout.sanity-darkness-special-death.nonlethal.runtime-disposed";
    internal const string HostAuthorityRequired =
        "passout.sanity-darkness-special-death.nonlethal.host-authority-required";
    internal const string RuntimeSessionMismatch =
        "passout.sanity-darkness-special-death.nonlethal.runtime-session-mismatch";
    internal const string ReceiptMissing =
        "passout.sanity-darkness-special-death.nonlethal.receipt-missing";
    internal const string ReceiptInvalid =
        "passout.sanity-darkness-special-death.nonlethal.receipt-invalid";
    internal const string ReceiptMismatch =
        "passout.sanity-darkness-special-death.nonlethal.receipt-mismatch";
    internal const string ReceiptOutcomeMismatch =
        "passout.sanity-darkness-special-death.nonlethal.receipt-outcome-mismatch";
}
