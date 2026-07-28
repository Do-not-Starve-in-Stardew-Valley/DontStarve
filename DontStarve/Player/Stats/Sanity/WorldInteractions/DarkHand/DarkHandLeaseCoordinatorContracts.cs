#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class DarkHandLeaseCoordinatorReasonIds
{
    internal const string HostAuthorityRequired = "dark-hand.lease-coordinator.host-authority-required";
    internal const string SenderOwnerMismatch = "dark-hand.lease-coordinator.sender-owner-mismatch";
    internal const string OwnerLocationInvalid = "dark-hand.lease-coordinator.owner-location-invalid";
    internal const string OwnerLocationMismatch = "dark-hand.lease-coordinator.owner-location-mismatch";
    internal const string OwnerOutOfRange = "dark-hand.lease-coordinator.owner-out-of-range";
    internal const string SanityAuthorityInvalid = "dark-hand.lease-coordinator.sanity-authority-invalid";
    internal const string SanityIneligible = "dark-hand.lease-coordinator.sanity-ineligible";
    internal const string OperationBindingUnavailable = "dark-hand.lease-coordinator.operation-binding-unavailable";
    internal const string OperationBindingDuplicated = "dark-hand.lease-coordinator.operation-binding-duplicated";
    internal const string OperationModeMismatch = "dark-hand.lease-coordinator.operation-mode-mismatch";
    internal const string CommitRequestInvalid = "dark-hand.lease-coordinator.commit-request-invalid";
    internal const string CommitRetireFailed = "dark-hand.lease-coordinator.commit-retire-failed";
}

/// <summary>
/// Current host-owned facts for one candidate or commit. Sanity eligibility and revision come from
/// the existing host Sanity authority/tier pipeline; this seam neither calculates a second ratio
/// threshold nor accepts a client-provided gameplay fingerprint.
/// </summary>
internal readonly record struct DarkHandLeaseOwnerContext(
    bool IsHostAuthority,
    string SenderPlayerKey,
    string OwnerPlayerKey,
    string OwnerLocationId,
    double OwnerDistanceTiles,
    bool HostSanityEligible,
    long HostSanityRevision,
    string ModeId
);

internal enum DarkHandLeaseBoundCommitDisposition
{
    Applied,
    NoChange,
    RolledBack,
    Rejected,
    Duplicate,
}

/// <summary>
/// Host-internal routing result. OperationReceipt remains the original FireThief, Harassment, or
/// Thief receipt object; the coordinator never defines a competing rollback or transaction truth.
/// </summary>
internal readonly record struct DarkHandLeaseBoundCommitResult(
    DarkHandLeaseBoundCommitDisposition Disposition,
    string Reason,
    string OperationId,
    bool WorldMutationApplied,
    object? OperationReceipt
);

internal interface IDarkHandLeaseOperationBinding
{
    string OperationId { get; }

    string ModeId { get; }

    DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    );

    DarkHandLeaseBoundCommitResult Commit(
        DarkHandInteractionLease lease,
        DarkHandLeaseOwnerContext context,
        long nowTick
    );

    void ClearWindow();
}

/// <summary>
/// Shared host-only two-phase router. The task-07 authority remains the sole session/nonce/lease
/// owner; operation bindings only adapt its private lease to the existing operation service.
/// </summary>
internal sealed class DarkHandLeaseCoordinator
{
    internal const double MaximumRangeTiles = 20d;
    internal const int MaximumBindings = 3;

    private readonly DarkHandInteractionLeaseAuthority leaseAuthority;
    private readonly Dictionary<string, IDarkHandLeaseOperationBinding> bindings =
        new(StringComparer.Ordinal);

    internal DarkHandLeaseCoordinator(
        DarkHandInteractionLeaseAuthority leaseAuthority,
        IEnumerable<IDarkHandLeaseOperationBinding> operationBindings
    )
    {
        this.leaseAuthority = leaseAuthority
            ?? throw new ArgumentNullException(nameof(leaseAuthority));
        ArgumentNullException.ThrowIfNull(operationBindings);

        foreach (var binding in operationBindings)
        {
            if (
                binding is null
                || !DarkHandInteractionLeaseProtocol.IsIdentifier(binding.OperationId)
                || !DarkHandInteractionLeaseProtocol.IsIdentifier(binding.ModeId)
                || bindings.Count >= MaximumBindings
                || !bindings.TryAdd(binding.OperationId, binding)
            )
            {
                throw new ArgumentException(
                    DarkHandLeaseCoordinatorReasonIds.OperationBindingDuplicated,
                    nameof(operationBindings)
                );
            }
        }
    }

    internal int BindingCount => bindings.Count;

    internal DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest? request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    )
    {
        var contextReason = ValidateSender(context);
        if (contextReason is not null)
            return IssueRejected(contextReason);
        contextReason = ValidateMutableOwnerState(context);
        if (contextReason is not null)
            return IssueRejected(contextReason);
        if (
            request is null
            || !string.Equals(
                request.OwnerPlayerKey,
                context.OwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !string.Equals(
                request.LocationId,
                context.OwnerLocationId,
                StringComparison.Ordinal
            )
        )
        {
            return IssueRejected(DarkHandLeaseCoordinatorReasonIds.OwnerLocationMismatch);
        }
        if (!bindings.TryGetValue(request.OperationId, out var binding))
            return IssueRejected(DarkHandLeaseCoordinatorReasonIds.OperationBindingUnavailable);
        if (!string.Equals(binding.ModeId, context.ModeId, StringComparison.Ordinal))
            return IssueRejected(DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch);

        return binding.TryIssue(request, context, nowTick);
    }

    internal DarkHandLeaseBoundCommitResult Commit(
        DarkHandInteractionLeaseCommitRequest? request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    )
    {
        var contextReason = ValidateSender(context);
        if (contextReason is not null)
            return CommitRejected(contextReason);
        if (request is null)
            return CommitRejected(DarkHandLeaseCoordinatorReasonIds.CommitRequestInvalid);

        var lookup = leaseAuthority.TryResolveForCommit(
            request,
            context.OwnerPlayerKey,
            nowTick
        );
        if (!lookup.Resolved || lookup.Lease is null)
            return CommitRejected(lookup.Reason);
        if (!bindings.TryGetValue(lookup.Lease.OperationId, out var binding))
        {
            RetireIfCurrent(lookup, context.OwnerPlayerKey);
            return CommitRejected(DarkHandLeaseCoordinatorReasonIds.OperationBindingUnavailable);
        }

        // A consumed lease is routed straight to its operation-owned replay window. Mutable
        // location/Sanity/config changes cannot cause a second mutation or erase its terminal
        // receipt, and the observer still never receives the private lease.
        if (lookup.IsDuplicate)
            return binding.Commit(lookup.Lease, context, nowTick);

        contextReason = ValidateMutableOwnerState(context);
        if (contextReason is null
            && !string.Equals(
                lookup.Lease.LocationId,
                context.OwnerLocationId,
                StringComparison.Ordinal
            ))
        {
            contextReason = DarkHandLeaseCoordinatorReasonIds.OwnerLocationMismatch;
        }
        if (contextReason is null
            && !string.Equals(binding.ModeId, context.ModeId, StringComparison.Ordinal))
        {
            contextReason = DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch;
        }
        if (contextReason is not null)
        {
            var retired = leaseAuthority.RetireResolvedCommit(
                lookup.Lease,
                context.OwnerPlayerKey
            );
            return CommitRejected(
                retired.Accepted
                    ? contextReason
                    : DarkHandLeaseCoordinatorReasonIds.CommitRetireFailed
            );
        }

        return binding.Commit(lookup.Lease, context, nowTick);
    }

    internal void ClearOperationWindows()
    {
        foreach (var binding in bindings.Values)
            binding.ClearWindow();
    }

    private void RetireIfCurrent(
        DarkHandLeaseCommitLookupResult lookup,
        string ownerPlayerKey
    )
    {
        if (!lookup.IsDuplicate && lookup.Lease is not null)
            leaseAuthority.RetireResolvedCommit(lookup.Lease, ownerPlayerKey);
    }

    private static string? ValidateSender(DarkHandLeaseOwnerContext context)
    {
        if (!context.IsHostAuthority)
            return DarkHandLeaseCoordinatorReasonIds.HostAuthorityRequired;
        if (
            !SanityPlayerKey.IsCanonical(context.OwnerPlayerKey)
            || !string.Equals(
                context.SenderPlayerKey,
                context.OwnerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return DarkHandLeaseCoordinatorReasonIds.SenderOwnerMismatch;
        }
        return null;
    }

    private static string? ValidateMutableOwnerState(DarkHandLeaseOwnerContext context)
    {
        if (!DarkHandInteractionLeaseProtocol.IsIdentifier(context.OwnerLocationId))
            return DarkHandLeaseCoordinatorReasonIds.OwnerLocationInvalid;
        if (
            !double.IsFinite(context.OwnerDistanceTiles)
            || context.OwnerDistanceTiles < 0d
            || context.OwnerDistanceTiles > MaximumRangeTiles
        )
        {
            return DarkHandLeaseCoordinatorReasonIds.OwnerOutOfRange;
        }
        if (context.HostSanityRevision <= 0)
            return DarkHandLeaseCoordinatorReasonIds.SanityAuthorityInvalid;
        if (!context.HostSanityEligible)
            return DarkHandLeaseCoordinatorReasonIds.SanityIneligible;
        if (!DarkHandInteractionLeaseProtocol.IsIdentifier(context.ModeId))
            return DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch;
        return null;
    }

    private static DarkHandLeaseIssueResult IssueRejected(string reason) =>
        new(DarkHandLeaseIssueStatus.Rejected, reason, null);

    private static DarkHandLeaseBoundCommitResult CommitRejected(string reason) =>
        new(
            DarkHandLeaseBoundCommitDisposition.Rejected,
            reason,
            string.Empty,
            WorldMutationApplied: false,
            OperationReceipt: null
        );
}

internal sealed class DarkHandFireThiefLeaseOperationBinding
    : IDarkHandLeaseOperationBinding
{
    private readonly DarkHandFireThiefOperationService operation;
    private readonly IDarkHandFireExtinguishAdapter adapter;

    internal DarkHandFireThiefLeaseOperationBinding(
        DarkHandFireThiefOperationService operation,
        IDarkHandFireExtinguishAdapter adapter
    )
    {
        this.operation = operation ?? throw new ArgumentNullException(nameof(operation));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public string OperationId => DarkHandFireOperationIds.Extinguish;
    public string ModeId => DarkHandFireModeIds.FireThief;

    public DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    ) => operation.TryIssue(
        request,
        Scope(context),
        nowTick,
        requireExclusiveTargetOwner: true
    );

    public DarkHandLeaseBoundCommitResult Commit(
        DarkHandInteractionLease lease,
        DarkHandLeaseOwnerContext context,
        long nowTick
    )
    {
        var result = operation.Commit(lease, Scope(context), nowTick, adapter);
        return new DarkHandLeaseBoundCommitResult(
            result.Status switch
            {
                DarkHandFireCommitStatus.Applied => DarkHandLeaseBoundCommitDisposition.Applied,
                DarkHandFireCommitStatus.AlreadyExtinguished => DarkHandLeaseBoundCommitDisposition.NoChange,
                DarkHandFireCommitStatus.RolledBack => DarkHandLeaseBoundCommitDisposition.RolledBack,
                DarkHandFireCommitStatus.Duplicate => DarkHandLeaseBoundCommitDisposition.Duplicate,
                _ => DarkHandLeaseBoundCommitDisposition.Rejected,
            },
            result.Reason,
            OperationId,
            result.Status == DarkHandFireCommitStatus.Applied && result.WorldMutationApplied,
            result.Receipt
        );
    }

    public void ClearWindow() => operation.ClearWindow();

    private static DarkHandFireOperationScope Scope(DarkHandLeaseOwnerContext context) =>
        new(
            context.IsHostAuthority,
            context.SenderPlayerKey,
            context.OwnerPlayerKey,
            context.ModeId,
            context.OwnerLocationId,
            context.OwnerDistanceTiles
        );
}

internal sealed class DarkHandHarassmentLeaseOperationBinding
    : IDarkHandLeaseOperationBinding
{
    private readonly DarkHandHarassmentOperationService operation;
    private readonly IDarkHandHarassmentRandomSource random;
    private readonly IDarkHandHarassmentMachineAdapter adapter;

    internal DarkHandHarassmentLeaseOperationBinding(
        DarkHandHarassmentOperationService operation,
        IDarkHandHarassmentRandomSource random,
        IDarkHandHarassmentMachineAdapter adapter
    )
    {
        this.operation = operation ?? throw new ArgumentNullException(nameof(operation));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public string OperationId => DarkHandHarassmentOperationIds.Harassment;
    public string ModeId => DarkHandHarassmentModeIds.Harassment;

    public DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    ) => operation.TryIssue(
        request,
        Scope(context),
        nowTick,
        requireExclusiveTargetOwner: true
    );

    public DarkHandLeaseBoundCommitResult Commit(
        DarkHandInteractionLease lease,
        DarkHandLeaseOwnerContext context,
        long nowTick
    )
    {
        var result = operation.Commit(lease, Scope(context), nowTick, random, adapter);
        return new DarkHandLeaseBoundCommitResult(
            result.Disposition switch
            {
                DarkHandHarassmentTransactionDisposition.Applied => DarkHandLeaseBoundCommitDisposition.Applied,
                DarkHandHarassmentTransactionDisposition.RolledBack => DarkHandLeaseBoundCommitDisposition.RolledBack,
                DarkHandHarassmentTransactionDisposition.Duplicate => DarkHandLeaseBoundCommitDisposition.Duplicate,
                _ => DarkHandLeaseBoundCommitDisposition.Rejected,
            },
            result.Reason,
            OperationId,
            result.Disposition == DarkHandHarassmentTransactionDisposition.Applied
                && result.Receipt?.WorldMutationApplied == true,
            result.Receipt
        );
    }

    public void ClearWindow() => operation.ClearWindow();

    private static DarkHandHarassmentOperationScope Scope(DarkHandLeaseOwnerContext context) =>
        new(
            context.IsHostAuthority,
            context.SenderPlayerKey,
            context.OwnerPlayerKey,
            context.ModeId,
            context.OwnerLocationId,
            context.OwnerDistanceTiles
        );
}

internal sealed class DarkHandThiefLeaseOperationBinding
    : IDarkHandLeaseOperationBinding
{
    private readonly DarkHandThiefOperationService operation;
    private readonly IDarkHandThiefMachineAdapter adapter;

    internal DarkHandThiefLeaseOperationBinding(
        DarkHandThiefOperationService operation,
        IDarkHandThiefMachineAdapter adapter
    )
    {
        this.operation = operation ?? throw new ArgumentNullException(nameof(operation));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public string OperationId => DarkHandThiefOperationIds.Thief;
    public string ModeId => DarkHandThiefModeIds.Thief;

    public DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest request,
        DarkHandLeaseOwnerContext context,
        long nowTick
    ) => operation.TryIssue(
        request,
        Scope(context),
        nowTick,
        requireExclusiveTargetOwner: true
    );

    public DarkHandLeaseBoundCommitResult Commit(
        DarkHandInteractionLease lease,
        DarkHandLeaseOwnerContext context,
        long nowTick
    )
    {
        var result = operation.Commit(lease, Scope(context), nowTick, adapter);
        return new DarkHandLeaseBoundCommitResult(
            result.Disposition switch
            {
                DarkHandThiefTransactionDisposition.Applied => DarkHandLeaseBoundCommitDisposition.Applied,
                DarkHandThiefTransactionDisposition.RolledBack => DarkHandLeaseBoundCommitDisposition.RolledBack,
                DarkHandThiefTransactionDisposition.Duplicate => DarkHandLeaseBoundCommitDisposition.Duplicate,
                _ => DarkHandLeaseBoundCommitDisposition.Rejected,
            },
            result.Reason,
            OperationId,
            result.Disposition == DarkHandThiefTransactionDisposition.Applied
                && result.Receipt?.WorldMutationApplied == true,
            result.Receipt
        );
    }

    public void ClearWindow() => operation.ClearWindow();

    private static DarkHandThiefOperationScope Scope(DarkHandLeaseOwnerContext context) =>
        new(
            context.IsHostAuthority,
            context.SenderPlayerKey,
            context.OwnerPlayerKey,
            context.ModeId,
            context.OwnerLocationId,
            context.OwnerDistanceTiles,
            DarkHandThiefDeletionScope.MachineContents
        );
}
