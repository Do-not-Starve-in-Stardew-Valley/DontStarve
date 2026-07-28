using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class DarkHandInteractionLeaseTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";
    private const string TargetId = "dark-hand-target-1";
    private const string OperationId = "dark-hand.interaction.probe";

    [Fact]
    public void Issue_binds_sender_nonce_location_operation_and_exact_target_revision()
    {
        var authority = Authority(lifetimeTicks: 10);
        var target = new FixedTargetAuthority(Target(revision: 7));

        var issued = authority.TryIssue(Request(nonce: 1), Owner, 100, target);
        var replay = authority.TryIssue(Request(nonce: 1), Owner, 101, target);
        var next = authority.TryIssue(Request(nonce: 3), Owner, 102, target);
        var outOfOrder = authority.TryIssue(Request(nonce: 2), Owner, 103, target);
        var forgedOwner = authority.TryIssue(Request(nonce: 1), OtherOwner, 104, target);
        var revisionChanged = authority.TryIssue(
            Request(nonce: 4, revision: 6),
            Owner,
            105,
            target
        );

        Assert.True(issued.Issued);
        Assert.Equal(Owner, issued.Lease!.OwnerPlayerKey);
        Assert.Equal(TargetId, issued.Lease.TargetId);
        Assert.Equal(7, issued.Lease.TargetRevision);
        Assert.Equal(110, issued.Lease.ExpiresAtTick);
        Assert.Equal("dark-hand.lease-nonce-replay", replay.Reason);
        Assert.True(next.Issued);
        Assert.Equal("dark-hand.lease-nonce-out-of-order", outOfOrder.Reason);
        Assert.Equal("dark-hand.lease-request-invalid", forgedOwner.Reason);
        Assert.Equal(
            "dark-hand.lease-target-revision-or-scope-changed",
            revisionChanged.Reason
        );
    }

    [Fact]
    public void Lease_expiry_replay_revision_change_and_old_session_all_fail_closed()
    {
        var authority = Authority(lifetimeTicks: 5);
        var targetAuthority = new FixedTargetAuthority(Target(revision: 7));
        var expiring = authority.TryIssue(
            Request(nonce: 1),
            Owner,
            10,
            targetAuthority
        ).Lease!;
        var expired = authority.ValidateAndConsume(
            expiring,
            Owner,
            15,
            Target(revision: 7)
        );

        var consumable = authority.TryIssue(
            Request(nonce: 2),
            Owner,
            20,
            targetAuthority
        ).Lease!;
        var changed = authority.ValidateAndConsume(
            consumable,
            Owner,
            21,
            Target(revision: 8)
        );
        var accepted = authority.ValidateAndConsume(
            consumable,
            Owner,
            21,
            Target(revision: 7)
        );
        var replay = authority.ValidateAndConsume(
            consumable,
            Owner,
            22,
            Target(revision: 7)
        );

        Assert.Equal("dark-hand.lease-expired", expired.Reason);
        Assert.Equal("dark-hand.lease-target-revision-changed", changed.Reason);
        Assert.True(accepted.Accepted);
        Assert.Equal("dark-hand.lease-replay", replay.Reason);

        Assert.True(authority.BeginSession(SessionB, out var beginReason), beginReason);
        var oldLease = authority.ValidateAndConsume(
            consumable,
            Owner,
            23,
            Target(revision: 7)
        );
        var oldRequest = authority.TryIssue(
            Request(nonce: 1, session: SessionA),
            Owner,
            23,
            targetAuthority
        );
        Assert.Equal("dark-hand.lease-session-or-owner-invalid", oldLease.Reason);
        Assert.Equal("dark-hand.lease-request-invalid", oldRequest.Reason);
    }

    [Fact]
    public void Unavailable_target_consumes_nonce_without_issuing_or_executing_anything()
    {
        var authority = Authority();
        var unavailable = authority.TryIssue(
            Request(nonce: 1),
            Owner,
            10,
            UnavailableDarkHandLeaseTargetAuthority.Instance
        );
        var replay = authority.TryIssue(
            Request(nonce: 1),
            Owner,
            11,
            new FixedTargetAuthority(Target(revision: 7))
        );

        Assert.False(unavailable.Issued);
        Assert.Equal(
            "dark-hand.lease-target-authority-unavailable",
            unavailable.Reason
        );
        Assert.Equal(0, authority.ReceiptCount);
        Assert.Equal("dark-hand.lease-nonce-replay", replay.Reason);
    }

    [Fact]
    public void Client_inbox_accepts_only_exact_owner_session_and_content()
    {
        var authority = Authority();
        var lease = authority.TryIssue(
            Request(nonce: 1),
            Owner,
            10,
            new FixedTargetAuthority(Target(revision: 7))
        ).Lease!;
        var inbox = new DarkHandInteractionLeaseInbox();
        Assert.True(inbox.BeginSession(SessionA, Owner, out var reason), reason);

        var applied = inbox.Apply(lease, 10, "Farm");
        var duplicate = inbox.Apply(lease.Clone(), 10, "Farm");
        var conflictLease = lease.Clone();
        conflictLease.TargetRevision++;
        var conflict = inbox.Apply(conflictLease, 10, "Farm");
        var otherOwner = lease.Clone();
        otherOwner.LeaseId = "dark-hand.lease.other";
        otherOwner.OwnerPlayerKey = OtherOwner;
        var rejectedOwner = inbox.Apply(otherOwner, 10, "Farm");

        Assert.Equal(DarkHandLeaseInboxStatus.Applied, applied.Status);
        Assert.Equal(DarkHandLeaseInboxStatus.IgnoredDuplicate, duplicate.Status);
        Assert.Equal("dark-hand.lease-inbox-conflict", conflict.Reason);
        Assert.Equal(
            "dark-hand.lease-inbox-session-owner-or-location-invalid",
            rejectedOwner.Reason
        );
        Assert.Equal(1, inbox.Count);
        inbox.Clear();
        Assert.Equal(0, inbox.Count);
        Assert.Equal(string.Empty, inbox.SessionId);

        Assert.True(inbox.BeginSession(SessionA, Owner, out reason), reason);
        Assert.Equal(
            "dark-hand.lease-inbox-session-owner-or-location-invalid",
            inbox.Apply(lease, 10, "Mine").Reason
        );
        Assert.Equal(
            "dark-hand.lease-inbox-expired",
            inbox.Apply(lease, lease.ExpiresAtTick, "Farm").Reason
        );
    }

    [Fact]
    public void Client_inbox_take_is_exact_one_shot_and_location_scoped()
    {
        var lease = Authority().TryIssue(
            Request(nonce: 1),
            Owner,
            10,
            new FixedTargetAuthority(Target(revision: 7))
        ).Lease!;
        var inbox = new DarkHandInteractionLeaseInbox();
        Assert.True(inbox.BeginSession(SessionA, Owner, out var reason), reason);
        Assert.Equal(
            DarkHandLeaseInboxStatus.Applied,
            inbox.Apply(lease, 10, "Farm").Status
        );

        Assert.False(
            inbox.TryTake(
                "other-target",
                OperationId,
                11,
                "Farm",
                out _,
                out reason
            )
        );
        Assert.Equal(1, inbox.Count);
        Assert.True(
            inbox.TryTake(
                TargetId,
                OperationId,
                11,
                "Farm",
                out var taken,
                out reason
            ),
            reason
        );
        Assert.True(DarkHandInteractionLeaseProtocol.SameLease(lease, taken!));
        Assert.Equal(0, inbox.Count);
        Assert.False(
            inbox.TryTake(
                TargetId,
                OperationId,
                11,
                "Farm",
                out _,
                out reason
            )
        );
        Assert.Equal("dark-hand.lease-inbox-target-missing", reason);
    }

    [Fact]
    public void Commit_result_is_owner_private_bounded_data_without_mutation_handles()
    {
        var result = new DarkHandInteractionCommitResult
        {
            SessionId = SessionA,
            LeaseId = "dark-hand.lease.result",
            Nonce = 1,
            OwnerPlayerKey = Owner,
            TargetId = TargetId,
            OperationId = OperationId,
            Disposition = "Applied",
            Reason = "dark-hand.fire.commit-applied",
            WorldMutationApplied = true,
        };

        Assert.True(
            DarkHandInteractionLeaseProtocol.IsValidCommitResult(
                result,
                SessionA,
                Owner,
                out var reason
            ),
            reason
        );
        Assert.False(
            DarkHandInteractionLeaseProtocol.IsValidCommitResult(
                result,
                SessionA,
                OtherOwner,
                out _
            )
        );
        var propertyTypes = typeof(DarkHandInteractionCommitResult)
            .GetProperties()
            .Select(property => property.PropertyType)
            .ToArray();
        Assert.DoesNotContain(propertyTypes, type => typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(
            typeof(DarkHandInteractionCommitResult).GetProperties(),
            property => property.Name.Contains("Item", StringComparison.Ordinal)
        );
    }

    private static DarkHandInteractionLeaseAuthority Authority(long lifetimeTicks = 120)
    {
        var authority = new DarkHandInteractionLeaseAuthority(lifetimeTicks);
        Assert.True(authority.BeginSession(SessionA, out var reason), reason);
        return authority;
    }

    private static DarkHandInteractionLeaseRequest Request(
        long nonce,
        long revision = 7,
        string session = SessionA
    )
    {
        return new DarkHandInteractionLeaseRequest
        {
            SessionId = session,
            Nonce = nonce,
            OwnerPlayerKey = Owner,
            LocationId = "Farm",
            TargetId = TargetId,
            OperationId = OperationId,
            ObservedTargetRevision = revision,
        };
    }

    private static DarkHandLeaseTargetSnapshot Target(long revision)
    {
        return new DarkHandLeaseTargetSnapshot(
            TargetId,
            "Farm",
            OperationId,
            revision
        );
    }

    private sealed class FixedTargetAuthority : IDarkHandLeaseTargetAuthority
    {
        private readonly DarkHandLeaseTargetSnapshot target;

        internal FixedTargetAuthority(DarkHandLeaseTargetSnapshot target)
        {
            this.target = target;
        }

        public bool TryResolve(
            string targetId,
            out DarkHandLeaseTargetSnapshot? resolved,
            out string reason
        )
        {
            resolved = target;
            reason = "dark-hand.lease-target-resolved";
            return true;
        }
    }
}
