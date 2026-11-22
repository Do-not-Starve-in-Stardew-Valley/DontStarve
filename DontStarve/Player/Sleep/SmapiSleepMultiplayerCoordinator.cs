#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Sleep;

internal sealed class SmapiSleepMultiplayerCoordinator : IDisposable
{
    internal const string BeginRequestMessageType = "Sleep.BeginRequest.v2";
    internal const string BeginDecisionMessageType = "Sleep.BeginDecision.v2";
    internal const string CancelRequestMessageType = "Sleep.CancelRequest.v1";
    internal const string SnapshotMessageType = "Sleep.Snapshot.v2";
    internal const string SnapshotRequestMessageType = "Sleep.SnapshotRequest.v1";
    // Pending-darkness 死亡标记同步（防逃课决议③）：SMAPI ReadSaveData 只在主机电脑可用，
    // 远程农场工读不到存档里的标记，必须由主机下发；客户端在读档完成后主动请求。
    internal const string PendingDarknessWakeMessageType = "Sleep.PendingDarknessWake.v1";
    internal const string PendingDarknessWakeRequestMessageType = "Sleep.PendingDarknessWakeRequest.v1";

    private readonly IModHelper helper;
    private readonly string modId;
    private readonly SmapiSleepLifecycleAdapter adapter;
    private long nextNonce;
    private bool disposed;

    internal SmapiSleepMultiplayerCoordinator(
        IModHelper helper,
        string modId,
        SmapiSleepLifecycleAdapter adapter
    )
    {
        this.helper = helper;
        this.modId = modId;
        this.adapter = adapter;
        adapter.HostSnapshotChanged += OnHostSnapshotChanged;
    }

    internal void Initialize()
    {
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
    }

    internal void OnSessionStarted()
    {
        nextNonce = 0;
        if (adapter.Role == SleepAuthorityRole.Host)
            BroadcastFullSnapshot();
        else if (adapter.Role == SleepAuthorityRole.Client)
            RequestFullSnapshot();
    }

    internal void BroadcastFullSnapshot()
    {
        if (adapter.Role != SleepAuthorityRole.Host)
            return;
        helper.Multiplayer.SendMessage(
            adapter.CreateFullSnapshot(),
            SnapshotMessageType,
            new[] { modId },
            null
        );
    }

    internal void SendFullSnapshot(long playerId)
    {
        if (adapter.Role != SleepAuthorityRole.Host)
            return;
        helper.Multiplayer.SendMessage(
            adapter.CreateFullSnapshot(),
            SnapshotMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    internal void RequestFullSnapshot()
    {
        var local = Game1.player;
        var host = Game1.MasterPlayer;
        if (adapter.Role == SleepAuthorityRole.Host || local is null || host is null)
            return;
        helper.Multiplayer.SendMessage(
            new SleepSnapshotRequest
            {
                SessionId = adapter.SessionId,
                PlayerKey = local.UniqueMultiplayerID.ToString(),
                KnownRevision = 0,
            },
            SnapshotRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    internal void SendBeginRequest(SleepSnapshot snapshot, string attemptId)
    {
        var host = Game1.MasterPlayer;
        if (adapter.Role != SleepAuthorityRole.Client || host is null)
            return;
        helper.Multiplayer.SendMessage(
            new SleepBeginRequest
            {
                SessionId = adapter.SessionId,
                PlayerKey = snapshot.PlayerKey,
                BedId = snapshot.BedId,
                AttemptId = attemptId,
                Nonce = ++nextNonce,
                ExpectedRevision = adapter.KnownRevision(snapshot.PlayerKey),
            },
            BeginRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    internal void SendCancelRequest(string playerKey, string attemptId)
    {
        var host = Game1.MasterPlayer;
        if (adapter.Role != SleepAuthorityRole.Client || host is null)
            return;
        helper.Multiplayer.SendMessage(
            new SleepCancelRequest
            {
                SessionId = adapter.SessionId,
                PlayerKey = playerKey,
                AttemptId = attemptId,
                Nonce = ++nextNonce,
            },
            CancelRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    internal void RequestPendingDarknessWake()
    {
        var host = Game1.MasterPlayer;
        if (adapter.Role != SleepAuthorityRole.Client || host is null)
            return;
        helper.Multiplayer.SendMessage(
            new SleepPendingDarknessWakeRequest { SessionId = adapter.SessionId },
            PendingDarknessWakeRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    internal void SendPendingDarknessWake(long playerId, IReadOnlyDictionary<string, int> markers)
    {
        if (adapter.Role != SleepAuthorityRole.Host || !Context.IsMainPlayer)
            return;
        helper.Multiplayer.SendMessage(
            new SleepPendingDarknessWakeMessage
            {
                SessionId = adapter.SessionId,
                Markers = new Dictionary<string, int>(markers),
            },
            PendingDarknessWakeMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.Multiplayer.PeerConnected -= OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        adapter.HostSnapshotChanged -= OnHostSnapshotChanged;
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        adapter.OnPeerConnected(e.Peer.PlayerID, e.Peer.IsHost);
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        adapter.OnPeerDisconnected(e.Peer.PlayerID, e.Peer.IsHost);
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;
        try
        {
            switch (e.Type)
            {
                case BeginRequestMessageType when adapter.Role == SleepAuthorityRole.Host:
                    adapter.HandleBeginRequest(e.ReadAs<SleepBeginRequest>(), e.FromPlayerID);
                    break;
                case CancelRequestMessageType when adapter.Role == SleepAuthorityRole.Host:
                    adapter.HandleCancelRequest(e.ReadAs<SleepCancelRequest>(), e.FromPlayerID);
                    break;
                case SnapshotRequestMessageType when adapter.Role == SleepAuthorityRole.Host:
                    adapter.HandleSnapshotRequest(e.ReadAs<SleepSnapshotRequest>(), e.FromPlayerID);
                    break;
                case SnapshotMessageType when adapter.Role != SleepAuthorityRole.Host:
                    var host = Game1.MasterPlayer;
                    if (host is not null && e.FromPlayerID == host.UniqueMultiplayerID)
                        adapter.ApplyClientSnapshot(e.ReadAs<SleepSnapshotMessage>());
                    break;
                case BeginDecisionMessageType when adapter.Role == SleepAuthorityRole.Client:
                    if (Game1.MasterPlayer is Farmer decisionHost && e.FromPlayerID == decisionHost.UniqueMultiplayerID)
                        adapter.HandleBeginDecision(e.ReadAs<SleepBeginDecision>());
                    break;
                case PendingDarknessWakeRequestMessageType when adapter.Role == SleepAuthorityRole.Host:
                    adapter.SendPendingDarknessWakeSnapshot(e.FromPlayerID);
                    break;
                case PendingDarknessWakeMessageType when adapter.Role != SleepAuthorityRole.Host:
                    var wakeSyncHost = Game1.MasterPlayer;
                    if (wakeSyncHost is not null && e.FromPlayerID == wakeSyncHost.UniqueMultiplayerID)
                        adapter.ApplyPendingDarknessWakeSync(e.ReadAs<SleepPendingDarknessWakeMessage>()?.Markers ?? new Dictionary<string, int>());
                    break;
            }
        }
        catch (Exception)
        {
            // A malformed sleep message is dropped; the next full snapshot request repairs a
            // client mirror without allowing a remote payload to mutate gameplay state.
        }
    }

    private void OnHostSnapshotChanged(SleepPlayerSnapshot snapshot)
    {
        if (adapter.Role != SleepAuthorityRole.Host)
            return;
        helper.Multiplayer.SendMessage(
            new SleepSnapshotMessage
            {
                SessionId = adapter.SessionId,
                IsFull = false,
                Players = new() { snapshot.Clone() },
            },
            SnapshotMessageType,
            new[] { modId },
            null
        );
    }

    internal void SendBeginDecision(long playerId, SleepBeginRequest request, bool accepted, string reason)
    {
        helper.Multiplayer.SendMessage(new SleepBeginDecision
        {
            SessionId = adapter.SessionId, PlayerKey = request.PlayerKey,
            AttemptId = request.AttemptId, Accepted = accepted, Reason = reason,
        }, BeginDecisionMessageType, new[] { modId }, new[] { playerId });
    }
}
