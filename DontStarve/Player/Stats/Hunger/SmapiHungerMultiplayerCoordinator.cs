#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Stats.Food;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

internal sealed class SmapiHungerMultiplayerCoordinator
{
    internal const string FoodRequestMessageType = "Hunger.FoodRequest.v1";
    internal const string SnapshotMessageType = "Hunger.Snapshot.v1";
    internal const string SnapshotRequestMessageType = "Hunger.SnapshotRequest.v1";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly HungerChangeService service;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);

    internal SmapiHungerMultiplayerCoordinator(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        HungerChangeService service
    )
    {
        this.helper = helper;
        this.monitor = monitor;
        this.modId = modId;
        this.service = service;
    }

    internal void Initialize()
    {
        service.SetFoodCompletionPatchInstalled(
            HungerFoodCompletionPatch.TryInstall(modId, monitor, service)
        );
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        service.HostStateChanged += OnHostStateChanged;
        service.ClientFoodRequestCreated += OnClientFoodRequestCreated;
    }

    internal void OnSessionStarted()
    {
        loggedReasons.Clear();
        if (service.Role == HungerAuthorityRole.Host)
        {
            service.SetHostFoodCompletionSupported(
                service.HostFoodCompletionPatchInstalled
            );
            HungerFoodCompletionPatch.ResetSessionDiagnostics();
            BroadcastFullSnapshot();
        }
        else if (service.Role == HungerAuthorityRole.Client)
            RequestFullSnapshot();
    }

    internal void ResetSessionDiagnostics()
    {
        loggedReasons.Clear();
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (service.Role == HungerAuthorityRole.Host)
        {
            var playerKey = FromPlayerId(e.Peer.PlayerID);
            service.SetHostFoodCompletionSupported(
                service.HostFoodCompletionPatchInstalled
            );
            if (!service.TryEnsureHostPlayer(playerKey, out _, out var reason))
            {
                LogOnce(reason, LogLevel.Warn);
                return;
            }

            SendFullSnapshot(e.Peer.PlayerID);
        }
        else if (service.Role == HungerAuthorityRole.Client && e.Peer.IsHost)
        {
            RequestFullSnapshot();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        var playerKey = FromPlayerId(e.Peer.PlayerID);
        service.ForgetPeer(playerKey);
        if (service.Role == HungerAuthorityRole.Client && e.Peer.IsHost)
        {
            var localPlayer = Game1.player;
            if (localPlayer is not null)
                service.BeginClientSession(FromPlayerId(localPlayer.UniqueMultiplayerID), out _);
            LogOnce("host-peer-disconnected-client-hunger-state-reset", LogLevel.Warn);
        }
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        try
        {
            switch (e.Type)
            {
                case FoodRequestMessageType when service.Role == HungerAuthorityRole.Host:
                    HandleFoodRequest(e);
                    break;
                case SnapshotRequestMessageType when service.Role == HungerAuthorityRole.Host:
                    HandleSnapshotRequest(e);
                    break;
                case SnapshotMessageType when service.Role == HungerAuthorityRole.Client:
                    HandleSnapshot(e);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogOnce($"hunger-message-decode-failed:{ex.GetType().Name}", LogLevel.Warn);
        }
    }

    private void HandleFoodRequest(ModMessageReceivedEventArgs e)
    {
        if (
            !HungerFoodCompletionPolicy.ShouldAcceptLegacyRequest(
                service.Role,
                service.HostFoodCompletionPatchInstalled
            )
        )
        {
            LogOnce("legacy-hunger-food-request-ignored-by-host-completion", LogLevel.Trace);
            return;
        }

        var request = e.ReadAs<HungerFoodChangeRequest>();
        if (
            request is null
            || !TryResolveFoodDelta(request.InteractionId, request.PlayerKey, out var delta)
        )
        {
            LogOnce("hunger-food-request-is-not-in-host-data", LogLevel.Trace);
            return;
        }

        var result = service.HandleHostFoodRequest(request, e.FromPlayerID, delta);
        if (!result.Accepted && result.NeedsSnapshot)
            SendFullSnapshot(e.FromPlayerID);
    }

    private void HandleSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<HungerSnapshotRequest>();
        var expectedPlayerKey = FromPlayerId(e.FromPlayerID);
        if (
            request is null
            || !string.Equals(request.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
            || (
                !string.IsNullOrEmpty(request.SessionId)
                && !string.Equals(request.SessionId, service.SessionId, StringComparison.Ordinal)
            )
        )
        {
            LogOnce("hunger-snapshot-request-owner-or-session-is-invalid", LogLevel.Trace);
            return;
        }
        if (!service.TryEnsureHostPlayer(expectedPlayerKey, out _, out var reason))
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }

        service.SetHostFoodCompletionSupported(
            service.HostFoodCompletionPatchInstalled
        );
        SendFullSnapshot(e.FromPlayerID);
    }

    private void HandleSnapshot(ModMessageReceivedEventArgs e)
    {
        var host = Game1.MasterPlayer;
        if (host is null || e.FromPlayerID != host.UniqueMultiplayerID)
        {
            LogOnce("hunger-snapshot-sender-is-not-the-host", LogLevel.Trace);
            return;
        }

        var message = e.ReadAs<HungerSnapshotMessage>();
        var result = service.ApplyClientSnapshot(message);
        if (result.NeedsFullSnapshot)
            RequestFullSnapshot();
        else if (result.Status == HungerSnapshotApplyStatus.Rejected)
            LogOnce(result.Reason, LogLevel.Warn);
    }

    private void OnHostStateChanged(HungerPlayerSnapshot snapshot)
    {
        if (service.Role != HungerAuthorityRole.Host)
            return;

        service.SetHostFoodCompletionSupported(
            service.HostFoodCompletionPatchInstalled
        );
        helper.Multiplayer.SendMessage(
            service.CreateDeltaSnapshot(snapshot),
            SnapshotMessageType,
            new[] { modId },
            null
        );
    }

    private void OnClientFoodRequestCreated(HungerFoodChangeRequest request)
    {
        var host = Game1.MasterPlayer;
        if (service.Role != HungerAuthorityRole.Client || host is null)
        {
            LogOnce("client-hunger-request-host-is-unavailable", LogLevel.Trace);
            return;
        }

        if (
            !HungerFoodCompletionPolicy.ShouldSendLegacyRequest(
                service.Role,
                service.HostFoodCompletionSupported
            )
        )
        {
            LogOnce("legacy-hunger-food-request-suppressed-by-host-completion", LogLevel.Trace);
            return;
        }

        helper.Multiplayer.SendMessage(
            request,
            FoodRequestMessageType,
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
            LogOnce("hunger-snapshot-request-player-or-host-is-unavailable", LogLevel.Trace);
            return;
        }

        var playerKey = FromPlayerId(localPlayer.UniqueMultiplayerID);
        var knownRevision = service.TryGetSnapshot(playerKey, out var snapshot)
            ? snapshot.Revision
            : 0;
        helper.Multiplayer.SendMessage(
            new HungerSnapshotRequest
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
            service.Role != HungerAuthorityRole.Host
            || !HungerProtocol.IsValidSessionId(service.SessionId)
        )
            return;

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
            service.Role != HungerAuthorityRole.Host
            || !HungerProtocol.IsValidSessionId(service.SessionId)
        )
            return;

        helper.Multiplayer.SendMessage(
            service.CreateFullSnapshot(),
            SnapshotMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    private bool TryResolveFoodDelta(
        string interactionId,
        string playerKey,
        out double delta
    )
    {
        delta = 0;
        if (string.IsNullOrWhiteSpace(interactionId))
            return false;

        var itemId = interactionId.StartsWith("(O)", StringComparison.Ordinal)
            ? interactionId[3..]
            : interactionId;

        Farmer? player = null;
        if (
            long.TryParse(
                playerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var playerId
            )
        )
            player = Game1.GetPlayer(playerId, onlyOnline: true);

        if (
            player is null
            || player.itemToEat is not { } hostFood
            || !string.Equals(hostFood.ItemId, itemId, StringComparison.Ordinal)
        )
            return false;

        if (StarfruitFoodRules.IsStarfruit(itemId))
        {
            delta = StarfruitFoodRules.GetFillDelta(
                service.GetCurrent(playerKey),
                service.GetMaximum(playerKey)
            );
            return true;
        }

        // 主机使用自己的 itemToEat 重建 preserve/seed 等来源事实，客户端不提交数值。
        return FoodRuleRuntime.TryGetHunger(hostFood, out delta);
    }

    private static string FromPlayerId(long playerId) =>
        playerId.ToString(CultureInfo.InvariantCulture);

    private void LogOnce(string reason, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedReasons.Add(reason))
            return;
        // Prefix matches the Sanity coordinator so both multiplayer lanes are attributable in
        // the SMAPI log; dedup keeps one line per reason per session.
        monitor.Log($"Hunger multiplayer: {reason}", level);
    }
}
