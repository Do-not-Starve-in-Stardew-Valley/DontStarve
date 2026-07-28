#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Resource.Sanity;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal static class HostileShadowNetSerializationProbe
{
    /// <summary>
    /// Exercises the exact NetCollection&lt;NPC&gt; full serializer used by GameLocation.characters.
    /// The custom subclass has a public parameterless constructor and inherited net fields only.
    /// </summary>
    internal static HostileShadowPhysicalEntityCapability Probe()
    {
        try
        {
            var source = new NetCollection<NPC>();
            var original = new HostileShadowMonster();
            original.modData[HostileShadowMonster.EntityIdModDataKey] = "1";
            source.Add(original);

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                source.WriteFull(writer);
            stream.Position = 0;

            var copy = new NetCollection<NPC>();
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                copy.ReadFull(reader, default);
            if (
                copy.Count != 1
                || copy[0] is not HostileShadowMonster roundTripped
                || !roundTripped.modData.TryGetValue(
                    HostileShadowMonster.EntityIdModDataKey,
                    out var entityId
                )
                || !string.Equals(entityId, "1", StringComparison.Ordinal)
            )
            {
                return Unavailable(
                    "hostile-shadow.physical-monster-netcollection-roundtrip-mismatch"
                );
            }

            return new HostileShadowPhysicalEntityCapability(
                HostileShadowPhysicalEntityCapabilityStatus.Available,
                "hostile-shadow.physical-monster-netcollection-roundtrip-verified"
            );
        }
        catch (Exception exception)
        {
            return Unavailable(
                string.Concat(
                    "hostile-shadow.physical-monster-netcollection-roundtrip-threw-",
                    exception.GetType().Name
                )
            );
        }
    }

    private static HostileShadowPhysicalEntityCapability Unavailable(
        string reason
    )
    {
        return new HostileShadowPhysicalEntityCapability(
            HostileShadowPhysicalEntityCapabilityStatus.Unavailable,
            reason
        );
    }
}

/// <summary>
/// Prevents the host from putting a custom CLR network type into a location until every remote
/// peer has the exact mod version and has reported loader-owned shared visuals available.
/// </summary>
internal sealed class HostileShadowPeerVisibilityGate : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly string modVersion;
    private readonly HostileShadowAuthority authority;
    private readonly HostileShadowPhysicalEntityCapability serializationCapability;
    private readonly HashSet<long> incompatiblePeerIds = new();
    private readonly HashSet<long> pendingVisibilityPeerIds = new();
    private readonly HashSet<long> unavailableVisibilityPeerIds = new();
    private bool disposed;

    internal HostileShadowPeerVisibilityGate(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        string modVersion,
        HostileShadowAuthority authority,
        HostileShadowPhysicalEntityCapability serializationCapability
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.modVersion = string.IsNullOrWhiteSpace(modVersion)
            ? throw new ArgumentException("A mod version is required.", nameof(modVersion))
            : modVersion;
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.serializationCapability = serializationCapability;

        helper.Events.Multiplayer.PeerContextReceived += OnPeerContextReceived;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    internal HostileShadowPhysicalEntityCapability CurrentCapability
    {
        get
        {
            if (!serializationCapability.IsAvailable)
                return serializationCapability;
            if (incompatiblePeerIds.Count > 0)
            {
                return Unavailable(
                    "hostile-shadow.peer-mod-missing-or-version-mismatch"
                );
            }
            if (pendingVisibilityPeerIds.Count > 0)
            {
                return Unavailable(
                    "hostile-shadow.peer-shared-visibility-capability-pending"
                );
            }
            return unavailableVisibilityPeerIds.Count == 0
                ? serializationCapability
                : Unavailable(
                    "hostile-shadow.peer-shared-visibility-capability-unavailable"
                );
        }
    }

    internal void RefreshForSession()
    {
        ClearSession();
        foreach (var peer in helper.Multiplayer.GetConnectedPlayers())
        {
            if (!IsCompatible(peer))
                incompatiblePeerIds.Add(peer.PlayerID);
            else if (Game1.IsMasterGame && !peer.IsSplitScreen)
                pendingVisibilityPeerIds.Add(peer.PlayerID);
        }
        if (incompatiblePeerIds.Count > 0)
        {
            monitor.Log(
                "Hostile shadow peer gate: hostile-shadow.peer-mod-missing-or-version-mismatch",
                LogLevel.Warn
            );
        }
    }

    internal bool Record(
        ShadowPhysicalCapabilityReport report,
        long senderPlayerId,
        out string reason
    )
    {
        if (disposed || !Game1.IsMasterGame || !IsConnectedPeer(senderPlayerId))
        {
            reason = "hostile-shadow.physical-capability-sender-unavailable";
            return false;
        }
        pendingVisibilityPeerIds.Remove(senderPlayerId);
        if (report.Available)
        {
            unavailableVisibilityPeerIds.Remove(senderPlayerId);
            reason = "hostile-shadow.peer-shared-visibility-capability-ready";
            return true;
        }

        unavailableVisibilityPeerIds.Add(senderPlayerId);
        reason = "hostile-shadow.peer-shared-visibility-capability-unavailable";
        monitor.Log(
            $"Hostile shadow peer gate: {reason} ({report.Reason})",
            LogLevel.Warn
        );
        if (authority.IsHostSessionActive)
        {
            authority.CleanupAll(
                HostileShadowCleanupReasonIds.PeerCapabilityChanged
            );
        }
        return true;
    }

    internal void OnResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (
            !Game1.IsMasterGame
            || reason != SanityResourceReleaseReason.ContentInvalidated
        )
        {
            return;
        }

        pendingVisibilityPeerIds.Clear();
        unavailableVisibilityPeerIds.Clear();
        foreach (var peer in helper.Multiplayer.GetConnectedPlayers())
        {
            if (IsCompatible(peer) && !peer.IsSplitScreen)
                pendingVisibilityPeerIds.Add(peer.PlayerID);
        }
    }

    internal void ClearSession()
    {
        incompatiblePeerIds.Clear();
        pendingVisibilityPeerIds.Clear();
        unavailableVisibilityPeerIds.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.Multiplayer.PeerContextReceived -= OnPeerContextReceived;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        ClearSession();
    }

    private void OnPeerContextReceived(
        object? sender,
        PeerContextReceivedEventArgs e
    )
    {
        if (disposed)
            return;
        if (IsCompatible(e.Peer))
        {
            incompatiblePeerIds.Remove(e.Peer.PlayerID);
            unavailableVisibilityPeerIds.Remove(e.Peer.PlayerID);
            if (Game1.IsMasterGame && !e.Peer.IsSplitScreen)
            {
                pendingVisibilityPeerIds.Add(e.Peer.PlayerID);
                CleanupForPeerChange();
            }
            return;
        }

        incompatiblePeerIds.Add(e.Peer.PlayerID);
        pendingVisibilityPeerIds.Remove(e.Peer.PlayerID);
        unavailableVisibilityPeerIds.Remove(e.Peer.PlayerID);
        monitor.Log(
            "Hostile shadow peer gate: hostile-shadow.peer-mod-missing-or-version-mismatch",
            LogLevel.Warn
        );
        if (Game1.IsMasterGame && authority.IsHostSessionActive)
        {
            authority.CleanupAll(
                HostileShadowCleanupReasonIds.IncompatiblePeer
            );
        }
    }

    private void OnPeerDisconnected(
        object? sender,
        PeerDisconnectedEventArgs e
    )
    {
        incompatiblePeerIds.Remove(e.Peer.PlayerID);
        pendingVisibilityPeerIds.Remove(e.Peer.PlayerID);
        unavailableVisibilityPeerIds.Remove(e.Peer.PlayerID);
    }

    private void CleanupForPeerChange()
    {
        if (authority.IsHostSessionActive)
        {
            authority.CleanupAll(
                HostileShadowCleanupReasonIds.PeerCapabilityChanged
            );
        }
    }

    private bool IsConnectedPeer(long playerId)
    {
        foreach (var peer in helper.Multiplayer.GetConnectedPlayers())
        {
            if (peer.PlayerID == playerId && IsCompatible(peer))
                return true;
        }
        return false;
    }

    private bool IsCompatible(IMultiplayerPeer peer)
    {
        if (!peer.HasSmapi)
            return false;
        var peerMod = peer.GetMod(modId);
        return peerMod is not null
            && string.Equals(
                peerMod.Version.ToString(),
                modVersion,
                StringComparison.Ordinal
            );
    }

    private static HostileShadowPhysicalEntityCapability Unavailable(
        string reason
    )
    {
        return new HostileShadowPhysicalEntityCapability(
            HostileShadowPhysicalEntityCapabilityStatus.Unavailable,
            reason
        );
    }
}
