#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class DarkHandThiefOperationIds
{
    internal const string Thief = "dark-hand.interaction.thief";
}

internal static class DarkHandThiefModeIds
{
    internal const string Thief = "Thief";
}

internal static class DarkHandThiefReasonIds
{
    internal const string CatalogUnavailable = "dark-hand.thief.capability.catalog-unavailable";
    internal const string NoEnabledTargets = "dark-hand.thief.capability.no-enabled-targets";
    internal const string NoAuthorizedTargets = "dark-hand.thief.capability.no-authorized-targets";
    internal const string AuthorityRevisionUnavailable =
        "dark-hand.thief.capability.authority-revision-unavailable";
    internal const string AtomicAdapterUnavailable =
        "dark-hand.thief.capability.atomic-adapter-unavailable";
    internal const string HostAuthorityRequired = "dark-hand.thief.host-authority-required";
    internal const string SenderOwnerMismatch = "dark-hand.thief.sender-owner-mismatch";
    internal const string ModeRequired = "dark-hand.thief.mode-required";
    internal const string DeletionScopeForbidden = "dark-hand.thief.deletion-scope-forbidden";
    internal const string TargetMissing = "dark-hand.thief.target-missing";
    internal const string TargetScopeMismatch = "dark-hand.thief.target-scope-mismatch";
    internal const string OwnerLocationMismatch = "dark-hand.thief.owner-location-mismatch";
    internal const string OwnerOutOfRange = "dark-hand.thief.owner-out-of-range";
    internal const string TargetNotAllowlisted = "dark-hand.thief.target-not-allowlisted";
    internal const string TargetDisabled = "dark-hand.thief.target-disabled";
    internal const string TargetExcluded = "dark-hand.thief.target-excluded";
    internal const string TargetNotAuthorized = "dark-hand.thief.target-not-authorized";
    internal const string LocationNotAllowlisted = "dark-hand.thief.location-not-allowlisted";
    internal const string CategoryMismatch = "dark-hand.thief.category-mismatch";
    internal const string CategoryUnsupported = "dark-hand.thief.category-unsupported";
    internal const string Empty = "dark-hand.thief.machine-empty";
    internal const string ContentProtectionUnproven =
        "dark-hand.thief.content-protection-unproven";
    internal const string OperationUnavailable = "dark-hand.thief.operation-unavailable";
    internal const string Eligible = "dark-hand.thief.eligible";
    internal const string AdapterRejected = "dark-hand.thief.adapter-rejected";
    internal const string AdapterRolledBack = "dark-hand.thief.adapter-rolled-back";
    internal const string AdapterContractInvalid =
        "dark-hand.thief.adapter-contract-invalid";
    internal const string RegistryUpdateRejected =
        "dark-hand.thief.registry-update-rejected";
    internal const string Applied = "dark-hand.thief.applied";
    internal const string ReceiptWindowFull = "dark-hand.thief.receipt-window-full";
    internal const string ReceiptDuplicate = "dark-hand.thief.receipt-duplicate";
    internal const string ReceiptConflict = "dark-hand.thief.receipt-conflict";
}

internal enum DarkHandThiefCapabilityStatus
{
    Available,
    UnavailableCatalog,
    ReadOnlyNoEnabledTargets,
    ReadOnlyNoAuthorizedTargets,
    ReadOnlyAuthorityRevisionUnavailable,
    ReadOnlyAtomicAdapterUnavailable,
}

internal readonly record struct DarkHandThiefCapability(
    DarkHandThiefCapabilityStatus Status,
    string Reason,
    int CandidateTargetCount,
    int EnabledTargetCount,
    int AuthorizedTargetCount,
    bool CanCommitDelete
);

internal static class DarkHandThiefCapabilityGate
{
    internal static DarkHandThiefCapability Evaluate(
        MachineTargetCatalog catalog,
        MachineRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsAvailable)
            return Result(DarkHandThiefCapabilityStatus.UnavailableCatalog, DarkHandThiefReasonIds.CatalogUnavailable, 0, false);
        if (catalog.EnabledTargetCount == 0)
            return Result(DarkHandThiefCapabilityStatus.ReadOnlyNoEnabledTargets, DarkHandThiefReasonIds.NoEnabledTargets, 0, false);

        var authorized = 0;
        foreach (var target in catalog.Targets)
        {
            if (target.Enabled && !target.Excluded && target.AllowThief)
                authorized++;
        }
        if (authorized == 0)
            return Result(DarkHandThiefCapabilityStatus.ReadOnlyNoAuthorizedTargets, DarkHandThiefReasonIds.NoAuthorizedTargets, 0, false);
        if (!evidence.StableAuthorityRevisionAvailable)
            return Result(DarkHandThiefCapabilityStatus.ReadOnlyAuthorityRevisionUnavailable, DarkHandThiefReasonIds.AuthorityRevisionUnavailable, authorized, false);
        if (!evidence.AtomicContentDeletionAdapterAvailable)
            return Result(DarkHandThiefCapabilityStatus.ReadOnlyAtomicAdapterUnavailable, DarkHandThiefReasonIds.AtomicAdapterUnavailable, authorized, false);
        return Result(DarkHandThiefCapabilityStatus.Available, DarkHandThiefReasonIds.Eligible, authorized, true);

        DarkHandThiefCapability Result(
            DarkHandThiefCapabilityStatus status,
            string reason,
            int authorizedTargetCount,
            bool canCommitDelete
        ) => new(
            status,
            reason,
            catalog.Targets.Count,
            catalog.EnabledTargetCount,
            authorizedTargetCount,
            canCommitDelete
        );
    }
}

internal enum DarkHandThiefDeletionScope
{
    MachineContents,
    MachineObject,
    Chest,
    PlayerInventoryItem,
    QuestOrProtectedItem,
    Map,
    SaveData,
}

internal readonly record struct DarkHandThiefOperationScope(
    bool IsHostAuthority,
    string SenderPlayerKey,
    string OwnerPlayerKey,
    string ModeId,
    string OwnerLocationId,
    double OwnerDistanceTiles,
    DarkHandThiefDeletionScope DeletionScope
);

internal enum DarkHandThiefDeletedContentKind
{
    None,
    RunningContents,
    ReadyOutput,
}

internal readonly record struct DarkHandThiefEligibilityResult(
    bool IsEligible,
    string Reason,
    MachineTargetDefinition? Definition,
    DarkHandThiefDeletedContentKind ContentKind
);

internal static class DarkHandThiefEligibilityGate
{
    internal const double MaximumRangeTiles = 20d;

    internal static DarkHandThiefEligibilityResult Evaluate(
        MachineTargetCatalog catalog,
        DarkHandThiefCapability capability,
        MachineInteractionSnapshot? snapshot,
        DarkHandThiefOperationScope scope
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!scope.IsHostAuthority)
            return Rejected(DarkHandThiefReasonIds.HostAuthorityRequired);
        if (!SanityPlayerKey.IsCanonical(scope.OwnerPlayerKey)
            || !string.Equals(scope.SenderPlayerKey, scope.OwnerPlayerKey, StringComparison.Ordinal))
            return Rejected(DarkHandThiefReasonIds.SenderOwnerMismatch);
        if (!string.Equals(scope.ModeId, DarkHandThiefModeIds.Thief, StringComparison.Ordinal))
            return Rejected(DarkHandThiefReasonIds.ModeRequired);
        if (scope.DeletionScope != DarkHandThiefDeletionScope.MachineContents)
            return Rejected(DarkHandThiefReasonIds.DeletionScopeForbidden);
        if (snapshot is null)
            return Rejected(DarkHandThiefReasonIds.TargetMissing);
        if (snapshot.AuthorityRevision <= 0
            || snapshot.StateFingerprint.Length != 64
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.TargetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.LocationId))
            return Rejected(DarkHandThiefReasonIds.TargetScopeMismatch);
        if (!string.Equals(scope.OwnerLocationId, snapshot.LocationId, StringComparison.Ordinal))
            return Rejected(DarkHandThiefReasonIds.OwnerLocationMismatch);
        if (!double.IsFinite(scope.OwnerDistanceTiles)
            || scope.OwnerDistanceTiles < 0d
            || scope.OwnerDistanceTiles > MaximumRangeTiles)
            return Rejected(DarkHandThiefReasonIds.OwnerOutOfRange);

        var definition = catalog.Find(snapshot.QualifiedItemId);
        if (definition is null)
            return Rejected(DarkHandThiefReasonIds.TargetNotAllowlisted);
        if (definition.Excluded)
            return Rejected(DarkHandThiefReasonIds.TargetExcluded, definition);
        if (!definition.Enabled)
            return Rejected(DarkHandThiefReasonIds.TargetDisabled, definition);
        if (!definition.AllowThief)
            return Rejected(DarkHandThiefReasonIds.TargetNotAuthorized, definition);
        if (!definition.AllowsLocation(snapshot.LocationId))
            return Rejected(DarkHandThiefReasonIds.LocationNotAllowlisted, definition);
        if (definition.ExpectedCategory != snapshot.Category)
            return Rejected(DarkHandThiefReasonIds.CategoryMismatch, definition);
        if (snapshot.Category is MachineInteractionCategory.MultiInput
            or MachineInteractionCategory.Unsupported)
            return Rejected(DarkHandThiefReasonIds.CategoryUnsupported, definition);
        if (snapshot.State == MachineLifecycleState.Empty)
            return Rejected(DarkHandThiefReasonIds.Empty, definition);
        if (!HasOnlyOrdinaryContent(snapshot))
            return Rejected(DarkHandThiefReasonIds.ContentProtectionUnproven, definition);
        if (!capability.CanCommitDelete || !snapshot.CanDeleteContent)
            return Rejected(DarkHandThiefReasonIds.OperationUnavailable, definition);

        return new DarkHandThiefEligibilityResult(
            true,
            DarkHandThiefReasonIds.Eligible,
            definition,
            snapshot.State == MachineLifecycleState.Ready
                ? DarkHandThiefDeletedContentKind.ReadyOutput
                : DarkHandThiefDeletedContentKind.RunningContents
        );
    }

    private static bool HasOnlyOrdinaryContent(MachineInteractionSnapshot snapshot)
    {
        var found = false;
        foreach (var item in new[] { snapshot.HeldOutput, snapshot.LastInputItem })
        {
            if (item is null)
                continue;
            found = true;
            if (item.IsRecipe || item.Protection != MachineContentProtection.Ordinary)
                return false;
        }
        return found;
    }

    private static DarkHandThiefEligibilityResult Rejected(
        string reason,
        MachineTargetDefinition? definition = null
    ) => new(false, reason, definition, DarkHandThiefDeletedContentKind.None);
}

internal sealed record DarkHandThiefCommitPlan(
    string OwnerPlayerKey,
    string LeaseId,
    MachineMutationStateImage Before,
    DarkHandThiefDeletedContentKind ContentKind,
    MachineItemFacts? DeletedInput,
    MachineItemFacts? DeletedOutput
);

internal enum DarkHandThiefAdapterStatus
{
    Applied,
    Rejected,
    RolledBack,
}

internal sealed record DarkHandThiefAdapterResult(
    DarkHandThiefAdapterStatus Status,
    string Reason,
    MachineInteractionSnapshot? SnapshotAfter,
    bool WorldMutationApplied,
    bool RollbackVerified,
    bool ContentPermanentlyDeleted,
    bool MachineObjectPreserved,
    bool ExternalStatePreserved
);

/// <summary>
/// Atomic machine-contents-only seam. Implementations may stop one exact machine and clear its
/// current contents, but can never receive a chest, player inventory, map, save, or machine-object
/// deletion handle. Rejected and rolled-back results must reproduce the frozen state image.
/// </summary>
internal interface IDarkHandThiefMachineAdapter
{
    DarkHandThiefAdapterResult CommitDelete(DarkHandThiefCommitPlan plan);
}

internal enum DarkHandThiefReceiptOutcome
{
    Applied,
    Rejected,
    RolledBack,
}

internal sealed record DarkHandThiefReceipt(
    int SchemaVersion,
    string LeaseId,
    string SessionId,
    string OwnerPlayerKey,
    DarkHandThiefDeletedContentKind ContentKind,
    MachineItemFacts? DeletedInput,
    MachineItemFacts? DeletedOutput,
    DarkHandThiefReceiptOutcome Outcome,
    string Reason,
    bool WorldMutationApplied,
    bool ContentPermanentlyDeleted,
    bool MachineObjectPreserved,
    bool ExternalStatePreserved,
    bool RollbackVerified,
    MachineMutationStateImage Before,
    MachineMutationStateImage? After
)
{
    internal const int CurrentSchemaVersion = 1;
}

internal enum DarkHandThiefTransactionDisposition
{
    Applied,
    Duplicate,
    Rejected,
    RolledBack,
}

internal readonly record struct DarkHandThiefTransactionResult(
    DarkHandThiefTransactionDisposition Disposition,
    string Reason,
    DarkHandThiefReceipt? Receipt
)
{
    internal bool Applied => Disposition == DarkHandThiefTransactionDisposition.Applied;
}

/// <summary>
/// Pure host transaction that consumes task-07's existing one-shot lease and task-11's existing
/// machine snapshot registry. Production does not construct it while the shipped catalog,
/// authority revision, and atomic deletion adapter remain unavailable.
/// </summary>
internal sealed class DarkHandThiefOperationService
{
    internal const int MaximumReceipts = 256;

    private sealed record ReceiptEntry(DarkHandInteractionLease Lease, DarkHandThiefReceipt Receipt);

    private readonly MachineTargetCatalog catalog;
    private readonly DarkHandThiefCapability capability;
    private readonly DarkHandInteractionLeaseAuthority leaseAuthority;
    private readonly MachineSnapshotRegistry registry;
    private readonly Dictionary<string, ReceiptEntry> receipts = new(StringComparer.Ordinal);

    internal DarkHandThiefOperationService(
        MachineTargetCatalog catalog,
        DarkHandThiefCapability capability,
        DarkHandInteractionLeaseAuthority leaseAuthority,
        MachineSnapshotRegistry registry
    )
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.capability = capability;
        this.leaseAuthority = leaseAuthority ?? throw new ArgumentNullException(nameof(leaseAuthority));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal int ReceiptCount => receipts.Count;
    internal int SuccessfulCommitCount { get; private set; }

    internal DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest? request,
        DarkHandThiefOperationScope scope,
        long nowTick,
        bool requireExclusiveTargetOwner = false
    )
    {
        if (!capability.CanCommitDelete)
            return new DarkHandLeaseIssueResult(DarkHandLeaseIssueStatus.Rejected, capability.Reason, null);
        return leaseAuthority.TryIssue(
            request,
            scope.SenderPlayerKey,
            nowTick,
            new ScopedTargetAuthority(catalog, capability, registry, scope),
            requireExclusiveTargetOwner
        );
    }

    internal DarkHandThiefTransactionResult Commit(
        DarkHandInteractionLease? lease,
        DarkHandThiefOperationScope scope,
        long nowTick,
        IDarkHandThiefMachineAdapter adapter
    )
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!capability.CanCommitDelete)
            return Rejected(capability.Reason);
        if (lease is not null && receipts.TryGetValue(lease.LeaseId, out var existing))
        {
            return DarkHandInteractionLeaseProtocol.SameLease(existing.Lease, lease)
                ? new DarkHandThiefTransactionResult(
                    DarkHandThiefTransactionDisposition.Duplicate,
                    DarkHandThiefReasonIds.ReceiptDuplicate,
                    existing.Receipt
                )
                : Rejected(DarkHandThiefReasonIds.ReceiptConflict);
        }
        if (receipts.Count >= MaximumReceipts)
            return Rejected(DarkHandThiefReasonIds.ReceiptWindowFull);

        registry.TryGet(lease?.TargetId ?? string.Empty, out var snapshot);
        var currentTarget = snapshot is null
            ? null
            : new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                DarkHandThiefOperationIds.Thief,
                snapshot.AuthorityRevision
            );
        var leaseValidation = leaseAuthority.ValidateAndConsume(
            lease,
            scope.SenderPlayerKey,
            nowTick,
            currentTarget
        );
        if (!leaseValidation.Accepted)
            return Rejected(leaseValidation.Reason);

        var eligibility = DarkHandThiefEligibilityGate.Evaluate(catalog, capability, snapshot, scope);
        if (!eligibility.IsEligible)
            return CompletePolicyRejection(lease!, snapshot!, eligibility.Reason);

        var currentSnapshot = snapshot!;
        var adapterResult = adapter.CommitDelete(
            new DarkHandThiefCommitPlan(
                scope.OwnerPlayerKey,
                lease!.LeaseId,
                MachineMutationStateImage.Capture(currentSnapshot),
                eligibility.ContentKind,
                currentSnapshot.LastInputItem,
                currentSnapshot.HeldOutput
            )
        );
        return CompleteAdapterResult(lease, currentSnapshot, eligibility.ContentKind, adapterResult);
    }

    internal void ClearWindow()
    {
        receipts.Clear();
        SuccessfulCommitCount = 0;
    }

    private DarkHandThiefTransactionResult CompleteAdapterResult(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot beforeSnapshot,
        DarkHandThiefDeletedContentKind contentKind,
        DarkHandThiefAdapterResult adapterResult
    )
    {
        var before = MachineMutationStateImage.Capture(beforeSnapshot);
        var after = adapterResult.SnapshotAfter is null
            ? null
            : MachineMutationStateImage.Capture(adapterResult.SnapshotAfter);
        if (adapterResult.Status is DarkHandThiefAdapterStatus.Rejected or DarkHandThiefAdapterStatus.RolledBack)
        {
            var exactRollback = after is not null && Equals(before, after);
            var contractValid = exactRollback
                && !adapterResult.WorldMutationApplied
                && !adapterResult.ContentPermanentlyDeleted
                && adapterResult.MachineObjectPreserved
                && adapterResult.ExternalStatePreserved
                && (adapterResult.Status != DarkHandThiefAdapterStatus.RolledBack || adapterResult.RollbackVerified);
            return Complete(
                lease,
                contentKind,
                adapterResult.Status == DarkHandThiefAdapterStatus.RolledBack
                    ? DarkHandThiefReceiptOutcome.RolledBack
                    : DarkHandThiefReceiptOutcome.Rejected,
                contractValid
                    ? adapterResult.Status == DarkHandThiefAdapterStatus.RolledBack
                        ? DarkHandThiefReasonIds.AdapterRolledBack
                        : string.IsNullOrWhiteSpace(adapterResult.Reason)
                            ? DarkHandThiefReasonIds.AdapterRejected
                            : adapterResult.Reason
                    : DarkHandThiefReasonIds.AdapterContractInvalid,
                adapterResult.WorldMutationApplied,
                adapterResult.ContentPermanentlyDeleted,
                adapterResult.MachineObjectPreserved,
                adapterResult.ExternalStatePreserved,
                contractValid && (adapterResult.Status == DarkHandThiefAdapterStatus.Rejected || adapterResult.RollbackVerified),
                before,
                after
            );
        }

        var appliedContractValid = adapterResult.Status == DarkHandThiefAdapterStatus.Applied
            && adapterResult.WorldMutationApplied
            && adapterResult.ContentPermanentlyDeleted
            && adapterResult.MachineObjectPreserved
            && adapterResult.ExternalStatePreserved
            && !adapterResult.RollbackVerified
            && adapterResult.SnapshotAfter is not null
            && IsValidAppliedSnapshot(beforeSnapshot, adapterResult.SnapshotAfter);
        if (!appliedContractValid)
        {
            return Complete(
                lease,
                contentKind,
                DarkHandThiefReceiptOutcome.Rejected,
                DarkHandThiefReasonIds.AdapterContractInvalid,
                adapterResult.WorldMutationApplied,
                adapterResult.ContentPermanentlyDeleted,
                adapterResult.MachineObjectPreserved,
                adapterResult.ExternalStatePreserved,
                adapterResult.RollbackVerified,
                before,
                after
            );
        }

        var registryUpdate = registry.Upsert(adapterResult.SnapshotAfter);
        if (registryUpdate.Status != MachineSnapshotRegistryUpdateStatus.Accepted)
        {
            return Complete(
                lease,
                contentKind,
                DarkHandThiefReceiptOutcome.Rejected,
                DarkHandThiefReasonIds.RegistryUpdateRejected,
                adapterResult.WorldMutationApplied,
                adapterResult.ContentPermanentlyDeleted,
                adapterResult.MachineObjectPreserved,
                adapterResult.ExternalStatePreserved,
                adapterResult.RollbackVerified,
                before,
                after
            );
        }

        SuccessfulCommitCount++;
        return Complete(
            lease,
            contentKind,
            DarkHandThiefReceiptOutcome.Applied,
            DarkHandThiefReasonIds.Applied,
            WorldMutationApplied: true,
            ContentPermanentlyDeleted: true,
            MachineObjectPreserved: true,
            ExternalStatePreserved: true,
            RollbackVerified: false,
            before,
            after
        );
    }

    private DarkHandThiefTransactionResult CompletePolicyRejection(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot snapshot,
        string reason
    )
    {
        var image = MachineMutationStateImage.Capture(snapshot);
        return Complete(
            lease,
            DarkHandThiefDeletedContentKind.None,
            DarkHandThiefReceiptOutcome.Rejected,
            reason,
            WorldMutationApplied: false,
            ContentPermanentlyDeleted: false,
            MachineObjectPreserved: true,
            ExternalStatePreserved: true,
            RollbackVerified: true,
            image,
            image
        );
    }

    private DarkHandThiefTransactionResult Complete(
        DarkHandInteractionLease lease,
        DarkHandThiefDeletedContentKind contentKind,
        DarkHandThiefReceiptOutcome outcome,
        string reason,
        bool WorldMutationApplied,
        bool ContentPermanentlyDeleted,
        bool MachineObjectPreserved,
        bool ExternalStatePreserved,
        bool RollbackVerified,
        MachineMutationStateImage before,
        MachineMutationStateImage? after
    )
    {
        var receipt = new DarkHandThiefReceipt(
            DarkHandThiefReceipt.CurrentSchemaVersion,
            lease.LeaseId,
            lease.SessionId,
            lease.OwnerPlayerKey,
            contentKind,
            before.LastInputItem,
            before.HeldOutput,
            outcome,
            reason,
            WorldMutationApplied,
            ContentPermanentlyDeleted,
            MachineObjectPreserved,
            ExternalStatePreserved,
            RollbackVerified,
            before,
            after
        );
        receipts.Add(lease.LeaseId, new ReceiptEntry(lease.Clone(), receipt));
        return new DarkHandThiefTransactionResult(
            outcome switch
            {
                DarkHandThiefReceiptOutcome.Applied => DarkHandThiefTransactionDisposition.Applied,
                DarkHandThiefReceiptOutcome.RolledBack => DarkHandThiefTransactionDisposition.RolledBack,
                _ => DarkHandThiefTransactionDisposition.Rejected,
            },
            reason,
            receipt
        );
    }

    private static bool IsValidAppliedSnapshot(
        MachineInteractionSnapshot before,
        MachineInteractionSnapshot after
    )
    {
        return SameIdentity(before, after)
            && after.AuthorityRevision > before.AuthorityRevision
            && after.StateFingerprint.Length == 64
            && !string.Equals(after.StateFingerprint, before.StateFingerprint, StringComparison.Ordinal)
            && after.State == MachineLifecycleState.Empty
            && (after.Category == MachineInteractionCategory.Unsupported || after.Category == before.Category)
            && (after.Schedule == MachineScheduleKind.None || after.Schedule == before.Schedule)
            && string.IsNullOrEmpty(after.ActiveRuleId)
            && after.MinutesUntilReady == 0
            && !after.ReadyForHarvest
            && !after.ShowNextIndex
            && after.HeldOutput is null
            && after.LastInputItem is null
            && !after.CanDeleteContent;
    }

    private static bool SameIdentity(MachineInteractionSnapshot left, MachineInteractionSnapshot right) =>
        string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
        && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
        && string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal);

    private static DarkHandThiefTransactionResult Rejected(string reason) =>
        new(DarkHandThiefTransactionDisposition.Rejected, reason, null);

    private sealed class ScopedTargetAuthority : IDarkHandLeaseTargetAuthority
    {
        private readonly MachineTargetCatalog catalog;
        private readonly DarkHandThiefCapability capability;
        private readonly MachineSnapshotRegistry registry;
        private readonly DarkHandThiefOperationScope scope;

        internal ScopedTargetAuthority(
            MachineTargetCatalog catalog,
            DarkHandThiefCapability capability,
            MachineSnapshotRegistry registry,
            DarkHandThiefOperationScope scope
        )
        {
            this.catalog = catalog;
            this.capability = capability;
            this.registry = registry;
            this.scope = scope;
        }

        public bool TryResolve(string targetId, out DarkHandLeaseTargetSnapshot? target, out string reason)
        {
            if (!registry.TryGet(targetId, out var snapshot) || snapshot is null)
            {
                target = null;
                reason = DarkHandThiefReasonIds.TargetMissing;
                return false;
            }
            var eligibility = DarkHandThiefEligibilityGate.Evaluate(catalog, capability, snapshot, scope);
            if (!eligibility.IsEligible)
            {
                target = null;
                reason = eligibility.Reason;
                return false;
            }
            target = new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                DarkHandThiefOperationIds.Thief,
                snapshot.AuthorityRevision
            );
            reason = DarkHandThiefReasonIds.Eligible;
            return true;
        }
    }
}
