#nullable enable

using System;
using System.Globalization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Buff.Buffs;

internal sealed class RestoreBuffObservation
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string BuffId { get; set; } = string.Empty;

    public int TotalMilliseconds { get; set; }

    public int RemainingMilliseconds { get; set; }
}

internal sealed class ElectricityBuffObservation
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string BuffId { get; set; } = string.Empty;
}

internal sealed class ElectricityBuffStateMessage
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string BuffId { get; set; } = string.Empty;

    public bool Active { get; set; }

    public float Bonus { get; set; }
}

internal sealed class BuffSessionRequest
{
    public string PlayerKey { get; set; } = string.Empty;
}

internal sealed class BuffSessionMessage
{
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// Buff 的 SMAPI 网络边界。客户端只报告本地 active Buff 事实，主机负责恢复和电击倍率的写回。
/// </summary>
internal sealed class BuffMultiplayerCoordinator
{
    internal const string RestoreObservationMessageType = "Buff.RestoreObservation.v1";
    internal const string ElectricityObservationMessageType = "Buff.ElectricityObservation.v1";
    internal const string ElectricityStateMessageType = "Buff.ElectricityState.v1";
    internal const string SessionRequestMessageType = "Buff.SessionRequest.v1";
    internal const string SessionMessageType = "Buff.Session.v1";

    private readonly IModHelper helper;
    private readonly string modId;
    private string sessionId = string.Empty;
    private string loadedSaveIdentity = string.Empty;

    internal BuffMultiplayerCoordinator(IModHelper helper, string modId)
    {
        this.helper = helper;
        this.modId = modId;
    }

    internal event Action<RestoreBuffObservation, long>? RestoreObservationReceived;

    internal event Action<ElectricityBuffObservation, long>? ElectricityObservationReceived;

    internal event Action<ElectricityBuffStateMessage>? ElectricityStateReceived;

    internal string SessionId => sessionId;

    internal void Initialize()
    {
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
    }

    internal void ObserveRestore(RestoreBuffObservation observation)
    {
        if (Context.IsMainPlayer || string.IsNullOrWhiteSpace(sessionId))
            return;

        observation.SessionId = sessionId;
        SendToHost(observation, RestoreObservationMessageType);
    }

    internal void ObserveElectricity(ElectricityBuffObservation observation)
    {
        if (Context.IsMainPlayer || string.IsNullOrWhiteSpace(sessionId))
            return;

        observation.SessionId = sessionId;
        SendToHost(observation, ElectricityObservationMessageType);
    }

    internal void PublishElectricityState(
        long playerId,
        string playerKey,
        string buffId,
        bool active,
        float bonus
    )
    {
        if (!Context.IsMainPlayer || string.IsNullOrWhiteSpace(sessionId))
            return;

        helper.Multiplayer.SendMessage(
            new ElectricityBuffStateMessage
            {
                SessionId = sessionId,
                PlayerKey = playerKey,
                BuffId = buffId,
                Active = active,
                Bonus = bonus,
            },
            ElectricityStateMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (Context.IsMainPlayer)
        {
            var identity = GetSaveIdentity();
            if (!string.Equals(identity, loadedSaveIdentity, StringComparison.Ordinal))
            {
                loadedSaveIdentity = identity;
                sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            sessionId = string.Empty;
            SendSessionRequest();
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        loadedSaveIdentity = string.Empty;
        sessionId = string.Empty;
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (Context.IsMainPlayer)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                SendSession(e.Peer.PlayerID);
        }
        else if (e.Peer.IsHost)
        {
            SendSessionRequest();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (!Context.IsMainPlayer && e.Peer.IsHost)
            sessionId = string.Empty;
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        try
        {
            switch (e.Type)
            {
                case SessionRequestMessageType when Context.IsMainPlayer:
                    HandleSessionRequest(e);
                    break;
                case SessionMessageType when !Context.IsMainPlayer:
                    HandleSessionMessage(e);
                    break;
                case RestoreObservationMessageType when Context.IsMainPlayer:
                    HandleRestoreObservation(e);
                    break;
                case ElectricityObservationMessageType when Context.IsMainPlayer:
                    HandleElectricityObservation(e);
                    break;
                case ElectricityStateMessageType when !Context.IsMainPlayer:
                    HandleElectricityState(e);
                    break;
            }
        }
        catch
        {
            // A malformed optional observation must not affect the main game loop.
        }
    }

    private void HandleSessionRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<BuffSessionRequest>();
        if (
            request is null
            || !string.Equals(request.PlayerKey, FromPlayerId(e.FromPlayerID), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sessionId)
        )
            return;

        SendSession(e.FromPlayerID);
    }

    private void HandleSessionMessage(ModMessageReceivedEventArgs e)
    {
        var host = Game1.MasterPlayer;
        var message = e.ReadAs<BuffSessionMessage>();
        if (
            host is null
            || e.FromPlayerID != host.UniqueMultiplayerID
            || message is null
            || !IsValidSessionId(message.SessionId)
        )
            return;

        sessionId = message.SessionId;
    }

    private void HandleRestoreObservation(ModMessageReceivedEventArgs e)
    {
        var observation = e.ReadAs<RestoreBuffObservation>();
        if (
            !IsValidObservation(observation, e.FromPlayerID)
            || !string.Equals(observation.SessionId, sessionId, StringComparison.Ordinal)
        )
            return;

        RestoreObservationReceived?.Invoke(observation, e.FromPlayerID);
    }

    private void HandleElectricityObservation(ModMessageReceivedEventArgs e)
    {
        var observation = e.ReadAs<ElectricityBuffObservation>();
        if (
            !IsValidObservation(observation, e.FromPlayerID)
            || !string.Equals(observation.SessionId, sessionId, StringComparison.Ordinal)
        )
            return;

        ElectricityObservationReceived?.Invoke(observation, e.FromPlayerID);
    }

    private void HandleElectricityState(ModMessageReceivedEventArgs e)
    {
        var host = Game1.MasterPlayer;
        var message = e.ReadAs<ElectricityBuffStateMessage>();
        if (
            host is null
            || e.FromPlayerID != host.UniqueMultiplayerID
            || message is null
            || !IsValidSessionId(message.SessionId)
            || !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(
                message.PlayerKey,
                FromPlayerId(Game1.player?.UniqueMultiplayerID ?? 0),
                StringComparison.Ordinal
            )
            || !float.IsFinite(message.Bonus)
            || message.Bonus < 0
            || message.Bonus > 2
        )
            return;

        ElectricityStateReceived?.Invoke(message);
    }

    private void SendSessionRequest()
    {
        var host = Game1.MasterPlayer;
        var localPlayer = Game1.player;
        if (host is null || localPlayer is null)
            return;

        helper.Multiplayer.SendMessage(
            new BuffSessionRequest
            {
                PlayerKey = FromPlayerId(localPlayer.UniqueMultiplayerID),
            },
            SessionRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    private void SendSession(long playerId)
    {
        helper.Multiplayer.SendMessage(
            new BuffSessionMessage { SessionId = sessionId },
            SessionMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    private void SendToHost<T>(T message, string messageType)
    {
        var host = Game1.MasterPlayer;
        if (host is null)
            return;

        helper.Multiplayer.SendMessage(
            message,
            messageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    private static bool IsValidObservation(
        RestoreBuffObservation? observation,
        long senderPlayerId
    )
    {
        if (observation is null)
            return false;
        if (!IsValidSessionId(observation.SessionId))
            return false;
        if (!string.Equals(observation.PlayerKey, FromPlayerId(senderPlayerId), StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(observation.BuffId) || observation.BuffId.Length > 128)
            return false;
        return observation.TotalMilliseconds >= 0
            && observation.RemainingMilliseconds >= 0
            && observation.RemainingMilliseconds <= observation.TotalMilliseconds;
    }

    private static bool IsValidObservation(
        ElectricityBuffObservation? observation,
        long senderPlayerId
    )
    {
        return observation is not null
            && IsValidSessionId(observation.SessionId)
            && string.Equals(observation.PlayerKey, FromPlayerId(senderPlayerId), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(observation.BuffId)
            && observation.BuffId.Length <= 128;
    }

    private static bool IsValidSessionId(string value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "N", out _);

    private static string FromPlayerId(long playerId) =>
        playerId.ToString(CultureInfo.InvariantCulture);

    private static string GetSaveIdentity()
    {
        var master = Game1.MasterPlayer;
        return master is null
            ? string.Empty
            : string.Concat(
                Game1.uniqueIDForThisGame.ToString(CultureInfo.InvariantCulture),
                ":",
                FromPlayerId(master.UniqueMultiplayerID)
            );
    }
}
