using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class HostileShadowSessionLifecycleTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";

    [Fact]
    public void Join_warp_and_disconnect_replace_subscription_then_clear_owner_window()
    {
        var lifecycle = new HostileShadowSessionLifecycleCoordinator();
        Assert.True(lifecycle.BeginSession(SessionA, enabled: true, out var reason), reason);
        Assert.True(
            lifecycle.TrySubscribe(
                123456789,
                Owner,
                "Farm",
                ShadowSnapshotTrigger.Join,
                out reason
            ),
            reason
        );
        Assert.True(
            lifecycle.RecordFingerprint(
                123456789,
                Owner,
                Fingerprint(SessionA, Owner, 'A'),
                out reason
            ),
            reason
        );
        lifecycle.LeaseAuthority.TryIssue(
            LeaseRequest(SessionA, nonce: 1),
            Owner,
            10,
            UnavailableDarkHandLeaseTargetAuthority.Instance
        );
        Assert.Equal(1, lifecycle.LeaseAuthority.NonceOwnerCount);
        Assert.True(
            lifecycle.TrySubscribe(
                123456789,
                Owner,
                "Mine",
                ShadowSnapshotTrigger.Warp,
                out reason
            ),
            reason
        );

        Assert.Equal(1, lifecycle.SubscriptionCount);
        Assert.True(
            lifecycle.TryGetSubscription(
                123456789,
                out var playerKey,
                out var locationId,
                out var trigger
            )
        );
        Assert.Equal(Owner, playerKey);
        Assert.Equal("Mine", locationId);
        Assert.Equal(ShadowSnapshotTrigger.Warp, trigger);
        Assert.Equal(1, lifecycle.FingerprintCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
        var unavailable = lifecycle.LeaseAuthority.TryIssue(
            LeaseRequest(SessionA, nonce: 1),
            Owner,
            10,
            UnavailableDarkHandLeaseTargetAuthority.Instance
        );
        Assert.False(unavailable.Issued);
        Assert.Equal(1, lifecycle.LeaseAuthority.NonceOwnerCount);

        lifecycle.Disconnect(123456789, Owner);
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(0, lifecycle.FingerprintCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
    }

    [Fact]
    public void Day_disabled_returned_title_and_dispose_clear_symmetrically()
    {
        var lifecycle = new HostileShadowSessionLifecycleCoordinator();
        Assert.True(lifecycle.BeginSession(SessionA, enabled: true, out var reason), reason);
        Subscribe(lifecycle);
        lifecycle.LeaseAuthority.TryIssue(
            LeaseRequest(SessionA, nonce: 1),
            Owner,
            10,
            UnavailableDarkHandLeaseTargetAuthority.Instance
        );

        lifecycle.DayEnding();
        Assert.False(lifecycle.IsWorldActive);
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
        lifecycle.SetEnabled(false);
        lifecycle.SetEnabled(true);
        Assert.False(lifecycle.IsWorldActive);
        Assert.False(
            lifecycle.TrySubscribe(
                123456789,
                Owner,
                "Farm",
                ShadowSnapshotTrigger.Resync,
                out _
            )
        );

        lifecycle.DayStarted();
        Assert.True(lifecycle.IsWorldActive);
        Subscribe(lifecycle, ShadowSnapshotTrigger.Resync);
        lifecycle.SetEnabled(false);
        Assert.False(lifecycle.IsEnabled);
        Assert.False(lifecycle.IsWorldActive);
        Assert.Equal(0, lifecycle.SubscriptionCount);

        lifecycle.SetEnabled(true);
        Assert.True(lifecycle.IsWorldActive);
        Subscribe(lifecycle, ShadowSnapshotTrigger.Resync);
        lifecycle.ClearSession();
        Assert.False(lifecycle.IsSessionActive);
        Assert.Equal(string.Empty, lifecycle.SessionId);
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(string.Empty, lifecycle.LeaseAuthority.SessionId);

        Assert.True(lifecycle.BeginSession(SessionB, enabled: true, out reason), reason);
        lifecycle.Dispose();
        Assert.False(lifecycle.IsSessionActive);
        Assert.False(lifecycle.BeginSession(SessionA, enabled: true, out reason));
        Assert.Equal("hostile-shadow.lifecycle-disposed", reason);
    }

    [Fact]
    public void Fingerprint_exchanges_only_schema_and_hash_and_never_changes_lifecycle_state()
    {
        var lifecycle = new HostileShadowSessionLifecycleCoordinator();
        Assert.True(lifecycle.BeginSession(SessionA, enabled: true, out var reason), reason);
        var local = new HostileShadowConfigFingerprintSnapshot(1, new string('A', 64));
        var matching = Fingerprint(SessionA, Owner, 'a');
        var mismatching = Fingerprint(SessionA, Owner, 'B');

        Assert.True(
            HostileShadowConfigFingerprintProtocol.IsValidReport(
                matching,
                Owner,
                SessionA,
                out reason
            ),
            reason
        );
        Assert.Equal(
            HostileShadowFingerprintComparison.Match,
            HostileShadowConfigFingerprintProtocol.Compare(local, matching)
        );
        Assert.Equal(
            HostileShadowFingerprintComparison.Mismatch,
            HostileShadowConfigFingerprintProtocol.Compare(local, mismatching)
        );
        Assert.Equal(
            HostileShadowFingerprintComparison.Unavailable,
            HostileShadowConfigFingerprintProtocol.Compare(
                HostileShadowConfigFingerprintSnapshot.Unavailable,
                matching
            )
        );
        Assert.True(lifecycle.IsEnabled);
        Assert.True(lifecycle.IsWorldActive);
    }

    private static void Subscribe(
        HostileShadowSessionLifecycleCoordinator lifecycle,
        ShadowSnapshotTrigger trigger = ShadowSnapshotTrigger.Join
    )
    {
        Assert.True(
            lifecycle.TrySubscribe(
                123456789,
                Owner,
                "Farm",
                trigger,
                out var reason
            ),
            reason
        );
    }

    private static HostileShadowConfigFingerprintReport Fingerprint(
        string session,
        string playerKey,
        char hashCharacter
    )
    {
        return new HostileShadowConfigFingerprintReport
        {
            SessionId = session,
            PlayerKey = playerKey,
            ConfigSchemaVersion = 1,
            Hash = new string(hashCharacter, 64),
        };
    }

    private static DarkHandInteractionLeaseRequest LeaseRequest(
        string session,
        long nonce
    )
    {
        return new DarkHandInteractionLeaseRequest
        {
            SessionId = session,
            Nonce = nonce,
            OwnerPlayerKey = Owner,
            LocationId = "Farm",
            TargetId = "target",
            OperationId = "operation",
            ObservedTargetRevision = 1,
        };
    }
}
