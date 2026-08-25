#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Transports cooperative owner-local light evidence to the host and presentation back to that
/// owner. It never evaluates a remote framebuffer and exposes no damage or settlement operation.
/// </summary>
internal sealed class SmapiEnvironmentLightMultiplayerCoordinator
    : IEnvironmentLightRemoteEvidenceSource,
        IEnvironmentLightRemotePresentationSink,
        IDisposable
{
    private const int MaximumLoggedReasons = 32;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly string modVersion;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly EnvironmentLightService lightService;
    private readonly EnvironmentLightLocationRuleCatalog locationRules;
    private readonly DarknessAttackLocationAuthorizationPolicy
        darknessAttackLocationAuthorization;
    private readonly SanitySmapiAudioService audio;
    private readonly Func<EnvironmentLightConfigIdentity> configIdentityProvider;
    private readonly Dictionary<DarknessAttackOwnerKey, EnvironmentLightAcceptedRemoteEvidence>
        acceptedByOwner = new();
    private readonly Dictionary<string, long> lastAcceptedSequenceByPlayer =
        new(StringComparer.Ordinal);
    private readonly Dictionary<OwnerScreenKey, ClientReportTracker> clientReports =
        new();
    private readonly Dictionary<string, long> clientSequenceByPlayer =
        new(StringComparer.Ordinal);
    private readonly Dictionary<DarknessAttackOwnerKey, long> presentationSequences =
        new();
    private readonly Dictionary<DarknessAttackOwnerKey, long>
        lastReceivedPresentationSequences = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool disposed;

    internal SmapiEnvironmentLightMultiplayerCoordinator(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        string modVersion,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        EnvironmentLightService lightService,
        EnvironmentLightLocationRuleCatalog locationRules,
        DarknessAttackLocationAuthorizationPolicy darknessAttackLocationAuthorization,
        SanitySmapiAudioService audio,
        Func<EnvironmentLightConfigIdentity> configIdentityProvider
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
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.lightService = lightService
            ?? throw new ArgumentNullException(nameof(lightService));
        this.locationRules = locationRules
            ?? throw new ArgumentNullException(nameof(locationRules));
        this.darknessAttackLocationAuthorization = darknessAttackLocationAuthorization
            ?? throw new ArgumentNullException(nameof(darknessAttackLocationAuthorization));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.configIdentityProvider = configIdentityProvider
            ?? throw new ArgumentNullException(nameof(configIdentityProvider));

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
    }

    public event Action<DarknessAttackOwnerKey, string>? EvidenceInvalidated;

    public void VisitFresh(Action<EnvironmentLightAcceptedRemoteEvidence> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (
            disposed
            || !Context.IsMainPlayer
            || !Context.IsWorldReady
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
        )
        {
            return;
        }

        var now = MonotonicMilliseconds();
        var config = GetConfigIdentity();
        List<(DarknessAttackOwnerKey Key, string Reason)>? removals = null;
        foreach (var pair in acceptedByOwner)
        {
            var evidence = pair.Value;
            var reason = ValidateLiveEvidence(evidence, now, config);
            if (reason is not null)
            {
                removals ??= new List<(DarknessAttackOwnerKey, string)>();
                removals.Add((pair.Key, reason));
                continue;
            }
            visitor(evidence);
        }
        if (removals is null)
            return;
        foreach (var removal in removals)
            RemoveAccepted(removal.Key, removal.Reason, forgetSequence: false);
    }

    public void Publish(
        DarknessAttackOwnerKey key,
        EnvironmentLightPresentationKind kind,
        DarknessWarningClaimAction warningAction,
        string warningRequestId,
        long observationRevision,
        string reason
    )
    {
        if (
            disposed
            || !Context.IsMainPlayer
            || !Context.IsWorldReady
            || !SanityPlayerKey.IsCanonical(key.PlayerKey)
            || !SanityProtocol.IsValidSessionId(key.SessionId)
            || !long.TryParse(
                key.PlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var targetPlayerId
            )
            || targetPlayerId <= 0
            || targetPlayerId == Game1.player?.UniqueMultiplayerID
            || Game1.GetPlayer(targetPlayerId, onlyOnline: true) is null
        )
        {
            return;
        }
        if (
            !presentationSequences.ContainsKey(key)
            && presentationSequences.Count
                >= EnvironmentLightMultiplayerContract.MaximumRemoteOwners
        )
        {
            LogOnce(
                "environment-light.network.presentation-capacity-exceeded",
                "Remote environment-light presentation capacity was exceeded; the message failed closed.",
                LogLevel.Warn
            );
            return;
        }

        var sequence = presentationSequences.TryGetValue(key, out var previous)
            ? NextSequence(previous)
            : 1L;
        if (sequence <= 0)
            return;
        presentationSequences[key] = sequence;
        var message = new EnvironmentLightPresentationMessage
        {
            ProtocolVersion = EnvironmentLightMultiplayerContract.ProtocolVersion,
            SessionId = key.SessionId,
            PlayerKey = key.PlayerKey,
            ScreenId = key.ScreenId,
            Sequence = sequence,
            Kind = kind.ToString(),
            WarningAction = warningAction.ToString(),
            WarningRequestId = Bound(
                warningRequestId,
                DarknessAttackContract.MaximumRequestIdLength
            ),
            ObservationRevision = Math.Max(0L, observationRevision),
            Reason = Bound(
                reason,
                EnvironmentLightMultiplayerContract.MaximumReasonLength
            ),
        };
        helper.Multiplayer.SendMessage(
            message,
            EnvironmentLightMultiplayerContract.PresentationMessageType,
            new[] { modId },
            new[] { targetPlayerId }
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        ClearAll("environment-light.network.runtime-disposed");
        loggedReasons.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (
            disposed
            || Context.IsMainPlayer
            || !Context.IsWorldReady
            || !lifecycle.IsEnabled
            || lifecycle.AuthorityRole != SanityAuthorityRole.Client
        )
        {
            return;
        }

        TrySendLocalEvidence();
    }

    private void TrySendLocalEvidence()
    {
        var owner = Game1.player;
        var location = Game1.currentLocation;
        var screenId = Context.ScreenId;
        var sessionId = lifecycle.SessionId;
        if (
            owner is null
            || location is null
            || !owner.IsLocalPlayer
            || screenId < 0
            || !Context.HasScreenId(screenId)
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            return;
        }
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        var locationName = location.NameOrUniqueName;
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || string.IsNullOrWhiteSpace(locationName)
        )
        {
            return;
        }

        var result = lightService.Evaluate(
            owner,
            screenId,
            location,
            timeApi.Time
        );
        if (
            result.EvidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
            || !lightService.TryGetDiagnostic(playerKey, screenId, out var diagnostic)
            || diagnostic.FinalVisibilityCapabilityStatus
                != EnvironmentLightCapabilityStatus.Available
            || !diagnostic.FinalVisibilityScore.HasValue
            || !double.IsFinite(diagnostic.FinalVisibilityScore.Value)
            || diagnostic.RendererRevision <= 0
            || diagnostic.LocationRuleStatus is not (
                EnvironmentLightLocationRuleStatus.Matched
                or EnvironmentLightLocationRuleStatus.Unmatched
            )
            || diagnostic.NightVisionCapabilityStatus
                != EnvironmentLightCapabilityStatus.Available
            || diagnostic.IsNightVisionActive != false
            || !string.Equals(
                diagnostic.EvaluatorRevision,
                EnvironmentLightProductionContract.EvaluatorRevision,
                StringComparison.Ordinal
            )
            || !string.Equals(
                diagnostic.LocationNameOrUniqueName,
                locationName,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var config = GetConfigIdentity();
        if (!config.IsAvailable)
        {
            LogOnce(
                "environment-light.network.config-unavailable",
                "Remote environment-light reporting is fail-closed because the gameplay config fingerprint is unavailable.",
                LogLevel.Warn
            );
            return;
        }
        var ownerScreen = new OwnerScreenKey(playerKey, screenId);
        clientReports.TryGetValue(ownerScreen, out var tracker);
        var currentTick = Math.Max(0L, Game1.ticks);
        var revisionChanged =
            tracker is null || tracker.LastDiagnosticRevision != diagnostic.Revision;
        var cadenceElapsed =
            tracker is null
            || currentTick < tracker.LastSentAtTick
            || currentTick - tracker.LastSentAtTick
                >= EnvironmentLightMultiplayerContract.EvidenceReportCadenceTicks;
        if (
            !revisionChanged
            && !cadenceElapsed
        )
        {
            return;
        }
        var sequence = clientSequenceByPlayer.TryGetValue(
            playerKey,
            out var previousClientSequence
        )
            ? NextSequence(previousClientSequence)
            : 1L;
        if (sequence <= 0)
            return;

        var eventActive = lifecycle.IsEventCoverageActive
            || Game1.CurrentEvent is not null
            || Game1.eventUp;
        var festivalActive = Game1.CurrentEvent?.isFestival == true;
        var warping = Game1.isWarping;
        var minigameActive = Game1.currentMinigame is not null;
        var settleable = !eventActive && !festivalActive && !warping && !minigameActive;
        var paused = settleable
            && (
                Game1.paused
                || Game1.activeClickableMenu is not null
                || Game1.dialogueUp
                || !Game1.game1.IsActive
            );
        var message = new EnvironmentLightEvidenceMessage
        {
            ProtocolVersion = EnvironmentLightMultiplayerContract.ProtocolVersion,
            SessionId = sessionId,
            PlayerKey = playerKey,
            ScreenId = screenId,
            LocationNameOrUniqueName = locationName,
            LocationRuleContractVersion = diagnostic.LocationRuleContractVersion,
            LocationRuleId = diagnostic.LocationRuleId,
            ConfigSchemaVersion = config.SchemaVersion,
            ConfigFingerprint = config.Fingerprint,
            GameVersion = Game1.version,
            ModVersion = modVersion,
            EvaluatorRevision = diagnostic.EvaluatorRevision,
            RuleRevision = EnvironmentLightThresholds.Default.RuleRevision,
            Sequence = sequence,
            SampleTick = Math.Max(0L, diagnostic.FinalVisibilityCapturedAtTick),
            SampleUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RendererRevision = diagnostic.RendererRevision,
            VisibilityScore = diagnostic.FinalVisibilityScore.Value,
            Classification = result.Level.ToString(),
            EvidenceStatus = result.EvidenceStatus.ToString(),
            PitchBlackAuthorized = result.PitchBlackAuthorized,
            ExplicitNightVisionActive = false,
            GameplaySettleable = settleable,
            Paused = paused,
            Reason = result.Reason,
        };
        var host = Game1.MasterPlayer;
        if (
            host is null
            || host.UniqueMultiplayerID <= 0
            || host.UniqueMultiplayerID == owner.UniqueMultiplayerID
        )
        {
            return;
        }
        helper.Multiplayer.SendMessage(
            message,
            EnvironmentLightMultiplayerContract.EvidenceMessageType,
            new[] { modId },
            new[] { host.UniqueMultiplayerID }
        );
        clientSequenceByPlayer[playerKey] = sequence;
        clientReports[ownerScreen] = new ClientReportTracker(
            diagnostic.Revision,
            currentTick
        );
    }

    private void OnModMessageReceived(
        object? sender,
        ModMessageReceivedEventArgs e
    )
    {
        _ = sender;
        if (
            disposed
            || !string.Equals(e.FromModID, modId, StringComparison.Ordinal)
        )
        {
            return;
        }

        try
        {
            if (
                string.Equals(
                    e.Type,
                    EnvironmentLightMultiplayerContract.EvidenceMessageType,
                    StringComparison.Ordinal
                )
                && Context.IsMainPlayer
            )
            {
                HandleEvidence(e);
                return;
            }
            if (
                string.Equals(
                    e.Type,
                    EnvironmentLightMultiplayerContract.PresentationMessageType,
                    StringComparison.Ordinal
                )
                && !Context.IsMainPlayer
            )
            {
                HandlePresentation(e);
            }
        }
        catch (Exception exception)
        {
            LogOnce(
                $"environment-light.network.message-threw:{exception.GetType().Name}",
                $"Environment-light multiplayer message failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void HandleEvidence(ModMessageReceivedEventArgs e)
    {
        if (
            !Context.IsWorldReady
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || e.FromPlayerID <= 0
        )
        {
            return;
        }
        var player = Game1.GetPlayer(e.FromPlayerID, onlyOnline: true);
        var location = player?.currentLocation;
        if (
            player is null
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            return;
        }
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.FromPlayerID
        );
        var locationRule =
            SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(
                location,
                locationRules
            );
        var lastSequence = lastAcceptedSequenceByPlayer.TryGetValue(
            expectedPlayerKey,
            out var previousSequence
        )
            ? previousSequence
            : 0L;
        var message = e.ReadAs<EnvironmentLightEvidenceMessage>();
        var validation = EnvironmentLightEvidenceProtocol.Validate(
            message,
            new EnvironmentLightEvidenceValidationContext(
                lifecycle.SessionId,
                expectedPlayerKey,
                location.NameOrUniqueName,
                locationRule.ContractVersion,
                locationRule.RuleId,
                darknessAttackLocationAuthorization.Allows(locationRule),
                GetConfigIdentity(),
                Game1.version,
                modVersion,
                lastSequence,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            )
        );
        if (!validation.Accepted)
        {
            LogOnce(
                $"{expectedPlayerKey}:{validation.Reason}",
                $"Remote environment-light evidence was rejected (owner={expectedPlayerKey}, reason={validation.Reason}).",
                LogLevel.Warn
            );
            return;
        }

        var key = new DarknessAttackOwnerKey(
            expectedPlayerKey,
            message.ScreenId,
            message.SessionId
        );
        RemoveOtherAcceptedKeys(expectedPlayerKey, key);
        if (
            !acceptedByOwner.ContainsKey(key)
            && acceptedByOwner.Count
                >= EnvironmentLightMultiplayerContract.MaximumRemoteOwners
        )
        {
            LogOnce(
                "environment-light.network.owner-capacity-exceeded",
                "Remote environment-light evidence capacity was exceeded; the new owner was rejected.",
                LogLevel.Warn
            );
            return;
        }
        var accepted = new EnvironmentLightAcceptedRemoteEvidence(
            key,
            e.FromPlayerID,
            message.LocationNameOrUniqueName,
            message.LocationRuleContractVersion,
            message.LocationRuleId,
            new EnvironmentLightConfigIdentity(
                message.ConfigSchemaVersion,
                message.ConfigFingerprint
            ),
            message.Sequence,
            message.SampleTick,
            message.RendererRevision,
            MonotonicMilliseconds(),
            message.VisibilityScore,
            validation.Level,
            validation.EvidenceStatus,
            message.PitchBlackAuthorized,
            message.GameplaySettleable,
            message.Paused,
            message.Reason
        );
        acceptedByOwner[key] = accepted;
        lastAcceptedSequenceByPlayer[expectedPlayerKey] = message.Sequence;
    }

    private void HandlePresentation(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        var host = Game1.MasterPlayer;
        var owner = Game1.player;
        if (
            host is null
            || owner is null
            || e.FromPlayerID != host.UniqueMultiplayerID
        )
        {
            return;
        }
        var message = e.ReadAs<EnvironmentLightPresentationMessage>();
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        if (
            message is null
            || message.ProtocolVersion
                != EnvironmentLightMultiplayerContract.ProtocolVersion
            || !string.Equals(
                message.SessionId,
                lifecycle.SessionId,
                StringComparison.Ordinal
            )
            || !string.Equals(message.PlayerKey, playerKey, StringComparison.Ordinal)
            || message.ScreenId != Context.ScreenId
            || message.Sequence <= 0
            || message.ObservationRevision < 0
            || !Enum.TryParse(
                message.Kind,
                ignoreCase: false,
                out EnvironmentLightPresentationKind kind
            )
            || !Enum.IsDefined(kind)
            || !Enum.TryParse(
                message.WarningAction,
                ignoreCase: false,
                out DarknessWarningClaimAction warningAction
            )
            || !Enum.IsDefined(warningAction)
        )
        {
            return;
        }
        var key = new DarknessAttackOwnerKey(
            playerKey,
            message.ScreenId,
            message.SessionId
        );
        if (
            lastReceivedPresentationSequences.TryGetValue(key, out var previous)
            && message.Sequence <= previous
        )
        {
            return;
        }
        lastReceivedPresentationSequences[key] = message.Sequence;

        switch (warningAction)
        {
            case DarknessWarningClaimAction.Activate:
                if (
                    string.IsNullOrWhiteSpace(message.WarningRequestId)
                    || message.WarningRequestId.Length
                        > DarknessAttackContract.MaximumRequestIdLength
                )
                {
                    return;
                }
                audio.SubmitDarknessWarningClaim(
                    new SanityDarknessWarningClaim(
                        key.PlayerKey,
                        key.ScreenId,
                        key.SessionId,
                        message.WarningRequestId,
                        message.ObservationRevision
                    )
                );
                break;
            case DarknessWarningClaimAction.Release:
                if (!string.IsNullOrWhiteSpace(message.WarningRequestId))
                {
                    audio.RemoveDarknessWarningClaim(
                        key.PlayerKey,
                        key.ScreenId,
                        key.SessionId,
                        message.WarningRequestId
                    );
                }
                else
                {
                    audio.RemoveDarknessWarningOwner(key.PlayerKey);
                }
                break;
        }

        if (kind == EnvironmentLightPresentationKind.Clear)
        {
            audio.RemoveDarknessWarningOwner(key.PlayerKey);
            return;
        }
        ShowPrompt(kind);
    }

    private string? ValidateLiveEvidence(
        EnvironmentLightAcceptedRemoteEvidence evidence,
        long now,
        EnvironmentLightConfigIdentity config
    )
    {
        if (
            now < evidence.ReceivedAtMonotonicMilliseconds
            || now - evidence.ReceivedAtMonotonicMilliseconds
                > EnvironmentLightMultiplayerContract.MaximumHostEvidenceAgeMilliseconds
        )
        {
            return "environment-light.network.evidence-expired";
        }
        if (
            !string.Equals(
                lifecycle.SessionId,
                evidence.Key.SessionId,
                StringComparison.Ordinal
            )
            || !config.IsAvailable
            || config != evidence.Config
        )
        {
            return "environment-light.network.authority-context-changed";
        }
        var player = Game1.GetPlayer(evidence.SenderPlayerId, onlyOnline: true);
        if (
            player is null
            || !string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                evidence.Key.PlayerKey,
                StringComparison.Ordinal
            )
            || player.currentLocation is null
            || !string.Equals(
                player.currentLocation.NameOrUniqueName,
                evidence.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return "environment-light.network.owner-location-changed";
        }
        var rule = SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(
            player.currentLocation,
            locationRules
        );
        var expectedAuthorization =
            darknessAttackLocationAuthorization.Allows(rule)
            && evidence.Level == EnvironmentLightLevel.PitchBlack
            && EnvironmentLightVisibilityMath.CanAuthorizePitchBlack(
                evidence.VisibilityScore,
                EnvironmentLightThresholds.Default
            );
        if (
            rule.ContractVersion != evidence.LocationRuleContractVersion
            || !string.Equals(
                rule.RuleId,
                evidence.LocationRuleId,
                StringComparison.Ordinal
            )
            || evidence.PitchBlackAuthorized != expectedAuthorization
        )
        {
            return "environment-light.network.location-rule-changed";
        }
        return null;
    }

    private void OnPeerDisconnected(
        object? sender,
        PeerDisconnectedEventArgs e
    )
    {
        _ = sender;
        if (disposed)
            return;
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
        if (Context.IsMainPlayer)
        {
            RemoveAcceptedForPlayer(
                playerKey,
                "environment-light.network.peer-disconnected",
                forgetSequence: true
            );
            return;
        }
        var host = Game1.MasterPlayer;
        if (host is not null && e.Peer.PlayerID == host.UniqueMultiplayerID)
            ClearClientPresentation();
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;
        if (stateEvent.Kind == SanityStateEventKind.SystemDisabled)
        {
            ClearAll("environment-light.network.system-disabled");
            return;
        }
        if (stateEvent.Kind == SanityStateEventKind.OwnerInvalidated)
        {
            RemoveAcceptedForPlayer(
                stateEvent.PlayerKey,
                "environment-light.network.owner-invalidated",
                forgetSequence: false
            );
            audio.RemoveDarknessWarningOwner(stateEvent.PlayerKey);
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (!disposed)
            ClearAll($"environment-light.network.world-boundary:{boundary}");
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
        {
            ClearAll($"environment-light.network.session-boundary:{boundary}");
            clientSequenceByPlayer.Clear();
        }
    }

    private void RemoveOtherAcceptedKeys(
        string playerKey,
        DarknessAttackOwnerKey keep
    )
    {
        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in acceptedByOwner.Keys)
        {
            if (
                key.Equals(keep)
                || !string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                continue;
            }
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
        {
            RemoveAccepted(
                key,
                "environment-light.network.owner-context-replaced",
                forgetSequence: false
            );
        }
    }

    private void RemoveAcceptedForPlayer(
        string playerKey,
        string reason,
        bool forgetSequence
    )
    {
        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in acceptedByOwner.Keys)
        {
            if (!string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is not null)
        {
            foreach (var key in removals)
                RemoveAccepted(key, reason, forgetSequence: false);
        }
        if (forgetSequence)
            lastAcceptedSequenceByPlayer.Remove(playerKey);
    }

    private void RemoveAccepted(
        DarknessAttackOwnerKey key,
        string reason,
        bool forgetSequence
    )
    {
        if (!acceptedByOwner.Remove(key))
            return;
        if (forgetSequence)
            lastAcceptedSequenceByPlayer.Remove(key.PlayerKey);
        EvidenceInvalidated?.Invoke(key, reason);
        // Keep the bounded per-session sequence after invalidation. The same owner may report
        // again before the session ends, and restarting at 1 would look like a replay to its client.
    }

    private void ClearAll(string reason)
    {
        if (Context.IsMainPlayer)
        {
            if (acceptedByOwner.Count > 0)
            {
                var keys = new DarknessAttackOwnerKey[acceptedByOwner.Count];
                acceptedByOwner.Keys.CopyTo(keys, 0);
                foreach (var key in keys)
                    RemoveAccepted(key, reason, forgetSequence: false);
            }
            lastAcceptedSequenceByPlayer.Clear();
            presentationSequences.Clear();
        }
        clientReports.Clear();
        ClearClientPresentation();
    }

    private void ClearClientPresentation()
    {
        if (lastReceivedPresentationSequences.Count > 0)
        {
            foreach (var key in lastReceivedPresentationSequences.Keys)
                audio.RemoveDarknessWarningOwner(key.PlayerKey);
        }
        lastReceivedPresentationSequences.Clear();
    }

    private EnvironmentLightConfigIdentity GetConfigIdentity()
    {
        try
        {
            return configIdentityProvider();
        }
        catch (Exception exception)
        {
            LogOnce(
                $"environment-light.network.config-threw:{exception.GetType().Name}",
                $"Environment-light config identity failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return EnvironmentLightConfigIdentity.Unavailable;
        }
    }

    private void ShowPrompt(EnvironmentLightPresentationKind kind)
    {
        var key = kind switch
        {
            EnvironmentLightPresentationKind.EnteredDarkness =>
                "darkness-attack.prompt.entered",
            EnvironmentLightPresentationKind.Warning =>
                "darkness-attack.prompt.warning",
            EnvironmentLightPresentationKind.EscapedDarkness =>
                "darkness-attack.prompt.escaped",
            EnvironmentLightPresentationKind.Resolved =>
                "darkness-attack.prompt.resolved",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(key))
            return;
        Game1.addHUDMessage(
            HUDMessage.ForCornerTextbox(helper.Translation.Get(key).ToString())
        );
    }

    private void LogOnce(string reason, string message, LogLevel level)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || loggedReasons.Count >= MaximumLoggedReasons
            || !loggedReasons.Add(reason)
        )
        {
            return;
        }
        monitor.Log(message, level);
    }

    private static long MonotonicMilliseconds()
    {
        return Math.Max(0L, Environment.TickCount64);
    }

    private static long NextSequence(long previous)
    {
        return previous is >= 0 and < long.MaxValue ? previous + 1L : -1L;
    }

    private static string Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumLength <= 0)
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength];
    }

    private readonly record struct OwnerScreenKey(string PlayerKey, int ScreenId);

    private sealed record ClientReportTracker(
        long LastDiagnosticRevision,
        long LastSentAtTick
    );
}
