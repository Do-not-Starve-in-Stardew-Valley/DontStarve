#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

internal sealed class DarkHandInteractionLeaseRequest
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = DarkHandInteractionLeaseProtocol.SchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public string OwnerPlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public long ObservedTargetRevision { get; set; }
}

/// <summary>
/// Owner-local animation completion carries only the opaque lease identity and its request nonce.
/// Sender, owner, session, mode, location, Sanity, operation, and target facts are host context or
/// host-retained lease facts and must never be trusted from the animation client.
/// </summary>
internal sealed class DarkHandInteractionLeaseCommitRequest
{
    public string LeaseId { get; set; } = string.Empty;

    public long Nonce { get; set; }
}

/// <summary>
/// Host-authored terminal feedback. The transport sends it only to the lease owner; it contains
/// no item payload, object reference, rollback image, or mutation delegate.
/// </summary>
internal sealed class DarkHandInteractionCommitResult
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = DarkHandInteractionLeaseProtocol.SchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string LeaseId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public string OwnerPlayerKey { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string Disposition { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public bool WorldMutationApplied { get; set; }
}

/// <summary>
/// A short-lived, owner-private proof that a host observed one exact target revision. It carries
/// no machine state, fire state, animation, screen coordinate, or executable world operation.
/// </summary>
internal sealed class DarkHandInteractionLease
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = DarkHandInteractionLeaseProtocol.SchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string LeaseId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public string OwnerPlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public long TargetRevision { get; set; }

    public long IssuedAtTick { get; set; }

    public long ExpiresAtTick { get; set; }

    internal DarkHandInteractionLease Clone()
    {
        return new DarkHandInteractionLease
        {
            ProtocolVersion = ProtocolVersion,
            SchemaVersion = SchemaVersion,
            SessionId = SessionId,
            LeaseId = LeaseId,
            Nonce = Nonce,
            OwnerPlayerKey = OwnerPlayerKey,
            LocationId = LocationId,
            TargetId = TargetId,
            OperationId = OperationId,
            TargetRevision = TargetRevision,
            IssuedAtTick = IssuedAtTick,
            ExpiresAtTick = ExpiresAtTick,
        };
    }
}

internal sealed record DarkHandLeaseTargetSnapshot(
    string TargetId,
    string LocationId,
    string OperationId,
    long Revision
);

internal interface IDarkHandLeaseTargetAuthority
{
    bool TryResolve(
        string targetId,
        out DarkHandLeaseTargetSnapshot? target,
        out string reason
    );
}

/// <summary>
/// Stage 08 deliberately has no machine/fire target adapter. Future target-specific tasks must
/// inject an authority; until then every request fails closed before a lease can be issued.
/// </summary>
internal sealed class UnavailableDarkHandLeaseTargetAuthority
    : IDarkHandLeaseTargetAuthority
{
    internal static UnavailableDarkHandLeaseTargetAuthority Instance { get; } = new();

    private UnavailableDarkHandLeaseTargetAuthority() { }

    public bool TryResolve(
        string targetId,
        out DarkHandLeaseTargetSnapshot? target,
        out string reason
    )
    {
        target = null;
        reason = "dark-hand.lease-target-authority-unavailable";
        return false;
    }
}

internal enum DarkHandLeaseIssueStatus
{
    Issued,
    Rejected,
}

internal readonly record struct DarkHandLeaseIssueResult(
    DarkHandLeaseIssueStatus Status,
    string Reason,
    DarkHandInteractionLease? Lease
)
{
    internal bool Issued => Status == DarkHandLeaseIssueStatus.Issued && Lease is not null;
}

internal enum DarkHandLeaseValidationStatus
{
    Accepted,
    Rejected,
}

internal readonly record struct DarkHandLeaseValidationResult(
    DarkHandLeaseValidationStatus Status,
    string Reason
)
{
    internal bool Accepted => Status == DarkHandLeaseValidationStatus.Accepted;
}

internal enum DarkHandLeaseCommitLookupStatus
{
    Resolved,
    ResolvedDuplicate,
    Rejected,
}

internal readonly record struct DarkHandLeaseCommitLookupResult(
    DarkHandLeaseCommitLookupStatus Status,
    string Reason,
    DarkHandInteractionLease? Lease
)
{
    internal bool Resolved => Status is DarkHandLeaseCommitLookupStatus.Resolved
        or DarkHandLeaseCommitLookupStatus.ResolvedDuplicate;

    internal bool IsDuplicate => Status == DarkHandLeaseCommitLookupStatus.ResolvedDuplicate;
}

internal static class DarkHandInteractionLeaseProtocol
{
    internal const int SchemaVersion = 1;
    internal const int MaximumIdentifierLength = 256;
    internal const int MaximumHashInputLength = 2048;

    internal static bool IsValidRequest(
        DarkHandInteractionLeaseRequest? request,
        string expectedOwnerPlayerKey,
        string expectedSessionId,
        out string reason
    )
    {
        if (
            request is null
            || request.ProtocolVersion != HostileShadowProtocol.CurrentProtocolVersion
            || request.SchemaVersion != SchemaVersion
            || !SanityProtocol.IsValidSessionId(expectedSessionId)
            || !string.Equals(request.SessionId, expectedSessionId, StringComparison.Ordinal)
            || request.Nonce <= 0
            || !SanityPlayerKey.IsCanonical(expectedOwnerPlayerKey)
            || !string.Equals(
                request.OwnerPlayerKey,
                expectedOwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !IsIdentifier(request.LocationId)
            || !IsIdentifier(request.TargetId)
            || !IsIdentifier(request.OperationId)
            || request.ObservedTargetRevision <= 0
        )
        {
            reason = "dark-hand.lease-request-invalid";
            return false;
        }

        reason = "dark-hand.lease-request-valid";
        return true;
    }

    internal static bool IsValidLeaseEnvelope(
        DarkHandInteractionLease? lease,
        out string reason
    )
    {
        if (
            lease is null
            || lease.ProtocolVersion != HostileShadowProtocol.CurrentProtocolVersion
            || lease.SchemaVersion != SchemaVersion
            || !SanityProtocol.IsValidSessionId(lease.SessionId)
            || !IsIdentifier(lease.LeaseId)
            || lease.Nonce <= 0
            || !SanityPlayerKey.IsCanonical(lease.OwnerPlayerKey)
            || !IsIdentifier(lease.LocationId)
            || !IsIdentifier(lease.TargetId)
            || !IsIdentifier(lease.OperationId)
            || lease.TargetRevision <= 0
            || lease.IssuedAtTick < 0
            || lease.ExpiresAtTick <= lease.IssuedAtTick
        )
        {
            reason = "dark-hand.lease-envelope-invalid";
            return false;
        }

        reason = "dark-hand.lease-envelope-valid";
        return true;
    }

    internal static bool SameLease(
        DarkHandInteractionLease left,
        DarkHandInteractionLease right
    )
    {
        return left.ProtocolVersion == right.ProtocolVersion
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            && string.Equals(left.LeaseId, right.LeaseId, StringComparison.Ordinal)
            && left.Nonce == right.Nonce
            && string.Equals(
                left.OwnerPlayerKey,
                right.OwnerPlayerKey,
                StringComparison.Ordinal
            )
            && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
            && string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && left.TargetRevision == right.TargetRevision
            && left.IssuedAtTick == right.IssuedAtTick
            && left.ExpiresAtTick == right.ExpiresAtTick;
    }

    internal static bool IsValidCommitResult(
        DarkHandInteractionCommitResult? result,
        string expectedSessionId,
        string expectedOwnerPlayerKey,
        out string reason
    )
    {
        if (
            result is null
            || result.ProtocolVersion != HostileShadowProtocol.CurrentProtocolVersion
            || result.SchemaVersion != SchemaVersion
            || !string.Equals(result.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(
                result.OwnerPlayerKey,
                expectedOwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !IsIdentifier(result.LeaseId)
            || result.Nonce <= 0
            || !IsIdentifier(result.TargetId)
            || !IsIdentifier(result.OperationId)
            || !IsIdentifier(result.Disposition)
            || !IsIdentifier(result.Reason)
        )
        {
            reason = "dark-hand.commit-result-invalid";
            return false;
        }

        reason = "dark-hand.commit-result-valid";
        return true;
    }

    internal static bool IsIdentifier(string value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdentifierLength
        )
        {
            return false;
        }
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Host-only, session-bounded nonce and lease window. Receipt entries are never evicted within a
/// live window, so an exact replay cannot become valid again because of cache pressure.
/// </summary>
internal sealed class DarkHandInteractionLeaseAuthority
{
    internal const int MaximumOwners = 256;
    internal const int MaximumLeaseReceipts = 256;
    internal const long DefaultLifetimeTicks = 120;

    private sealed class LeaseReceipt
    {
        internal LeaseReceipt(DarkHandInteractionLease lease)
        {
            Lease = lease;
        }

        internal DarkHandInteractionLease Lease { get; }
        internal bool Consumed { get; set; }
    }

    private readonly Dictionary<string, long> lastNonceByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeaseReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly long lifetimeTicks;

    internal DarkHandInteractionLeaseAuthority(long lifetimeTicks = DefaultLifetimeTicks)
    {
        if (lifetimeTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(lifetimeTicks));
        this.lifetimeTicks = lifetimeTicks;
    }

    internal string SessionId { get; private set; } = string.Empty;
    internal int NonceOwnerCount => lastNonceByOwner.Count;
    internal int ReceiptCount => receipts.Count;

    internal bool BeginSession(string sessionId, out string reason)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "dark-hand.lease-session-invalid";
            return false;
        }

        ClearSession();
        SessionId = sessionId;
        reason = "dark-hand.lease-session-started";
        return true;
    }

    internal DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest? request,
        string expectedOwnerPlayerKey,
        long nowTick,
        IDarkHandLeaseTargetAuthority targetAuthority,
        bool requireExclusiveTargetOwner = false
    )
    {
        ArgumentNullException.ThrowIfNull(targetAuthority);
        if (nowTick < 0)
            return Rejected("dark-hand.lease-clock-invalid");
        if (
            !DarkHandInteractionLeaseProtocol.IsValidRequest(
                request,
                expectedOwnerPlayerKey,
                SessionId,
                out var reason
            )
        )
        {
            return Rejected(reason);
        }

        if (lastNonceByOwner.TryGetValue(expectedOwnerPlayerKey, out var lastNonce))
        {
            if (request!.Nonce == lastNonce)
                return Rejected("dark-hand.lease-nonce-replay");
            if (request.Nonce < lastNonce)
                return Rejected("dark-hand.lease-nonce-out-of-order");
        }
        else if (lastNonceByOwner.Count >= MaximumOwners)
        {
            return Rejected("dark-hand.lease-owner-window-full");
        }

        // Consume a valid sender nonce before target resolution. A transient or forged target must
        // use a new nonce and can never replay an earlier request into a changed world revision.
        lastNonceByOwner[expectedOwnerPlayerKey] = request!.Nonce;
        if (
            !targetAuthority.TryResolve(
                request.TargetId,
                out var target,
                out reason
            )
            || target is null
        )
        {
            return Rejected(
                string.IsNullOrWhiteSpace(reason)
                    ? "dark-hand.lease-target-unavailable"
                    : reason
            );
        }
        if (
            !string.Equals(target.TargetId, request.TargetId, StringComparison.Ordinal)
            || !string.Equals(target.LocationId, request.LocationId, StringComparison.Ordinal)
            || !string.Equals(target.OperationId, request.OperationId, StringComparison.Ordinal)
            || target.Revision != request.ObservedTargetRevision
        )
        {
            return Rejected("dark-hand.lease-target-revision-or-scope-changed");
        }
        if (requireExclusiveTargetOwner)
        {
            foreach (var receipt in receipts.Values)
            {
                // The coordinator's two-phase path reserves one target revision for one owner.
                // Direct pre-11-08 operation fixtures keep their original revision-at-commit
                // competition semantics by leaving requireExclusiveTargetOwner false.
                if (
                    !receipt.Consumed
                    && nowTick < receipt.Lease.ExpiresAtTick
                    && !string.Equals(
                        receipt.Lease.OwnerPlayerKey,
                        expectedOwnerPlayerKey,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        receipt.Lease.TargetId,
                        target.TargetId,
                        StringComparison.Ordinal
                    )
                    && receipt.Lease.TargetRevision == target.Revision
                )
                {
                    return Rejected("dark-hand.lease-target-owned-by-another");
                }
            }
        }
        if (receipts.Count >= MaximumLeaseReceipts)
            return Rejected("dark-hand.lease-receipt-window-full");
        if (nowTick > long.MaxValue - lifetimeTicks)
            return Rejected("dark-hand.lease-expiry-overflow");

        var lease = new DarkHandInteractionLease
        {
            SessionId = SessionId,
            LeaseId = CreateLeaseId(request, target),
            Nonce = request.Nonce,
            OwnerPlayerKey = request.OwnerPlayerKey,
            LocationId = target.LocationId,
            TargetId = target.TargetId,
            OperationId = target.OperationId,
            TargetRevision = target.Revision,
            IssuedAtTick = nowTick,
            ExpiresAtTick = nowTick + lifetimeTicks,
        };
        if (receipts.ContainsKey(lease.LeaseId))
            return Rejected("dark-hand.lease-id-conflict");
        receipts.Add(lease.LeaseId, new LeaseReceipt(lease.Clone()));
        return new DarkHandLeaseIssueResult(
            DarkHandLeaseIssueStatus.Issued,
            "dark-hand.lease-issued",
            lease
        );
    }

    internal DarkHandLeaseValidationResult ValidateAndConsume(
        DarkHandInteractionLease? lease,
        string expectedOwnerPlayerKey,
        long nowTick,
        DarkHandLeaseTargetSnapshot? currentTarget
    )
    {
        var reason = string.Empty;
        if (
            nowTick < 0
            || !DarkHandInteractionLeaseProtocol.IsValidLeaseEnvelope(lease, out reason)
        )
        {
            return ValidationRejected(
                nowTick < 0 ? "dark-hand.lease-clock-invalid" : reason
            );
        }
        if (
            !string.Equals(lease!.SessionId, SessionId, StringComparison.Ordinal)
            || !string.Equals(
                lease.OwnerPlayerKey,
                expectedOwnerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return ValidationRejected("dark-hand.lease-session-or-owner-invalid");
        }
        if (!receipts.TryGetValue(lease.LeaseId, out var receipt))
            return ValidationRejected("dark-hand.lease-unknown");
        if (!DarkHandInteractionLeaseProtocol.SameLease(receipt.Lease, lease))
            return ValidationRejected("dark-hand.lease-content-conflict");
        if (receipt.Consumed)
            return ValidationRejected("dark-hand.lease-replay");
        if (nowTick >= lease.ExpiresAtTick)
            return ValidationRejected("dark-hand.lease-expired");
        if (
            currentTarget is null
            || !string.Equals(
                currentTarget.TargetId,
                lease.TargetId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentTarget.LocationId,
                lease.LocationId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentTarget.OperationId,
                lease.OperationId,
                StringComparison.Ordinal
            )
            || currentTarget.Revision != lease.TargetRevision
        )
        {
            return ValidationRejected("dark-hand.lease-target-revision-changed");
        }

        receipt.Consumed = true;
        return new DarkHandLeaseValidationResult(
            DarkHandLeaseValidationStatus.Accepted,
            "dark-hand.lease-consumed"
        );
    }

    internal DarkHandLeaseCommitLookupResult TryResolveForCommit(
        DarkHandInteractionLeaseCommitRequest? request,
        string expectedOwnerPlayerKey,
        long nowTick
    )
    {
        if (
            nowTick < 0
            || request is null
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(request.LeaseId)
            || request.Nonce <= 0
            || !SanityPlayerKey.IsCanonical(expectedOwnerPlayerKey)
        )
        {
            return CommitLookupRejected(
                nowTick < 0
                    ? "dark-hand.lease-commit-clock-invalid"
                    : "dark-hand.lease-commit-request-invalid"
            );
        }
        if (!receipts.TryGetValue(request.LeaseId, out var receipt))
            return CommitLookupRejected("dark-hand.lease-commit-unknown");
        if (
            !string.Equals(receipt.Lease.SessionId, SessionId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.Lease.OwnerPlayerKey,
                expectedOwnerPlayerKey,
                StringComparison.Ordinal
            )
            || receipt.Lease.Nonce != request.Nonce
        )
        {
            return CommitLookupRejected("dark-hand.lease-commit-owner-nonce-or-session-invalid");
        }

        // A consumed lease may still resolve only so its operation-owned terminal receipt can be
        // replayed. It can never execute the adapter again. Unconsumed leases still expire.
        if (receipt.Consumed)
        {
            return new DarkHandLeaseCommitLookupResult(
                DarkHandLeaseCommitLookupStatus.ResolvedDuplicate,
                "dark-hand.lease-commit-duplicate-resolved",
                receipt.Lease.Clone()
            );
        }
        if (nowTick >= receipt.Lease.ExpiresAtTick)
            return CommitLookupRejected("dark-hand.lease-expired");

        return new DarkHandLeaseCommitLookupResult(
            DarkHandLeaseCommitLookupStatus.Resolved,
            "dark-hand.lease-commit-resolved",
            receipt.Lease.Clone()
        );
    }

    internal DarkHandLeaseValidationResult RetireResolvedCommit(
        DarkHandInteractionLease lease,
        string expectedOwnerPlayerKey
    )
    {
        if (
            !DarkHandInteractionLeaseProtocol.IsValidLeaseEnvelope(lease, out var reason)
            || !string.Equals(lease.SessionId, SessionId, StringComparison.Ordinal)
            || !string.Equals(
                lease.OwnerPlayerKey,
                expectedOwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !receipts.TryGetValue(lease.LeaseId, out var receipt)
            || !DarkHandInteractionLeaseProtocol.SameLease(receipt.Lease, lease)
        )
        {
            return ValidationRejected(
                reason == "dark-hand.lease-envelope-valid"
                    ? "dark-hand.lease-commit-retire-invalid"
                    : reason
            );
        }
        if (receipt.Consumed)
            return ValidationRejected("dark-hand.lease-replay");

        receipt.Consumed = true;
        return new DarkHandLeaseValidationResult(
            DarkHandLeaseValidationStatus.Accepted,
            "dark-hand.lease-commit-retired"
        );
    }

    internal void ClearOwner(string ownerPlayerKey)
    {
        lastNonceByOwner.Remove(ownerPlayerKey);
        if (receipts.Count == 0)
            return;

        var remove = new List<string>();
        foreach (var pair in receipts)
        {
            if (string.Equals(
                pair.Value.Lease.OwnerPlayerKey,
                ownerPlayerKey,
                StringComparison.Ordinal
            ))
            {
                remove.Add(pair.Key);
            }
        }
        foreach (var leaseId in remove)
            receipts.Remove(leaseId);
    }

    internal void ClearWindow()
    {
        lastNonceByOwner.Clear();
        receipts.Clear();
    }

    internal void ClearSession()
    {
        ClearWindow();
        SessionId = string.Empty;
    }

    private static DarkHandLeaseIssueResult Rejected(string reason)
    {
        return new DarkHandLeaseIssueResult(
            DarkHandLeaseIssueStatus.Rejected,
            reason,
            null
        );
    }

    private static DarkHandLeaseValidationResult ValidationRejected(string reason)
    {
        return new DarkHandLeaseValidationResult(
            DarkHandLeaseValidationStatus.Rejected,
            reason
        );
    }

    private static DarkHandLeaseCommitLookupResult CommitLookupRejected(string reason)
    {
        return new DarkHandLeaseCommitLookupResult(
            DarkHandLeaseCommitLookupStatus.Rejected,
            reason,
            null
        );
    }

    private static string CreateLeaseId(
        DarkHandInteractionLeaseRequest request,
        DarkHandLeaseTargetSnapshot target
    )
    {
        var input = string.Concat(
            request.SessionId,
            "|",
            request.OwnerPlayerKey,
            "|",
            request.Nonce.ToString(CultureInfo.InvariantCulture),
            "|",
            target.TargetId,
            "|",
            target.Revision.ToString(CultureInfo.InvariantCulture)
        );
        if (input.Length > DarkHandInteractionLeaseProtocol.MaximumHashInputLength)
            input = input[..DarkHandInteractionLeaseProtocol.MaximumHashInputLength];

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in input)
        {
            hash ^= character;
            hash *= prime;
        }
        return string.Concat(
            "dark-hand.lease.",
            hash.ToString("X16", CultureInfo.InvariantCulture)
        );
    }
}

internal enum DarkHandLeaseInboxStatus
{
    Applied,
    IgnoredDuplicate,
    Rejected,
}

internal readonly record struct DarkHandLeaseInboxResult(
    DarkHandLeaseInboxStatus Status,
    string Reason
);

/// <summary>Client-only owner-private receipt window; it never executes the represented operation.</summary>
internal sealed class DarkHandInteractionLeaseInbox
{
    internal const int MaximumReceipts = 256;

    private readonly Dictionary<string, DarkHandInteractionLease> receipts =
        new(StringComparer.Ordinal);

    internal string SessionId { get; private set; } = string.Empty;
    internal string OwnerPlayerKey { get; private set; } = string.Empty;
    internal int Count => receipts.Count;

    internal bool BeginSession(
        string sessionId,
        string ownerPlayerKey,
        out string reason
    )
    {
        if (
            !SanityProtocol.IsValidSessionId(sessionId)
            || !SanityPlayerKey.IsCanonical(ownerPlayerKey)
        )
        {
            reason = "dark-hand.lease-inbox-session-invalid";
            return false;
        }
        Clear();
        SessionId = sessionId;
        OwnerPlayerKey = ownerPlayerKey;
        reason = "dark-hand.lease-inbox-session-started";
        return true;
    }

    internal DarkHandLeaseInboxResult Apply(
        DarkHandInteractionLease? lease,
        long nowTick,
        string expectedLocationId
    )
    {
        var reason = string.Empty;
        if (
            nowTick < 0
            || !DarkHandInteractionLeaseProtocol.IsValidLeaseEnvelope(lease, out reason)
            || !string.Equals(lease!.SessionId, SessionId, StringComparison.Ordinal)
            || !string.Equals(
                lease.OwnerPlayerKey,
                OwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !string.Equals(
                lease.LocationId,
                expectedLocationId,
                StringComparison.Ordinal
            )
            || nowTick >= lease.ExpiresAtTick
        )
        {
            return new DarkHandLeaseInboxResult(
                DarkHandLeaseInboxStatus.Rejected,
                nowTick < 0
                    ? "dark-hand.lease-inbox-clock-invalid"
                    : reason != "dark-hand.lease-envelope-valid"
                        ? reason
                        : nowTick >= lease!.ExpiresAtTick
                            ? "dark-hand.lease-inbox-expired"
                            : "dark-hand.lease-inbox-session-owner-or-location-invalid"
            );
        }
        if (receipts.TryGetValue(lease.LeaseId, out var existing))
        {
            return new DarkHandLeaseInboxResult(
                DarkHandInteractionLeaseProtocol.SameLease(existing, lease)
                    ? DarkHandLeaseInboxStatus.IgnoredDuplicate
                    : DarkHandLeaseInboxStatus.Rejected,
                DarkHandInteractionLeaseProtocol.SameLease(existing, lease)
                    ? "dark-hand.lease-inbox-duplicate"
                    : "dark-hand.lease-inbox-conflict"
            );
        }
        if (receipts.Count >= MaximumReceipts)
        {
            return new DarkHandLeaseInboxResult(
                DarkHandLeaseInboxStatus.Rejected,
                "dark-hand.lease-inbox-full"
            );
        }

        receipts.Add(lease.LeaseId, lease.Clone());
        return new DarkHandLeaseInboxResult(
            DarkHandLeaseInboxStatus.Applied,
            "dark-hand.lease-inbox-applied"
        );
    }

    internal bool TryTake(
        string targetId,
        string operationId,
        long nowTick,
        string expectedLocationId,
        out DarkHandInteractionLease? lease,
        out string reason
    )
    {
        lease = null;
        string? matchedLeaseId = null;
        foreach (var pair in receipts)
        {
            var candidate = pair.Value;
            if (
                string.Equals(candidate.TargetId, targetId, StringComparison.Ordinal)
                && string.Equals(
                    candidate.OperationId,
                    operationId,
                    StringComparison.Ordinal
                )
            )
            {
                matchedLeaseId = pair.Key;
                lease = candidate;
                break;
            }
        }

        if (matchedLeaseId is null || lease is null)
        {
            reason = "dark-hand.lease-inbox-target-missing";
            return false;
        }
        var expired = nowTick >= 0 && nowTick >= lease.ExpiresAtTick;
        receipts.Remove(matchedLeaseId);
        if (
            nowTick < 0
            || expired
            || !string.Equals(
                lease.LocationId,
                expectedLocationId,
                StringComparison.Ordinal
            )
        )
        {
            lease = null;
            reason = expired
                ? "dark-hand.lease-inbox-expired"
                : "dark-hand.lease-inbox-location-changed";
            return false;
        }

        lease = lease.Clone();
        reason = "dark-hand.lease-inbox-taken";
        return true;
    }

    internal void Clear()
    {
        receipts.Clear();
        SessionId = string.Empty;
        OwnerPlayerKey = string.Empty;
    }
}
