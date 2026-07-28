#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// SMAPI 网络薄边界。协议判断与 revision 规则都在纯逻辑 service 中，
/// 此处只核对 sender/在线游戏事实并发送已有 DTO。
/// </summary>
internal sealed class SmapiSanityMultiplayerCoordinator : ISanityRequestTruthSource
{
    internal const string ChangeRequestMessageType = "Sanity.ChangeRequest.v1";
    internal const string SnapshotMessageType = "Sanity.Snapshot.v1";
    internal const string SnapshotRequestMessageType = "Sanity.SnapshotRequest.v1";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanityChangeService service;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);

    internal SmapiSanityMultiplayerCoordinator(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanityChangeService service
    )
    {
        this.helper = helper;
        this.monitor = monitor;
        this.modId = modId;
        this.service = service;
    }

    internal void Initialize()
    {
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        service.HostStateChanged += OnHostStateChanged;
        service.ClientRequestCreated += OnClientRequestCreated;
    }

    internal void OnSessionStarted()
    {
        loggedReasons.Clear();
        if (service.Role == SanityAuthorityRole.Host)
            BroadcastFullSnapshot();
        else if (service.Role == SanityAuthorityRole.Client)
            RequestFullSnapshot();
    }

    internal void ResetSessionDiagnostics()
    {
        loggedReasons.Clear();
    }

    public SanityRequestTruth Resolve(
        SanityChangeRequest request,
        long senderPlayerId
    )
    {
        if (request.Source != SanityChangeSource.Food)
            return SanityRequestTruth.Rejected("request-source-has-no-runtime-resolver");

        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        if (player is null)
            return SanityRequestTruth.Rejected("request-player-is-not-online");
        if (
            player.UniqueMultiplayerID != senderPlayerId
            || !string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                request.PlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return SanityRequestTruth.Rejected("request-player-context-does-not-match");
        }
        if (
            EatFood.FoodSanity is null
            || !EatFood.FoodSanity.TryGetValue(request.InteractionId, out var delta)
        )
        {
            return SanityRequestTruth.Rejected("food-interaction-is-not-in-host-data");
        }

        // 客户端不提交 delta；主机既要用自己的 food.json 重算，也要看到相同 itemToEat。
        // 若 SMAPI/游戏同步时序不能提供这项事实则 fail closed，留给实机矩阵确认。
        if (
            player.itemToEat is null
            || !string.Equals(
                player.itemToEat.ItemId,
                request.InteractionId,
                StringComparison.Ordinal
            )
        )
        {
            return SanityRequestTruth.Rejected("food-context-is-not-observable-by-host");
        }

        return SanityRequestTruth.Accepted(delta);
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (service.Role == SanityAuthorityRole.Host)
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
            if (!service.TryEnsureHostPlayer(playerKey, out _, out var reason))
            {
                LogOnce(reason, LogLevel.Warn);
                return;
            }

            SendFullSnapshot(e.Peer.PlayerID);
        }
        else if (service.Role == SanityAuthorityRole.Client && e.Peer.IsHost)
        {
            RequestFullSnapshot();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
        service.ForgetPeer(playerKey);

        if (service.Role == SanityAuthorityRole.Client && e.Peer.IsHost)
        {
            var localPlayer = Game1.player;
            if (localPlayer is not null)
            {
                service.BeginClientSession(
                    SanityPlayerKey.FromUniqueMultiplayerId(
                        localPlayer.UniqueMultiplayerID
                    ),
                    out _
                );
            }
            LogOnce("host-peer-disconnected-client-state-reset", LogLevel.Warn);
        }
    }

    private void OnModMessageReceived(
        object? sender,
        ModMessageReceivedEventArgs e
    )
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        try
        {
            switch (e.Type)
            {
                case ChangeRequestMessageType
                    when service.Role == SanityAuthorityRole.Host:
                    HandleChangeRequest(e);
                    break;
                case SnapshotRequestMessageType
                    when service.Role == SanityAuthorityRole.Host:
                    HandleSnapshotRequest(e);
                    break;
                case SnapshotMessageType
                    when service.Role == SanityAuthorityRole.Client:
                    HandleSnapshot(e);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogOnce(
                $"multiplayer-message-decode-failed:{ex.GetType().Name}",
                LogLevel.Warn
            );
        }
    }

    private void HandleChangeRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SanityChangeRequest>();
        var result = service.HandleHostRequest(
            request,
            e.FromPlayerID,
            this,
            Environment.TickCount64
        );
        if (!result.Accepted)
        {
            LogOnce(result.Reason, LogLevel.Trace);
            if (result.NeedsSnapshot)
                SendFullSnapshot(e.FromPlayerID);
        }
    }

    private void HandleSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SanitySnapshotRequest>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        if (
            request is null
            || !string.Equals(
                request.PlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || (
                !string.IsNullOrEmpty(request.SessionId)
                && !string.Equals(
                    request.SessionId,
                    service.SessionId,
                    StringComparison.Ordinal
                )
            )
        )
        {
            LogOnce("snapshot-request-owner-or-session-is-invalid", LogLevel.Trace);
            return;
        }
        if (!service.TryEnsureHostPlayer(expectedPlayerKey, out _, out var reason))
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }

        SendFullSnapshot(e.FromPlayerID);
    }

    private void HandleSnapshot(ModMessageReceivedEventArgs e)
    {
        var host = Game1.MasterPlayer;
        if (host is null || e.FromPlayerID != host.UniqueMultiplayerID)
        {
            LogOnce("snapshot-sender-is-not-the-host", LogLevel.Trace);
            return;
        }

        var message = e.ReadAs<SanitySnapshotMessage>();
        var result = service.ApplyClientSnapshot(message);
        if (result.NeedsFullSnapshot)
            RequestFullSnapshot();
        else if (result.Status == SanitySnapshotApplyStatus.Rejected)
            LogOnce(result.Reason, LogLevel.Warn);
    }

    private void OnHostStateChanged(SanityStateChanged change)
    {
        if (service.Role != SanityAuthorityRole.Host)
            return;

        helper.Multiplayer.SendMessage(
            service.CreateDeltaSnapshot(change.Snapshot),
            SnapshotMessageType,
            new[] { modId },
            null
        );
    }

    private void OnClientRequestCreated(SanityChangeRequest request)
    {
        var host = Game1.MasterPlayer;
        if (service.Role != SanityAuthorityRole.Client || host is null)
        {
            LogOnce("client-request-host-is-unavailable", LogLevel.Trace);
            return;
        }

        helper.Multiplayer.SendMessage(
            request,
            ChangeRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    private void RequestFullSnapshot()
    {
        var localPlayer = Game1.player;
        var host = Game1.MasterPlayer;
        if (localPlayer is null || host is null)
        {
            LogOnce("snapshot-request-player-or-host-is-unavailable", LogLevel.Trace);
            return;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            localPlayer.UniqueMultiplayerID
        );
        var knownRevision = service.TryGetSnapshot(playerKey, out var snapshot)
            ? snapshot.Revision
            : 0;
        helper.Multiplayer.SendMessage(
            new SanitySnapshotRequest
            {
                SessionId = service.SessionId,
                PlayerKey = playerKey,
                KnownRevision = knownRevision,
            },
            SnapshotRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    private void BroadcastFullSnapshot()
    {
        if (
            service.Role != SanityAuthorityRole.Host
            || !SanityProtocol.IsValidSessionId(service.SessionId)
        )
        {
            return;
        }

        helper.Multiplayer.SendMessage(
            service.CreateFullSnapshot(),
            SnapshotMessageType,
            new[] { modId },
            null
        );
    }

    private void SendFullSnapshot(long playerId)
    {
        if (
            service.Role != SanityAuthorityRole.Host
            || !SanityProtocol.IsValidSessionId(service.SessionId)
        )
        {
            return;
        }

        helper.Multiplayer.SendMessage(
            service.CreateFullSnapshot(),
            SnapshotMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    private void LogOnce(string reason, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedReasons.Add(reason))
            return;
        monitor.Log($"Sanity multiplayer: {reason}", level);
    }
}
