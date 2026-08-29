using System;
using System.Collections.Generic;
using System.IO;
using DontStarve.Buff;
using DontStarve.Config;
using DontStarve.Debug;
using DontStarve.Display;
using DontStarve.Display.UIElements;
using DontStarve.Music;
using DontStarve.Player;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Events;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Player.Stats.Sanity.PassOut;
using DontStarve.Player.Stats.Sanity.Visual;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;
using DontStarve.Recipe;
using DontStarve.Resource;
using DontStarve.Resource.Sanity;
using DontStarve.Time;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve;

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
        private bool isLooped;
        public bool IsLooped { get => isLooped; set => isLooped = value; }
        public void Play() => instance.Play();
        public void Stop() => instance.Stop();
        public void Dispose() => instance.Dispose();
    }
}

internal class ModEntry : Mod, IDisposable
{
    // 全局只维护这一份内部分钟时间服务；Hunger、Sanity、Buff、Display 都从这里接收同一个时间源。
    private readonly TimeApi _timeApi = new();
    private ModConfig _config = new();
    private ConfigurationRuntime _configurationRuntime;
    private SanitySystemLifecycleCoordinator _sanityLifecycle;
    private SanitySmapiResourceService _sanityResources;
    private ShadowCreatureSmapiSfxService _shadowCreatureSfx;
    private SanitySmapiAudioService _sanityAudio;
    private TaggedHudMessageService _taggedHudMessages;
    private SanitySmapiEventService _sanityEvents;
    private SanitySmapiVisualService _sanityVisual;
    private SanityVignetteOverlayService _sanityVignette;
    private SmapiHarmlessProjectionHost _harmlessProjectionHost;
    private SmapiForageVisualProjectionService _forageVisualProjection;
    private SmapiForagePickupReplacementService _foragePickupReplacement;
    private SmapiVanillaSanityTooltipService _vanillaSanityTooltip;
    private SmapiDarkHandFireThiefService _darkHandFireThief;
    private SmapiDarkHandHarassmentService _darkHandHarassment;
    private SmapiDarkHandThiefService _darkHandThief;
    private SmapiDarkHandLeaseCoordinatorService _darkHandLeaseCoordinator;
    private SmapiHostileShadowHost _hostileShadowHost;
    private SmapiEnvironmentLightFinalVisibilitySampler _environmentLightSampler;
    private SmapiNaturalDarknessLightmapService _naturalDarknessLightmap;
    private SmapiNpcFlashlightService _npcFlashlights;
    private EnvironmentLightService _environmentLightService;
    private SmapiEnvironmentLightMultiplayerCoordinator
        _environmentLightMultiplayer;
    private SmapiDarknessAttackService _darknessAttack;
    private SmapiDarknessAttackResolutionService _darknessAttackResolution;
    private PassOutReasonLedger _passOutReasonLedger;
    private SmapiPassOutReasonService _passOutReasonService;
    private SmapiSanityTwoAmSpecialDeathService _sanityTwoAmPassOut;
    private SmapiEnvironmentLightDebugOverlay _environmentLightDebugOverlay;
    private bool _sanitySystemEnabled = true;
    private bool _sanityVisualEffectsEnabled = true;
    private bool _lowSanityScreenDistortionEnabled = true;
    private bool _sanityVignetteEnabled = true;
    private bool _naturalDarknessEnabled = true;

    private bool IsNaturalDarknessRuntimeEnabled =>
        _sanitySystemEnabled && _naturalDarknessEnabled;

    // schema 或配置根读坏时保持 fail-closed：允许本次用安全默认值运行，但不覆盖原文件。
    private bool _canWriteConfig;

    /// <summary>
    /// SMAPI 入口。初始化顺序有依赖关系，尤其是 TimeApi 必须先于所有时间消费者注册。
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        _config = ReadModConfig(helper);

        // 后续模块不要再从外部 mod 获取 MinuteTimeHelper；本项目的时间契约由内部 TimeApi 提供。
        _timeApi.Initialize(helper);
        TextureLoader.Initialize(helper);
        MusicManager.Initialize(helper, Monitor, ModManifest.UniqueID, _config.EnableDawnDuskMusic);
        // SMAPI 只允许在所有 mod 初始化完成后获取其它 mod API，GMCM 注册必须延后到 GameLaunched。
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        RecipeCategoryDisplayService.Initialize(helper, Monitor, ModManifest.UniqueID);
        BuffManager.Initialize(helper, _timeApi);
        _passOutReasonLedger = new PassOutReasonLedger();
        _sanityLifecycle = StatManager.Initialize(
            helper,
            _timeApi,
            Monitor,
            ModManifest.UniqueID,
            _canWriteConfig ? _configurationRuntime.Resolver : null,
            _passOutReasonLedger
        );
        DisplayManager.Initialize(helper, _timeApi, _sanityLifecycle);
        _taggedHudMessages = new TaggedHudMessageService(helper);
        // EatFood/Wearing have already loaded their unique data tables through StatManager.
        // The menu adapter consumes those exact values and never mutates state while drawing.
        _vanillaSanityTooltip = new SmapiVanillaSanityTooltipService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle
        );
        // Sanity.Init 已先完成 registry/tier/budget/cleanup 注册；这里才应用真实总开关。
        _sanityLifecycle.ApplyConfiguredState(_sanitySystemEnabled);
        _sanityEvents = new SanitySmapiEventService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanitySystemEnabled
        );
        // 任务族 03 阶段 06 只建立 manifest-driven 资源与诊断预览；具体玩法仍由下方
        // owner-local projection species 显式注册，资源 facade 本身不创建实体。
        _sanityResources = new SanitySmapiResourceService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanitySystemEnabled
        );
        // 影怪实体音效独立于 process-wide Sanity 音频；它借用同一资源服务的 cue 池，
        // 并由运行时宿主只提交状态事实。资源服务先构造，确保借用/释放顺序正确。
        _shadowCreatureSfx = new ShadowCreatureSmapiSfxService(
            helper,
            Monitor,
            _sanityResources
        );
        ActiveShadowCreatureSfx = _shadowCreatureSfx;
        // Physical XNA audio is process-shared. This single coordinator consumes owner/screen
        // tier claims and borrows effects from the already-created stage-03 resource owner.
        _sanityAudio = new SanitySmapiAudioService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources
        );
        // 视觉服务只消费既有 tier/effective/resource seam。版本受控 adapter 复用各
        // Game1.instanceId 的 screen/uiScreen 最终合成，idle 只持有可撤销负 token。
        _sanityVisual = new SanitySmapiVisualService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources,
            _sanityEvents.EffectiveSanityProvider,
            _sanityVisualEffectsEnabled,
            _lowSanityScreenDistortionEnabled
        );
        // 暗角是 HUD 覆盖层，独立于 world-only 低理智滤镜。即使滤镜关闭，它仍可依据
        // 专用配置显示 basic；只有滤镜开启且低于 15% 时才改为 insane 动画。
        _sanityVignette = new SanityVignetteOverlayService(
            helper,
            Monitor,
            _sanityLifecycle,
            _sanityEvents.EffectiveSanityProvider,
            _sanityVisualEffectsEnabled,
            _sanityVignetteEnabled
        );
        // Location 规则只在启动时读取一次；加载失败时 catalog 自身 fail closed，光照服务
        // 仍可提供有 reason 的 Dim/Fallback 诊断，而不会猜测地点或授权 PitchBlack。
        var environmentLightLocationRules = LoadEnvironmentLightLocationRules(helper);
        _environmentLightSampler = new SmapiEnvironmentLightFinalVisibilitySampler(
            helper,
            Monitor
        );
        IDarknessDamageModeResolver darknessDamageModeResolver = _canWriteConfig
            ? new TypedDarknessDamageModeResolver(_configurationRuntime.Resolver)
            : new UnavailableDarknessDamageModeResolver();
        IJunimoBlessingResolver junimoBlessingResolver = _canWriteConfig
            ? new TypedJunimoBlessingResolver(_configurationRuntime.Resolver)
            : new UnavailableJunimoBlessingResolver();
        var darknessAttackLocationAuthorization =
            new DarknessAttackLocationAuthorizationPolicy(() =>
            {
                var blessing = junimoBlessingResolver.Resolve();
                return new EnvironmentLightJunimoBlessingState(
                    blessing.HasValue,
                    blessing.Enabled
                );
            });
        // 地点计划通过原版 outdoorLight、ambientLight 或 MineShaft 的每层光色进入同一张
        // DrawLighting lightmap。祝尼魔保护与黑暗袭击复用同一地点语义，局部火把仍由原版随后绘制。
        _naturalDarknessLightmap = new SmapiNaturalDarknessLightmapService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            IsNaturalDarknessRuntimeEnabled,
            environmentLightLocationRules,
            darknessAttackLocationAuthorization
        );
        // NPC flashlight pairs consume only the already-owned natural-darkness scene state. They
        // do not perform one final-lightmap readback per NPC, so crowded maps keep the existing
        // owner-foot sampling budget intact.
        _npcFlashlights = new SmapiNpcFlashlightService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            IsNaturalDarknessRuntimeEnabled,
            _naturalDarknessLightmap.GetSceneStateForScreen
        );
        // 阶段 03 的无伤害光照服务先于需要消费其只读结果的物种创建；分类和 15-tick
        // cache 仍由该服务唯一拥有，DarkHand/Watcher 不建立第二套扫描或危险光照规则。
        _environmentLightService = new EnvironmentLightService(
            new SmapiEnvironmentLightSnapshotProvider(
                new KnownInactiveEnvironmentNightVisionProvider(),
                environmentLightLocationRules,
                _environmentLightSampler
            ),
            new EnvironmentLightClassifier(darknessAttackLocationAuthorization),
            () => Game1.ticks
        );
        _environmentLightMultiplayer =
            new SmapiEnvironmentLightMultiplayerCoordinator(
                helper,
                Monitor,
                ModManifest.UniqueID,
                ModManifest.Version.ToString(),
                _timeApi,
                _sanityLifecycle,
                _environmentLightService,
                environmentLightLocationRules,
                darknessAttackLocationAuthorization,
                _sanityAudio,
                _taggedHudMessages,
                () =>
                {
                    if (
                        !_canWriteConfig
                        || !_configurationRuntime.Fingerprint.IsAvailable
                    )
                    {
                        return EnvironmentLightConfigIdentity.Unavailable;
                    }
                    return new EnvironmentLightConfigIdentity(
                        _configurationRuntime.Registry.SchemaVersion,
                        _configurationRuntime.Fingerprint.FullHash
                    );
                }
            );
        // Countdown and settlement share the same typed mode resolver. A runtime mode change is
        // therefore observed before the old request can reach a different damage operation.
        _darknessAttack = new SmapiDarknessAttackService(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _environmentLightService,
            _sanityResources,
            _sanityAudio,
            _taggedHudMessages,
            darknessDamageModeResolver,
            _environmentLightMultiplayer,
            _environmentLightMultiplayer
        );
        _darknessAttackResolution = new SmapiDarknessAttackResolutionService(
            helper,
            Monitor,
            _sanityLifecycle,
            _darknessAttack,
            darknessDamageModeResolver,
            _sanityResources
        );
        // Ordinary committed sleep and confirmed kill-screen recovery have their own narrow
        // engine adapters. They only capture a reason or settle 50% of the current Sanity max;
        // the 2:00 prefix remains the sole owner of the special darkness flow.
        _passOutReasonService = new SmapiPassOutReasonService(
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _passOutReasonLedger
        );
        // The 2:00 adapter consumes the same typed mode, location result, lifecycle authority,
        // event overlay, audio coordinator and 10-03 Reduce receipt seam. It owns no duplicate
        // config, location, health-floor, Sanity, session or physical-audio authority.
        _sanityTwoAmPassOut = new SmapiSanityTwoAmSpecialDeathService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _passOutReasonService,
            darknessDamageModeResolver,
            junimoBlessingResolver,
            _environmentLightService,
            environmentLightLocationRules,
            _darknessAttackResolution,
            _sanityEvents,
            _sanityAudio
        );
        // 共享敌对态先注册为 owner-local 转换的唯一主机 sink。真实 Monster 只有在
        // NetCollection roundtrip、同版本 peer、共享视觉与 location 落点均成立时才生成。
        _hostileShadowHost = new SmapiHostileShadowHost(
            helper,
            Monitor,
            ModManifest.UniqueID,
            ModManifest.Version.ToString(),
            _timeApi,
            _sanityLifecycle,
            () =>
            {
                if (!_canWriteConfig || !_configurationRuntime.Fingerprint.IsAvailable)
                    return HostileShadowConfigFingerprintSnapshot.Unavailable;
                return new HostileShadowConfigFingerprintSnapshot(
                    _configurationRuntime.Registry.SchemaVersion,
                    _configurationRuntime.Fingerprint.FullHash
                );
            },
            _canWriteConfig ? _configurationRuntime.Resolver : null,
            _sanitySystemEnabled,
            _sanityResources
        );
        IDarkHandModeResolver darkHandModeResolver = _canWriteConfig
            ? new TypedConfigDarkHandModeResolver(_configurationRuntime.Resolver)
            : new UnavailableDarkHandModeResolver();
        // Fire and machine catalogs load once before the projection species is registered. The
        // transaction coordinator then binds the existing hostile-shadow lease authority and
        // exposes the only production owner-local target observer.
        _darkHandFireThief = new SmapiDarkHandFireThiefService(
            helper,
            Monitor,
            _sanityLifecycle
        );
        _darkHandHarassment = new SmapiDarkHandHarassmentService(
            helper,
            Monitor,
            _sanityLifecycle
        );
        _darkHandThief = new SmapiDarkHandThiefService(
            Monitor,
            _sanityLifecycle,
            _darkHandHarassment.Catalog,
            _darkHandHarassment.Evidence
        );
        _darkHandLeaseCoordinator = new SmapiDarkHandLeaseCoordinatorService(
            Monitor,
            _sanityLifecycle,
            _hostileShadowHost,
            darkHandModeResolver,
            () =>
            {
                if (
                    !_canWriteConfig
                    || !_configurationRuntime.Fingerprint.IsAvailable
                )
                {
                    return DarkHandRuntimeConfigIdentity.Unavailable;
                }
                return new DarkHandRuntimeConfigIdentity(
                    _configurationRuntime.Registry.SchemaVersion,
                    _configurationRuntime.Fingerprint.FullHash
                );
            },
            _darkHandFireThief,
            _darkHandHarassment,
            _darkHandThief
        );
        // 通用 host 仍只持有 mod-private owner-local 投影；具体物种必须显式注册。
        _harmlessProjectionHost = new SmapiHarmlessProjectionHost(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _sanityResources,
            _hostileShadowHost
        );
        // DIAG-20260809: 脱战绑定系统——危险影怪隐藏态与无害投影外观互相绑定。
        // 必须在两个 host 都构造完成后注入（投影 host 已持有 hostile host 引用）。
        if (
            !_hostileShadowHost.BindProjectionHost(
                _harmlessProjectionHost,
                out var bindingHostReason
            )
        )
        {
            Monitor.Log(
                $"Hostile shadow binding host injection failed ({bindingHostReason}).",
                LogLevel.Error
            );
        }
        var mrSkittsBehavior = new MrSkittsProjectionBehavior();
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                MrSkittsProjectionContract.CreatePolicy(),
                new MrSkittsProjectionRenderer(),
                mrSkittsBehavior,
                out var mrSkittsRegistrationReason
            )
        )
        {
            // 注册失败时 species 保持 disabled；不能回退旧 Critter 或共享世界集合。
            Monitor.Log(
                $"Mr.Skitts owner-local projection registration failed closed ({mrSkittsRegistrationReason}).",
                LogLevel.Error
            );
        }
        var darkHandBehavior = new DarkHandProjectionBehavior(
            darkHandModeResolver,
            new SmapiDarkHandEnvironmentLightProbe(_environmentLightService, _timeApi),
            _darkHandLeaseCoordinator
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                DarkHandProjectionContract.CreatePolicy(),
                new DarkHandProjectionRenderer(),
                darkHandBehavior,
                out var darkHandRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local DarkHand；不得回退旧 Critter 或触碰共享对象。
            Monitor.Log(
                $"DarkHand owner-local projection registration failed closed ({darkHandRegistrationReason}).",
                LogLevel.Error
            );
        }
        var darkWatcherBehavior = new DarkWatcherProjectionBehavior(
            new SmapiDarkWatcherEnvironmentLightProbe(
                _environmentLightService,
                _timeApi
            )
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                DarkWatcherProjectionContract.CreatePolicy(),
                new DarkWatcherProjectionRenderer(),
                darkWatcherBehavior,
                out var darkWatcherRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local Watcher；旧 Critter 不会成为隐式回退路径。
            Monitor.Log(
                $"DarkWatcher owner-local projection registration failed closed ({darkWatcherRegistrationReason}).",
                LogLevel.Error
            );
        }
        var eyesBehavior = new EyesProjectionBehavior(
            new SmapiEyesEnvironmentLightProbe(
                _environmentLightService,
                _timeApi
            )
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                EyesProjectionContract.CreatePolicy(),
                new EyesProjectionRenderer(),
                eyesBehavior,
                out var eyesRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local Eyes；资源或光照不可用不能触发旧 Critter 回退。
            Monitor.Log(
                $"Eyes owner-local projection registration failed closed ({eyesRegistrationReason}).",
                LogLevel.Error
            );
        }
        // The two 50% shadow appearances use the dedicated owner-shared permit lane. Keeping this
        // registration separate proves the ordinary four species cannot consume that permit.
        var shadowProjectionRenderer = new ShadowCreatureHarmlessProjectionRenderer();
        foreach (var policy in ShadowCreatureHarmlessProjectionCatalog.Policies)
        {
            if (
                _harmlessProjectionHost.RegisterShadowCreatureSpecies(
                    policy,
                    shadowProjectionRenderer,
                    out var shadowRegistrationReason
                )
            )
            {
                continue;
            }

            Monitor.Log(
                $"Shadow harmless projection registration failed closed (species={policy.SpeciesId}, reason={shadowRegistrationReason}).",
                LogLevel.Error
            );
        }
        // DIAG-20260804: 测试辅助控制台命令（ds_sanity/ds_spawn/ds_boxes）。
        // 在所有 species 注册完成后创建，保证 ds_spawn 可访问全部投影与影怪物种。
        // 全限定名避免与 StardewValley.DebugCommands 冲突。
        new DontStarve.Debug.DebugCommands(
            Monitor,
            _hostileShadowHost,
            _harmlessProjectionHost,
            _sanityLifecycle
        ).Register(helper);
        // DIAG-20260807: LookupAnything 显示欺骗——影怪攻击力显示跟随配置档位
        // （Lookup 读 DamageToFarmer 实例字段；该字段本体置 0 禁接触伤害）。
        DontStarve.Debug.LookupAnythingDisplayFake.TryPatch(Monitor);
        // DIAG-20260812: 守卫影怪（恐吓/脱战隐藏）受击跳字拦截——1.6.15 原版
        // damageMonster 在 takeDamage 返回 0 时仍飘出“0”伤害数字（守卫只挡扣血、
        // 挡不住跳字）。prefix 检测攻击范围：全部命中目标都是守卫影怪时直接跳过
        // 整个受击处理（不扣血、不跳字、无命中音效/反馈）。
        try
        {
            var combatHarmony = new HarmonyLib.Harmony(
                ModManifest.UniqueID + ".combat"
            );
            var damagePrefix = HarmonyLib.AccessTools.Method(
                typeof(HostileShadowDamageMonsterPatch),
                nameof(HostileShadowDamageMonsterPatch.Prefix)
            );
            // DIAG-20260812: 同样遍历全部 damageMonster 重载逐个 patch——固定
            // 4 参签名若与 1.6.15 实际签名不一致会导致 target not found、跳字 0
            // 拦截完全未生效（用户实测脱战无敌状态下仍飘 0）。
            var combatPatched = 0;
            foreach (
                var candidate in HarmonyLib.AccessTools.GetDeclaredMethods(
                    typeof(GameLocation)
                )
            )
            {
                if (
                    !string.Equals(
                        candidate.Name,
                        nameof(GameLocation.damageMonster),
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }
                combatHarmony.Patch(
                    candidate,
                    prefix: new HarmonyLib.HarmonyMethod(damagePrefix)
                );
                combatPatched++;
            }
            if (combatPatched == 0)
            {
                Monitor.Log(
                    "Hostile shadow damage patch target not found; guarded hit numbers remain.",
                    LogLevel.Warn
                );
            }
            else
            {
                Monitor.Log(
                    $"Hostile shadow damage patch applied to {combatPatched} overload(s).",
                    LogLevel.Trace
                );
            }
        }
        catch (Exception exception)
        {
            Monitor.Log(
                $"Hostile shadow damage patch failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }
        // The forage presentation remains private to the current owner screen. Its shared startup
        // catalog now drives both the exact ground-object draw projection and host pickup authority.
        _forageVisualProjection = new SmapiForageVisualProjectionService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources
        );
        // The same startup catalog drives projection and host authority. The exact checkAction
        // adapter still fails closed independently if its version/signature/IL owner gate drifts.
        _foragePickupReplacement = new SmapiForagePickupReplacementService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _forageVisualProjection.ReplacementCatalog
        );
        _environmentLightDebugOverlay = new SmapiEnvironmentLightDebugOverlay(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _environmentLightService,
            _darknessAttack,
            _darknessAttackResolution
        );
    }

    internal static ShadowCreatureSmapiSfxService? ActiveShadowCreatureSfx { get; private set; }

    void IDisposable.Dispose()
    {
        _shadowCreatureSfx?.Dispose();
        _shadowCreatureSfx = null;
        _taggedHudMessages?.Dispose();
        _taggedHudMessages = null;
        ActiveShadowCreatureSfx = null;
    }

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        var runtime = _canWriteConfig ? _configurationRuntime : null;
        ModConfigMenuRegistrar.Register(
            Helper,
            Monitor,
            ModManifest,
            runtime,
            saveCompleted: OnConfigMenuSaveCompleted
        );
        ModContentPatcherTokenRegistrar.Register(
            Helper,
            Monitor,
            ModManifest,
            runtime
        );
    }

    private ModConfig ReadModConfig(IModHelper helper)
    {
        try
        {
            var load = ConfigurationRuntime.Load(
                Path.Combine(
                    helper.DirectoryPath,
                    "Asset",
                    "Config",
                    "config-options.json"
                ),
                Path.Combine(helper.DirectoryPath, "config.json")
            );
            foreach (var diagnostic in load.Schema.Diagnostics)
            {
                Monitor.Log(
                    $"Config schema option was skipped ({diagnostic}).",
                    LogLevel.Warn
                );
            }

            if (!load.IsAvailable || load.Runtime is null)
            {
                _canWriteConfig = false;
                Monitor.Log(
                    $"Configuration is fail-closed ({load.Reason}); using safe defaults without writing config.json.",
                    LogLevel.Error
                );
                return new ModConfig();
            }

            _configurationRuntime = load.Runtime;
            _canWriteConfig = true;
            var config = new ModConfig();
            var music = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableDawnDuskMusic
            );
            if (music.HasValue)
            {
                config.LoadEnableDawnDuskMusic(music.Value);
            }
            else
            {
                Monitor.Log(
                    $"EnableDawnDuskMusic is unavailable ({music.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var sanitySystem = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableSanitySystem
            );
            if (sanitySystem.HasValue)
            {
                _sanitySystemEnabled = sanitySystem.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableSanitySystem is unavailable ({sanitySystem.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var sanityVisualEffects = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableSanityVisualEffects
            );
            if (sanityVisualEffects.HasValue)
            {
                _sanityVisualEffectsEnabled = sanityVisualEffects.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableSanityVisualEffects is unavailable ({sanityVisualEffects.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var lowSanityScreenDistortion = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableLowSanityScreenDistortion
            );
            if (lowSanityScreenDistortion.HasValue)
            {
                _lowSanityScreenDistortionEnabled = lowSanityScreenDistortion.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableLowSanityScreenDistortion is unavailable ({lowSanityScreenDistortion.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var sanityVignette = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableSanityVignette
            );
            if (sanityVignette.HasValue)
            {
                _sanityVignetteEnabled = sanityVignette.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableSanityVignette is unavailable ({sanityVignette.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var naturalDarkness = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableNaturalDarkness
            );
            if (naturalDarkness.HasValue)
            {
                _naturalDarknessEnabled = naturalDarkness.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableNaturalDarkness is unavailable ({naturalDarkness.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            LogFingerprint();
            return config;
        }
        catch (Exception ex)
        {
            _canWriteConfig = false;
            Monitor.Log(
                $"Failed to initialize configuration; using safe defaults without overwriting config.json. {ex.Message}",
                LogLevel.Error
            );
            return new ModConfig();
        }
    }

    private EnvironmentLightLocationRuleCatalog LoadEnvironmentLightLocationRules(
        IModHelper helper
    )
    {
        var path = Path.Combine(
            helper.DirectoryPath,
            EnvironmentLightLocationRuleCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        try
        {
            var load = EnvironmentLightLocationRuleCatalog.Load(
                File.ReadAllText(path)
            );
            if (!load.IsAvailable)
            {
                Monitor.Log(
                    $"Environment-light location rules are unavailable ({load.Reason}); classification will remain Dim/Fallback.",
                    LogLevel.Error
                );
            }
            else
            {
                Monitor.Log(
                    $"Environment-light location rules loaded (version={load.Catalog.ContractVersion}, rules={load.Catalog.Count}).",
                    LogLevel.Debug
                );
            }
            return load.Catalog;
        }
        catch (Exception ex)
        {
            const string reason = "environment-light.location-rules-file-read-failed";
            Monitor.Log(
                $"Environment-light location rules are unavailable ({reason}); classification will remain Dim/Fallback. {ex.Message}",
                LogLevel.Error
            );
            return EnvironmentLightLocationRuleCatalog.Unavailable(reason);
        }
    }

    private void OnConfigMenuSaveCompleted(FlatConfigSaveResult save)
    {
        if (!save.Success)
            return;

        if (
            string.Equals(
                save.Reason,
                "config.write-succeeded-temp-cleanup-failed",
                StringComparison.Ordinal
            )
        )
        {
            Monitor.Log(
                "config.json was saved, but its bounded temporary file could not be removed (config.write-succeeded-temp-cleanup-failed).",
                LogLevel.Warn
            );
        }

        ApplySavedConfiguration();
        LogFingerprint();
    }

    private void ApplySavedConfiguration()
    {
        var savedMusic = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableDawnDuskMusic
        );
        if (savedMusic.HasValue)
        {
            _config.LoadEnableDawnDuskMusic(savedMusic.Value);
            MusicManager.SetEnabled(savedMusic.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableDawnDuskMusic remains unavailable after GMCM save ({savedMusic.Reason}); the active music state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedSanitySystem = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableSanitySystem
        );
        if (savedSanitySystem.HasValue)
        {
            _sanitySystemEnabled = savedSanitySystem.Value;
            // Hostile entities must observe the explicit Disabled boundary before tier exits can
            // clean them under a less specific danger-exit reason.
            _hostileShadowHost?.SetEnabled(savedSanitySystem.Value);
            if (!savedSanitySystem.Value)
                _passOutReasonLedger?.Clear();
            _sanityLifecycle.ApplyConfiguredState(savedSanitySystem.Value);
            if (savedSanitySystem.Value)
                _sanityEvents?.SetEnabled(true);
            else
                _sanityEvents?.SetEnabled(false);
            _sanityResources?.SetEnabled(savedSanitySystem.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableSanitySystem remains unavailable after GMCM save ({savedSanitySystem.Reason}); the active Sanity system state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedNaturalDarkness = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableNaturalDarkness
        );
        if (savedNaturalDarkness.HasValue)
        {
            _naturalDarknessEnabled = savedNaturalDarkness.Value;
        }
        else
        {
            Monitor.Log(
                $"EnableNaturalDarkness remains unavailable after GMCM save ({savedNaturalDarkness.Reason}); the active natural darkness state was left unchanged.",
                LogLevel.Warn
            );
        }
        ApplyNaturalDarknessRuntimeState();

        var savedSanityVisualEffects = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableSanityVisualEffects
        );
        if (savedSanityVisualEffects.HasValue)
        {
            _sanityVisualEffectsEnabled = savedSanityVisualEffects.Value;
            _sanityVisual?.SetEnabled(savedSanityVisualEffects.Value);
            _sanityVignette?.SetLowSanityFilterEnabled(
                savedSanityVisualEffects.Value
            );
        }
        else
        {
            Monitor.Log(
                $"EnableSanityVisualEffects remains unavailable after GMCM save ({savedSanityVisualEffects.Reason}); the active visual state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedLowSanityScreenDistortion = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableLowSanityScreenDistortion
        );
        if (savedLowSanityScreenDistortion.HasValue)
        {
            _lowSanityScreenDistortionEnabled = savedLowSanityScreenDistortion.Value;
            _sanityVisual?.SetScreenDistortionEnabled(
                savedLowSanityScreenDistortion.Value
            );
        }
        else
        {
            Monitor.Log(
                $"EnableLowSanityScreenDistortion remains unavailable after GMCM save ({savedLowSanityScreenDistortion.Reason}); the active screen-distortion state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedSanityVignette = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableSanityVignette
        );
        if (savedSanityVignette.HasValue)
        {
            _sanityVignetteEnabled = savedSanityVignette.Value;
            _sanityVignette?.SetVignetteEnabled(savedSanityVignette.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableSanityVignette remains unavailable after GMCM save ({savedSanityVignette.Reason}); the active vignette state was left unchanged.",
                LogLevel.Warn
            );
        }
    }

    private void ApplyNaturalDarknessRuntimeState()
    {
        var enabled = IsNaturalDarknessRuntimeEnabled;
        _naturalDarknessLightmap?.SetEnabled(enabled);
        _npcFlashlights?.SetEnabled(enabled);
    }

    private void LogFingerprint()
    {
        var fingerprint = _configurationRuntime.Fingerprint;
        if (!fingerprint.IsAvailable)
        {
            Monitor.Log(
                $"Gameplay config fingerprint is {fingerprint.PublicIdentifier}.",
                LogLevel.Warn
            );
            return;
        }

        Monitor.Log(
            $"Gameplay config fingerprint: {fingerprint.PublicIdentifier}.",
            LogLevel.Debug
        );
        // 完整值仅进入诊断日志；它不会携带具体配置，也不参与同步、覆盖或拒绝联机。
        Monitor.Log(
            $"Gameplay config fingerprint full SHA-256: {fingerprint.FullHash}.",
            LogLevel.Trace
        );
    }
}
