#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Session-only lifecycle owner for peer location subscriptions, diagnostic fingerprints, and the
/// private DarkHand lease window. It owns no entity, config value, persistence, or world mutation.
/// </summary>
internal sealed class HostileShadowSessionLifecycleCoordinator : IDisposable
{
    internal const int MaximumPeerRecords = 32;

    private sealed record PeerSubscription(
        string PlayerKey,
        string LocationId,
        ShadowSnapshotTrigger Trigger
    );

    private readonly Dictionary<long, PeerSubscription> subscriptions = new();
    private readonly Dictionary<long, HostileShadowConfigFingerprintReport> fingerprints = new();
    private bool dayActive;
    private bool disposed;

    internal HostileShadowSessionLifecycleCoordinator(
        DarkHandInteractionLeaseAuthority? leaseAuthority = null
    )
    {
        LeaseAuthority = leaseAuthority ?? new DarkHandInteractionLeaseAuthority();
    }

    internal DarkHandInteractionLeaseAuthority LeaseAuthority { get; }
    internal string SessionId { get; private set; } = string.Empty;
    internal bool IsSessionActive { get; private set; }
    internal bool IsEnabled { get; private set; }
    internal bool IsWorldActive { get; private set; }
    internal int SubscriptionCount => subscriptions.Count;
    internal int FingerprintCount => fingerprints.Count;

    internal bool BeginSession(
        string sessionId,
        bool enabled,
        out string reason
    )
    {
        if (disposed || !SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = disposed
                ? "hostile-shadow.lifecycle-disposed"
                : "hostile-shadow.lifecycle-session-invalid";
            return false;
        }
        ClearWindow();
        if (!LeaseAuthority.BeginSession(sessionId, out reason))
            return false;
        SessionId = sessionId;
        IsSessionActive = true;
        IsEnabled = enabled;
        dayActive = true;
        IsWorldActive = enabled;
        reason = enabled
            ? "hostile-shadow.lifecycle-session-started"
            : "hostile-shadow.lifecycle-session-started-disabled";
        return true;
    }

    internal bool TrySubscribe(
        long peerPlayerId,
        string playerKey,
        string locationId,
        ShadowSnapshotTrigger trigger,
        out string reason
    )
    {
        if (
            disposed
            || !IsSessionActive
            || !IsEnabled
            || !IsWorldActive
            || peerPlayerId <= 0
            || !SanityPlayerKey.IsCanonical(playerKey)
            || !HostileShadowProtocol.IsValidLocationId(locationId)
            || !HostileShadowProtocol.IsValidSnapshotTrigger(trigger)
        )
        {
            reason = "hostile-shadow.subscription-context-invalid";
            return false;
        }
        if (
            !subscriptions.ContainsKey(peerPlayerId)
            && subscriptions.Count >= MaximumPeerRecords
        )
        {
            reason = "hostile-shadow.subscription-window-full";
            return false;
        }
        if (
            subscriptions.TryGetValue(peerPlayerId, out var previous)
            && !string.Equals(
                previous.LocationId,
                locationId,
                StringComparison.Ordinal
            )
        )
        {
            // A lease is location-bound; a warp retires its nonce/receipt window before the new
            // subscription becomes visible, so a late old-location request cannot be replayed.
            LeaseAuthority.ClearOwner(previous.PlayerKey);
        }
        subscriptions[peerPlayerId] = new PeerSubscription(
            playerKey,
            locationId,
            trigger
        );
        reason = "hostile-shadow.subscription-updated";
        return true;
    }

    internal bool TryGetSubscription(
        long peerPlayerId,
        out string playerKey,
        out string locationId,
        out ShadowSnapshotTrigger trigger
    )
    {
        if (subscriptions.TryGetValue(peerPlayerId, out var subscription))
        {
            playerKey = subscription.PlayerKey;
            locationId = subscription.LocationId;
            trigger = subscription.Trigger;
            return true;
        }
        playerKey = string.Empty;
        locationId = string.Empty;
        trigger = default;
        return false;
    }

    internal IReadOnlyList<long> GetSubscriberPlayerIds()
    {
        var result = new List<long>(subscriptions.Keys);
        result.Sort();
        return result;
    }

    internal bool RecordFingerprint(
        long peerPlayerId,
        string expectedPlayerKey,
        HostileShadowConfigFingerprintReport report,
        out string reason
    )
    {
        if (
            disposed
            || !IsSessionActive
            || peerPlayerId <= 0
            || !HostileShadowConfigFingerprintProtocol.IsValidReport(
                report,
                expectedPlayerKey,
                SessionId,
                out reason
            )
        )
        {
            reason = "hostile-shadow.config-fingerprint-context-invalid";
            return false;
        }
        if (
            !fingerprints.ContainsKey(peerPlayerId)
            && fingerprints.Count >= MaximumPeerRecords
        )
        {
            reason = "hostile-shadow.config-fingerprint-window-full";
            return false;
        }
        fingerprints[peerPlayerId] = new HostileShadowConfigFingerprintReport
        {
            ProtocolVersion = report.ProtocolVersion,
            SchemaVersion = report.SchemaVersion,
            SessionId = report.SessionId,
            PlayerKey = report.PlayerKey,
            ConfigSchemaVersion = report.ConfigSchemaVersion,
            Hash = report.Hash,
        };
        reason = "hostile-shadow.config-fingerprint-recorded";
        return true;
    }

    internal void Disconnect(long peerPlayerId, string ownerPlayerKey)
    {
        subscriptions.Remove(peerPlayerId);
        fingerprints.Remove(peerPlayerId);
        if (SanityPlayerKey.IsCanonical(ownerPlayerKey))
            LeaseAuthority.ClearOwner(ownerPlayerKey);
    }

    internal void DayEnding()
    {
        dayActive = false;
        IsWorldActive = false;
        ClearWindow();
    }

    internal void DayStarted()
    {
        dayActive = !disposed && IsSessionActive;
        IsWorldActive = dayActive && IsEnabled;
    }

    internal void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        IsWorldActive = enabled && IsSessionActive && dayActive && !disposed;
        if (!enabled)
            ClearWindow();
    }

    internal void ClearSession()
    {
        ClearWindow();
        LeaseAuthority.ClearSession();
        SessionId = string.Empty;
        IsSessionActive = false;
        IsEnabled = false;
        dayActive = false;
        IsWorldActive = false;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ClearSession();
    }

    private void ClearWindow()
    {
        subscriptions.Clear();
        fingerprints.Clear();
        LeaseAuthority.ClearWindow();
    }
}
