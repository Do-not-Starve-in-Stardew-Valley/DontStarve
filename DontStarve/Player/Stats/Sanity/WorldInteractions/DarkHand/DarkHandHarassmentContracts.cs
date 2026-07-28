#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class DarkHandHarassmentOperationIds
{
    internal const string Harassment = "dark-hand.interaction.harassment";
}

internal static class DarkHandHarassmentModeIds
{
    internal const string Harassment = "Harassment";
}

internal static class DarkHandHarassmentReasonIds
{
    internal const string HostAuthorityRequired =
        "dark-hand.harassment.host-authority-required";
    internal const string SenderOwnerMismatch =
        "dark-hand.harassment.sender-owner-mismatch";
    internal const string ModeRequired = "dark-hand.harassment.mode-required";
    internal const string TargetMissing = "dark-hand.harassment.target-missing";
    internal const string TargetScopeMismatch =
        "dark-hand.harassment.target-scope-mismatch";
    internal const string OwnerLocationMismatch =
        "dark-hand.harassment.owner-location-mismatch";
    internal const string OwnerOutOfRange =
        "dark-hand.harassment.owner-out-of-range";
    internal const string TargetNotAllowlisted =
        "dark-hand.harassment.target-not-allowlisted";
    internal const string TargetDisabled = "dark-hand.harassment.target-disabled";
    internal const string TargetExcluded = "dark-hand.harassment.target-excluded";
    internal const string LocationNotAllowlisted =
        "dark-hand.harassment.location-not-allowlisted";
    internal const string CategoryMismatch =
        "dark-hand.harassment.category-mismatch";
    internal const string OperationUnavailable =
        "dark-hand.harassment.operation-unavailable";
    internal const string Eligible = "dark-hand.harassment.eligible";
    internal const string RandomInvalid = "dark-hand.harassment.random-invalid";
    internal const string DelayUnavailable =
        "dark-hand.harassment.delay-unavailable";
    internal const string ReadyDelayInvalid =
        "dark-hand.harassment.ready-delay-invalid";
    internal const string DelayOverflow = "dark-hand.harassment.delay-overflow";
    internal const string EjectUnavailable =
        "dark-hand.harassment.eject-unavailable";
    internal const string LandingUnavailable =
        "dark-hand.harassment.landing-unavailable";
    internal const string LandingReservationInvalid =
        "dark-hand.harassment.landing-reservation-invalid";
    internal const string AdapterRejected =
        "dark-hand.harassment.adapter-rejected";
    internal const string AdapterRolledBack =
        "dark-hand.harassment.adapter-rolled-back";
    internal const string AdapterContractInvalid =
        "dark-hand.harassment.adapter-contract-invalid";
    internal const string RegistryUpdateRejected =
        "dark-hand.harassment.registry-update-rejected";
    internal const string DelayApplied = "dark-hand.harassment.delay-applied";
    internal const string EjectApplied = "dark-hand.harassment.eject-applied";
    internal const string ReceiptWindowFull =
        "dark-hand.harassment.receipt-window-full";
    internal const string ReceiptDuplicate =
        "dark-hand.harassment.receipt-duplicate";
    internal const string ReceiptConflict =
        "dark-hand.harassment.receipt-conflict";
}

internal enum DarkHandHarassmentAction
{
    None,
    Delay,
    Eject,
}

internal interface IDarkHandHarassmentRandomSource
{
    /// <summary>
    /// Returns a deterministic sample in the inclusive interval [0,1]. Inclusive endpoints keep
    /// the 25% and 125% delay bounds directly testable without wall-clock or statistical tests.
    /// </summary>
    double NextUnitInterval();
}

internal readonly record struct DarkHandHarassmentOperationScope(
    bool IsHostAuthority,
    string SenderPlayerKey,
    string OwnerPlayerKey,
    string ModeId,
    string OwnerLocationId,
    double OwnerDistanceTiles
);

internal readonly record struct DarkHandHarassmentEligibilityResult(
    bool IsEligible,
    string Reason,
    MachineTargetDefinition? Definition,
    bool CanDelay,
    bool CanEject
);

internal static class DarkHandHarassmentEligibilityGate
{
    internal const double MaximumRangeTiles = 20d;

    internal static DarkHandHarassmentEligibilityResult Evaluate(
        MachineTargetCatalog catalog,
        MachineInteractionCapability capability,
        MachineInteractionSnapshot? snapshot,
        DarkHandHarassmentOperationScope scope
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!scope.IsHostAuthority)
            return Rejected(DarkHandHarassmentReasonIds.HostAuthorityRequired);
        if (
            !SanityPlayerKey.IsCanonical(scope.OwnerPlayerKey)
            || !string.Equals(
                scope.SenderPlayerKey,
                scope.OwnerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(DarkHandHarassmentReasonIds.SenderOwnerMismatch);
        }
        if (
            !string.Equals(
                scope.ModeId,
                DarkHandHarassmentModeIds.Harassment,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(DarkHandHarassmentReasonIds.ModeRequired);
        }
        if (snapshot is null)
            return Rejected(DarkHandHarassmentReasonIds.TargetMissing);
        if (
            snapshot.AuthorityRevision <= 0
            || snapshot.StateFingerprint.Length != 64
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.TargetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.LocationId)
        )
        {
            return Rejected(DarkHandHarassmentReasonIds.TargetScopeMismatch);
        }
        if (!string.Equals(scope.OwnerLocationId, snapshot.LocationId, StringComparison.Ordinal))
            return Rejected(DarkHandHarassmentReasonIds.OwnerLocationMismatch);
        if (
            !double.IsFinite(scope.OwnerDistanceTiles)
            || scope.OwnerDistanceTiles < 0d
            || scope.OwnerDistanceTiles > MaximumRangeTiles
        )
        {
            return Rejected(DarkHandHarassmentReasonIds.OwnerOutOfRange);
        }

        var definition = catalog.Find(snapshot.QualifiedItemId);
        if (definition is null)
            return Rejected(DarkHandHarassmentReasonIds.TargetNotAllowlisted);
        if (definition.Excluded)
            return Rejected(DarkHandHarassmentReasonIds.TargetExcluded, definition);
        if (!definition.Enabled)
            return Rejected(DarkHandHarassmentReasonIds.TargetDisabled, definition);
        if (!definition.AllowsLocation(snapshot.LocationId))
            return Rejected(DarkHandHarassmentReasonIds.LocationNotAllowlisted, definition);
        if (definition.ExpectedCategory != snapshot.Category)
            return Rejected(DarkHandHarassmentReasonIds.CategoryMismatch, definition);

        var canDelay = capability.CanCommitDelay && snapshot.CanDelay;
        var canEject =
            capability.CanCommitEject
            && snapshot.CanEject
            && snapshot.HeldOutput is not null
            && snapshot.Category == MachineInteractionCategory.OrdinarySingleInputFinite;
        var categoryAllowsAny = snapshot.Category switch
        {
            MachineInteractionCategory.OrdinarySingleInputFinite => canDelay || canEject,
            MachineInteractionCategory.PermanentInput
            or MachineInteractionCategory.NoInputInfiniteOutput
            or MachineInteractionCategory.MultiInput => canDelay,
            _ => false,
        };
        return categoryAllowsAny
            ? new DarkHandHarassmentEligibilityResult(
                true,
                DarkHandHarassmentReasonIds.Eligible,
                definition,
                canDelay,
                canEject
            )
            : Rejected(
                DarkHandHarassmentReasonIds.OperationUnavailable,
                definition,
                canDelay,
                canEject
            );
    }

    private static DarkHandHarassmentEligibilityResult Rejected(
        string reason,
        MachineTargetDefinition? definition = null,
        bool canDelay = false,
        bool canEject = false
    ) => new(false, reason, definition, canDelay, canEject);
}

internal sealed record MachineMutationStateImage(
    string TargetId,
    string LocationId,
    string QualifiedItemId,
    long AuthorityRevision,
    string StateFingerprint,
    MachineLifecycleState State,
    MachineInteractionCategory Category,
    MachineScheduleKind Schedule,
    string ActiveRuleId,
    int MinutesUntilReady,
    bool ReadyForHarvest,
    bool ShowNextIndex,
    MachineItemFacts? HeldOutput,
    MachineItemFacts? LastInputItem
)
{
    internal static MachineMutationStateImage Capture(MachineInteractionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MachineMutationStateImage(
            snapshot.TargetId,
            snapshot.LocationId,
            snapshot.QualifiedItemId,
            snapshot.AuthorityRevision,
            snapshot.StateFingerprint,
            snapshot.State,
            snapshot.Category,
            snapshot.Schedule,
            snapshot.ActiveRuleId,
            snapshot.MinutesUntilReady,
            snapshot.ReadyForHarvest,
            snapshot.ShowNextIndex,
            snapshot.HeldOutput,
            snapshot.LastInputItem
        );
    }
}

internal sealed record DarkHandHarassmentDelayCommitPlan(
    string OwnerPlayerKey,
    string LeaseId,
    MachineMutationStateImage Before,
    double DelayPercent,
    int MinutesUntilReadyAfter
);

internal sealed record DarkHandHarassmentLandingRequest(
    string OwnerPlayerKey,
    string LeaseId,
    string TargetId,
    string LocationId,
    long TargetRevision,
    string StateFingerprint,
    MachineItemFacts Item
);

internal sealed record DarkHandHarassmentLandingReservation(
    string ReservationId,
    string TargetId,
    string LocationId,
    long TargetRevision,
    string StateFingerprint,
    MachineItemFacts Item
);

internal readonly record struct DarkHandHarassmentLandingResult(
    bool IsReserved,
    string Reason,
    DarkHandHarassmentLandingReservation? Reservation
);

internal sealed record DarkHandHarassmentEjectCommitPlan(
    string OwnerPlayerKey,
    string LeaseId,
    MachineMutationStateImage Before,
    MachineItemFacts Item,
    DarkHandHarassmentLandingReservation Reservation
);

internal enum DarkHandHarassmentAdapterStatus
{
    Applied,
    Rejected,
    RolledBack,
}

internal sealed record DarkHandHarassmentAdapterResult(
    DarkHandHarassmentAdapterStatus Status,
    string Reason,
    MachineInteractionSnapshot? SnapshotAfter,
    bool WorldMutationApplied,
    bool RollbackVerified,
    bool ItemLandedExactlyOnce
);

/// <summary>
/// Atomic seam only. ReserveLanding must prove a destination without changing machine or item
/// state; CommitEject may be called only with that exact proof. Rejected/RolledBack results must
/// return a byte/field-equivalent snapshot image and leave no landed item behind.
/// </summary>
internal interface IDarkHandHarassmentMachineAdapter
{
    DarkHandHarassmentLandingResult ReserveLanding(
        DarkHandHarassmentLandingRequest request
    );

    DarkHandHarassmentAdapterResult CommitDelay(
        DarkHandHarassmentDelayCommitPlan plan
    );

    DarkHandHarassmentAdapterResult CommitEject(
        DarkHandHarassmentEjectCommitPlan plan
    );
}

internal enum DarkHandHarassmentReceiptOutcome
{
    Applied,
    Rejected,
    RolledBack,
}

internal sealed record DarkHandHarassmentReceipt(
    int SchemaVersion,
    string LeaseId,
    string SessionId,
    string OwnerPlayerKey,
    DarkHandHarassmentAction Action,
    double? ActionRoll,
    double? DelayRoll,
    double? DelayPercent,
    DarkHandHarassmentReceiptOutcome Outcome,
    string Reason,
    bool LandingProvenBeforeMutation,
    bool WorldMutationApplied,
    bool ItemLandedExactlyOnce,
    bool RollbackVerified,
    MachineMutationStateImage Before,
    MachineMutationStateImage? After
)
{
    internal const int CurrentSchemaVersion = 1;
}

internal enum DarkHandHarassmentTransactionDisposition
{
    Applied,
    Duplicate,
    Rejected,
    RolledBack,
}

internal readonly record struct DarkHandHarassmentTransactionResult(
    DarkHandHarassmentTransactionDisposition Disposition,
    string Reason,
    DarkHandHarassmentReceipt? Receipt
)
{
    internal bool Applied => Disposition == DarkHandHarassmentTransactionDisposition.Applied;
}

/// <summary>
/// Pure host transaction. It reuses task-07's lease authority verbatim, consumes RNG only after
/// that one-shot lease is accepted, and memoizes a terminal receipt so retries never re-roll or
/// call the adapter twice. Production deliberately doesn't construct this service while shipped
/// catalog/revision/safe-landing capabilities remain unavailable.
/// </summary>
internal sealed class DarkHandHarassmentOperationService
{
    internal const int MaximumReceipts = 256;
    internal const double MinimumDelayPercent = 25d;
    internal const double MaximumDelayPercent = 125d;

    private sealed record ReceiptEntry(
        DarkHandInteractionLease Lease,
        DarkHandHarassmentReceipt Receipt
    );

    private readonly MachineTargetCatalog catalog;
    private readonly MachineInteractionCapability capability;
    private readonly DarkHandInteractionLeaseAuthority leaseAuthority;
    private readonly MachineSnapshotRegistry registry;
    private readonly Dictionary<string, ReceiptEntry> receipts =
        new(StringComparer.Ordinal);

    internal DarkHandHarassmentOperationService(
        MachineTargetCatalog catalog,
        MachineInteractionCapability capability,
        DarkHandInteractionLeaseAuthority leaseAuthority,
        MachineSnapshotRegistry registry
    )
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.capability = capability;
        this.leaseAuthority = leaseAuthority
            ?? throw new ArgumentNullException(nameof(leaseAuthority));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal int ReceiptCount => receipts.Count;
    internal int SuccessfulCommitCount { get; private set; }

    internal DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest? request,
        DarkHandHarassmentOperationScope scope,
        long nowTick,
        bool requireExclusiveTargetOwner = false
    )
    {
        if (!HasAnyCommitCapability())
        {
            return new DarkHandLeaseIssueResult(
                DarkHandLeaseIssueStatus.Rejected,
                capability.Reason,
                null
            );
        }
        return leaseAuthority.TryIssue(
            request,
            scope.SenderPlayerKey,
            nowTick,
            new ScopedTargetAuthority(catalog, capability, registry, scope),
            requireExclusiveTargetOwner
        );
    }

    internal DarkHandHarassmentTransactionResult Commit(
        DarkHandInteractionLease? lease,
        DarkHandHarassmentOperationScope scope,
        long nowTick,
        IDarkHandHarassmentRandomSource random,
        IDarkHandHarassmentMachineAdapter adapter
    )
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(adapter);
        if (!HasAnyCommitCapability())
            return Rejected(capability.Reason);

        if (
            lease is not null
            && receipts.TryGetValue(lease.LeaseId, out var existing)
        )
        {
            return DarkHandInteractionLeaseProtocol.SameLease(existing.Lease, lease)
                ? new DarkHandHarassmentTransactionResult(
                    DarkHandHarassmentTransactionDisposition.Duplicate,
                    DarkHandHarassmentReasonIds.ReceiptDuplicate,
                    existing.Receipt
                )
                : Rejected(DarkHandHarassmentReasonIds.ReceiptConflict);
        }
        if (receipts.Count >= MaximumReceipts)
            return Rejected(DarkHandHarassmentReasonIds.ReceiptWindowFull);

        registry.TryGet(lease?.TargetId ?? string.Empty, out var snapshot);
        var currentTarget = snapshot is null
            ? null
            : new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                DarkHandHarassmentOperationIds.Harassment,
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

        var eligibility = DarkHandHarassmentEligibilityGate.Evaluate(
            catalog,
            capability,
            snapshot,
            scope
        );
        if (!eligibility.IsEligible)
        {
            return CompletePolicyRejection(
                lease!,
                snapshot!,
                DarkHandHarassmentAction.None,
                null,
                null,
                null,
                eligibility.Reason
            );
        }

        double? actionRoll = null;
        var action = DarkHandHarassmentAction.Delay;
        if (
            snapshot!.Category == MachineInteractionCategory.OrdinarySingleInputFinite
            && snapshot.CanEject
        )
        {
            actionRoll = random.NextUnitInterval();
            if (!IsUnitSample(actionRoll.Value))
            {
                return CompletePolicyRejection(
                    lease!,
                    snapshot,
                    DarkHandHarassmentAction.None,
                    actionRoll,
                    null,
                    null,
                    DarkHandHarassmentReasonIds.RandomInvalid
                );
            }
            action = actionRoll.Value < 0.5d
                ? DarkHandHarassmentAction.Delay
                : DarkHandHarassmentAction.Eject;
        }

        return action == DarkHandHarassmentAction.Delay
            ? CommitDelay(lease!, snapshot, scope, actionRoll, random, adapter)
            : CommitEject(lease!, snapshot, scope, actionRoll, adapter);
    }

    internal void ClearWindow()
    {
        receipts.Clear();
        SuccessfulCommitCount = 0;
    }

    private DarkHandHarassmentTransactionResult CommitDelay(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot snapshot,
        DarkHandHarassmentOperationScope scope,
        double? actionRoll,
        IDarkHandHarassmentRandomSource random,
        IDarkHandHarassmentMachineAdapter adapter
    )
    {
        if (snapshot.State == MachineLifecycleState.Ready)
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Delay,
                actionRoll,
                null,
                null,
                DarkHandHarassmentReasonIds.ReadyDelayInvalid
            );
        }
        if (!snapshot.CanDelay || snapshot.MinutesUntilReady <= 0)
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Delay,
                actionRoll,
                null,
                null,
                DarkHandHarassmentReasonIds.DelayUnavailable
            );
        }

        var delayRoll = random.NextUnitInterval();
        if (!IsUnitSample(delayRoll))
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Delay,
                actionRoll,
                delayRoll,
                null,
                DarkHandHarassmentReasonIds.RandomInvalid
            );
        }
        var delayPercent = MinimumDelayPercent
            + ((MaximumDelayPercent - MinimumDelayPercent) * delayRoll);
        int minutesAfter;
        try
        {
            var extension = checked(
                (int)Math.Ceiling(snapshot.MinutesUntilReady * delayPercent / 100d)
            );
            minutesAfter = checked(snapshot.MinutesUntilReady + Math.Max(1, extension));
        }
        catch (OverflowException)
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Delay,
                actionRoll,
                delayRoll,
                delayPercent,
                DarkHandHarassmentReasonIds.DelayOverflow
            );
        }

        var adapterResult = adapter.CommitDelay(
            new DarkHandHarassmentDelayCommitPlan(
                scope.OwnerPlayerKey,
                lease.LeaseId,
                MachineMutationStateImage.Capture(snapshot),
                delayPercent,
                minutesAfter
            )
        );
        return CompleteAdapterResult(
            lease,
            snapshot,
            DarkHandHarassmentAction.Delay,
            actionRoll,
            delayRoll,
            delayPercent,
            landingProven: false,
            expectedMinutesAfter: minutesAfter,
            adapterResult
        );
    }

    private DarkHandHarassmentTransactionResult CommitEject(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot snapshot,
        DarkHandHarassmentOperationScope scope,
        double? actionRoll,
        IDarkHandHarassmentMachineAdapter adapter
    )
    {
        if (
            snapshot.Category != MachineInteractionCategory.OrdinarySingleInputFinite
            || !snapshot.CanEject
            || snapshot.HeldOutput is null
        )
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Eject,
                actionRoll,
                null,
                null,
                DarkHandHarassmentReasonIds.EjectUnavailable
            );
        }

        var landing = adapter.ReserveLanding(
            new DarkHandHarassmentLandingRequest(
                scope.OwnerPlayerKey,
                lease.LeaseId,
                snapshot.TargetId,
                snapshot.LocationId,
                snapshot.AuthorityRevision,
                snapshot.StateFingerprint,
                snapshot.HeldOutput
            )
        );
        if (!landing.IsReserved || landing.Reservation is null)
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Eject,
                actionRoll,
                null,
                null,
                string.IsNullOrWhiteSpace(landing.Reason)
                    ? DarkHandHarassmentReasonIds.LandingUnavailable
                    : landing.Reason
            );
        }
        if (!IsExactReservation(landing.Reservation, snapshot))
        {
            return CompletePolicyRejection(
                lease,
                snapshot,
                DarkHandHarassmentAction.Eject,
                actionRoll,
                null,
                null,
                DarkHandHarassmentReasonIds.LandingReservationInvalid
            );
        }

        var adapterResult = adapter.CommitEject(
            new DarkHandHarassmentEjectCommitPlan(
                scope.OwnerPlayerKey,
                lease.LeaseId,
                MachineMutationStateImage.Capture(snapshot),
                snapshot.HeldOutput,
                landing.Reservation
            )
        );
        return CompleteAdapterResult(
            lease,
            snapshot,
            DarkHandHarassmentAction.Eject,
            actionRoll,
            null,
            null,
            landingProven: true,
            expectedMinutesAfter: 0,
            adapterResult
        );
    }

    private DarkHandHarassmentTransactionResult CompleteAdapterResult(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot beforeSnapshot,
        DarkHandHarassmentAction action,
        double? actionRoll,
        double? delayRoll,
        double? delayPercent,
        bool landingProven,
        int expectedMinutesAfter,
        DarkHandHarassmentAdapterResult adapterResult
    )
    {
        var before = MachineMutationStateImage.Capture(beforeSnapshot);
        var after = adapterResult.SnapshotAfter is null
            ? null
            : MachineMutationStateImage.Capture(adapterResult.SnapshotAfter);

        if (adapterResult.Status is DarkHandHarassmentAdapterStatus.Rejected
            or DarkHandHarassmentAdapterStatus.RolledBack)
        {
            var exactRollback = after is not null && Equals(before, after);
            var contractValid =
                exactRollback
                && !adapterResult.WorldMutationApplied
                && !adapterResult.ItemLandedExactlyOnce
                && (
                    adapterResult.Status != DarkHandHarassmentAdapterStatus.RolledBack
                    || adapterResult.RollbackVerified
                );
            var outcome = adapterResult.Status == DarkHandHarassmentAdapterStatus.RolledBack
                ? DarkHandHarassmentReceiptOutcome.RolledBack
                : DarkHandHarassmentReceiptOutcome.Rejected;
            return Complete(
                lease,
                action,
                actionRoll,
                delayRoll,
                delayPercent,
                outcome,
                contractValid
                    ? adapterResult.Status == DarkHandHarassmentAdapterStatus.RolledBack
                        ? DarkHandHarassmentReasonIds.AdapterRolledBack
                        : string.IsNullOrWhiteSpace(adapterResult.Reason)
                            ? DarkHandHarassmentReasonIds.AdapterRejected
                            : adapterResult.Reason
                    : DarkHandHarassmentReasonIds.AdapterContractInvalid,
                landingProven,
                adapterResult.WorldMutationApplied,
                adapterResult.ItemLandedExactlyOnce,
                contractValid && adapterResult.RollbackVerified,
                before,
                after
            );
        }

        var appliedContractValid =
            adapterResult.Status == DarkHandHarassmentAdapterStatus.Applied
            && adapterResult.WorldMutationApplied
            && !adapterResult.RollbackVerified
            && adapterResult.SnapshotAfter is not null
            && IsValidAppliedSnapshot(
                action,
                beforeSnapshot,
                adapterResult.SnapshotAfter,
                expectedMinutesAfter,
                adapterResult.ItemLandedExactlyOnce
            );
        if (!appliedContractValid)
        {
            return Complete(
                lease,
                action,
                actionRoll,
                delayRoll,
                delayPercent,
                DarkHandHarassmentReceiptOutcome.Rejected,
                DarkHandHarassmentReasonIds.AdapterContractInvalid,
                landingProven,
                adapterResult.WorldMutationApplied,
                adapterResult.ItemLandedExactlyOnce,
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
                action,
                actionRoll,
                delayRoll,
                delayPercent,
                DarkHandHarassmentReceiptOutcome.Rejected,
                DarkHandHarassmentReasonIds.RegistryUpdateRejected,
                landingProven,
                adapterResult.WorldMutationApplied,
                adapterResult.ItemLandedExactlyOnce,
                adapterResult.RollbackVerified,
                before,
                after
            );
        }

        SuccessfulCommitCount++;
        return Complete(
            lease,
            action,
            actionRoll,
            delayRoll,
            delayPercent,
            DarkHandHarassmentReceiptOutcome.Applied,
            action == DarkHandHarassmentAction.Delay
                ? DarkHandHarassmentReasonIds.DelayApplied
                : DarkHandHarassmentReasonIds.EjectApplied,
            landingProven,
            WorldMutationApplied: true,
            adapterResult.ItemLandedExactlyOnce,
            RollbackVerified: false,
            before,
            after
        );
    }

    private DarkHandHarassmentTransactionResult CompletePolicyRejection(
        DarkHandInteractionLease lease,
        MachineInteractionSnapshot snapshot,
        DarkHandHarassmentAction action,
        double? actionRoll,
        double? delayRoll,
        double? delayPercent,
        string reason
    )
    {
        var image = MachineMutationStateImage.Capture(snapshot);
        return Complete(
            lease,
            action,
            actionRoll,
            delayRoll,
            delayPercent,
            DarkHandHarassmentReceiptOutcome.Rejected,
            reason,
            LandingProvenBeforeMutation: false,
            WorldMutationApplied: false,
            ItemLandedExactlyOnce: false,
            RollbackVerified: true,
            image,
            image
        );
    }

    private DarkHandHarassmentTransactionResult Complete(
        DarkHandInteractionLease lease,
        DarkHandHarassmentAction action,
        double? actionRoll,
        double? delayRoll,
        double? delayPercent,
        DarkHandHarassmentReceiptOutcome outcome,
        string reason,
        bool LandingProvenBeforeMutation,
        bool WorldMutationApplied,
        bool ItemLandedExactlyOnce,
        bool RollbackVerified,
        MachineMutationStateImage before,
        MachineMutationStateImage? after
    )
    {
        var receipt = new DarkHandHarassmentReceipt(
            DarkHandHarassmentReceipt.CurrentSchemaVersion,
            lease.LeaseId,
            lease.SessionId,
            lease.OwnerPlayerKey,
            action,
            actionRoll,
            delayRoll,
            delayPercent,
            outcome,
            reason,
            LandingProvenBeforeMutation,
            WorldMutationApplied,
            ItemLandedExactlyOnce,
            RollbackVerified,
            before,
            after
        );
        receipts.Add(lease.LeaseId, new ReceiptEntry(lease.Clone(), receipt));
        return new DarkHandHarassmentTransactionResult(
            outcome switch
            {
                DarkHandHarassmentReceiptOutcome.Applied =>
                    DarkHandHarassmentTransactionDisposition.Applied,
                DarkHandHarassmentReceiptOutcome.RolledBack =>
                    DarkHandHarassmentTransactionDisposition.RolledBack,
                _ => DarkHandHarassmentTransactionDisposition.Rejected,
            },
            reason,
            receipt
        );
    }

    private bool HasAnyCommitCapability() =>
        capability.CanCommitDelay || capability.CanCommitEject;

    private static bool IsUnitSample(double value) =>
        double.IsFinite(value) && value >= 0d && value <= 1d;

    private static bool IsExactReservation(
        DarkHandHarassmentLandingReservation reservation,
        MachineInteractionSnapshot snapshot
    )
    {
        return DarkHandInteractionLeaseProtocol.IsIdentifier(reservation.ReservationId)
            && string.Equals(reservation.TargetId, snapshot.TargetId, StringComparison.Ordinal)
            && string.Equals(
                reservation.LocationId,
                snapshot.LocationId,
                StringComparison.Ordinal
            )
            && reservation.TargetRevision == snapshot.AuthorityRevision
            && string.Equals(
                reservation.StateFingerprint,
                snapshot.StateFingerprint,
                StringComparison.Ordinal
            )
            && Equals(reservation.Item, snapshot.HeldOutput);
    }

    private static bool IsValidAppliedSnapshot(
        DarkHandHarassmentAction action,
        MachineInteractionSnapshot before,
        MachineInteractionSnapshot after,
        int expectedMinutesAfter,
        bool itemLandedExactlyOnce
    )
    {
        if (
            !SameIdentity(before, after)
            || after.AuthorityRevision <= before.AuthorityRevision
            || after.StateFingerprint.Length != 64
            || string.Equals(
                after.StateFingerprint,
                before.StateFingerprint,
                StringComparison.Ordinal
            )
            || after.Category != before.Category
            || after.Schedule != before.Schedule
            || !Equals(after.LastInputItem, before.LastInputItem)
        )
        {
            return false;
        }

        if (action == DarkHandHarassmentAction.Delay)
        {
            return !itemLandedExactlyOnce
                && after.State == MachineLifecycleState.Running
                && after.MinutesUntilReady == expectedMinutesAfter
                && after.ReadyForHarvest == before.ReadyForHarvest
                && after.ShowNextIndex == before.ShowNextIndex
                && string.Equals(after.ActiveRuleId, before.ActiveRuleId, StringComparison.Ordinal)
                && Equals(after.HeldOutput, before.HeldOutput);
        }

        return action == DarkHandHarassmentAction.Eject
            && itemLandedExactlyOnce
            && after.State == MachineLifecycleState.Empty
            && after.MinutesUntilReady == 0
            && !after.ReadyForHarvest
            && !after.ShowNextIndex
            && string.IsNullOrEmpty(after.ActiveRuleId)
            && after.HeldOutput is null;
    }

    private static bool SameIdentity(
        MachineInteractionSnapshot left,
        MachineInteractionSnapshot right
    ) =>
        string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
        && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
        && string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal);

    private static DarkHandHarassmentTransactionResult Rejected(string reason) =>
        new(DarkHandHarassmentTransactionDisposition.Rejected, reason, null);

    private sealed class ScopedTargetAuthority : IDarkHandLeaseTargetAuthority
    {
        private readonly MachineTargetCatalog catalog;
        private readonly MachineInteractionCapability capability;
        private readonly MachineSnapshotRegistry registry;
        private readonly DarkHandHarassmentOperationScope scope;

        internal ScopedTargetAuthority(
            MachineTargetCatalog catalog,
            MachineInteractionCapability capability,
            MachineSnapshotRegistry registry,
            DarkHandHarassmentOperationScope scope
        )
        {
            this.catalog = catalog;
            this.capability = capability;
            this.registry = registry;
            this.scope = scope;
        }

        public bool TryResolve(
            string targetId,
            out DarkHandLeaseTargetSnapshot? target,
            out string reason
        )
        {
            if (!registry.TryGet(targetId, out var snapshot) || snapshot is null)
            {
                target = null;
                reason = DarkHandHarassmentReasonIds.TargetMissing;
                return false;
            }
            var eligibility = DarkHandHarassmentEligibilityGate.Evaluate(
                catalog,
                capability,
                snapshot,
                scope
            );
            if (!eligibility.IsEligible)
            {
                target = null;
                reason = eligibility.Reason;
                return false;
            }
            target = new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                DarkHandHarassmentOperationIds.Harassment,
                snapshot.AuthorityRevision
            );
            reason = DarkHandHarassmentReasonIds.Eligible;
            return true;
        }
    }
}
