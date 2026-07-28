#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// Bridges owner-local tier observations to one process-shared physical audio coordinator.
/// It neither calculates thresholds nor broadcasts presentation state to other game processes.
/// </summary>
internal sealed class SanitySmapiAudioService : IDisposable
{
    private const int MaximumLoggedDiagnostics = 48;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanitySmapiResourceService resources;
    private readonly SanityProcessAudioCoordinator coordinator;
    private readonly SanityGameMusicCoordinator gameMusicCoordinator;
    private readonly Dictionary<int, OwnerBinding> ownersByScreen = new();
    private readonly HashSet<SanityAudioClaimKey> initializedClaims = new();
    private readonly HashSet<SanityAudioClaimKey> dirtyClaims = new();
    private readonly HashSet<int> localMenuScreens = new();
    private readonly HashSet<int> processPausedScreens = new();
    private readonly HashSet<int> miniJukeboxScreens = new();
    private readonly HashSet<int> miniJukeboxUnknownScreens = new();
    private readonly HashSet<int> islandScreens = new();
    private readonly HashSet<string> loggedDiagnostics = new(StringComparer.Ordinal);
    private bool windowInactive;
    private bool disposed;

    internal SanitySmapiAudioService(
        IModHelper helper,
        IMonitor monitor,
        string manifestId,
        SanitySystemLifecycleCoordinator lifecycle,
        SanitySmapiResourceService resources
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));

        var output = new SmapiSanityProcessAudioOutput(
            resources,
            Environment.CurrentManagedThreadId,
            LogDiagnostic
        );
        coordinator = new SanityProcessAudioCoordinator(output, LogDiagnostic);
        gameMusicCoordinator = new SanityGameMusicCoordinator(
            new SanityGameMusicRuntimeAdapter(manifestId, LogDiagnostic)
        );
        LogGameMusicCapability();

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.TierStateObserved += OnTierStateObserved;
        lifecycle.EventCoverageChanged += OnEventCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
    }

    internal SanityProcessAudioSnapshot Snapshot() => coordinator.Snapshot();

    internal SanityAudioClaimUpdateResult SubmitDarknessWarningClaim(
        SanityDarknessWarningClaim claim
    ) => coordinator.SubmitDarknessWarningClaim(claim);

    internal SanityAudioClaimUpdateResult RemoveDarknessWarningClaim(
        string playerKey,
        int screenId,
        string sessionId,
        string requestId
    ) => coordinator.RemoveDarknessWarningClaim(playerKey, screenId, sessionId, requestId);

    internal int RemoveDarknessWarningOwner(string playerKey) =>
        coordinator.RemoveDarknessWarningOwner(playerKey);

    internal int RemoveInvalidDarknessWarningScreens(Func<int, bool> isValidScreen) =>
        coordinator.RemoveInvalidDarknessWarningScreens(isValidScreen);

    internal void ClearDarknessWarningClaims() =>
        coordinator.ClearDarknessWarningClaims();

    internal void SetSpecialEventAudioAllowed(bool allowed) =>
        coordinator.SetSpecialEventAudioAllowed(allowed);

    internal SanityGameMusicSnapshot GameMusicSnapshot() =>
        gameMusicCoordinator.Snapshot();

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.TierStateObserved -= OnTierStateObserved;
        lifecycle.EventCoverageChanged -= OnEventCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        ownersByScreen.Clear();
        initializedClaims.Clear();
        dirtyClaims.Clear();
        localMenuScreens.Clear();
        processPausedScreens.Clear();
        miniJukeboxScreens.Clear();
        miniJukeboxUnknownScreens.Clear();
        islandScreens.Clear();
        gameMusicCoordinator.Dispose();
        coordinator.Dispose();
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
                MarkAllClaimsDirty();
                break;
            case SanityStateEventKind.OwnerInvalidated:
                RemoveOwner(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.TierEntered:
            case SanityStateEventKind.TierExited:
            case SanityStateEventKind.WorldCleanup:
                // TierStateObserved carries the final nested-tier snapshot. WorldCleanup is
                // intentionally ignored here because event suspension retains claim receipts.
                break;
            default:
                LogDiagnostic(
                    new SanityAudioDiagnostic(
                        null,
                        "audio.lifecycle.state-event-unsupported",
                        $"Unsupported Sanity state event: {stateEvent.Kind}."
                    )
                );
                break;
        }
    }

    private void OnTierStateObserved(SanityTierOwnerStateSnapshot snapshot)
    {
        if (disposed)
            return;

        var matched = false;
        foreach (var pair in ownersByScreen)
        {
            if (!string.Equals(pair.Value.PlayerKey, snapshot.PlayerKey, StringComparison.Ordinal))
                continue;

            matched = true;
            SubmitSnapshot(pair.Key, snapshot);
        }

        if (matched)
            return;

        // The first observation can precede the first screen-context update after load. The
        // initial screen binding will seed from the lifecycle snapshot exactly once.
    }

    private void OnEventCoverageChanged(bool active)
    {
        if (disposed)
            return;

        coordinator.SetEventSuspended(active);
        ReconcileGameMusic();
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
            RemoveScreen(Context.ScreenId);
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

        if (reason == SanityResourceReleaseReason.ContentInvalidated)
        {
            coordinator.InvalidateResources();
            return;
        }

        if (reason == SanityResourceReleaseReason.Dispose)
        {
            Dispose();
            return;
        }

        ClearAll();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(60))
            CleanupInvalidScreens();

        var hasBinding = false;
        var screenId = Context.ScreenId;
        OwnerBinding? binding = null;
        if (lifecycle.IsEnabled && !lifecycle.IsEventCoverageActive)
        {
            hasBinding = TryBindCurrentOwner(out screenId, out binding);
            if (!hasBinding)
                RemoveScreen(Context.ScreenId);
        }

        // Binding replacement clears the old per-screen sets, so resample playback context after
        // binding and before the initial claim is submitted.
        UpdateCurrentScreenPlaybackContext();
        if (!lifecycle.IsEnabled)
        {
            ReconcileGameMusic();
            coordinator.Tick();
            return;
        }
        if (!lifecycle.IsEventCoverageActive && hasBinding && binding is not null)
        {
            var key = new SanityAudioClaimKey(binding.PlayerKey, screenId);
            if (!initializedClaims.Contains(key) || dirtyClaims.Contains(key))
                SeedClaim(screenId, binding.PlayerKey);
        }

        ReconcileGameMusic();
        coordinator.Tick();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            ClearAll();
    }

    private void SeedClaim(int screenId, string playerKey)
    {
        var key = new SanityAudioClaimKey(playerKey, screenId);
        if (
            !lifecycle.TryGetTierState(playerKey, out var snapshot)
            || snapshot is null
        )
        {
            coordinator.RemoveClaim(playerKey, screenId);
            dirtyClaims.Remove(key);
            initializedClaims.Add(key);
            ReconcileGameMusic();
            return;
        }

        SubmitSnapshot(screenId, snapshot);
    }

    private void SubmitSnapshot(int screenId, SanityTierOwnerStateSnapshot snapshot)
    {
        var key = new SanityAudioClaimKey(snapshot.PlayerKey, screenId);
        dirtyClaims.Remove(key);
        initializedClaims.Add(key);
        if (!snapshot.IsAvailable || snapshot.Maximum <= 0d)
        {
            coordinator.RemoveClaim(snapshot.PlayerKey, screenId);
            ReconcileGameMusic();
            return;
        }

        var ambience = false;
        var whispers = false;
        var danger = false;
        foreach (var tierId in snapshot.ActiveTierIds)
        {
            switch (tierId)
            {
                case SanityTierIds.ShadowCreatures:
                    ambience = true;
                    break;
                case SanityTierIds.Whispers:
                    whispers = true;
                    break;
                case SanityTierIds.Danger:
                    danger = true;
                    break;
            }
        }

        coordinator.SubmitClaim(
            new SanityAudioOwnerClaim(
                snapshot.PlayerKey,
                screenId,
                snapshot.Revision,
                snapshot.Current / snapshot.Maximum,
                ambience,
                whispers,
                danger
            ),
            playbackPaused: localMenuScreens.Contains(screenId)
        );
        coordinator.SetClaimPlaybackPaused(
            snapshot.PlayerKey,
            screenId,
            localMenuScreens.Contains(screenId)
        );
        ReconcileGameMusic();
    }

    private bool TryBindCurrentOwner(out int screenId, out OwnerBinding binding)
    {
        screenId = Context.ScreenId;
        binding = null!;
        var player = Game1.player;
        var location = Game1.currentLocation;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return false;

        if (
            ownersByScreen.TryGetValue(screenId, out var cached)
            && string.Equals(cached.PlayerKey, playerKey, StringComparison.Ordinal)
            && ReferenceEquals(cached.LocationReference, location)
            && string.Equals(
                cached.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            binding = cached;
            return true;
        }

        RemoveScreen(screenId);
        binding = new OwnerBinding(
            playerKey,
            location,
            location.NameOrUniqueName
        );
        ownersByScreen[screenId] = binding;
        var key = new SanityAudioClaimKey(playerKey, screenId);
        initializedClaims.Remove(key);
        dirtyClaims.Add(key);
        return true;
    }

    private void UpdateCurrentScreenPlaybackContext()
    {
        var screenId = Context.ScreenId;
        if (!Context.HasScreenId(screenId))
            return;

        SetMembership(
            localMenuScreens,
            screenId,
            Game1.activeClickableMenu is not null
        );
        if (ownersByScreen.TryGetValue(screenId, out var binding))
        {
            coordinator.SetClaimPlaybackPaused(
                binding.PlayerKey,
                screenId,
                localMenuScreens.Contains(screenId)
            );
        }

        SetMembership(processPausedScreens, screenId, Game1.paused);
        windowInactive = Game1.game1 is null || !Game1.game1.IsActive;
        coordinator.SetProcessPaused(windowInactive || processPausedScreens.Count > 0);

        var location = Game1.currentLocation;
        SetMembership(islandScreens, screenId, IsIslandContext(location));
        if (!Context.IsWorldReady || location is null)
        {
            miniJukeboxScreens.Remove(screenId);
            miniJukeboxUnknownScreens.Remove(screenId);
            return;
        }

        try
        {
            SetMembership(
                miniJukeboxScreens,
                screenId,
                location.IsMiniJukeboxPlaying()
            );
            miniJukeboxUnknownScreens.Remove(screenId);
        }
        catch (Exception exception)
        {
            // Unknown is exempt: suppressing a requested jukebox track would be the unsafe side.
            miniJukeboxScreens.Remove(screenId);
            miniJukeboxUnknownScreens.Add(screenId);
            LogDiagnostic(
                new SanityAudioDiagnostic(
                    null,
                    "music.jukebox.state-unavailable",
                    $"Mini-jukebox state failed with {exception.GetType().Name}: {exception.Message}"
                )
            );
        }
    }

    private void ReconcileGameMusic()
    {
        if (disposed)
            return;

        gameMusicCoordinator.Reconcile(
            coordinator.MusicState(),
            miniJukeboxScreens.Count > 0 || miniJukeboxUnknownScreens.Count > 0,
            islandScreens.Count > 0
        );
        gameMusicCoordinator.Tick();
    }

    private static bool IsIslandContext(GameLocation? location)
    {
        return location is IslandLocation
            || location?.NameOrUniqueName?.Contains(
                "Island",
                StringComparison.OrdinalIgnoreCase
            ) == true;
    }

    private static void SetMembership(HashSet<int> set, int screenId, bool present)
    {
        if (present)
            set.Add(screenId);
        else
            set.Remove(screenId);
    }

    private void CleanupInvalidScreens()
    {
        coordinator.RemoveInvalidScreens(Context.HasScreenId);
        List<int>? invalid = null;
        foreach (var screenId in ownersByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;
            invalid ??= new List<int>();
            invalid.Add(screenId);
        }

        if (invalid is not null)
        {
            foreach (var screenId in invalid)
                RemoveScreenState(screenId);
        }

        localMenuScreens.RemoveWhere(screenId => !Context.HasScreenId(screenId));
        processPausedScreens.RemoveWhere(screenId => !Context.HasScreenId(screenId));
        miniJukeboxScreens.RemoveWhere(screenId => !Context.HasScreenId(screenId));
        miniJukeboxUnknownScreens.RemoveWhere(screenId => !Context.HasScreenId(screenId));
        islandScreens.RemoveWhere(screenId => !Context.HasScreenId(screenId));
        coordinator.SetProcessPaused(windowInactive || processPausedScreens.Count > 0);
        ReconcileGameMusic();
    }

    private void RemoveOwner(string playerKey)
    {
        coordinator.RemoveOwner(playerKey);
        List<int>? screens = null;
        foreach (var pair in ownersByScreen)
        {
            if (!string.Equals(pair.Value.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            screens ??= new List<int>();
            screens.Add(pair.Key);
        }

        if (screens is null)
            return;
        foreach (var screenId in screens)
            RemoveScreenState(screenId);
    }

    private void RemoveScreen(int screenId)
    {
        if (ownersByScreen.TryGetValue(screenId, out var binding))
            coordinator.RemoveClaim(binding.PlayerKey, screenId);
        RemoveScreenState(screenId);
    }

    private void RemoveScreenState(int screenId)
    {
        if (ownersByScreen.Remove(screenId, out var binding))
        {
            var key = new SanityAudioClaimKey(binding.PlayerKey, screenId);
            initializedClaims.Remove(key);
            dirtyClaims.Remove(key);
        }
        localMenuScreens.Remove(screenId);
        processPausedScreens.Remove(screenId);
        miniJukeboxScreens.Remove(screenId);
        miniJukeboxUnknownScreens.Remove(screenId);
        islandScreens.Remove(screenId);
        coordinator.SetProcessPaused(windowInactive || processPausedScreens.Count > 0);
        ReconcileGameMusic();
    }

    private void MarkAllClaimsDirty()
    {
        foreach (var pair in ownersByScreen)
            dirtyClaims.Add(new SanityAudioClaimKey(pair.Value.PlayerKey, pair.Key));
    }

    private void ClearAll()
    {
        ownersByScreen.Clear();
        initializedClaims.Clear();
        dirtyClaims.Clear();
        localMenuScreens.Clear();
        processPausedScreens.Clear();
        miniJukeboxScreens.Clear();
        miniJukeboxUnknownScreens.Clear();
        islandScreens.Clear();
        windowInactive = false;
        coordinator.ClearClaims();
        gameMusicCoordinator.Clear();
    }

    private void LogGameMusicCapability()
    {
        var capability = gameMusicCoordinator.Snapshot().Capability;
        monitor.Log(
            $"Sanity game music capability (capability={capability.Capability}, status={capability.Status}, reason={capability.Reason}, game-version={capability.TargetGameVersion}, target={capability.TargetSignature}, patch-owner={capability.PatchOwnerId}, patch-installed={capability.PatchInstalled}, fallback={capability.Fallback}).",
            capability.Status == SanityGameMusicCapabilityStatus.Available
                ? LogLevel.Debug
                : LogLevel.Error
        );
    }

    private void LogDiagnostic(SanityAudioDiagnostic diagnostic)
    {
        if (loggedDiagnostics.Count >= MaximumLoggedDiagnostics)
            return;

        var key = string.Concat(
            diagnostic.Lane?.ToString() ?? "process",
            "|",
            diagnostic.Code
        );
        if (!loggedDiagnostics.Add(key))
            return;

        monitor.Log(
            $"Sanity audio failed closed (lane={diagnostic.Lane?.ToString() ?? "process"}, code={diagnostic.Code}, reason={diagnostic.Reason}).",
            LogLevel.Error
        );
    }

    private sealed record OwnerBinding(
        string PlayerKey,
        GameLocation LocationReference,
        string LocationNameOrUniqueName
    );
}

/// <summary>
/// Owns the three frozen tier lanes plus one independent darkness-warning lane. Each lane borrows effects from the stage-03 loader and owns
/// only its currently-created SoundEffectInstance.
/// </summary>
internal sealed class SmapiSanityProcessAudioOutput : ISanityProcessAudioOutput
{
    private const string AmbienceCueSetId = "sanity.cue.ambience";
    private const string WhispersCueSetId = "sanity.cue.whispers";
    private const string DangerCueSetId = "sanity.cue.thresholds";
    private const string DarknessWarningCueSetId = "sanity.cue.darkness";
    private const string DarknessWarningCueId = "sanity.cue.darkness.warning";
    private const string ContinuousPlaybackMode = "RandomContinuousOneShotPool";
    private const string OneShotPlaybackMode = "OneShot";
    private const string CancelableOneShotPlaybackMode = "CancelableOneShot";

    private readonly SanitySmapiResourceService resources;
    private readonly CapturedSanityAudioThreadContext threadContext;
    private readonly ISanityAudioRandom random = new SystemSanityAudioRandom();
    private readonly Action<SanityAudioDiagnostic> diagnosticSink;
    private readonly HashSet<SanityAudioLaneKind> unavailableLanes = new();
    private SanityAudioInstanceLane? ambienceLane;
    private SanityAudioInstanceLane? whispersLane;
    private SanityAudioInstanceLane? dangerLane;
    private SanityAudioInstanceLane? darknessWarningLane;
    private bool ambienceDesired;
    private bool whispersDesired;
    private bool dangerDesired;
    private bool darknessWarningDesired;
    private bool darknessWarningTriggered;
    private bool paused;
    private bool suspended;
    private bool specialEventAudioAllowed;
    private bool disposed;
    private float ambientVolume = 1f;
    private float soundVolume = 1f;

    internal SmapiSanityProcessAudioOutput(
        SanitySmapiResourceService resources,
        int owningThreadId,
        Action<SanityAudioDiagnostic> diagnosticSink
    )
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        threadContext = new CapturedSanityAudioThreadContext(owningThreadId);
        this.diagnosticSink = diagnosticSink
            ?? throw new ArgumentNullException(nameof(diagnosticSink));
        RefreshVolume();
    }

    public int PhysicalInstanceCount =>
        (ambienceLane?.PhysicalInstanceCount ?? 0)
        + (whispersLane?.PhysicalInstanceCount ?? 0)
        + (dangerLane?.PhysicalInstanceCount ?? 0)
        + (darknessWarningLane?.PhysicalInstanceCount ?? 0);

    public void SetPoolActive(SanityAudioLaneKind lane, bool active)
    {
        if (disposed)
            return;

        switch (lane)
        {
            case SanityAudioLaneKind.Ambience:
                ambienceDesired = active;
                SetContinuousLane(ref ambienceLane, lane, active);
                break;
            case SanityAudioLaneKind.Whispers:
                whispersDesired = active;
                SetContinuousLane(ref whispersLane, lane, active);
                break;
            default:
                FailLane(
                    lane,
                    "audio.output.pool-lane-invalid",
                    "The danger lane cannot be activated as a continuous pool."
                );
                break;
        }
    }

    public void SetDangerActive(bool active)
    {
        if (disposed)
            return;

        dangerDesired = active;
        if (!active)
            dangerLane?.StopPlayback();
    }

    public void TriggerDanger()
    {
        if (disposed || suspended || paused || !dangerDesired)
            return;

        dangerLane ??= CreateLane(SanityAudioLaneKind.Danger);
        dangerLane?.TriggerOneShot(soundVolume);
    }

    public void SetDarknessWarningActive(bool active)
    {
        if (disposed)
            return;

        if (!active)
        {
            darknessWarningDesired = false;
            darknessWarningTriggered = false;
            darknessWarningLane?.StopPlayback();
            return;
        }

        if (!darknessWarningDesired)
        {
            darknessWarningDesired = true;
            darknessWarningTriggered = false;
        }
        EnsureDarknessWarning();
    }

    public void SetPaused(bool value)
    {
        if (disposed || paused == value)
            return;

        paused = value;
        if (value)
        {
            ambienceLane?.Pause();
            whispersLane?.Pause();
            dangerLane?.Pause();
            darknessWarningLane?.Pause();
            return;
        }

        ambienceLane?.Resume();
        whispersLane?.Resume();
        dangerLane?.Resume();
        darknessWarningLane?.Resume();
        if (!suspended)
        {
            EnsureDesiredPools(reapplyExisting: true);
            EnsureDarknessWarning();
        }
    }

    public void SetSuspended(bool value)
    {
        if (disposed || suspended == value)
            return;

        suspended = value;
        if (value)
        {
            ambienceLane?.StopPlayback();
            whispersLane?.StopPlayback();
            dangerLane?.StopPlayback();
            if (!specialEventAudioAllowed)
                darknessWarningLane?.StopPlayback();
            return;
        }

        if (!paused)
        {
            EnsureDesiredPools(reapplyExisting: true);
            EnsureDarknessWarning();
        }
    }

    public void SetSpecialEventAudioAllowed(bool allowed)
    {
        if (disposed || specialEventAudioAllowed == allowed)
            return;

        specialEventAudioAllowed = allowed;
        if (!allowed && suspended)
        {
            darknessWarningLane?.StopPlayback();
            return;
        }
        if (allowed && !paused)
            EnsureDarknessWarning();
    }

    public void InvalidateResources()
    {
        if (disposed)
            return;

        DisposeLanes();
        unavailableLanes.Clear();
    }

    public void Tick()
    {
        if (disposed)
            return;

        RefreshVolume();
        ambienceLane?.Tick();
        whispersLane?.Tick();
        dangerLane?.Tick();
        darknessWarningLane?.Tick();
        if (!paused && (!suspended || specialEventAudioAllowed))
        {
            if (!suspended)
                EnsureDesiredPools(reapplyExisting: false);
            EnsureDarknessWarning();
        }
    }

    public void Clear()
    {
        if (disposed)
            return;

        ambienceDesired = false;
        whispersDesired = false;
        dangerDesired = false;
        darknessWarningDesired = false;
        darknessWarningTriggered = false;
        paused = false;
        suspended = false;
        specialEventAudioAllowed = false;
        DisposeLanes();
        unavailableLanes.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Clear();
        disposed = true;
    }

    private void SetContinuousLane(
        ref SanityAudioInstanceLane? target,
        SanityAudioLaneKind lane,
        bool active
    )
    {
        if (!active)
        {
            target?.SetContinuousActive(false, VolumeFor(lane));
            return;
        }
        if (paused || suspended)
            return;

        target ??= CreateLane(lane);
        target?.SetContinuousActive(true, VolumeFor(lane));
    }

    private void EnsureDesiredPools(bool reapplyExisting)
    {
        if (ambienceDesired && (reapplyExisting || ambienceLane is null))
            SetContinuousLane(ref ambienceLane, SanityAudioLaneKind.Ambience, true);
        if (whispersDesired && (reapplyExisting || whispersLane is null))
            SetContinuousLane(ref whispersLane, SanityAudioLaneKind.Whispers, true);
    }

    private void EnsureDarknessWarning()
    {
        if (
            !darknessWarningDesired
            || darknessWarningTriggered
            || paused
            || (suspended && !specialEventAudioAllowed)
        )
        {
            return;
        }

        darknessWarningLane ??= CreateLane(SanityAudioLaneKind.DarknessWarning);
        if (darknessWarningLane is null)
            return;
        darknessWarningTriggered = true;
        darknessWarningLane.TriggerOneShot(soundVolume);
    }

    private SanityAudioInstanceLane? CreateLane(SanityAudioLaneKind lane)
    {
        if (unavailableLanes.Contains(lane))
            return null;

        var cueSetId = lane switch
        {
            SanityAudioLaneKind.Ambience => AmbienceCueSetId,
            SanityAudioLaneKind.Whispers => WhispersCueSetId,
            SanityAudioLaneKind.Danger => DangerCueSetId,
            SanityAudioLaneKind.DarknessWarning => DarknessWarningCueSetId,
            _ => string.Empty,
        };
        var playbackMode = lane switch
        {
            SanityAudioLaneKind.Danger => OneShotPlaybackMode,
            SanityAudioLaneKind.DarknessWarning => CancelableOneShotPlaybackMode,
            _ => ContinuousPlaybackMode,
        };
        var result = resources.LoadAudioCueSet(cueSetId);
        if (!TryBorrowEffects(lane, cueSetId, playbackMode, result, out var effects))
            return null;

        return new SanityAudioInstanceLane(
            lane,
            effects,
            continuous: lane is SanityAudioLaneKind.Ambience or SanityAudioLaneKind.Whispers,
            random,
            threadContext,
            diagnosticSink
        );
    }

    private bool TryBorrowEffects(
        SanityAudioLaneKind lane,
        string cueSetId,
        string playbackMode,
        SanitySlotResourceResult result,
        out IReadOnlyList<ISanityAudioEffect> effects
    )
    {
        effects = Array.Empty<ISanityAudioEffect>();
        var allowPlaceholder = lane == SanityAudioLaneKind.DarknessWarning;
        if (!result.Success || result.CueSet is null)
        {
            return FailLane(
                lane,
                result.Diagnostic.Code,
                result.Diagnostic.Reason
            );
        }
        if (result.Diagnostic.IsPlaceholder && !allowPlaceholder)
        {
            return FailLane(
                lane,
                "audio.output.placeholder-rejected",
                $"Cue set {cueSetId} is a Development placeholder and cannot drive gameplay audio."
            );
        }

        var runtime = result.CueSet;
        var definition = runtime.Definition;
        if (
            !string.Equals(definition.CueSetId, cueSetId, StringComparison.Ordinal)
            || definition.MaxConcurrentInstances != 1
            || definition.Cues.Count != 1
            || (
                lane == SanityAudioLaneKind.DarknessWarning
                && !string.Equals(
                    definition.LifecyclePolicy,
                    CancelableOneShotPlaybackMode,
                    StringComparison.Ordinal
                )
            )
        )
        {
            return FailLane(
                lane,
                "audio.output.cue-set-contract-invalid",
                $"Cue set {cueSetId} must contain one cue and declare MaxConcurrentInstances=1."
            );
        }

        var cue = definition.Cues[0];
        if (
            !cue.Enabled
            || !cue.RequiredForRelease
            || (cue.IsPlaceholder && !allowPlaceholder)
            || (
                lane == SanityAudioLaneKind.DarknessWarning
                && !string.Equals(cue.CueId, DarknessWarningCueId, StringComparison.Ordinal)
            )
            || !string.Equals(cue.PlaybackMode, playbackMode, StringComparison.Ordinal)
            || cue.Clips.Count == 0
            || cue.Clips.Count != runtime.PhysicalResources.Count
        )
        {
            return FailLane(
                lane,
                "audio.output.cue-contract-invalid",
                $"Cue set {cueSetId} does not match the frozen enabled/playback/clip contract."
            );
        }

        var borrowed = new ISanityAudioEffect[runtime.PhysicalResources.Count];
        for (var index = 0; index < runtime.PhysicalResources.Count; index++)
        {
            if (
                runtime.PhysicalResources[index]
                    is not XnaSanitySoundResource soundResource
            )
            {
                return FailLane(
                    lane,
                    "audio.output.effect-type-invalid",
                    $"Cue set {cueSetId} returned a non-SoundEffect physical resource."
                );
            }
            borrowed[index] = new XnaSanityAudioEffect(soundResource);
        }

        effects = borrowed;
        return true;
    }

    private bool FailLane(SanityAudioLaneKind lane, string code, string reason)
    {
        unavailableLanes.Add(lane);
        diagnosticSink(new SanityAudioDiagnostic(lane, code, reason));
        return false;
    }

    private void RefreshVolume()
    {
        float nextAmbient;
        float nextSound;
        try
        {
            nextAmbient = Math.Clamp(
                (float)Game1.options.ambientVolumeLevel,
                0f,
                1f
            );
        }
        catch (Exception exception)
        {
            nextAmbient = 0f;
            diagnosticSink(
                new SanityAudioDiagnostic(
                    SanityAudioLaneKind.Ambience,
                    "audio.output.ambient-volume-unavailable",
                    $"Player ambient volume was unavailable ({exception.GetType().Name}: {exception.Message})."
                )
            );
        }

        try
        {
            nextSound = Math.Clamp((float)Game1.options.soundVolumeLevel, 0f, 1f);
        }
        catch (Exception exception)
        {
            nextSound = 0f;
            diagnosticSink(
                new SanityAudioDiagnostic(
                    null,
                    "audio.output.sound-volume-unavailable",
                    $"Player sound volume was unavailable ({exception.GetType().Name}: {exception.Message})."
                )
            );
        }

        if (Math.Abs(nextAmbient - ambientVolume) >= 0.0001f)
        {
            ambientVolume = nextAmbient;
            ambienceLane?.SetVolume(ambientVolume);
        }
        if (Math.Abs(nextSound - soundVolume) < 0.0001f)
            return;

        soundVolume = nextSound;
        whispersLane?.SetVolume(soundVolume);
        dangerLane?.SetVolume(soundVolume);
        darknessWarningLane?.SetVolume(soundVolume);
    }

    private float VolumeFor(SanityAudioLaneKind lane)
    {
        return lane == SanityAudioLaneKind.Ambience
            ? ambientVolume
            : soundVolume;
    }

    private void DisposeLanes()
    {
        ambienceLane?.Dispose();
        whispersLane?.Dispose();
        dangerLane?.Dispose();
        darknessWarningLane?.Dispose();
        ambienceLane = null;
        whispersLane = null;
        dangerLane = null;
        darknessWarningLane = null;
    }
}

internal sealed class CapturedSanityAudioThreadContext : ISanityAudioThreadContext
{
    private readonly int owningThreadId;

    internal CapturedSanityAudioThreadContext(int owningThreadId)
    {
        this.owningThreadId = owningThreadId;
    }

    public bool IsOnOwningThread =>
        Environment.CurrentManagedThreadId == owningThreadId;
}

internal sealed class SystemSanityAudioRandom : ISanityAudioRandom
{
    public int NextIndex(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
            return -1;
        return Random.Shared.Next(exclusiveUpperBound);
    }
}

internal sealed class XnaSanityAudioEffect : ISanityAudioEffect
{
    private readonly XnaSanitySoundResource resource;

    internal XnaSanityAudioEffect(XnaSanitySoundResource resource)
    {
        this.resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    public string ResourceId => resource.Path;

    public ISanityAudioInstance CreateInstance()
    {
        return new XnaSanityAudioInstance(resource.SoundEffect.CreateInstance());
    }
}

internal sealed class XnaSanityAudioInstance : ISanityAudioInstance
{
    private readonly SoundEffectInstance instance;

    internal XnaSanityAudioInstance(SoundEffectInstance instance)
    {
        this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    public SanityAudioPlaybackState State => instance.State switch
    {
        SoundState.Playing => SanityAudioPlaybackState.Playing,
        SoundState.Paused => SanityAudioPlaybackState.Paused,
        _ => SanityAudioPlaybackState.Stopped,
    };

    public float Volume
    {
        get => instance.Volume;
        set => instance.Volume = value;
    }

    public void Play() => instance.Play();

    public void Pause() => instance.Pause();

    public void Resume() => instance.Resume();

    public void Stop() => instance.Stop();

    public void Dispose() => instance.Dispose();
}
