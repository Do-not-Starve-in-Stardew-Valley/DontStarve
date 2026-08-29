#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using DontStarve.Display;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Resource.Sanity;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Owner-local adapter for the pure host state machine. It consumes the existing light authority,
/// publishes expiry intents, and owns no health, Sanity, or attack-settlement operation.
/// </summary>
internal sealed class SmapiDarknessAttackService : IDisposable
{
    private const string WarningPlaybackMode = "CancelableOneShot";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly EnvironmentLightService lightService;
    private readonly SanitySmapiResourceService resources;
    private readonly SanitySmapiAudioService audio;
    private readonly TaggedHudMessageService hudMessages;
    private readonly IDarknessDamageModeResolver modeResolver;
    private readonly IEnvironmentLightRemoteEvidenceSource? remoteEvidenceSource;
    private readonly IEnvironmentLightRemotePresentationSink? remotePresentationSink;
    private readonly Dictionary<int, DarknessDamageModeTracker> modeTrackersByScreen = new();
    private readonly IDarknessAttackClock clock = new SmapiDarknessAttackClock();
    private readonly IDarknessAttackRandom random = new SystemDarknessAttackRandom();
    private readonly IDarknessAttackRequestIdSource requestIds =
        new GuidDarknessAttackRequestIdSource();
    private readonly Dictionary<int, OwnerBinding> ownersByScreen = new();
    private readonly Dictionary<DarknessAttackOwnerKey, RemoteOwnerBinding>
        remoteOwners = new();
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private DarknessAttackStateMachine? stateMachine;
    private double attackDurationSeconds;
    private bool metadataRefreshPending;
    private bool disposed;

    internal SmapiDarknessAttackService(
        IModHelper helper,
        IMonitor monitor,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        EnvironmentLightService lightService,
        SanitySmapiResourceService resources,
        SanitySmapiAudioService audio,
        TaggedHudMessageService hudMessages,
        IDarknessDamageModeResolver modeResolver,
        IEnvironmentLightRemoteEvidenceSource? remoteEvidenceSource = null,
        IEnvironmentLightRemotePresentationSink? remotePresentationSink = null
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.lightService = lightService ?? throw new ArgumentNullException(nameof(lightService));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.hudMessages = hudMessages
            ?? throw new ArgumentNullException(nameof(hudMessages));
        this.modeResolver = modeResolver
            ?? throw new ArgumentNullException(nameof(modeResolver));
        this.remoteEvidenceSource = remoteEvidenceSource;
        this.remotePresentationSink = remotePresentationSink;

        TryRefreshStateMachine();
        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
        if (remoteEvidenceSource is not null)
            remoteEvidenceSource.EvidenceInvalidated += OnRemoteEvidenceInvalidated;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
    }

    /// <summary>Settlement remains external so this adapter never owns health or Sanity mutation.</summary>
    internal event Action<DarknessAttackExpiryIntent>? ExpiryIntentCreated;

    internal bool TryGetSnapshot(
        string playerKey,
        int screenId,
        out DarknessAttackStateSnapshot snapshot
    )
    {
        if (
            !disposed
            && stateMachine is not null
            && ownersByScreen.TryGetValue(screenId, out var binding)
            && string.Equals(binding.Key.PlayerKey, playerKey, StringComparison.Ordinal)
        )
        {
            return stateMachine.TryGetSnapshot(binding.Key, out snapshot);
        }
        snapshot = null!;
        return false;
    }

    internal DarknessAttackUpdateResult CompleteReceipt(DarknessAttackReceipt receipt)
    {
        if (disposed || stateMachine is null)
        {
            return new DarknessAttackUpdateResult(
                disposed
                    ? DarknessAttackMutationStatus.Disposed
                    : DarknessAttackMutationStatus.Invalid,
                disposed ? "darkness.runtime.disposed" : "darkness.runtime.metadata-unavailable",
                DarknessAttackPromptKind.None,
                DarknessWarningClaimAction.None,
                string.Empty,
                null
            );
        }
        var mode = RefreshMode(receipt.Key.ScreenId);
        if (mode.ResetRequired)
        {
            ResetForModeChange(receipt.Key.ScreenId, mode.Reason);
            ResetRemoteForModeChange(mode.Reason);
        }
        if (!mode.HasValue || !mode.CanRun)
        {
            return DarknessAttackUpdateResult.NoChange(
                mode.HasValue
                    ? "darkness.runtime.mode-off"
                    : "darkness.runtime.mode-unavailable"
            );
        }
        DarknessAttackObservation observation;
        var isRemote = false;
        if (
            ownersByScreen.TryGetValue(receipt.Key.ScreenId, out var binding)
            && binding.Key.Equals(receipt.Key)
            && TryCreateObservation(binding, mode.Mode, out observation)
        )
        {
            isRemote = false;
        }
        else if (
            remoteOwners.TryGetValue(receipt.Key, out var remoteBinding)
            && TryCreateRemoteObservation(
                remoteBinding,
                mode.Mode,
                out observation
            )
        )
        {
            isRemote = true;
        }
        else
        {
            return DarknessAttackUpdateResult.NoChange(
                "darkness.runtime.receipt-owner-unavailable"
            );
        }

        var result = stateMachine.CompleteReceipt(observation, receipt);
        ApplyResult(observation, result, isRemote);
        return result;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        if (remoteEvidenceSource is not null)
            remoteEvidenceSource.EvidenceInvalidated -= OnRemoteEvidenceInvalidated;
        ClearAll();
        stateMachine?.Dispose();
        stateMachine = null;
        loggedFailures.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;
        if (metadataRefreshPending)
            TryRefreshStateMachine();
        if (stateMachine is null)
            return;
        if (e.IsMultipleOf(60))
            CleanupInvalidScreens();
        if (!lifecycle.IsEnabled || lifecycle.AuthorityRole != SanityAuthorityRole.Host)
        {
            if (ownersByScreen.Count > 0 || remoteOwners.Count > 0)
                ClearAll();
            return;
        }
        var mode = RefreshMode(Context.ScreenId);
        if (mode.ResetRequired)
        {
            ResetForModeChange(Context.ScreenId, mode.Reason);
            ResetRemoteForModeChange(mode.Reason);
        }
        if (!mode.HasValue)
        {
            LogFailureOnce(
                mode.Reason,
                $"Darkness countdown mode failed closed (reason={mode.Reason})."
            );
            return;
        }
        if (!mode.CanRun)
            return;

        if (TryBindCurrentOwner(out var binding))
        {
            if (TryCreateObservation(binding, mode.Mode, out var observation))
            {
                var result = stateMachine.Observe(observation);
                ApplyResult(observation, result, isRemote: false);
            }
            else
            {
                RemoveScreen(
                    binding.Key.ScreenId,
                    showPrompt: false,
                    "darkness.runtime.observation-unavailable"
                );
            }
        }
        else
        {
            RemoveScreen(Context.ScreenId, showPrompt: false, "darkness.runtime.owner-unavailable");
        }

        if (remoteEvidenceSource is null)
            return;
        try
        {
            remoteEvidenceSource.VisitFresh(
                evidence => ObserveRemoteEvidence(evidence, mode.Mode)
            );
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                $"darkness.runtime.remote-evidence-threw:{exception.GetType().Name}",
                $"Remote darkness evidence failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            ClearAll();
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;
        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemDisabled:
                ClearAll();
                break;
            case SanityStateEventKind.SystemEnabled:
                if (stateMachine is null)
                    metadataRefreshPending = true;
                break;
            case SanityStateEventKind.OwnerInvalidated:
                stateMachine?.RemoveOwner(stateEvent.PlayerKey);
                audio.RemoveDarknessWarningOwner(stateEvent.PlayerKey);
                RemoveBindingsForOwner(stateEvent.PlayerKey);
                RemoveRemoteBindingsForOwner(stateEvent.PlayerKey);
                break;
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;
        if (boundary == SanityWorldBoundary.DayStarted)
        {
            ClearAll();
            return;
        }
        if (boundary == SanityWorldBoundary.Warp)
        {
            RemoveScreen(
                Context.ScreenId,
                showPrompt: true,
                "darkness.state.owner-warped"
            );
        }
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            ClearAll();
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (disposed)
            return;
        ClearAll();
        stateMachine?.Dispose();
        stateMachine = null;
        if (reason == SanityResourceReleaseReason.ContentInvalidated)
        {
            metadataRefreshPending = true;
            return;
        }
        if (reason == SanityResourceReleaseReason.Dispose)
            Dispose();
    }

    private bool TryBindCurrentOwner(out OwnerBinding binding)
    {
        binding = null!;
        var screenId = Context.ScreenId;
        var player = Game1.player;
        var location = Game1.currentLocation;
        var sessionId = lifecycle.SessionId;
        if (
            !Context.IsWorldReady
            || screenId < 0
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return false;
        var key = new DarknessAttackOwnerKey(playerKey, screenId, sessionId);
        if (
            ownersByScreen.TryGetValue(screenId, out var existing)
            && existing.Key.Equals(key)
            && ReferenceEquals(existing.LocationReference, location)
            && string.Equals(
                existing.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            binding = existing;
            return true;
        }

        RemoveScreen(screenId, showPrompt: true, "darkness.state.owner-context-changed");
        binding = new OwnerBinding(key, player, location, location.NameOrUniqueName);
        ownersByScreen[screenId] = binding;
        return true;
    }

    private bool TryCreateObservation(
        OwnerBinding binding,
        DarknessDamageMode mode,
        out DarknessAttackObservation observation
    )
    {
        observation = default;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(binding.Key.ScreenId)
            || !ReferenceEquals(Game1.player, binding.PlayerReference)
            || !ReferenceEquals(Game1.currentLocation, binding.LocationReference)
        )
        {
            return false;
        }

        var modePolicy = DarknessAttackModePolicy.Evaluate(
            mode,
            binding.PlayerReference.health,
            binding.PlayerReference.maxHealth
        );
        if (!modePolicy.CanSettle)
        {
            observation = new DarknessAttackObservation(
                binding.Key,
                Math.Max(0L, Game1.ticks),
                lifecycle.AuthorityRole,
                false,
                false,
                EnvironmentLightLevel.Dim,
                EnvironmentLightEvidenceStatus.Fallback,
                false,
                modePolicy.Reason,
                modePolicy.Reason
            );
            return true;
        }

        var result = lightService.Evaluate(
            binding.PlayerReference,
            binding.Key.ScreenId,
            binding.LocationReference,
            timeApi.Time
        );
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
        var unsettledReason = festivalActive
            ? "darkness.state.festival-active"
            : eventActive
                ? "darkness.state.event-active"
                : warping
                    ? "darkness.state.warping"
                    : minigameActive
                        ? "darkness.state.minigame-active"
                        : string.Empty;
        observation = new DarknessAttackObservation(
            binding.Key,
            Math.Max(0L, Game1.ticks),
            lifecycle.AuthorityRole,
            settleable,
            paused,
            result.Level,
            result.EvidenceStatus,
            result.PitchBlackAuthorized,
            result.Reason,
            unsettledReason
        );
        return true;
    }

    private void ObserveRemoteEvidence(
        EnvironmentLightAcceptedRemoteEvidence evidence,
        DarknessDamageMode mode
    )
    {
        if (stateMachine is null)
            return;
        var player = Game1.GetPlayer(evidence.SenderPlayerId, onlyOnline: true);
        var location = player?.currentLocation;
        if (
            player is null
            || location is null
            || !string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                evidence.Key.PlayerKey,
                StringComparison.Ordinal
            )
            || !string.Equals(
                location.NameOrUniqueName,
                evidence.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            RemoveRemoteOwner(
                evidence.Key,
                showPrompt: true,
                "darkness.runtime.remote-owner-unavailable"
            );
            return;
        }
        if (
            !remoteOwners.ContainsKey(evidence.Key)
            && remoteOwners.Count >= DarknessAttackContract.MaximumOwnerStates
        )
        {
            LogFailureOnce(
                "darkness.runtime.remote-owner-capacity-exceeded",
                "Remote darkness owner capacity was exceeded; the new owner failed closed."
            );
            return;
        }

        var binding = new RemoteOwnerBinding(
            evidence,
            player,
            location,
            location.NameOrUniqueName
        );
        remoteOwners[evidence.Key] = binding;
        if (!TryCreateRemoteObservation(binding, mode, out var observation))
        {
            RemoveRemoteOwner(
                evidence.Key,
                showPrompt: true,
                "darkness.runtime.remote-observation-unavailable"
            );
            return;
        }
        var result = stateMachine.Observe(observation);
        ApplyResult(observation, result, isRemote: true);
    }

    private bool TryCreateRemoteObservation(
        RemoteOwnerBinding binding,
        DarknessDamageMode mode,
        out DarknessAttackObservation observation
    )
    {
        observation = default;
        var evidence = binding.Evidence;
        if (
            !Context.IsWorldReady
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !string.Equals(
                lifecycle.SessionId,
                evidence.Key.SessionId,
                StringComparison.Ordinal
            )
            || Game1.GetPlayer(evidence.SenderPlayerId, onlyOnline: true)
                is not { } player
            || !ReferenceEquals(player, binding.PlayerReference)
            || player.currentLocation is not { } location
            || !ReferenceEquals(location, binding.LocationReference)
            || !string.Equals(
                location.NameOrUniqueName,
                binding.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        var modePolicy = DarknessAttackModePolicy.Evaluate(
            mode,
            player.health,
            player.maxHealth
        );
        if (!modePolicy.CanSettle)
        {
            observation = new DarknessAttackObservation(
                evidence.Key,
                Math.Max(0L, Game1.ticks),
                lifecycle.AuthorityRole,
                false,
                false,
                EnvironmentLightLevel.Dim,
                EnvironmentLightEvidenceStatus.Fallback,
                false,
                modePolicy.Reason,
                modePolicy.Reason
            );
            return true;
        }

        var eventActive = lifecycle.IsEventCoverageActive
            || Game1.CurrentEvent is not null
            || Game1.eventUp;
        var festivalActive = Game1.CurrentEvent?.isFestival == true;
        var warping = Game1.isWarping;
        var minigameActive = Game1.currentMinigame is not null;
        var hostSettleable =
            !eventActive && !festivalActive && !warping && !minigameActive;
        var settleable = evidence.GameplaySettleable && hostSettleable;
        var paused = settleable
            && (
                evidence.Paused
                || Game1.paused
                || !Game1.game1.IsActive
            );
        var unsettledReason = !evidence.GameplaySettleable
            ? "darkness.state.remote-client-unsettleable"
            : festivalActive
                ? "darkness.state.festival-active"
                : eventActive
                    ? "darkness.state.event-active"
                    : warping
                        ? "darkness.state.warping"
                        : minigameActive
                            ? "darkness.state.minigame-active"
                            : string.Empty;
        observation = new DarknessAttackObservation(
            evidence.Key,
            Math.Max(0L, Game1.ticks),
            lifecycle.AuthorityRole,
            settleable,
            paused,
            evidence.Level,
            evidence.EvidenceStatus,
            evidence.PitchBlackAuthorized,
            evidence.Reason,
            unsettledReason
        );
        return true;
    }

    private void ApplyResult(
        DarknessAttackObservation observation,
        DarknessAttackUpdateResult result,
        bool isRemote
    )
    {
        if (result.Status != DarknessAttackMutationStatus.Applied)
            return;

        if (isRemote)
        {
            remotePresentationSink?.Publish(
                observation.Key,
                ToPresentationKind(result.Prompt),
                result.WarningClaimAction,
                result.WarningRequestId,
                observation.Revision,
                result.Reason,
                result.WarningClipId,
                result.WarningDurationSeconds,
                result.Prompt == DarknessAttackPromptKind.Warning
                    ? result.WarningDurationSeconds
                    : 0d
            );
        }
        else
        {
            switch (result.WarningClaimAction)
            {
                case DarknessWarningClaimAction.Activate:
                    audio.SubmitDarknessWarningClaim(
                        new SanityDarknessWarningClaim(
                            observation.Key.PlayerKey,
                            observation.Key.ScreenId,
                            observation.Key.SessionId,
                            result.WarningRequestId,
                            observation.Revision,
                            result.WarningClipId,
                            result.WarningDurationSeconds
                        )
                    );
                    break;
                case DarknessWarningClaimAction.Release:
                    audio.RemoveDarknessWarningClaim(
                        observation.Key.PlayerKey,
                        observation.Key.ScreenId,
                        observation.Key.SessionId,
                        result.WarningRequestId
                    );
                    break;
            }
            ShowPrompt(
                result.Prompt,
                resolved: false,
                durationSeconds: result.Prompt == DarknessAttackPromptKind.Warning
                    ? result.WarningDurationSeconds
                    : 0d
            );
        }

        if (result.ExpiryIntent is not { } intent)
            return;
        try
        {
            ExpiryIntentCreated?.Invoke(intent);
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "darkness.runtime.intent-consumer-failed",
                $"Darkness expiry intent consumer failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void RemoveScreen(int screenId, bool showPrompt, string reason)
    {
        if (!ownersByScreen.Remove(screenId, out var binding))
            return;
        if (stateMachine is not null)
        {
            var result = stateMachine.Cancel(binding.Key, reason);
            if (showPrompt)
            {
                var observation = new DarknessAttackObservation(
                    binding.Key,
                    Math.Max(0L, Game1.ticks),
                    SanityAuthorityRole.Host,
                    false,
                    false,
                    EnvironmentLightLevel.Dim,
                    EnvironmentLightEvidenceStatus.Fallback,
                    false,
                    reason,
                    reason
                );
                ApplyResult(observation, result, isRemote: false);
            }
            else if (result.WarningClaimAction == DarknessWarningClaimAction.Release)
            {
                audio.RemoveDarknessWarningClaim(
                    binding.Key.PlayerKey,
                    binding.Key.ScreenId,
                    binding.Key.SessionId,
                    result.WarningRequestId
                );
            }
            stateMachine.Remove(binding.Key);
        }
    }

    private void OnRemoteEvidenceInvalidated(
        DarknessAttackOwnerKey key,
        string reason
    )
    {
        if (disposed)
            return;
        RemoveRemoteOwner(
            key,
            showPrompt: true,
            string.IsNullOrWhiteSpace(reason)
                ? "darkness.runtime.remote-evidence-invalidated"
                : reason
        );
    }

    private void RemoveRemoteOwner(
        DarknessAttackOwnerKey key,
        bool showPrompt,
        string reason
    )
    {
        remoteOwners.Remove(key);
        if (stateMachine is not null)
        {
            var result = stateMachine.Cancel(key, reason);
            if (showPrompt)
            {
                var observation = new DarknessAttackObservation(
                    key,
                    Math.Max(0L, Game1.ticks),
                    SanityAuthorityRole.Host,
                    false,
                    false,
                    EnvironmentLightLevel.Dim,
                    EnvironmentLightEvidenceStatus.Fallback,
                    false,
                    reason,
                    reason
                );
                ApplyResult(observation, result, isRemote: true);
            }
            stateMachine.Remove(key);
        }
        remotePresentationSink?.Publish(
            key,
            EnvironmentLightPresentationKind.Clear,
            DarknessWarningClaimAction.Release,
            string.Empty,
            Math.Max(0L, Game1.ticks),
            reason
        );
    }

    private void ResetRemoteForModeChange(string reason)
    {
        if (remoteOwners.Count == 0)
            return;
        var keys = new DarknessAttackOwnerKey[remoteOwners.Count];
        remoteOwners.Keys.CopyTo(keys, 0);
        foreach (var key in keys)
        {
            RemoveRemoteOwner(
                key,
                showPrompt: true,
                string.IsNullOrWhiteSpace(reason)
                    ? "darkness.state.mode-changed"
                    : $"darkness.state.mode-changed:{reason}"
            );
        }
    }

    private void CleanupInvalidScreens()
    {
        List<int>? removals = null;
        foreach (var screenId in ownersByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;
            removals ??= new List<int>();
            removals.Add(screenId);
        }
        if (removals is not null)
        {
            foreach (var screenId in removals)
            {
                RemoveScreen(screenId, showPrompt: false, "darkness.state.screen-left");
                modeTrackersByScreen.Remove(screenId);
            }
        }
        List<int>? trackerRemovals = null;
        foreach (var screenId in modeTrackersByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;
            trackerRemovals ??= new List<int>();
            trackerRemovals.Add(screenId);
        }
        if (trackerRemovals is not null)
        {
            foreach (var screenId in trackerRemovals)
                modeTrackersByScreen.Remove(screenId);
        }
        audio.RemoveInvalidDarknessWarningScreens(Context.HasScreenId);
    }

    private void RemoveBindingsForOwner(string playerKey)
    {
        List<int>? removals = null;
        foreach (var pair in ownersByScreen)
        {
            if (!string.Equals(pair.Value.Key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<int>();
            removals.Add(pair.Key);
        }
        if (removals is null)
            return;
        foreach (var screenId in removals)
            ownersByScreen.Remove(screenId);
    }

    private void RemoveRemoteBindingsForOwner(string playerKey)
    {
        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in remoteOwners.Keys)
        {
            if (!string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
        {
            remoteOwners.Remove(key);
            remotePresentationSink?.Publish(
                key,
                EnvironmentLightPresentationKind.Clear,
                DarknessWarningClaimAction.Release,
                string.Empty,
                Math.Max(0L, Game1.ticks),
                "darkness.runtime.owner-invalidated"
            );
        }
    }

    private void ClearAll()
    {
        if (remoteOwners.Count > 0)
        {
            foreach (var key in remoteOwners.Keys)
            {
                remotePresentationSink?.Publish(
                    key,
                    EnvironmentLightPresentationKind.Clear,
                    DarknessWarningClaimAction.Release,
                    string.Empty,
                    Math.Max(0L, Game1.ticks),
                    "darkness.runtime.cleared"
                );
            }
        }
        ownersByScreen.Clear();
        remoteOwners.Clear();
        modeTrackersByScreen.Clear();
        audio.ClearDarknessWarningClaims();
        stateMachine?.Clear();
    }

    private DarknessDamageModeTransition RefreshMode(int screenId)
    {
        if (screenId < 0)
        {
            return new DarknessDamageModeTransition(
                false,
                DarknessDamageMode.Off,
                false,
                false,
                "darkness.mode.screen-invalid"
            );
        }
        if (!modeTrackersByScreen.TryGetValue(screenId, out var tracker))
        {
            if (modeTrackersByScreen.Count >= DarknessAttackContract.MaximumOwnerStates)
            {
                return new DarknessDamageModeTransition(
                    false,
                    DarknessDamageMode.Off,
                    false,
                    false,
                    "darkness.mode.screen-capacity-exceeded"
                );
            }
            tracker = new DarknessDamageModeTracker();
            modeTrackersByScreen.Add(screenId, tracker);
        }
        DarknessDamageModeResolution resolution;
        try
        {
            resolution = modeResolver.Resolve();
        }
        catch (Exception exception)
        {
            var reason = $"darkness.mode.resolver-threw:{exception.GetType().Name}";
            LogFailureOnce(
                reason,
                $"Darkness damage mode resolver failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            resolution = DarknessDamageModeResolution.Unavailable(reason);
        }
        return tracker.Observe(resolution);
    }

    private void ResetForModeChange(int screenId, string reason)
    {
        RemoveScreen(
            screenId,
            showPrompt: true,
            string.IsNullOrWhiteSpace(reason)
                ? "darkness.state.mode-changed"
                : $"darkness.state.mode-changed:{reason}"
        );
    }

    private void TryRefreshStateMachine()
    {
        metadataRefreshPending = false;
        var warningResult = resources.GetAudioCueMetadata(DarknessAttackContract.WarningCueId);
        var warningCue = warningResult.Cue;
        var attackResult = resources.GetAudioCueMetadata(DarknessAttackContract.AttackCueId);
        var attackCue = attackResult.Cue;
        if (
            !warningResult.Success
            || warningCue is null
            || !string.Equals(
                warningCue.CueId,
                DarknessAttackContract.WarningCueId,
                StringComparison.Ordinal
            )
            || !warningCue.Enabled
            || !string.Equals(warningCue.PlaybackMode, WarningPlaybackMode, StringComparison.Ordinal)
            || warningCue.Clips.Count != DarknessAttackContract.WarningClipCount
            || warningCue.Clips.Any(
                clip =>
                    string.IsNullOrWhiteSpace(clip.ClipId)
                    || !double.IsFinite(clip.DurationSeconds)
                    || clip.DurationSeconds <= 0d
            )
            || !attackResult.Success
            || attackCue is null
            || !string.Equals(
                attackCue.CueId,
                DarknessAttackContract.AttackCueId,
                StringComparison.Ordinal
            )
            || !attackCue.Enabled
            || !string.Equals(attackCue.PlaybackMode, "OneShot", StringComparison.Ordinal)
            || attackCue.Clips.Count != 1
            || !double.IsFinite(attackCue.Clips[0].DurationSeconds)
            || attackCue.Clips[0].DurationSeconds <= 0d
        )
        {
            var failedResult = !warningResult.Success || warningCue is null
                ? warningResult
                : attackResult;
            LogFailureOnce(
                failedResult.Diagnostic.Code,
                $"Darkness countdown is unavailable because event audio metadata failed closed (code={failedResult.Diagnostic.Code}, reason={failedResult.Diagnostic.Reason})."
            );
            return;
        }

        var warningClips = new DarknessWarningClip[warningCue.Clips.Count];
        for (var index = 0; index < warningCue.Clips.Count; index++)
        {
            var clip = warningCue.Clips[index];
            warningClips[index] = new DarknessWarningClip(
                clip.ClipId,
                clip.DurationSeconds
            );
        }

        stateMachine?.Dispose();
        stateMachine = new DarknessAttackStateMachine(
            clock,
            random,
            requestIds,
            warningClips
        );
        attackDurationSeconds = attackCue.Clips[0].DurationSeconds;
        monitor.Log(
            $"Darkness attack metadata available (warning-cue={warningCue.CueId}, warning-clips={warningCue.Clips.Count}, attack-cue={attackCue.CueId}, attack-duration={attackDurationSeconds:0.###}s, placeholder={warningResult.Diagnostic.IsPlaceholder || attackResult.Diagnostic.IsPlaceholder}, contract={DarknessAttackContract.ContractVersion}).",
            LogLevel.Debug
        );
    }

    internal void ShowResolvedPrompt(DarknessAttackOwnerKey key)
    {
        if (disposed)
            return;
        if (remoteOwners.ContainsKey(key))
        {
            remotePresentationSink?.Publish(
                key,
                EnvironmentLightPresentationKind.Resolved,
                DarknessWarningClaimAction.None,
                string.Empty,
                Math.Max(0L, Game1.ticks),
                "darkness.resolution.settled",
                presentationDurationSeconds: attackDurationSeconds
            );
            return;
        }
        audio.TriggerDarknessAttack();
        ShowPrompt(
            DarknessAttackPromptKind.None,
            resolved: true,
            durationSeconds: attackDurationSeconds
        );
    }

    private void ShowPrompt(
        DarknessAttackPromptKind prompt,
        bool resolved = false,
        double durationSeconds = 0d
    )
    {
        var key = resolved
            ? "darkness-attack.prompt.resolved"
            : prompt switch
        {
            DarknessAttackPromptKind.EnteredDarkness => "darkness-attack.prompt.entered",
            DarknessAttackPromptKind.Warning => "darkness-attack.prompt.warning",
            DarknessAttackPromptKind.EscapedDarkness => "darkness-attack.prompt.escaped",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(key))
            return;
        hudMessages.AddCornerTextbox(
            helper.Translation.Get(key).ToString(),
            HudMessageGroupTags.DarknessAttack,
            durationSeconds
        );
    }

    private static EnvironmentLightPresentationKind ToPresentationKind(
        DarknessAttackPromptKind prompt
    )
    {
        return prompt switch
        {
            DarknessAttackPromptKind.EnteredDarkness =>
                EnvironmentLightPresentationKind.EnteredDarkness,
            DarknessAttackPromptKind.Warning =>
                EnvironmentLightPresentationKind.Warning,
            DarknessAttackPromptKind.EscapedDarkness =>
                EnvironmentLightPresentationKind.EscapedDarkness,
            _ => EnvironmentLightPresentationKind.None,
        };
    }

    private void LogFailureOnce(string code, string message)
    {
        if (loggedFailures.Count >= 32 || !loggedFailures.Add(code))
            return;
        monitor.Log(message, LogLevel.Error);
    }

    private sealed record OwnerBinding(
        DarknessAttackOwnerKey Key,
        Farmer PlayerReference,
        GameLocation LocationReference,
        string LocationNameOrUniqueName
    );

    private sealed record RemoteOwnerBinding(
        EnvironmentLightAcceptedRemoteEvidence Evidence,
        Farmer PlayerReference,
        GameLocation LocationReference,
        string LocationNameOrUniqueName
    );
}

internal sealed class SmapiDarknessAttackClock : IDarknessAttackClock
{
    public TimeSpan ElapsedGameTime => Game1.currentGameTime.ElapsedGameTime;
}

internal sealed class SystemDarknessAttackRandom : IDarknessAttackRandom
{
    public int NextInclusive(int minimum, int maximum)
    {
        if (minimum > maximum || maximum == int.MaxValue)
            return int.MinValue;
        return Random.Shared.Next(minimum, maximum + 1);
    }
}

internal sealed class GuidDarknessAttackRequestIdSource
    : IDarknessAttackRequestIdSource
{
    public string NextRequestId(DarknessAttackOwnerKey key)
    {
        _ = key;
        return string.Concat("darkness-", Guid.NewGuid().ToString("N"));
    }
}
