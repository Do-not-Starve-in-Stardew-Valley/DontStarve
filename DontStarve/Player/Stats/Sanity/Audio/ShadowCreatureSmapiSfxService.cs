#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// SMAPI/XNA bridge for per-shadow voice lanes. The coordinator remains pure; this adapter owns
/// only SoundEffectInstance objects and borrows SoundEffect objects from the runtime resource
/// service. Hosts submit state facts, while this class owns ticking and resource invalidation.
/// </summary>
internal sealed class ShadowCreatureSmapiSfxService
    : DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxDiagnostics,
        IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly DontStarve.Resource.Sanity.SanitySmapiResourceService resources;
    private readonly DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCoordinator coordinator;
    private readonly Dictionary<
        DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey,
        (double X, double Y, string Location)
    > positions = new();
    private readonly HashSet<string> diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<
        DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies,
        IReadOnlyDictionary<
            DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCue,
            IReadOnlyList<DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxEffect>>
    > pools = new();
    private bool disposed;
    private Game? focusGame;
    private bool windowInactive;
    private bool focusResumePending;

    internal ShadowCreatureSmapiSfxService(
        IModHelper helper,
        IMonitor monitor,
        DontStarve.Resource.Sanity.SanitySmapiResourceService resources
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        coordinator = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCoordinator(
            GetPools,
            new DontStarve.Player.Stats.Sanity.Audio.SystemShadowCreatureSfxRandom(),
            this,
            OnPlaybackStarted
        );
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TryAttachWindowFocusEvents();
        SetWindowInactive(Game1.game1 is null || !Game1.game1.IsActive);
    }

    internal void ObserveHostile(
        string sessionId,
        long entityId,
        string assetBindingId,
        string stateId,
        double worldX,
        double worldY,
        int health,
        int maxHealth,
        string attackInstanceId,
        long revision,
        string locationName
    )
    {
        if (disposed)
            return;
        RefreshWindowFocus(allowResume: false);
        if (windowInactive || entityId <= 0)
            return;
        if (!TrySpecies(assetBindingId, out var species))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        positions[owner] = (worldX, worldY, locationName ?? string.Empty);
        var now = NowSeconds;
        var state = stateId switch
        {
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Chase
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Chase,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Taunt
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Taunt,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Attack
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Attack,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.HitTeleport
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.HitTeleport,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Dying
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Dying,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Despawn
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Despawn,
            DontStarve.Player.Stats.Sanity.HostileShadows.Runtime.HostileShadowStateIds.Spawn
                => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Spawning,
            _ => DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxObservedState.Idle,
        };
        coordinator.ObserveHostile(
            new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxHostileObservation(
                owner,
                species,
                state,
                Spatial(worldX, worldY, locationName),
                now,
                maxHealth <= 0 ? 0d : (double)health / maxHealth,
                attackInstanceId ?? string.Empty,
                revision
            )
        );
    }

    internal void ConfirmHostileDeath(
        string sessionId,
        long entityId,
        string assetBindingId,
        double worldX,
        double worldY,
        long revision,
        string locationName
    )
    {
        if (disposed)
            return;
        RefreshWindowFocus(allowResume: false);
        if (windowInactive || entityId <= 0 || !TrySpecies(assetBindingId, out var species))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        positions[owner] = (worldX, worldY, locationName ?? string.Empty);
        coordinator.NotifyConfirmedDeath(
            owner,
            species,
            Spatial(worldX, worldY, locationName),
            revision
        );
    }

    internal void NotifyHostileHit(
        string sessionId,
        long entityId,
        string assetBindingId,
        double worldX,
        double worldY,
        long revision,
        string locationName,
        DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxHitSource source
    )
    {
        if (disposed)
            return;
        RefreshWindowFocus(allowResume: false);
        if (windowInactive || entityId <= 0 || !TrySpecies(assetBindingId, out var species))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        positions[owner] = (worldX, worldY, locationName ?? string.Empty);
        coordinator.NotifyHostileHit(
            owner,
            species,
            Spatial(worldX, worldY, locationName),
            revision,
            lethal: false,
            source: source
        );
    }

    internal void ObserveHarmless(
        string correlationId,
        string speciesId,
        string stateName,
        double worldX,
        double worldY,
        long revision,
        string locationName
    )
    {
        if (disposed)
            return;
        RefreshWindowFocus(allowResume: false);
        if (
            windowInactive
            || string.IsNullOrWhiteSpace(correlationId)
            || !TrySpecies(speciesId, out var species)
        )
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            "projection",
            correlationId
        );
        positions[owner] = (worldX, worldY, locationName ?? string.Empty);
        var normalized = stateName ?? string.Empty;
        var state = normalized.IndexOf("Fading", StringComparison.OrdinalIgnoreCase) >= 0
            ? DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxProjectionState.FadingOut
            : normalized.IndexOf("Flee", StringComparison.OrdinalIgnoreCase) >= 0
                ? DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxProjectionState.Fleeing
                : normalized.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0
                    ? DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxProjectionState.Wander
                    : normalized.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0
                        ? DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxProjectionState.Spawning
                        : DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxProjectionState.Idle;
        coordinator.ObserveHarmless(
            new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxHarmlessObservation(
                owner,
                species,
                state,
                Spatial(worldX, worldY, locationName),
                NowSeconds,
                revision
            )
        );
    }

    internal void RemoveOwner(string sessionId, string entityId)
    {
        if (disposed || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(entityId))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId
        );
        positions.Remove(owner);
        coordinator.RemoveOwner(owner);
    }

    internal void RemoveProjectionOwner(string sessionId, string entityId)
    {
        if (disposed || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(entityId))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId
        );
        positions.Remove(owner);
        coordinator.RemoveProjectionOwner(owner);
    }

    internal void ForceRemoveOwner(string sessionId, string entityId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(entityId))
            return;
        var owner = new DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxOwnerKey(
            sessionId,
            entityId
        );
        positions.Remove(owner);
        coordinator.ForceRemoveOwner(owner);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        if (focusGame is not null)
        {
            focusGame.Deactivated -= OnWindowDeactivated;
            focusGame.Activated -= OnWindowActivated;
            focusGame = null;
        }
        coordinator.ForceRemoveAll();
        positions.Clear();
        pools.Clear();
        coordinator.Dispose();
    }

    void DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxDiagnostics.Report(
        string code,
        string reason
    )
    {
        if (!diagnostics.Add(code))
            return;
        monitor.Log($"Shadow creature SFX disabled ({code}: {reason}).", LogLevel.Warn);
    }

    private void OnPlaybackStarted(
        DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxPlaybackStarted playback
    )
    {
        monitor.Log(
            $"Shadow creature SFX started (owner={playback.Owner}, species={playback.Species}, cue={playback.Cue}, trigger={playback.DeduplicationKey}, volume={playback.Spatial.Volume}, pan={playback.Spatial.Pan}).",
            LogLevel.Info
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;
        TryAttachWindowFocusEvents();
        RefreshWindowFocus(allowResume: true);
        foreach (var pair in positions)
            coordinator.UpdateSpatial(pair.Key, Spatial(pair.Value.X, pair.Value.Y, pair.Value.Location));
        coordinator.Tick(NowSeconds, Game1.options?.soundVolumeLevel ?? 0f);
    }

    private void OnWorldResourcesReleasing(
        DontStarve.Resource.Sanity.SanityResourceReleaseReason reason
    )
    {
        coordinator.ForceRemoveAll();
        positions.Clear();
        pools.Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        OnWorldResourcesReleasing(DontStarve.Resource.Sanity.SanityResourceReleaseReason.ReturnedToTitle);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        focusResumePending = false;
        SetWindowInactive(true);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // MonoGame can raise Activated before the first update carrying the elapsed inactive
        // interval. Defer the resume until that owning-thread tick has the current game time.
        focusResumePending = true;
    }

    private void RefreshWindowFocus(bool allowResume)
    {
        if (Game1.game1 is null || !Game1.game1.IsActive)
        {
            focusResumePending = false;
            SetWindowInactive(true);
            return;
        }

        if (!allowResume && (windowInactive || focusResumePending))
            return;

        focusResumePending = false;
        SetWindowInactive(false);
    }

    private void SetWindowInactive(bool inactive)
    {
        if (disposed || windowInactive == inactive)
            return;

        windowInactive = inactive;
        // Focus only gates future voice creation. Existing SoundEffectInstance objects are left
        // untouched so they can finish normally; cadence due times resume from their remainder.
        coordinator.SetNewSoundsAllowed(!inactive, NowSeconds);
    }

    private void TryAttachWindowFocusEvents()
    {
        if (disposed || Game1.game1 is null)
            return;

        Game game;
        try
        {
            game = GameRunner.instance;
        }
        catch (Exception exception)
        {
            ReportFocusDiagnostic(
                "sfx.focus-window-unavailable",
                $"Game window focus events were unavailable ({exception.GetType().Name}: {exception.Message})."
            );
            return;
        }

        if (ReferenceEquals(focusGame, game))
            return;

        if (focusGame is not null)
        {
            focusGame.Deactivated -= OnWindowDeactivated;
            focusGame.Activated -= OnWindowActivated;
        }

        try
        {
            game.Deactivated += OnWindowDeactivated;
            game.Activated += OnWindowActivated;
            focusGame = game;
        }
        catch (Exception exception)
        {
            game.Deactivated -= OnWindowDeactivated;
            game.Activated -= OnWindowActivated;
            ReportFocusDiagnostic(
                "sfx.focus-window-subscribe-failed",
                $"Game window focus events could not be subscribed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void ReportFocusDiagnostic(string code, string reason)
    {
        if (!diagnostics.Add(code))
            return;
        monitor.Log($"Shadow creature SFX focus fallback active ({code}: {reason}).", LogLevel.Warn);
    }

    private IReadOnlyDictionary<
        DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCue,
        IReadOnlyList<DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxEffect>
    > GetPools(DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies species)
    {
        if (pools.TryGetValue(species, out var cached))
            return cached;
        var result = resources.LoadAudioCueSet(
            species == DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies.CreeperFear
                ? "sanity.cue.creeper-fear"
                : "sanity.cue.terrorbeak"
        );
        var map = new Dictionary<
            DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCue,
            IReadOnlyList<DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxEffect>
        >();
        if (!result.Success || result.CueSet is null)
        {
            ((DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxDiagnostics)this)
                .Report("cue-set:" + species, result.Diagnostic.Code);
            pools[species] = map;
            return map;
        }
        foreach (DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCue cue
            in Enum.GetValues(typeof(DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxCue)))
        {
            if (!result.CueSet.TryGetCueResources(
                    DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxPolicy.CueId(species, cue),
                    out var resourcesForCue
                ))
                continue;
            var effects = new List<DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxEffect>();
            foreach (var resource in resourcesForCue)
            {
                if (resource is DontStarve.Resource.Sanity.XnaSanitySoundResource sound)
                    effects.Add(new XnaShadowCreatureSfxEffect(sound));
            }
            if (effects.Count > 0)
                map[cue] = effects.AsReadOnly();
        }
        pools[species] = map;
        return map;
    }

    private DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxSpatial Spatial(
        double worldX,
        double worldY,
        string? locationName
    )
    {
        var volume = Game1.options?.soundVolumeLevel ?? 0f;
        var player = Game1.player;
        if (player is null || !Context.IsWorldReady ||
            (!string.IsNullOrWhiteSpace(locationName)
                && Game1.currentLocation is not null
                && !string.Equals(
                    Game1.currentLocation.NameOrUniqueName,
                    locationName,
                    StringComparison.Ordinal
                )))
            return DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxSpatial.FromDelta(
                double.PositiveInfinity,
                double.PositiveInfinity,
                volume
            );
        return DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxSpatial.FromDelta(
            (worldX - player.StandingPixel.X) / Game1.tileSize,
            (worldY - player.StandingPixel.Y) / Game1.tileSize,
            volume
        );
    }

    private static bool TrySpecies(
        string assetBindingId,
        out DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies species
    )
    {
        if (!string.IsNullOrWhiteSpace(assetBindingId)
            && assetBindingId.IndexOf("creeper", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            species = DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies.CreeperFear;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(assetBindingId)
            && assetBindingId.IndexOf("terror", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            species = DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSpecies.Terrorbeak;
            return true;
        }
        species = default;
        return false;
    }

    private double NowSeconds =>
        Game1.currentGameTime?.TotalGameTime.TotalSeconds
        ?? (Game1.ticks / 60d);

    private sealed class XnaShadowCreatureSfxEffect
        : DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxEffect
    {
        private readonly DontStarve.Resource.Sanity.XnaSanitySoundResource resource;

        internal XnaShadowCreatureSfxEffect(
            DontStarve.Resource.Sanity.XnaSanitySoundResource resource
        )
        {
            this.resource = resource;
        }

        public string ResourceId => resource.Path;

        public DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxInstance CreateInstance() =>
            new XnaShadowCreatureSfxInstance(resource.SoundEffect.CreateInstance());
    }

    private sealed class XnaShadowCreatureSfxInstance
        : DontStarve.Player.Stats.Sanity.Audio.IShadowCreatureSfxInstance
    {
        private readonly SoundEffectInstance instance;

        internal XnaShadowCreatureSfxInstance(SoundEffectInstance instance)
        {
            this.instance = instance;
        }

        public DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxPlaybackState State =>
            instance.State == SoundState.Stopped
                ? DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxPlaybackState.Stopped
                : DontStarve.Player.Stats.Sanity.Audio.ShadowCreatureSfxPlaybackState.Playing;
        public float Volume { get => instance.Volume; set => instance.Volume = value; }
        public float Pan { get => instance.Pan; set => instance.Pan = value; }
        // This MonoGame version exposes a repeat count; 255 is its infinite-loop sentinel.
        public bool IsLooped { get => instance.LoopCount != 0; set => instance.LoopCount = value ? 255u : 0u; }
        public void Play() => instance.Play();
        public void Stop() => instance.Stop();
        public void Dispose() => instance.Dispose();
    }
}
