#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Host-only, session-scoped settlement authority for one stage-06 expiry request. It owns the
/// 95/5 draw and the receipt window; physical health and Sanity remain behind injected authorities.
/// </summary>
internal sealed class DarknessAttackResolutionService
{
    internal const int MaximumReceipts = 256;
    internal const int MaximumDiagnosticOwners = DarknessAttackContract.MaximumOwnerStates;
    internal const double SanityDelta = -20d;

    private readonly IDarknessAttackResolutionRandom random;
    private readonly IDarknessAttackDamageAuthority damageAuthority;
    private readonly IDarknessAttackSanityAuthority sanityAuthority;
    private readonly Dictionary<string, DarknessAttackResolutionReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DarknessAttackDamageOperation> requestOperations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<DarknessAttackOwnerKey, DarknessAttackResolutionReceipt> latest =
        new();
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private string activeSessionId = string.Empty;

    internal DarknessAttackResolutionService(
        IDarknessAttackResolutionRandom random,
        IDarknessAttackDamageAuthority damageAuthority,
        IDarknessAttackSanityAuthority sanityAuthority
    )
    {
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.damageAuthority = damageAuthority
            ?? throw new ArgumentNullException(nameof(damageAuthority));
        this.sanityAuthority = sanityAuthority
            ?? throw new ArgumentNullException(nameof(sanityAuthority));
    }

    internal int ReceiptCount => receipts.Count;

    internal string ActiveSessionId => activeSessionId;

    internal DarknessAttackResolutionSessionResult BeginSession(string sessionId)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            return new DarknessAttackResolutionSessionResult(
                false,
                "darkness.resolution.session-invalid"
            );
        }
        if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            return new DarknessAttackResolutionSessionResult(
                true,
                "darkness.resolution.session-already-active"
            );
        }

        ClearSession();
        activeSessionId = sessionId;
        return new DarknessAttackResolutionSessionResult(
            true,
            "darkness.resolution.session-started"
        );
    }

    internal void ClearSession()
    {
        activeSessionId = string.Empty;
        receipts.Clear();
        requestOperations.Clear();
        latest.Clear();
        inFlight.Clear();
    }

    internal void ForgetDiagnosticOwner(string playerKey)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return;

        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in latest.Keys)
        {
            if (!string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
            latest.Remove(key);
    }

    internal bool TryGetLatest(
        string playerKey,
        int screenId,
        out DarknessAttackResolutionReceipt receipt
    )
    {
        foreach (var pair in latest)
        {
            if (
                pair.Key.ScreenId == screenId
                && string.Equals(pair.Key.PlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                receipt = pair.Value;
                return true;
            }
        }
        receipt = null!;
        return false;
    }

    internal DarknessAttackResolutionResult Resolve(
        DarknessAttackResolutionRequest request
    )
    {
        var validation = Validate(request);
        if (validation is not null)
            return new DarknessAttackResolutionResult(validation.Value.Status, validation.Value.Reason, null);

        var operation = OperationFor(request.Mode);
        var requestKey = CreateRequestKey(request.Intent.Key, request.Intent.RequestId);
        if (requestOperations.TryGetValue(requestKey, out var originalOperation))
        {
            var originalReceipt = receipts[CreateReceiptKey(requestKey, originalOperation)];
            if (originalOperation != operation)
            {
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.CorrelationConflict,
                    "darkness.resolution.request-operation-conflict",
                    originalReceipt
                );
            }
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.Duplicate,
                "darkness.resolution.duplicate",
                originalReceipt
            );
        }
        if (inFlight.Contains(requestKey))
        {
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.Rejected,
                "darkness.resolution.request-in-flight",
                null
            );
        }
        if (receipts.Count >= MaximumReceipts)
        {
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.CapacityExceeded,
                "darkness.resolution.receipt-capacity-exceeded",
                null
            );
        }

        inFlight.Add(requestKey);
        try
        {
            if (operation == DarknessAttackDamageOperation.Disabled)
            {
                var disabledReceipt = new DarknessAttackResolutionReceipt(
                    DarknessAttackResolutionReceiptStatus.Disabled,
                    request.Intent.Key,
                    request.Intent.RequestId,
                    request.Mode,
                    operation,
                    DarknessAttackResolutionCorrelation.Create(
                        request.Intent.Key,
                        request.Intent.RequestId,
                        operation
                    ),
                    -1,
                    0,
                    null,
                    null,
                    "darkness.resolution.mode-off"
                );
                Store(requestKey, disabledReceipt);
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.Disabled,
                    disabledReceipt.Reason,
                    disabledReceipt
                );
            }

            int roll;
            try
            {
                roll = random.NextRoll100();
            }
            catch (Exception exception)
            {
                return StoreRejectedBeforeDamage(
                    request,
                    requestKey,
                    operation,
                    -1,
                    0,
                    $"darkness.resolution.rng-threw:{exception.GetType().Name}"
                );
            }
            if (roll < 0 || roll > 99)
            {
                return StoreRejectedBeforeDamage(
                    request,
                    requestKey,
                    operation,
                    roll,
                    0,
                    "darkness.resolution.rng-out-of-range"
                );
            }

            var baseDamage = roll < 95 ? 100 : 101;
            var receiptId = DarknessAttackResolutionCorrelation.Create(
                request.Intent.Key,
                request.Intent.RequestId,
                operation
            );
            var damageRequest = new DarknessAttackDamageRequest(
                request.Intent.Key,
                request.Intent.RequestId,
                request.Mode,
                operation,
                receiptId,
                roll,
                baseDamage,
                request.SanityAuthorityRevision
            );
            DarknessAttackDamageReceipt damageReceipt;
            try
            {
                damageReceipt = damageAuthority.Settle(damageRequest);
            }
            catch (Exception exception)
            {
                damageReceipt = DarknessAttackDamageReceipt.Rejected(
                    damageRequest,
                    $"darkness.resolution.damage-authority-threw:{exception.GetType().Name}"
                );
            }
            if (!IsValidDamageReceipt(damageRequest, damageReceipt, out var damageReason))
            {
                damageReceipt = DarknessAttackDamageReceipt.Rejected(
                    damageRequest,
                    damageReason
                );
            }
            if (!damageReceipt.IsSettled)
            {
                var rejected = new DarknessAttackResolutionReceipt(
                    DarknessAttackResolutionReceiptStatus.Rejected,
                    request.Intent.Key,
                    request.Intent.RequestId,
                    request.Mode,
                    operation,
                    receiptId,
                    roll,
                    baseDamage,
                    damageReceipt,
                    null,
                    damageReceipt.Reason
                );
                Store(requestKey, rejected);
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.Rejected,
                    rejected.Reason,
                    rejected
                );
            }

            var sanityRequest = new DarknessAttackSanityRequest(
                request.Intent.Key,
                request.Intent.RequestId,
                receiptId,
                SanityDelta,
                SanityChangeSource.DarknessAttack,
                request.SanityAuthorityRevision
            );
            DarknessAttackSanityReceipt sanityReceipt;
            try
            {
                sanityReceipt = sanityAuthority.Apply(sanityRequest);
            }
            catch (Exception exception)
            {
                sanityReceipt = RejectedSanity(
                    sanityRequest,
                    $"darkness.resolution.sanity-authority-threw:{exception.GetType().Name}"
                );
            }
            if (!IsValidSanityReceipt(sanityRequest, sanityReceipt, out var sanityReason))
                sanityReceipt = RejectedSanity(sanityRequest, sanityReason);

            var settled = damageReceipt.IsSettled && sanityReceipt.IsAccepted;
            var receipt = new DarknessAttackResolutionReceipt(
                settled
                    ? DarknessAttackResolutionReceiptStatus.Settled
                    : DarknessAttackResolutionReceiptStatus.Rejected,
                request.Intent.Key,
                request.Intent.RequestId,
                request.Mode,
                operation,
                receiptId,
                roll,
                baseDamage,
                damageReceipt,
                sanityReceipt,
                settled
                    ? "darkness.resolution.settled"
                    : sanityReceipt.Reason
            );
            Store(requestKey, receipt);
            return new DarknessAttackResolutionResult(
                settled
                    ? DarknessAttackResolutionStatus.Settled
                    : DarknessAttackResolutionStatus.Rejected,
                receipt.Reason,
                receipt
            );
        }
        finally
        {
            inFlight.Remove(requestKey);
        }
    }

    private (DarknessAttackResolutionStatus Status, string Reason)? Validate(
        DarknessAttackResolutionRequest request
    )
    {
        var intent = request.Intent;
        if (
            !SanityPlayerKey.IsCanonical(intent.Key.PlayerKey)
            || intent.Key.ScreenId < 0
            || !SanityProtocol.IsValidSessionId(intent.Key.SessionId)
            || string.IsNullOrWhiteSpace(intent.RequestId)
            || intent.RequestId.Length > DarknessAttackContract.MaximumRequestIdLength
            || intent.Revision < 0
            || string.IsNullOrWhiteSpace(intent.LightReason)
            || !string.Equals(
                intent.ContractVersion,
                DarknessAttackContract.ContractVersion,
                StringComparison.Ordinal
            )
            || !Enum.IsDefined(typeof(DarknessDamageMode), request.Mode)
            || request.SanityAuthorityRevision < 0
        )
        {
            return (
                DarknessAttackResolutionStatus.Invalid,
                "darkness.resolution.request-invalid"
            );
        }
        if (request.Authority != SanityAuthorityRole.Host)
        {
            return (
                DarknessAttackResolutionStatus.RequiresHostAuthority,
                "darkness.resolution.requires-host-authority"
            );
        }
        if (
            !SanityProtocol.IsValidSessionId(activeSessionId)
            || !string.Equals(activeSessionId, intent.Key.SessionId, StringComparison.Ordinal)
        )
        {
            return (
                DarknessAttackResolutionStatus.SessionMismatch,
                "darkness.resolution.session-mismatch"
            );
        }
        return null;
    }

    private DarknessAttackResolutionResult StoreRejectedBeforeDamage(
        DarknessAttackResolutionRequest request,
        string requestKey,
        DarknessAttackDamageOperation operation,
        int roll,
        int baseDamage,
        string reason
    )
    {
        var receipt = new DarknessAttackResolutionReceipt(
            DarknessAttackResolutionReceiptStatus.Rejected,
            request.Intent.Key,
            request.Intent.RequestId,
            request.Mode,
            operation,
            DarknessAttackResolutionCorrelation.Create(
                request.Intent.Key,
                request.Intent.RequestId,
                operation
            ),
            roll,
            baseDamage,
            null,
            null,
            reason
        );
        Store(requestKey, receipt);
        return new DarknessAttackResolutionResult(
            DarknessAttackResolutionStatus.Rejected,
            reason,
            receipt
        );
    }

    private void Store(string requestKey, DarknessAttackResolutionReceipt receipt)
    {
        requestOperations.Add(requestKey, receipt.Operation);
        receipts.Add(CreateReceiptKey(requestKey, receipt.Operation), receipt);
        if (latest.ContainsKey(receipt.Key) || latest.Count < MaximumDiagnosticOwners)
            latest[receipt.Key] = receipt;
    }

    private static bool IsValidDamageReceipt(
        DarknessAttackDamageRequest request,
        DarknessAttackDamageReceipt? receipt,
        out string reason
    )
    {
        if (receipt is null)
        {
            reason = "darkness.resolution.damage-receipt-missing";
            return false;
        }
        if (
            !Enum.IsDefined(typeof(DarknessAttackDamageReceiptStatus), receipt.Status)
            || receipt.Operation != request.Operation
            || !receipt.Key.Equals(request.Key)
            || !string.Equals(receipt.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(receipt.ReceiptId, request.ReceiptId, StringComparison.Ordinal)
            || receipt.BaseDamage != request.BaseDamage
            || string.IsNullOrWhiteSpace(receipt.Reason)
        )
        {
            reason = "darkness.resolution.damage-receipt-mismatch";
            return false;
        }
        if (receipt.Status == DarknessAttackDamageReceiptStatus.Rejected)
        {
            reason = "darkness.resolution.damage-receipt-valid";
            return true;
        }
        if (
            receipt.MaximumHealth <= 0
            || receipt.BeforeHealth < 0
            || receipt.BeforeHealth > receipt.MaximumHealth
            || receipt.AfterHealth < 0
            || receipt.AfterHealth > receipt.MaximumHealth
            || receipt.ActualDamage < 0
            || (
                request.Operation == DarknessAttackDamageOperation.ApplyDamageUpToFloor
                && receipt.ActualDamage <= 0
            )
        )
        {
            reason = "darkness.resolution.damage-receipt-health-invalid";
            return false;
        }
        reason = "darkness.resolution.damage-receipt-valid";
        return true;
    }

    private static bool IsValidSanityReceipt(
        DarknessAttackSanityRequest request,
        DarknessAttackSanityReceipt? receipt,
        out string reason
    )
    {
        if (receipt is null)
        {
            reason = "darkness.resolution.sanity-receipt-missing";
            return false;
        }
        if (
            !Enum.IsDefined(typeof(DarknessAttackSanityReceiptStatus), receipt.Status)
            || !receipt.Key.Equals(request.Key)
            || !string.Equals(receipt.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.DamageReceiptId,
                request.DamageReceiptId,
                StringComparison.Ordinal
            )
            || receipt.Delta != request.Delta
            || receipt.Source != SanityChangeSource.DarknessAttack
            || receipt.BeforeRevision != request.ExpectedRevision
            || !double.IsFinite(receipt.BeforeSanity)
            || !double.IsFinite(receipt.AfterSanity)
            || string.IsNullOrWhiteSpace(receipt.Reason)
        )
        {
            reason = "darkness.resolution.sanity-receipt-mismatch";
            return false;
        }
        if (receipt.Status == DarknessAttackSanityReceiptStatus.Applied)
        {
            if (
                receipt.BeforeRevision == long.MaxValue
                || receipt.AfterRevision != receipt.BeforeRevision + 1
                || receipt.AfterSanity > receipt.BeforeSanity
                || receipt.BeforeSanity - receipt.AfterSanity > -request.Delta
            )
            {
                reason = "darkness.resolution.sanity-applied-receipt-invalid";
                return false;
            }
        }
        else if (receipt.Status == DarknessAttackSanityReceiptStatus.NoChange)
        {
            if (
                receipt.AfterRevision != receipt.BeforeRevision
                || receipt.AfterSanity != receipt.BeforeSanity
            )
            {
                reason = "darkness.resolution.sanity-no-change-receipt-invalid";
                return false;
            }
        }
        reason = "darkness.resolution.sanity-receipt-valid";
        return true;
    }

    private static DarknessAttackSanityReceipt RejectedSanity(
        DarknessAttackSanityRequest request,
        string reason
    )
    {
        return new DarknessAttackSanityReceipt(
            DarknessAttackSanityReceiptStatus.Rejected,
            request.Key,
            request.RequestId,
            request.DamageReceiptId,
            request.Delta,
            request.Source,
            request.ExpectedRevision,
            request.ExpectedRevision,
            0d,
            0d,
            reason
        );
    }

    private static DarknessAttackDamageOperation OperationFor(DarknessDamageMode mode)
    {
        return mode switch
        {
            DarknessDamageMode.Off => DarknessAttackDamageOperation.Disabled,
            DarknessDamageMode.NonLethal =>
                DarknessAttackDamageOperation.ApplyDamageUpToFloor,
            DarknessDamageMode.Default => DarknessAttackDamageOperation.DefaultPhysical,
            _ => DarknessAttackDamageOperation.Disabled,
        };
    }

    private static string CreateRequestKey(DarknessAttackOwnerKey key, string requestId)
    {
        return string.Concat(key.SessionId, "|", key.PlayerKey, "|", requestId);
    }

    private static string CreateReceiptKey(
        string requestKey,
        DarknessAttackDamageOperation operation
    )
    {
        return string.Concat(requestKey, "|", ((int)operation).ToString());
    }
}
