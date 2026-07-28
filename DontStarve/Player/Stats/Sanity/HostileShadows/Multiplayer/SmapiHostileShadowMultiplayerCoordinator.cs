#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

internal interface IHostileShadowConversionRequestHandler
{
    HostileShadowSpawnResult HandleConversionRequest(
        ShadowProjectionConversionRequest request,
        long senderPlayerId
    );
}

internal interface IHostileShadowAggroHintHandler
{
    bool HandleAggroHint(
        ShadowAggroHintRequest request,
        long senderPlayerId,
        out string reason
    );
}

internal interface IHostileShadowAttackHitHandler
{
    bool HandleAttackHit(
        ShadowAttackHitRequest request,
        long senderPlayerId,
        out HostileAttackReceipt receipt,
        out string reason
    );
}

internal interface IHostileShadowPhysicalCapabilityHandler
{
    HostileShadowPhysicalEntityCapability GetLocalPhysicalCapability();

    bool HandlePhysicalCapabilityReport(
        ShadowPhysicalCapabilityReport report,
        long senderPlayerId,
        out string reason
    );
}

internal interface IDarkHandInteractionTransportHandler
{
    DarkHandLeaseIssueResult HandleLeaseRequest(
        DarkHandInteractionLeaseRequest request,
        long senderPlayerId
    );

    DarkHandInteractionCommitResult HandleCommitRequest(
        DarkHandInteractionLeaseCommitRequest request,
        long senderPlayerId
    );

    void HandleOwnerContextInvalidated(long playerId);
}

/// <summary>
/// SMAPI transport boundary for the host table. It never accepts client state/config and
/// never turns a client message directly into a mutation without host context revalidation.
/// </summary>
internal sealed class SmapiHostileShadowMultiplayerCoordinator : IDisposable
{
    internal const string FullSnapshotMessageType =
        "HostileShadow.StateSnapshot.v4";
    internal const string DeltaMessageType = "HostileShadow.StateDelta.v4";
    internal const string SnapshotRequestMessageType =
        "HostileShadow.StateSnapshotRequest.v4";
    internal const string ConversionRequestMessageType =
        "HostileShadow.ConversionRequest.v4";
    internal const string AggroHintMessageType =
        "HostileShadow.AggroHint.v1";
    internal const string PhysicalCapabilityMessageType =
        "HostileShadow.PhysicalCapability.v1";
    internal const string AttackHitRequestMessageType =
        "HostileShadow.AttackHitRequest.v1";
    internal const string ConfigFingerprintMessageType =
        "HostileShadow.ConfigFingerprint.v1";
    internal const string DarkHandLeaseRequestMessageType =
        "HostileShadow.DarkHand.LeaseRequest.v1";
    internal const string DarkHandLeaseMessageType =
        "HostileShadow.DarkHand.Lease.v1";
    internal const string DarkHandCommitRequestMessageType =
        "HostileShadow.DarkHand.CommitRequest.v1";
    internal const string DarkHandCommitResultMessageType =
        "HostileShadow.DarkHand.CommitResult.v1";
    internal const uint DeltaFlushCadenceTicks = 15;
    internal const uint CapabilityRefreshCadenceTicks = 60;
    internal const int MaximumQueuedDeltas = 64;
    internal const int MaximumLoggedReasons = 64;
    internal const int MaximumLocalDarkHandOwners = 16;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly ITimeAPI timeApi;
    private readonly Func<string> sessionIdProvider;
    private readonly HostileShadowSessionLifecycleCoordinator sessionLifecycle;
    private readonly Func<HostileShadowConfigFingerprintSnapshot> configFingerprintProvider;
    private readonly IDarkHandLeaseTargetAuthority leaseTargetAuthority;
    private readonly HostileShadowAuthority authority;
    private readonly IHostileShadowConversionRequestHandler conversionHandler;
    private readonly IHostileShadowAggroHintHandler aggroHintHandler;
    private readonly IHostileShadowAttackHitHandler attackHitHandler;
    private readonly IHostileShadowPhysicalCapabilityHandler physicalCapabilityHandler;
    private readonly ShadowStateRevisionStore clientStore = new();
    private readonly Dictionary<string, DarkHandInteractionLeaseInbox> leaseInboxes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DarkHandInteractionCommitResult>
        lastDarkHandCommitResults = new(StringComparer.Ordinal);
    private readonly Queue<ShadowStateDeltaMessage> pendingDeltas = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool fullSnapshotQueued;
    private bool snapshotRequestPending;
    private bool? lastReportedCapabilityAvailable;
    private string lastReportedCapabilityReason = string.Empty;
    private long nextLeaseNonce;
    private IDarkHandInteractionTransportHandler? darkHandInteractionHandler;
    private bool disposed;

    internal SmapiHostileShadowMultiplayerCoordinator(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        ITimeAPI timeApi,
        Func<string> sessionIdProvider,
        HostileShadowSessionLifecycleCoordinator sessionLifecycle,
        Func<HostileShadowConfigFingerprintSnapshot> configFingerprintProvider,
        IDarkHandLeaseTargetAuthority leaseTargetAuthority,
        HostileShadowAuthority authority,
        IHostileShadowConversionRequestHandler conversionHandler,
        IHostileShadowAggroHintHandler aggroHintHandler,
        IHostileShadowAttackHitHandler attackHitHandler,
        IHostileShadowPhysicalCapabilityHandler physicalCapabilityHandler
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.sessionIdProvider = sessionIdProvider
            ?? throw new ArgumentNullException(nameof(sessionIdProvider));
        this.sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        this.configFingerprintProvider = configFingerprintProvider
            ?? throw new ArgumentNullException(nameof(configFingerprintProvider));
        this.leaseTargetAuthority = leaseTargetAuthority
            ?? throw new ArgumentNullException(nameof(leaseTargetAuthority));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.conversionHandler = conversionHandler
            ?? throw new ArgumentNullException(nameof(conversionHandler));
        this.aggroHintHandler = aggroHintHandler
            ?? throw new ArgumentNullException(nameof(aggroHintHandler));
        this.attackHitHandler = attackHitHandler
            ?? throw new ArgumentNullException(nameof(attackHitHandler));
        this.physicalCapabilityHandler = physicalCapabilityHandler
            ?? throw new ArgumentNullException(nameof(physicalCapabilityHandler));

        authority.DeltaProduced += OnDeltaProduced;
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    internal ShadowStateRevisionStore ClientStore => clientStore;

    internal bool BindDarkHandInteractionHandler(
        IDarkHandInteractionTransportHandler handler,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (disposed || darkHandInteractionHandler is not null)
        {
            reason = disposed
                ? "dark-hand.transport-disposed"
                : "dark-hand.transport-handler-already-bound";
            return false;
        }

        darkHandInteractionHandler = handler;
        reason = "dark-hand.transport-handler-bound";
        return true;
    }

    internal ShadowProjectionConversionSubmissionResult SubmitConversionIntent(
        ShadowProjectionConversionIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        var sessionId = sessionIdProvider();
        var localPlayer = Game1.player;
        if (
            localPlayer is null
            || !SanityProtocol.IsValidSessionId(sessionId)
            || !string.Equals(
                intent.PlayerKey,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    localPlayer.UniqueMultiplayerID
                ),
                StringComparison.Ordinal
            )
        )
        {
            return Submission(
                ShadowProjectionConversionSubmissionStatus.Rejected,
                "shadow-conversion.local-player-or-session-invalid"
            );
        }

        var request = new ShadowProjectionConversionRequest
        {
            SessionId = sessionId,
            CorrelationId = intent.CorrelationId,
            PlayerKey = intent.PlayerKey,
            SpeciesId = intent.SpeciesId,
            RequestedAtMinute = intent.RequestedAtMinute,
            DangerRevision = authority.TryGetConversionEpochRevision(
                intent.PlayerKey,
                out var dangerRevision
            )
                ? dangerRevision
                : -1,
        };
        if (
            !HostileShadowProtocol.IsValidConversionRequest(
                request,
                intent.PlayerKey,
                sessionId,
                timeApi.Time,
                request.DangerRevision,
                out var reason
            )
        )
        {
            return Submission(
                ShadowProjectionConversionSubmissionStatus.Rejected,
                reason
            );
        }

        if (Game1.IsMasterGame)
        {
            var result = conversionHandler.HandleConversionRequest(
                request,
                localPlayer.UniqueMultiplayerID
            );
            return MapSubmission(result);
        }

        var host = Game1.MasterPlayer;
        if (host is null)
        {
            return Submission(
                ShadowProjectionConversionSubmissionStatus.Failed,
                "shadow-conversion.host-player-unavailable"
            );
        }

        helper.Multiplayer.SendMessage(
            request,
            ConversionRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        return Submission(
            ShadowProjectionConversionSubmissionStatus.Delayed,
            "shadow-conversion.request-sent-to-host"
        );
    }

    /// <summary>
    /// Stage-05/06 hit resolution may call this seam after it has independently proven a hit. The
    /// client supplies only a clue; the host handler re-resolves sender, location, range, entity,
    /// session, and exact entity revision before changing recent-attacker priority.
    /// </summary>
    internal bool SubmitAggroHint(long entityId, out string reason)
    {
        reason = string.Empty;
        var player = Game1.player;
        if (
            player is null
            || player.currentLocation is null
            || entityId <= 0
        )
        {
            reason = "hostile-shadow.aggro-hint-local-context-invalid";
            return false;
        }

        ShadowStateSnapshot? state;
        var found = Game1.IsMasterGame
            ? authority.TryGetEntity(entityId, out state)
            : clientStore.TryGet(entityId, out state);
        if (!found || state is null)
        {
            reason = "hostile-shadow.aggro-hint-entity-unavailable";
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        var request = new ShadowAggroHintRequest
        {
            SessionId = sessionIdProvider(),
            EntityId = entityId,
            AttackerPlayerKey = playerKey,
            LocationId = player.currentLocation.NameOrUniqueName,
            KnownEntityRevision = state.Revision,
        };
        if (
            !HostileShadowProtocol.IsValidAggroHintRequest(
                request,
                playerKey,
                request.SessionId,
                out reason
            )
        )
        {
            return false;
        }

        if (Game1.IsMasterGame)
        {
            return aggroHintHandler.HandleAggroHint(
                request,
                player.UniqueMultiplayerID,
                out reason
            );
        }

        var host = Game1.MasterPlayer;
        if (host is null)
        {
            reason = "hostile-shadow.aggro-hint-host-unavailable";
            return false;
        }
        helper.Multiplayer.SendMessage(
            request,
            AggroHintMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        reason = "hostile-shadow.aggro-hint-sent-to-host";
        return true;
    }

    internal bool SubmitAttackHit(
        long entityId,
        string nonce,
        out string reason
    )
    {
        reason = string.Empty;
        var player = Game1.player;
        if (
            player is null
            || player.currentLocation is null
            || entityId <= 0
        )
        {
            reason = "hostile-shadow.attack-hit-local-context-invalid";
            return false;
        }

        ShadowStateSnapshot? state;
        var found = Game1.IsMasterGame
            ? authority.TryGetEntity(entityId, out state)
            : clientStore.TryGet(entityId, out state);
        if (
            !found
            || state is null
            || !string.Equals(
                state.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.attack-hit-state-unavailable";
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        var request = new ShadowAttackHitRequest
        {
            SessionId = sessionIdProvider(),
            Nonce = nonce,
            EntityId = entityId,
            TargetPlayerKey = playerKey,
            LocationId = player.currentLocation.NameOrUniqueName,
            AttackInstanceId = state.AttackInstanceId,
            ObservedEntityRevision = state.Revision,
            ObservedAttackInstanceRevision = state.AttackInstanceRevision,
            ObservedFrameNumber = state.AttackFrameNumber,
        };
        if (
            !HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                playerKey,
                request.SessionId,
                out reason
            )
        )
        {
            return false;
        }

        if (Game1.IsMasterGame)
        {
            return attackHitHandler.HandleAttackHit(
                request,
                player.UniqueMultiplayerID,
                out _,
                out reason
            );
        }
        var host = Game1.MasterPlayer;
        if (host is null)
        {
            reason = "hostile-shadow.attack-hit-host-unavailable";
            return false;
        }
        helper.Multiplayer.SendMessage(
            request,
            AttackHitRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        reason = "hostile-shadow.attack-hit-request-sent-to-host";
        return true;
    }

    /// <summary>
    /// DarkHand target adapters request an owner-private lease through this existing hostile-shadow
    /// transport. The bound host handler still owns all target revalidation and world mutation.
    /// </summary>
    internal bool RequestDarkHandInteractionLease(
        string targetId,
        string operationId,
        long observedTargetRevision,
        out string reason
    )
    {
        var player = Game1.player;
        var sessionId = sessionIdProvider();
        if (
            player?.currentLocation is null
            || !SanityProtocol.IsValidSessionId(sessionId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(targetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(operationId)
            || observedTargetRevision <= 0
            || nextLeaseNonce == long.MaxValue
        )
        {
            reason = "dark-hand.lease-local-context-invalid";
            return false;
        }

        var ownerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        var request = new DarkHandInteractionLeaseRequest
        {
            SessionId = sessionId,
            Nonce = ++nextLeaseNonce,
            OwnerPlayerKey = ownerPlayerKey,
            LocationId = player.currentLocation.NameOrUniqueName,
            TargetId = targetId,
            OperationId = operationId,
            ObservedTargetRevision = observedTargetRevision,
        };
        if (
            !DarkHandInteractionLeaseProtocol.IsValidRequest(
                request,
                ownerPlayerKey,
                sessionId,
                out reason
            )
        )
        {
            return false;
        }

        if (Game1.IsMasterGame)
        {
            var result = darkHandInteractionHandler is null
                ? sessionLifecycle.LeaseAuthority.TryIssue(
                    request,
                    ownerPlayerKey,
                    Game1.ticks,
                    leaseTargetAuthority
                )
                : darkHandInteractionHandler.HandleLeaseRequest(
                    request,
                    player.UniqueMultiplayerID
                );
            if (result.Issued)
            {
                if (
                    TryGetLeaseInbox(ownerPlayerKey, out var inbox, out _)
                )
                {
                    inbox.Apply(
                        result.Lease,
                        Game1.ticks,
                        player.currentLocation.NameOrUniqueName
                    );
                }
            }
            reason = result.Reason;
            return result.Issued;
        }
        var host = Game1.MasterPlayer;
        if (host is null)
        {
            reason = "dark-hand.lease-host-unavailable";
            return false;
        }
        helper.Multiplayer.SendMessage(
            request,
            DarkHandLeaseRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        reason = "dark-hand.lease-request-sent-to-host";
        return true;
    }

    internal bool CommitDarkHandInteraction(
        string targetId,
        string operationId,
        out string reason
    )
    {
        reason = "dark-hand.commit-local-context-invalid";
        var player = Game1.player;
        var ownerPlayerKey = player is null
            ? string.Empty
            : SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
        if (
            player?.currentLocation is null
            || !TryGetLeaseInbox(ownerPlayerKey, out var inbox, out reason)
            || !inbox.TryTake(
                targetId,
                operationId,
                Game1.ticks,
                player.currentLocation.NameOrUniqueName,
                out var lease,
                out reason
            )
            || lease is null
        )
        {
            return false;
        }

        var request = new DarkHandInteractionLeaseCommitRequest
        {
            LeaseId = lease.LeaseId,
            Nonce = lease.Nonce,
        };
        if (Game1.IsMasterGame)
        {
            if (darkHandInteractionHandler is null)
            {
                reason = "dark-hand.commit-handler-unavailable";
                return false;
            }
            var result = darkHandInteractionHandler.HandleCommitRequest(
                request,
                player.UniqueMultiplayerID
            );
            ApplyDarkHandCommitResult(result, out reason);
            return string.Equals(
                result.Disposition,
                "Applied",
                StringComparison.Ordinal
            ) || string.Equals(result.Disposition, "Duplicate", StringComparison.Ordinal);
        }

        var host = Game1.MasterPlayer;
        if (host is null)
        {
            reason = "dark-hand.commit-host-unavailable";
            return false;
        }
        helper.Multiplayer.SendMessage(
            request,
            DarkHandCommitRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        reason = "dark-hand.commit-request-sent-to-host";
        return true;
    }

    internal bool TryTakeDarkHandCommitResult(
        string targetId,
        string operationId,
        out DarkHandInteractionCommitResult? result
    )
    {
        if (
            !TryGetLocalOwnerKey(out var ownerPlayerKey)
            || !lastDarkHandCommitResults.TryGetValue(
                ownerPlayerKey,
                out var current
            )
            || !string.Equals(current.TargetId, targetId, StringComparison.Ordinal)
            || !string.Equals(
                current.OperationId,
                operationId,
                StringComparison.Ordinal
            )
        )
        {
            result = null;
            return false;
        }

        lastDarkHandCommitResults.Remove(ownerPlayerKey);
        result = current;
        return true;
    }

    internal void OnSessionStarted()
    {
        pendingDeltas.Clear();
        fullSnapshotQueued = false;
        snapshotRequestPending = false;
        loggedReasons.Clear();
        nextLeaseNonce = 0;
        ClearDarkHandLocalWindows();
        if (Game1.IsMasterGame)
            ResyncConnectedPeers(ShadowSnapshotTrigger.Join);
        else
        {
            RequestFullSnapshot(ShadowSnapshotTrigger.Join, replacePending: true);
            SendPhysicalCapabilityReport(force: true);
            SendConfigFingerprintReport();
        }
    }

    internal void OnLocalWarp()
    {
        if (disposed)
            return;
        ClearDarkHandLocalWindows();
        nextLeaseNonce = 0;
        if (!Game1.IsMasterGame)
            RequestFullSnapshot(ShadowSnapshotTrigger.Warp, replacePending: true);
    }

    internal void OnDayEnding()
    {
        pendingDeltas.Clear();
        fullSnapshotQueued = false;
        snapshotRequestPending = false;
        clientStore.Reset();
        ClearDarkHandLocalWindows();
        nextLeaseNonce = 0;
    }

    internal void OnDayStarted()
    {
        if (disposed)
            return;
        if (Game1.IsMasterGame)
            ResyncConnectedPeers(ShadowSnapshotTrigger.Resync);
        else
        {
            RequestFullSnapshot(ShadowSnapshotTrigger.Resync, replacePending: true);
            SendConfigFingerprintReport();
        }
    }

    internal void OnSystemEnabledChanged(bool enabled)
    {
        if (!enabled)
        {
            if (Game1.IsMasterGame)
            {
                // Disabled is a lifecycle resync, not config transport. Sending an empty scoped
                // host truth also retires client-private lease inboxes when configs differ.
                pendingDeltas.Clear();
                fullSnapshotQueued = false;
                ResyncSubscribedPeers();
            }
            OnDayEnding();
            return;
        }
        OnDayStarted();
    }

    internal void ClearSession()
    {
        pendingDeltas.Clear();
        fullSnapshotQueued = false;
        snapshotRequestPending = false;
        lastReportedCapabilityAvailable = null;
        lastReportedCapabilityReason = string.Empty;
        nextLeaseNonce = 0;
        clientStore.Reset();
        ClearDarkHandLocalWindows();
        loggedReasons.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        authority.DeltaProduced -= OnDeltaProduced;
        helper.Events.Multiplayer.PeerConnected -= OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        ClearSession();
        darkHandInteractionHandler = null;
    }

    private void OnDeltaProduced(ShadowStateDeltaMessage delta)
    {
        if (disposed || !Game1.IsMasterGame)
            return;
        if (fullSnapshotQueued)
            return;
        if (pendingDeltas.Count >= MaximumQueuedDeltas)
        {
            pendingDeltas.Clear();
            fullSnapshotQueued = true;
            LogOnce("hostile-shadow.delta-queue-fell-back-to-full", LogLevel.Trace);
            return;
        }
        pendingDeltas.Enqueue(delta.Clone());
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            disposed
            || !Context.IsWorldReady
        )
        {
            return;
        }
        if (!Game1.IsMasterGame)
        {
            if (e.IsMultipleOf(CapabilityRefreshCadenceTicks))
                SendPhysicalCapabilityReport(force: false);
            return;
        }
        if (!e.IsMultipleOf(DeltaFlushCadenceTicks))
            return;
        if (fullSnapshotQueued)
        {
            pendingDeltas.Clear();
            fullSnapshotQueued = false;
            ResyncSubscribedPeers();
            return;
        }
        FlushPendingDeltas();
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (Game1.IsMasterGame)
            SubscribeAndSendSnapshot(e.Peer.PlayerID, ShadowSnapshotTrigger.Join);
        else if (e.Peer.IsHost)
        {
            RequestFullSnapshot(ShadowSnapshotTrigger.Join, replacePending: true);
            SendPhysicalCapabilityReport(force: true);
            SendConfigFingerprintReport();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (Game1.IsMasterGame)
        {
            var ownerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                e.Peer.PlayerID
            );
            sessionLifecycle.Disconnect(e.Peer.PlayerID, ownerPlayerKey);
            darkHandInteractionHandler?.HandleOwnerContextInvalidated(
                e.Peer.PlayerID
            );
            authority.ForgetOwner(
                ownerPlayerKey,
                HostileShadowCleanupReasonIds.OwnerDisconnected
            );
            return;
        }
        if (e.Peer.IsHost)
        {
            clientStore.Reset();
            ClearDarkHandLocalWindows();
            sessionLifecycle.ClearSession();
            snapshotRequestPending = false;
            LogOnce("hostile-shadow.host-disconnected-client-store-reset", LogLevel.Warn);
        }
    }

    private void OnModMessageReceived(
        object? sender,
        ModMessageReceivedEventArgs e
    )
    {
        if (disposed || !string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;
        try
        {
            switch (e.Type)
            {
                case SnapshotRequestMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleSnapshotRequest);
                    break;
                case ConversionRequestMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleConversionRequest);
                    break;
                case AggroHintMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleAggroHint);
                    break;
                case AttackHitRequestMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleAttackHit);
                    break;
                case PhysicalCapabilityMessageType:
                    DispatchByDirection(
                        e,
                        hostReceives: true,
                        HandlePhysicalCapabilityReport
                    );
                    break;
                case DarkHandLeaseRequestMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleDarkHandLeaseRequest);
                    break;
                case DarkHandCommitRequestMessageType:
                    DispatchByDirection(e, hostReceives: true, HandleDarkHandCommitRequest);
                    break;
                case FullSnapshotMessageType:
                    DispatchByDirection(e, hostReceives: false, HandleFullSnapshot);
                    break;
                case DeltaMessageType:
                    DispatchByDirection(e, hostReceives: false, HandleDelta);
                    break;
                case DarkHandLeaseMessageType:
                    DispatchByDirection(e, hostReceives: false, HandleDarkHandLease);
                    break;
                case DarkHandCommitResultMessageType:
                    DispatchByDirection(e, hostReceives: false, HandleDarkHandCommitResult);
                    break;
                case ConfigFingerprintMessageType:
                    HandleConfigFingerprintReport(e);
                    break;
                default:
                    if (e.Type.StartsWith("HostileShadow.", StringComparison.Ordinal))
                    {
                        LogMessage(
                            e,
                            "hostile-shadow.protocol-type-unknown",
                            LogLevel.Warn
                        );
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            LogMessage(
                e,
                string.Concat(
                    "hostile-shadow.message-decode-failed:",
                    exception.GetType().Name
                ),
                LogLevel.Warn
            );
        }
    }

    private void DispatchByDirection(
        ModMessageReceivedEventArgs e,
        bool hostReceives,
        Action<ModMessageReceivedEventArgs> handler
    )
    {
        if (Game1.IsMasterGame != hostReceives)
        {
            LogMessage(e, "hostile-shadow.protocol-direction-invalid", LogLevel.Warn);
            return;
        }
        handler(e);
    }

    private void HandleSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<ShadowStateSnapshotRequest>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        var player = Game1.GetPlayer(e.FromPlayerID, onlyOnline: true);
        var expectedLocationId = player?.currentLocation?.NameOrUniqueName
            ?? string.Empty;
        if (
            !HostileShadowProtocol.IsValidSnapshotRequest(
                request,
                expectedPlayerKey,
                expectedLocationId,
                authority.SessionId,
                out var reason
            )
            || !sessionLifecycle.TrySubscribe(
                e.FromPlayerID,
                expectedPlayerKey,
                expectedLocationId,
                request!.Trigger,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Trace);
            return;
        }
        if (request.Trigger == ShadowSnapshotTrigger.Warp)
            darkHandInteractionHandler?.HandleOwnerContextInvalidated(
                e.FromPlayerID
            );
        SendFullSnapshot(
            e.FromPlayerID,
            expectedLocationId,
            request.Trigger
        );
        SendConfigFingerprintReport(e.FromPlayerID);
    }

    private void HandleConversionRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<ShadowProjectionConversionRequest>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        var hasDangerEpoch = authority.TryGetConversionEpochRevision(
            expectedPlayerKey,
            out var expectedDangerRevision
        );
        var reason = hasDangerEpoch
            ? string.Empty
            : "hostile-shadow.conversion-danger-epoch-unavailable";
        if (
            !hasDangerEpoch
            ||
            !HostileShadowProtocol.IsValidConversionRequest(
                request,
                expectedPlayerKey,
                authority.SessionId,
                timeApi.Time,
                expectedDangerRevision,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Trace);
            return;
        }

        var result = conversionHandler.HandleConversionRequest(
            request!,
            e.FromPlayerID
        );
        if (!result.Spawned && result.Status != HostileShadowSpawnStatus.Duplicate)
            LogMessage(e, result.Reason, LogLevel.Trace);
    }

    private void HandleAggroHint(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<ShadowAggroHintRequest>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        if (
            !HostileShadowProtocol.IsValidAggroHintRequest(
                request,
                expectedPlayerKey,
                authority.SessionId,
                out var reason
            )
            || !aggroHintHandler.HandleAggroHint(
                request,
                e.FromPlayerID,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Trace);
        }
    }

    private void HandlePhysicalCapabilityReport(ModMessageReceivedEventArgs e)
    {
        var report = e.ReadAs<ShadowPhysicalCapabilityReport>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        if (
            !HostileShadowProtocol.IsValidPhysicalCapabilityReport(
                report,
                expectedPlayerKey,
                authority.SessionId,
                out var reason
            )
            || !physicalCapabilityHandler.HandlePhysicalCapabilityReport(
                report,
                e.FromPlayerID,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Trace);
        }
    }

    private void HandleAttackHit(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<ShadowAttackHitRequest>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        if (
            !HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                expectedPlayerKey,
                authority.SessionId,
                out var reason
            )
            || !attackHitHandler.HandleAttackHit(
                request,
                e.FromPlayerID,
                out _,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Trace);
        }
    }

    private void HandleDarkHandLeaseRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<DarkHandInteractionLeaseRequest>();
        var expectedOwner = SanityPlayerKey.FromUniqueMultiplayerId(e.FromPlayerID);
        var player = Game1.GetPlayer(e.FromPlayerID, onlyOnline: true);
        if (
            player?.currentLocation is null
            || request is null
            || !string.Equals(
                request.LocationId,
                player.currentLocation.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            LogMessage(e, "dark-hand.lease-sender-location-invalid", LogLevel.Trace);
            return;
        }

        var result = darkHandInteractionHandler is null
            ? sessionLifecycle.LeaseAuthority.TryIssue(
                request,
                expectedOwner,
                Game1.ticks,
                leaseTargetAuthority
            )
            : darkHandInteractionHandler.HandleLeaseRequest(
                request,
                e.FromPlayerID
            );
        if (!result.Issued)
        {
            LogMessage(e, result.Reason, LogLevel.Trace);
            return;
        }
        helper.Multiplayer.SendMessage(
            result.Lease!,
            DarkHandLeaseMessageType,
            new[] { modId },
            new[] { e.FromPlayerID }
        );
    }

    private void HandleDarkHandLease(ModMessageReceivedEventArgs e)
    {
        if (!IsHostSender(e.FromPlayerID))
        {
            LogMessage(e, "dark-hand.lease-sender-not-host", LogLevel.Trace);
            return;
        }
        var lease = e.ReadAs<DarkHandInteractionLease>();
        var inboxReason = "dark-hand.lease-local-owner-unavailable";
        if (
            lease is null
            || !TryGetLeaseInbox(lease.OwnerPlayerKey, out var inbox, out inboxReason)
            || !TryResolveOwnerLocation(
                lease.OwnerPlayerKey,
                out var ownerLocationId
            )
        )
        {
            LogMessage(
                e,
                string.IsNullOrWhiteSpace(inboxReason)
                    ? "dark-hand.lease-local-owner-unavailable"
                    : inboxReason,
                LogLevel.Warn
            );
            return;
        }
        var result = inbox.Apply(
            lease,
            Game1.ticks,
            ownerLocationId
        );
        if (result.Status == DarkHandLeaseInboxStatus.Rejected)
            LogMessage(e, result.Reason, LogLevel.Warn);
    }

    private void HandleDarkHandCommitRequest(ModMessageReceivedEventArgs e)
    {
        if (darkHandInteractionHandler is null)
        {
            LogMessage(e, "dark-hand.commit-handler-unavailable", LogLevel.Trace);
            return;
        }
        var result = darkHandInteractionHandler.HandleCommitRequest(
            e.ReadAs<DarkHandInteractionLeaseCommitRequest>(),
            e.FromPlayerID
        );
        helper.Multiplayer.SendMessage(
            result,
            DarkHandCommitResultMessageType,
            new[] { modId },
            new[] { e.FromPlayerID }
        );
    }

    private void HandleDarkHandCommitResult(ModMessageReceivedEventArgs e)
    {
        if (!IsHostSender(e.FromPlayerID))
        {
            LogMessage(e, "dark-hand.commit-result-sender-not-host", LogLevel.Trace);
            return;
        }
        if (
            !ApplyDarkHandCommitResult(
                e.ReadAs<DarkHandInteractionCommitResult>(),
                out var reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Warn);
        }
    }

    private bool ApplyDarkHandCommitResult(
        DarkHandInteractionCommitResult? result,
        out string reason
    )
    {
        var ownerPlayerKey = result?.OwnerPlayerKey ?? string.Empty;
        reason = "dark-hand.commit-result-owner-unavailable";
        if (
            !TryResolveOwnerLocation(ownerPlayerKey, out _)
            ||
            !DarkHandInteractionLeaseProtocol.IsValidCommitResult(
                result,
                sessionIdProvider(),
                ownerPlayerKey,
                out reason
            )
        )
        {
            return false;
        }
        if (
            lastDarkHandCommitResults.TryGetValue(
                result!.OwnerPlayerKey,
                out var current
            )
            && result.Nonce < current.Nonce
        )
        {
            reason = "dark-hand.commit-result-stale";
            return false;
        }

        if (
            lastDarkHandCommitResults.Count >= MaximumLocalDarkHandOwners
            && !lastDarkHandCommitResults.ContainsKey(result!.OwnerPlayerKey)
        )
        {
            reason = "dark-hand.commit-result-owner-cap-reached";
            return false;
        }
        lastDarkHandCommitResults[result!.OwnerPlayerKey] = result;
        reason = "dark-hand.commit-result-applied";
        return true;
    }

    private void HandleConfigFingerprintReport(ModMessageReceivedEventArgs e)
    {
        if (!Game1.IsMasterGame && !IsHostSender(e.FromPlayerID))
        {
            LogMessage(
                e,
                "hostile-shadow.config-fingerprint-sender-not-host",
                LogLevel.Trace
            );
            return;
        }

        var report = e.ReadAs<HostileShadowConfigFingerprintReport>();
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.FromPlayerID);
        var expectedSessionId = Game1.IsMasterGame
            ? authority.SessionId
            : sessionIdProvider();
        if (
            !HostileShadowConfigFingerprintProtocol.IsValidReport(
                report,
                expectedPlayerKey,
                expectedSessionId,
                out var reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Warn);
            return;
        }
        if (
            Game1.IsMasterGame
            && !sessionLifecycle.RecordFingerprint(
                e.FromPlayerID,
                expectedPlayerKey,
                report!,
                out reason
            )
        )
        {
            LogMessage(e, reason, LogLevel.Warn);
            return;
        }

        var comparison = HostileShadowConfigFingerprintProtocol.Compare(
            configFingerprintProvider(),
            report!
        );
        reason = comparison switch
        {
            HostileShadowFingerprintComparison.Match =>
                "hostile-shadow.config-fingerprint-match",
            HostileShadowFingerprintComparison.Mismatch =>
                "hostile-shadow.config-fingerprint-mismatch",
            _ => "hostile-shadow.config-fingerprint-unavailable",
        };
        LogMessage(
            e,
            reason,
            comparison == HostileShadowFingerprintComparison.Mismatch
                ? LogLevel.Warn
                : LogLevel.Trace
        );
    }

    private void HandleFullSnapshot(ModMessageReceivedEventArgs e)
    {
        if (!IsHostSender(e.FromPlayerID))
        {
            LogMessage(e, "hostile-shadow.snapshot-sender-not-host", LogLevel.Trace);
            return;
        }

        var result = clientStore.ApplyFull(
            e.ReadAs<ShadowStateSnapshotMessage>()
        );
        if (
            result.Status is ShadowRevisionApplyStatus.Applied
                or ShadowRevisionApplyStatus.IgnoredDuplicate
        )
        {
            snapshotRequestPending = false;
            ClearDarkHandLocalWindows();
        }
        else if (result.RequiresFullSnapshot)
        {
            RequestFullSnapshot(ShadowSnapshotTrigger.Resync, replacePending: true);
        }
        else if (result.Status == ShadowRevisionApplyStatus.Rejected)
        {
            LogMessage(e, result.Reason, LogLevel.Warn);
        }
    }

    private void HandleDelta(ModMessageReceivedEventArgs e)
    {
        if (!IsHostSender(e.FromPlayerID))
        {
            LogMessage(e, "hostile-shadow.delta-sender-not-host", LogLevel.Trace);
            return;
        }

        var result = clientStore.ApplyDelta(
            e.ReadAs<ShadowStateDeltaMessage>()
        );
        if (result.RequiresFullSnapshot)
            RequestFullSnapshot(ShadowSnapshotTrigger.Resync, replacePending: false);
        else if (result.Status == ShadowRevisionApplyStatus.Rejected)
            LogMessage(e, result.Reason, LogLevel.Warn);
    }

    private bool IsHostSender(long playerId)
    {
        return Game1.MasterPlayer is { } host
            && host.UniqueMultiplayerID == playerId;
    }

    private bool TryGetLeaseInbox(
        string ownerPlayerKey,
        out DarkHandInteractionLeaseInbox inbox,
        out string reason
    )
    {
        inbox = null!;
        var sessionId = sessionIdProvider();
        if (
            !SanityPlayerKey.IsCanonical(ownerPlayerKey)
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            reason = "dark-hand.lease-inbox-owner-or-session-invalid";
            return false;
        }
        if (leaseInboxes.TryGetValue(ownerPlayerKey, out var existingInbox))
        {
            inbox = existingInbox;
            if (
                string.Equals(inbox.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(
                    inbox.OwnerPlayerKey,
                    ownerPlayerKey,
                    StringComparison.Ordinal
                )
            )
            {
                reason = "dark-hand.lease-inbox-owner-current";
                return true;
            }
            inbox.Clear();
            return inbox.BeginSession(sessionId, ownerPlayerKey, out reason);
        }
        if (leaseInboxes.Count >= MaximumLocalDarkHandOwners)
        {
            reason = "dark-hand.lease-inbox-owner-cap-reached";
            return false;
        }

        inbox = new DarkHandInteractionLeaseInbox();
        if (!inbox.BeginSession(sessionId, ownerPlayerKey, out reason))
            return false;
        leaseInboxes.Add(ownerPlayerKey, inbox);
        return true;
    }

    private static bool TryGetLocalOwnerKey(out string ownerPlayerKey)
    {
        var player = Game1.player;
        ownerPlayerKey = player is null
            ? string.Empty
            : SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
        return SanityPlayerKey.IsCanonical(ownerPlayerKey);
    }

    private static bool TryResolveOwnerLocation(
        string ownerPlayerKey,
        out string locationId
    )
    {
        locationId = string.Empty;
        if (
            !long.TryParse(
                ownerPlayerKey,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ownerPlayerId
            )
        )
        {
            return false;
        }
        var owner = Game1.GetPlayer(ownerPlayerId, onlyOnline: true);
        locationId = owner?.currentLocation?.NameOrUniqueName ?? string.Empty;
        return DarkHandInteractionLeaseProtocol.IsIdentifier(locationId);
    }

    private void ClearDarkHandLocalWindows()
    {
        foreach (var inbox in leaseInboxes.Values)
            inbox.Clear();
        leaseInboxes.Clear();
        lastDarkHandCommitResults.Clear();
    }

    private void RequestFullSnapshot(
        ShadowSnapshotTrigger trigger,
        bool replacePending
    )
    {
        if (snapshotRequestPending && !replacePending)
            return;
        var localPlayer = Game1.player;
        var host = Game1.MasterPlayer;
        var sessionId = sessionIdProvider();
        var locationId = localPlayer?.currentLocation?.NameOrUniqueName
            ?? string.Empty;
        var reason = string.Empty;
        if (
            localPlayer is null
            || host is null
            || !SanityProtocol.IsValidSessionId(sessionId)
            || !clientStore.BeginSubscription(locationId, trigger, out reason)
        )
        {
            LogOnce(
                localPlayer is null || host is null
                    ? "hostile-shadow.snapshot-request-player-or-host-unavailable"
                    : string.IsNullOrWhiteSpace(reason)
                        ? "hostile-shadow.snapshot-request-context-invalid"
                        : reason,
                LogLevel.Trace
            );
            return;
        }

        snapshotRequestPending = true;
        helper.Multiplayer.SendMessage(
            new ShadowStateSnapshotRequest
            {
                SessionId = sessionId,
                PlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                    localPlayer.UniqueMultiplayerID
                ),
                LocationId = locationId,
                Trigger = trigger,
                KnownRevision = clientStore.Revision,
            },
            SnapshotRequestMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
    }

    private void SendPhysicalCapabilityReport(bool force)
    {
        var localPlayer = Game1.player;
        var host = Game1.MasterPlayer;
        var sessionId = sessionIdProvider();
        if (
            localPlayer is null
            || host is null
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            LogOnce(
                "hostile-shadow.physical-capability-local-context-invalid",
                LogLevel.Trace
            );
            return;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            localPlayer.UniqueMultiplayerID
        );
        var capability =
            physicalCapabilityHandler.GetLocalPhysicalCapability();
        if (
            !force
            && lastReportedCapabilityAvailable == capability.IsAvailable
            && string.Equals(
                lastReportedCapabilityReason,
                capability.Reason,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }
        var report = new ShadowPhysicalCapabilityReport
        {
            SessionId = sessionId,
            PlayerKey = playerKey,
            Available = capability.IsAvailable,
            Reason = capability.Reason,
        };
        if (
            !HostileShadowProtocol.IsValidPhysicalCapabilityReport(
                report,
                playerKey,
                sessionId,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        helper.Multiplayer.SendMessage(
            report,
            PhysicalCapabilityMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        lastReportedCapabilityAvailable = capability.IsAvailable;
        lastReportedCapabilityReason = capability.Reason;
    }

    private void SendConfigFingerprintReport(long? targetPlayerId = null)
    {
        var localPlayer = Game1.player;
        var host = Game1.MasterPlayer;
        var sessionId = sessionIdProvider();
        if (localPlayer is null || !SanityProtocol.IsValidSessionId(sessionId))
            return;
        if (Game1.IsMasterGame && !targetPlayerId.HasValue)
            return;
        if (!Game1.IsMasterGame && host is null)
            return;

        var fingerprint = configFingerprintProvider();
        var report = new HostileShadowConfigFingerprintReport
        {
            SessionId = sessionId,
            PlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                localPlayer.UniqueMultiplayerID
            ),
            ConfigSchemaVersion = fingerprint.ConfigSchemaVersion,
            Hash = fingerprint.Hash ?? string.Empty,
        };
        if (
            !HostileShadowConfigFingerprintProtocol.IsValidReport(
                report,
                report.PlayerKey,
                sessionId,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        helper.Multiplayer.SendMessage(
            report,
            ConfigFingerprintMessageType,
            new[] { modId },
            new[]
            {
                Game1.IsMasterGame
                    ? targetPlayerId!.Value
                    : host!.UniqueMultiplayerID,
            }
        );
    }

    private void FlushPendingDeltas()
    {
        var subscribers = sessionLifecycle.GetSubscriberPlayerIds();
        if (subscribers.Count == 0)
        {
            pendingDeltas.Clear();
            return;
        }
        var recipients = new long[subscribers.Count];
        for (var index = 0; index < subscribers.Count; index++)
            recipients[index] = subscribers[index];
        while (pendingDeltas.Count > 0)
        {
            helper.Multiplayer.SendMessage(
                pendingDeltas.Dequeue(),
                DeltaMessageType,
                new[] { modId },
                recipients
            );
        }
    }

    private void ResyncConnectedPeers(ShadowSnapshotTrigger trigger)
    {
        if (!Game1.IsMasterGame || !authority.IsHostSessionActive)
            return;
        var localPlayerId = Game1.player?.UniqueMultiplayerID ?? long.MinValue;
        foreach (var player in Game1.getOnlineFarmers())
        {
            if (player.UniqueMultiplayerID == localPlayerId)
                continue;
            SubscribeAndSendSnapshot(player.UniqueMultiplayerID, trigger);
        }
    }

    private void ResyncSubscribedPeers()
    {
        var subscribers = sessionLifecycle.GetSubscriberPlayerIds();
        foreach (var playerId in subscribers)
            SubscribeAndSendSnapshot(playerId, ShadowSnapshotTrigger.Resync);
    }

    private void SubscribeAndSendSnapshot(
        long playerId,
        ShadowSnapshotTrigger trigger
    )
    {
        var player = Game1.GetPlayer(playerId, onlyOnline: true);
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(playerId);
        var locationId = player?.currentLocation?.NameOrUniqueName ?? string.Empty;
        var reason = string.Empty;
        if (
            player is null
            || !sessionLifecycle.TrySubscribe(
                playerId,
                playerKey,
                locationId,
                trigger,
                out reason
            )
        )
        {
            LogOnce(
                player is null
                    ? "hostile-shadow.subscription-player-unavailable"
                    : reason,
                LogLevel.Trace
            );
            return;
        }
        if (trigger == ShadowSnapshotTrigger.Warp)
            darkHandInteractionHandler?.HandleOwnerContextInvalidated(playerId);
        SendFullSnapshot(playerId, locationId, trigger);
        SendConfigFingerprintReport(playerId);
    }

    private void SendFullSnapshot(
        long playerId,
        string locationId,
        ShadowSnapshotTrigger trigger
    )
    {
        if (!authority.IsHostSessionActive)
            return;
        if (
            !HostileShadowProtocol.TryCreateScopedSnapshot(
                authority.CreateFullSnapshot(),
                locationId,
                trigger,
                out var snapshot,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        helper.Multiplayer.SendMessage(
            snapshot,
            FullSnapshotMessageType,
            new[] { modId },
            new[] { playerId }
        );
    }

    private static ShadowProjectionConversionSubmissionResult MapSubmission(
        HostileShadowSpawnResult result
    )
    {
        if (
            result.Spawned
            || (
                result.Status == HostileShadowSpawnStatus.Duplicate
                && result.EntityId.HasValue
            )
        )
        {
            return Submission(
                ShadowProjectionConversionSubmissionStatus.Confirmed,
                "shadow-conversion.host-confirmed"
            );
        }
        return result.Status switch
        {
            HostileShadowSpawnStatus.Waiting => Submission(
                ShadowProjectionConversionSubmissionStatus.Delayed,
                result.Reason
            ),
            HostileShadowSpawnStatus.Unavailable => Submission(
                ShadowProjectionConversionSubmissionStatus.Failed,
                result.Reason
            ),
            _ => Submission(
                ShadowProjectionConversionSubmissionStatus.Rejected,
                result.Reason
            ),
        };
    }

    private static ShadowProjectionConversionSubmissionResult Submission(
        ShadowProjectionConversionSubmissionStatus status,
        string reason
    )
    {
        return new ShadowProjectionConversionSubmissionResult(status, reason);
    }

    private void LogOnce(string reason, LogLevel level)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || loggedReasons.Count >= MaximumLoggedReasons
            || !loggedReasons.Add(reason)
        )
        {
            return;
        }
        monitor.Log($"Hostile shadow multiplayer: {reason}", level);
    }

    private void LogMessage(
        ModMessageReceivedEventArgs message,
        string reason,
        LogLevel level
    )
    {
        var structured = string.Concat(
            "protocol=",
            HostileShadowProtocol.CurrentProtocolVersion,
            " type=",
            message.Type,
            " sender=",
            message.FromPlayerID,
            " reason=",
            string.IsNullOrWhiteSpace(reason)
                ? "hostile-shadow.protocol-reason-missing"
                : reason
        );
        LogOnce(structured, level);
    }
}
