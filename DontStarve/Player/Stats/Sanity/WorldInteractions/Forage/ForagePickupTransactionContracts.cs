#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;

internal static class ForagePickupTransactionReasonIds
{
    internal const string CapabilityAvailable = "forage.pickup.capability.available";
    internal const string CatalogUnavailable = "forage.pickup.capability.catalog-unavailable";
    internal const string NoEnabledMappings = "forage.pickup.capability.no-enabled-mappings";
    internal const string NormalPickupHookUnavailable =
        "forage.pickup.capability.normal-pickup-hook-unavailable";
    internal const string ObjectFingerprintUnavailable =
        "forage.pickup.capability.object-fingerprint-unavailable";
    internal const string AtomicCommitAdapterUnavailable =
        "forage.pickup.capability.atomic-commit-adapter-unavailable";
    internal const string MultiplayerTransportUnavailable =
        "forage.pickup.capability.multiplayer-transport-unavailable";
    internal const string SessionInvalid = "forage.pickup.transaction-session-invalid";
    internal const string SessionStarted = "forage.pickup.transaction-session-started";
    internal const string SessionInactive = "forage.pickup.transaction-session-inactive";
    internal const string RequestInvalid = "forage.pickup.transaction-request-invalid";
    internal const string NonceReplay = "forage.pickup.transaction-nonce-replay";
    internal const string NonceOutOfOrder = "forage.pickup.transaction-nonce-out-of-order";
    internal const string NonceConflict = "forage.pickup.transaction-nonce-conflict";
    internal const string OwnerWindowFull = "forage.pickup.transaction-owner-window-full";
    internal const string ReceiptWindowFull = "forage.pickup.transaction-receipt-window-full";
    internal const string ObservationMismatch = "forage.pickup.transaction-observation-mismatch";
    internal const string SanitySnapshotInvalid =
        "forage.pickup.transaction-sanity-snapshot-invalid";
    internal const string SanitySnapshotMismatch =
        "forage.pickup.transaction-sanity-snapshot-mismatch";
    internal const string InventoryCapacityUnavailable =
        "forage.pickup.transaction-inventory-capacity-unavailable";
    internal const string OriginalCommitApplied =
        "forage.pickup.transaction-original-commit-applied";
    internal const string CommitApplied = "forage.pickup.transaction-commit-applied";
    internal const string CommitRejectedOriginalPreserved =
        "forage.pickup.transaction-commit-rejected-original-preserved";
    internal const string CommitRolledBackOriginalPreserved =
        "forage.pickup.transaction-commit-rolled-back-original-preserved";
    internal const string CommitResultInvalid =
        "forage.pickup.transaction-commit-result-invalid";
    internal const string DuplicateReceipt = "forage.pickup.transaction-receipt-duplicate";
}

internal enum ForagePickupReplacementCapabilityStatus
{
    CatalogUnavailable,
    DisabledNoEnabledMappings,
    UnavailableNormalPickupHook,
    UnavailableObjectFingerprint,
    UnavailableAtomicCommitAdapter,
    UnavailableMultiplayerTransport,
    Available,
}

/// <summary>
/// The production adapter proves each independent part of the exact direct-pickup transaction.
/// The object token is a bounded content fingerprint captured at the hook, not a nonexistent
/// Stardew net-field revision.
/// </summary>
internal readonly record struct ForagePickupRuntimeEvidence(
    bool NormalPickupHookAvailable,
    bool StableObjectFingerprintAvailable,
    bool AtomicCommitAdapterAvailable,
    bool MultiplayerTransportAvailable
)
{
    internal static ForagePickupRuntimeEvidence Current =>
        new(
            NormalPickupHookAvailable: true,
            StableObjectFingerprintAvailable: true,
            AtomicCommitAdapterAvailable: true,
            MultiplayerTransportAvailable: true
        );
}

internal readonly record struct ForagePickupReplacementCapability(
    ForagePickupReplacementCapabilityStatus Status,
    string Reason,
    int EnabledMappingCount,
    ForagePickupRuntimeEvidence Evidence
)
{
    internal bool CanExecute => Status == ForagePickupReplacementCapabilityStatus.Available;
}

internal readonly record struct ForagePickupRuntimeDiagnostic(
    ForagePickupReplacementCapabilityStatus Status,
    string Reason,
    int EnabledMappingCount,
    bool RuntimeHookInstalled,
    int SuccessfulTransactionCount
);

internal static class ForagePickupReplacementCapabilityGate
{
    internal static ForagePickupReplacementCapability Evaluate(
        ForageReplacementCatalog catalog,
        ForagePickupRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsAvailable)
            return Result(
                ForagePickupReplacementCapabilityStatus.CatalogUnavailable,
                ForagePickupTransactionReasonIds.CatalogUnavailable,
                0,
                evidence
            );

        var enabledCount = 0;
        foreach (var mapping in catalog.Mappings)
        {
            if (mapping.Enabled)
                enabledCount++;
        }

        if (enabledCount == 0)
            return Result(
                ForagePickupReplacementCapabilityStatus.DisabledNoEnabledMappings,
                ForagePickupTransactionReasonIds.NoEnabledMappings,
                0,
                evidence
            );
        if (!evidence.NormalPickupHookAvailable)
            return Result(
                ForagePickupReplacementCapabilityStatus.UnavailableNormalPickupHook,
                ForagePickupTransactionReasonIds.NormalPickupHookUnavailable,
                enabledCount,
                evidence
            );
        if (!evidence.StableObjectFingerprintAvailable)
            return Result(
                ForagePickupReplacementCapabilityStatus.UnavailableObjectFingerprint,
                ForagePickupTransactionReasonIds.ObjectFingerprintUnavailable,
                enabledCount,
                evidence
            );
        if (!evidence.AtomicCommitAdapterAvailable)
            return Result(
                ForagePickupReplacementCapabilityStatus.UnavailableAtomicCommitAdapter,
                ForagePickupTransactionReasonIds.AtomicCommitAdapterUnavailable,
                enabledCount,
                evidence
            );
        if (!evidence.MultiplayerTransportAvailable)
            return Result(
                ForagePickupReplacementCapabilityStatus.UnavailableMultiplayerTransport,
                ForagePickupTransactionReasonIds.MultiplayerTransportUnavailable,
                enabledCount,
                evidence
            );

        return Result(
            ForagePickupReplacementCapabilityStatus.Available,
            ForagePickupTransactionReasonIds.CapabilityAvailable,
            enabledCount,
            evidence
        );
    }

    private static ForagePickupReplacementCapability Result(
        ForagePickupReplacementCapabilityStatus status,
        string reason,
        int enabledCount,
        ForagePickupRuntimeEvidence evidence
    )
    {
        return new ForagePickupReplacementCapability(status, reason, enabledCount, evidence);
    }
}

internal sealed class ForagePickupTransactionRequest
{
    public int ProtocolVersion { get; set; } = ForagePickupTransactionProtocol.ProtocolVersion;
    public int SchemaVersion { get; set; } = ForagePickupTransactionProtocol.SchemaVersion;
    public string SessionId { get; set; } = string.Empty;
    public long Nonce { get; set; }
    public int CatalogRevision { get; set; }
    public string MappingId { get; set; } = string.Empty;
    public string PickerPlayerKey { get; set; } = string.Empty;
    public long PickerMultiplayerId { get; set; }
    public string LocationId { get; set; } = string.Empty;
    public string LocationInstanceId { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public string SourceQualifiedItemId { get; set; } = string.Empty;
    public long ObjectFingerprint { get; set; }
    public int SourceStack { get; set; }
    public int SourceOriginalQuality { get; set; }
}

internal static class ForagePickupTransactionProtocol
{
    internal const int ProtocolVersion = 2;
    internal const int SchemaVersion = 2;
    internal const int MaximumIdentifierLength = 256;
    internal const int MaximumStack = 999;

    internal static bool IsValidRequest(
        ForagePickupTransactionRequest? request,
        string expectedSessionId
    )
    {
        if (
            request is null
            || request.ProtocolVersion != ProtocolVersion
            || request.SchemaVersion != SchemaVersion
            || !SanityProtocol.IsValidSessionId(expectedSessionId)
            || !string.Equals(request.SessionId, expectedSessionId, StringComparison.Ordinal)
            || request.Nonce <= 0
            || request.CatalogRevision <= 0
            || !IsIdentifier(request.MappingId)
            || !SanityPlayerKey.IsCanonical(request.PickerPlayerKey)
            || !long.TryParse(
                request.PickerPlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pickerId
            )
            || pickerId != request.PickerMultiplayerId
            || request.PickerMultiplayerId <= 0
            || !IsIdentifier(request.LocationId)
            || !IsIdentifier(request.LocationInstanceId)
            || request.TileX < 0
            || request.TileY < 0
            || !string.Equals(
                request.ContextId,
                ForagePickupContextIds.NormalDirectObjectPickup,
                StringComparison.Ordinal
            )
            || !ForageReplacementCatalog.IsObjectQualifiedItemId(
                request.SourceQualifiedItemId
            )
            || request.ObjectFingerprint <= 0
            || request.SourceStack != 1
            || !IsValidQuality(request.SourceOriginalQuality)
        )
        {
            return false;
        }

        return true;
    }

    internal static bool SameRequest(
        ForagePickupTransactionRequest left,
        ForagePickupTransactionRequest right
    )
    {
        return left.ProtocolVersion == right.ProtocolVersion
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            && left.Nonce == right.Nonce
            && left.CatalogRevision == right.CatalogRevision
            && string.Equals(left.MappingId, right.MappingId, StringComparison.Ordinal)
            && string.Equals(
                left.PickerPlayerKey,
                right.PickerPlayerKey,
                StringComparison.Ordinal
            )
            && left.PickerMultiplayerId == right.PickerMultiplayerId
            && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
            && string.Equals(
                left.LocationInstanceId,
                right.LocationInstanceId,
                StringComparison.Ordinal
            )
            && left.TileX == right.TileX
            && left.TileY == right.TileY
            && string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
            && string.Equals(
                left.SourceQualifiedItemId,
                right.SourceQualifiedItemId,
                StringComparison.Ordinal
            )
            && left.ObjectFingerprint == right.ObjectFingerprint
            && left.SourceStack == right.SourceStack
            && left.SourceOriginalQuality == right.SourceOriginalQuality;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentifierLength)
            return false;
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    private static bool IsValidQuality(int quality)
    {
        return quality is 0 or 1 or 2 or 4;
    }
}

internal readonly record struct ForagePickupAuthorityObservation(
    ForagePickupFactSnapshot PickupFacts,
    SanityPlayerSnapshot? SanitySnapshot,
    int ObservedSourceStack,
    int ObservedSourceOriginalQuality,
    int HarvestQuality,
    int OriginalDeliveryStack,
    int ReplacementDeliveryStack,
    bool InventoryCanAcceptOriginal,
    bool InventoryCanAcceptReplacement
);

internal enum ForagePickupDeliveryKind
{
    Original,
    Replacement,
}

internal readonly record struct ForagePickupCommitPlan(
    string ReceiptId,
    string SessionId,
    string PickerPlayerKey,
    long PickerMultiplayerId,
    string LocationId,
    string LocationInstanceId,
    int TileX,
    int TileY,
    long ObjectFingerprint,
    string SourceQualifiedItemId,
    string DeliveryQualifiedItemId,
    ForagePickupDeliveryKind DeliveryKind,
    int Stack,
    int Quality
);

internal enum ForagePickupAtomicCommitStatus
{
    Applied,
    RejectedBeforeMutation,
    RolledBack,
}

internal readonly record struct ForagePickupAtomicCommitResult(
    ForagePickupAtomicCommitStatus Status,
    string Reason
)
{
    internal static ForagePickupAtomicCommitResult Applied(string reason = "target-granted-source-removed") =>
        new(ForagePickupAtomicCommitStatus.Applied, reason);

    internal static ForagePickupAtomicCommitResult Rejected(string reason = "preflight-rejected") =>
        new(ForagePickupAtomicCommitStatus.RejectedBeforeMutation, reason);

    internal static ForagePickupAtomicCommitResult RolledBack(string reason = "commit-rolled-back") =>
        new(ForagePickupAtomicCommitStatus.RolledBack, reason);
}

/// <summary>
/// A runtime adapter may implement this only when it can preserve the source through preflight,
/// grant the exact target, and remove the exact source as one host-authoritative operation. A
/// rejected or rolled-back result guarantees that the source remains and no provisional target
/// remains in inventory or on the ground.
/// </summary>
internal interface IForagePickupAtomicCommitAdapter
{
    ForagePickupAtomicCommitResult TryCommit(ForagePickupCommitPlan plan);
}

internal enum ForagePickupReceiptOutcome
{
    ReplacementApplied,
    OriginalApplied,
    OriginalFlowPreserved,
}

internal sealed class ForagePickupReceipt
{
    internal ForagePickupReceipt(
        string receiptId,
        ForagePickupTransactionRequest request,
        ForagePickupReceiptOutcome outcome,
        string reason,
        string? targetQualifiedItemId
    )
    {
        ReceiptId = receiptId;
        Request = request;
        Outcome = outcome;
        Reason = reason;
        TargetQualifiedItemId = targetQualifiedItemId;
    }

    internal string ReceiptId { get; }
    internal ForagePickupTransactionRequest Request { get; }
    internal ForagePickupReceiptOutcome Outcome { get; }
    internal string Reason { get; }
    internal string? TargetQualifiedItemId { get; }
}

internal enum ForagePickupTransactionDisposition
{
    ReplacementApplied,
    OriginalApplied,
    OriginalFlowPreserved,
    IgnoredDuplicate,
    Rejected,
}

internal readonly record struct ForagePickupTransactionResult(
    ForagePickupTransactionDisposition Disposition,
    string Reason,
    ForagePickupReceipt? Receipt
)
{
    internal bool Applied =>
        Disposition
            is ForagePickupTransactionDisposition.ReplacementApplied
                or ForagePickupTransactionDisposition.OriginalApplied;

    internal bool ReplacementApplied =>
        Disposition == ForagePickupTransactionDisposition.ReplacementApplied;
}

/// <summary>
/// Pure host authority. It consumes a positive per-picker nonce before world fact evaluation,
/// retains a bounded receipt window while keeping each picker high-water nonce, and never calls
/// the commit adapter unless every request, object, Sanity, mapping, and selected-item capacity
/// fact has been revalidated.
/// </summary>
internal sealed class ForagePickupTransactionService
{
    internal const int MaximumOwners = 256;
    internal const int MaximumReceipts = 256;

    private readonly ForageReplacementCatalog catalog;
    private readonly ForagePickupReplacementCapability capability;
    private readonly Dictionary<string, long> highestNonceByPicker =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForagePickupReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    private string sessionId = string.Empty;

    internal ForagePickupTransactionService(
        ForageReplacementCatalog catalog,
        ForagePickupReplacementCapability capability
    )
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.capability = capability;
    }

    internal string SessionId => sessionId;
    internal int OwnerCount => highestNonceByPicker.Count;
    internal int ReceiptCount => receipts.Count;
    internal int SuccessfulTransactionCount { get; private set; }

    internal bool BeginSession(string newSessionId, out string reason)
    {
        if (!SanityProtocol.IsValidSessionId(newSessionId))
        {
            reason = ForagePickupTransactionReasonIds.SessionInvalid;
            return false;
        }

        ClearSession();
        sessionId = newSessionId;
        reason = ForagePickupTransactionReasonIds.SessionStarted;
        return true;
    }

    internal ForagePickupTransactionResult Handle(
        ForagePickupTransactionRequest? request,
        ForagePickupAuthorityObservation observation,
        IForagePickupAtomicCommitAdapter commitAdapter
    )
    {
        ArgumentNullException.ThrowIfNull(commitAdapter);
        if (!SanityProtocol.IsValidSessionId(sessionId))
            return Rejected(ForagePickupTransactionReasonIds.SessionInactive);
        if (!ForagePickupTransactionProtocol.IsValidRequest(request, sessionId))
            return Rejected(ForagePickupTransactionReasonIds.RequestInvalid);

        var receiptKey = CreateReceiptKey(request!.PickerPlayerKey, request.Nonce);
        if (receipts.TryGetValue(receiptKey, out var previousReceipt))
        {
            if (ForagePickupTransactionProtocol.SameRequest(previousReceipt.Request, request))
            {
                return new ForagePickupTransactionResult(
                    ForagePickupTransactionDisposition.IgnoredDuplicate,
                    ForagePickupTransactionReasonIds.DuplicateReceipt,
                    previousReceipt
                );
            }
            return new ForagePickupTransactionResult(
                ForagePickupTransactionDisposition.Rejected,
                ForagePickupTransactionReasonIds.NonceConflict,
                previousReceipt
            );
        }

        if (
            highestNonceByPicker.TryGetValue(request.PickerPlayerKey, out var highestNonce)
            && request.Nonce <= highestNonce
        )
        {
            return Rejected(
                request.Nonce == highestNonce
                    ? ForagePickupTransactionReasonIds.NonceReplay
                    : ForagePickupTransactionReasonIds.NonceOutOfOrder
            );
        }
        if (
            !highestNonceByPicker.ContainsKey(request.PickerPlayerKey)
            && highestNonceByPicker.Count >= MaximumOwners
        )
        {
            return Rejected(ForagePickupTransactionReasonIds.OwnerWindowFull);
        }
        // A syntactically valid nonce is consumed before evaluating mutable world facts. A failed
        // request must use a new nonce and cannot be replayed after the object or Sanity changes.
        highestNonceByPicker[request.PickerPlayerKey] = request.Nonce;
        var receipt = EvaluateAndCommit(request, observation, commitAdapter);
        while (receipts.Count >= MaximumReceipts && receiptOrder.Count > 0)
            receipts.Remove(receiptOrder.Dequeue());
        receipts.Add(receiptKey, receipt);
        receiptOrder.Enqueue(receiptKey);
        return receipt.Outcome switch
        {
            ForagePickupReceiptOutcome.ReplacementApplied =>
                new ForagePickupTransactionResult(
                ForagePickupTransactionDisposition.ReplacementApplied,
                receipt.Reason,
                receipt
            ),
            ForagePickupReceiptOutcome.OriginalApplied =>
                new ForagePickupTransactionResult(
                    ForagePickupTransactionDisposition.OriginalApplied,
                    receipt.Reason,
                    receipt
                ),
            _ => new ForagePickupTransactionResult(
                ForagePickupTransactionDisposition.OriginalFlowPreserved,
                receipt.Reason,
                receipt
            ),
        };
    }

    internal void ClearOwner(string pickerPlayerKey)
    {
        // Keep the high-water nonce for the whole session. A disconnect/reconnect or owner
        // invalidation must not make an already-consumed network request replayable.
        if (receipts.Count == 0)
            return;

        var remove = new List<string>();
        foreach (var pair in receipts)
        {
            if (
                string.Equals(
                    pair.Value.Request.PickerPlayerKey,
                    pickerPlayerKey,
                    StringComparison.Ordinal
                )
            )
            {
                remove.Add(pair.Key);
            }
        }
        foreach (var key in remove)
            receipts.Remove(key);
        if (remove.Count > 0)
            RebuildReceiptOrder();
    }

    internal void ClearWindow()
    {
        highestNonceByPicker.Clear();
        receipts.Clear();
        receiptOrder.Clear();
        SuccessfulTransactionCount = 0;
    }

    internal void ClearSession()
    {
        ClearWindow();
        sessionId = string.Empty;
    }

    private ForagePickupReceipt EvaluateAndCommit(
        ForagePickupTransactionRequest request,
        ForagePickupAuthorityObservation observation,
        IForagePickupAtomicCommitAdapter commitAdapter
    )
    {
        var receiptId = CreateReceiptId(request);
        if (!capability.CanExecute)
            return Preserved(receiptId, request, capability.Reason);
        if (!ObservationMatchesRequest(request, observation))
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.ObservationMismatch
            );
        }

        var sanity = observation.SanitySnapshot;
        if (sanity is null || !SanityProtocol.IsValidSnapshot(sanity))
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.SanitySnapshotInvalid
            );
        }
        if (
            !string.Equals(
                sanity.PlayerKey,
                request.PickerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.SanitySnapshotMismatch
            );
        }

        var sanityEligibility = ForageInteractionSanityGate.Evaluate(
            sanity.Current,
            sanity.Maximum
        );

        var eligibility = ForageReplacementEligibilityGate.Evaluate(
            catalog,
            observation.PickupFacts
        );
        if (!eligibility.IsEligible || eligibility.Mapping?.TargetQualifiedItemId is null)
            return Preserved(receiptId, request, eligibility.Reason);
        if (
            request.CatalogRevision != catalog.Revision
            || !string.Equals(
                request.MappingId,
                eligibility.Mapping.Id,
                StringComparison.Ordinal
            )
        )
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.ObservationMismatch
            );
        }

        var replacementSelected = sanityEligibility.IsEligible;
        var inventoryCanAccept = replacementSelected
            ? observation.InventoryCanAcceptReplacement
            : observation.InventoryCanAcceptOriginal;
        if (!inventoryCanAccept)
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.InventoryCapacityUnavailable,
                replacementSelected
                    ? eligibility.Mapping.TargetQualifiedItemId
                    : request.SourceQualifiedItemId
            );
        }

        var deliveryQualifiedItemId = replacementSelected
            ? eligibility.Mapping.TargetQualifiedItemId
            : request.SourceQualifiedItemId;
        var deliveryStack = replacementSelected
            ? observation.ReplacementDeliveryStack
            : observation.OriginalDeliveryStack;
        if (
            deliveryStack is < 1 or > ForagePickupTransactionProtocol.MaximumStack
            || observation.HarvestQuality is not (0 or 1 or 2 or 4)
        )
        {
            return Preserved(
                receiptId,
                request,
                ForagePickupTransactionReasonIds.ObservationMismatch
            );
        }
        var plan = new ForagePickupCommitPlan(
            receiptId,
            request.SessionId,
            request.PickerPlayerKey,
            request.PickerMultiplayerId,
            request.LocationId,
            request.LocationInstanceId,
            request.TileX,
            request.TileY,
            request.ObjectFingerprint,
            request.SourceQualifiedItemId,
            deliveryQualifiedItemId,
            replacementSelected
                ? ForagePickupDeliveryKind.Replacement
                : ForagePickupDeliveryKind.Original,
            deliveryStack,
            observation.HarvestQuality
        );
        var commit = commitAdapter.TryCommit(plan);
        switch (commit.Status)
        {
            case ForagePickupAtomicCommitStatus.Applied:
                SuccessfulTransactionCount++;
                return new ForagePickupReceipt(
                    receiptId,
                    request,
                    replacementSelected
                        ? ForagePickupReceiptOutcome.ReplacementApplied
                        : ForagePickupReceiptOutcome.OriginalApplied,
                    replacementSelected
                        ? ForagePickupTransactionReasonIds.CommitApplied
                        : ForagePickupTransactionReasonIds.OriginalCommitApplied,
                    plan.DeliveryQualifiedItemId
                );
            case ForagePickupAtomicCommitStatus.RejectedBeforeMutation:
                return Preserved(
                    receiptId,
                    request,
                    ForagePickupTransactionReasonIds.CommitRejectedOriginalPreserved,
                    plan.DeliveryQualifiedItemId
                );
            case ForagePickupAtomicCommitStatus.RolledBack:
                return Preserved(
                    receiptId,
                    request,
                    ForagePickupTransactionReasonIds.CommitRolledBackOriginalPreserved,
                    plan.DeliveryQualifiedItemId
                );
            default:
                return Preserved(
                    receiptId,
                    request,
                    ForagePickupTransactionReasonIds.CommitResultInvalid,
                    plan.DeliveryQualifiedItemId
                );
        }
    }

    private static bool ObservationMatchesRequest(
        ForagePickupTransactionRequest request,
        ForagePickupAuthorityObservation observation
    )
    {
        var facts = observation.PickupFacts;
        return facts.PickerMultiplayerId == request.PickerMultiplayerId
            && string.Equals(facts.LocationId, request.LocationId, StringComparison.Ordinal)
            && string.Equals(
                facts.LocationInstanceId,
                request.LocationInstanceId,
                StringComparison.Ordinal
            )
            && facts.TileX == request.TileX
            && facts.TileY == request.TileY
            && string.Equals(facts.ContextId, request.ContextId, StringComparison.Ordinal)
            && string.Equals(
                facts.SourceQualifiedItemId,
                request.SourceQualifiedItemId,
                StringComparison.Ordinal
            )
            && facts.ObjectFingerprint == request.ObjectFingerprint
            && observation.ObservedSourceStack == request.SourceStack
            && observation.ObservedSourceOriginalQuality
                == request.SourceOriginalQuality;
    }

    private static ForagePickupReceipt Preserved(
        string receiptId,
        ForagePickupTransactionRequest request,
        string reason,
        string? targetQualifiedItemId = null
    )
    {
        return new ForagePickupReceipt(
            receiptId,
            request,
            ForagePickupReceiptOutcome.OriginalFlowPreserved,
            reason,
            targetQualifiedItemId
        );
    }

    private static ForagePickupTransactionResult Rejected(string reason)
    {
        return new ForagePickupTransactionResult(
            ForagePickupTransactionDisposition.Rejected,
            reason,
            null
        );
    }

    private static string CreateReceiptKey(string pickerPlayerKey, long nonce)
    {
        return string.Concat(
            pickerPlayerKey,
            "|",
            nonce.ToString(CultureInfo.InvariantCulture)
        );
    }

    private void RebuildReceiptOrder()
    {
        var retained = new Queue<string>();
        while (receiptOrder.Count > 0)
        {
            var key = receiptOrder.Dequeue();
            if (receipts.ContainsKey(key))
                retained.Enqueue(key);
        }
        while (retained.Count > 0)
            receiptOrder.Enqueue(retained.Dequeue());
    }

    private static string CreateReceiptId(ForagePickupTransactionRequest request)
    {
        var input = string.Concat(
            request.SessionId,
            "|",
            request.PickerPlayerKey,
            "|",
            request.Nonce.ToString(CultureInfo.InvariantCulture),
            "|",
            request.LocationId,
            "|",
            request.LocationInstanceId,
            "|",
            request.TileX.ToString(CultureInfo.InvariantCulture),
            ",",
            request.TileY.ToString(CultureInfo.InvariantCulture),
            "|",
            request.SourceQualifiedItemId,
            "|",
            request.ObjectFingerprint.ToString(CultureInfo.InvariantCulture),
            "|",
            request.CatalogRevision.ToString(CultureInfo.InvariantCulture),
            "|",
            request.MappingId
        );

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in input)
        {
            hash ^= character;
            hash *= prime;
        }
        return string.Concat(
            "forage.pickup.receipt.",
            hash.ToString("X16", CultureInfo.InvariantCulture)
        );
    }
}
