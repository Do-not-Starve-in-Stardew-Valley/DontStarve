#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Each species provides its own owner-local presenter. This seam has no world entity, audio,
/// light, damage, or hostile-shadow surface.
/// </summary>
internal interface IHarmlessProjectionWorldRenderer
{
    void Draw(
        SpriteBatch spriteBatch,
        HarmlessProjectionInstance instance,
        Vector2 screenPixel
    );
}

/// <summary>
/// The only Stardew/SMAPI adapter for owner-local harmless projections. Instances remain in the
/// mod-private index; this host never writes location critters, characters, temporary sprites, or
/// multiplayer snapshots.
/// </summary>
internal sealed class SmapiHarmlessProjectionHost
    : IHarmlessProjectionSpawnFactory,
        IShadowCreatureHarmlessProjectionSpawnFactory,
        IDisposable
{
    private const int MaximumLoggedFailures = 64;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanitySmapiResourceService resourceService;
    private readonly IHarmlessProjectionResourceProvider resourceProvider;
    private readonly HarmlessProjectionScheduler scheduler;
    private readonly ShadowCreatureHarmlessProjectionCoordinator shadowCoordinator;
    private readonly HarmlessProjectionSpawnPointSelector spawnPointSelector = new();
    private readonly IHarmlessProjectionRandom random = new SystemHarmlessProjectionRandom();
    private readonly Dictionary<string, IHarmlessProjectionWorldRenderer>
        renderersBySpecies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IHarmlessProjectionSpeciesBehavior>
        behaviorsBySpecies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IShadowCreatureHarmlessProjectionWorldRenderer>
        shadowRenderersBySpecies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IHarmlessProjectionSpawnGate>
        spawnGatesBySpecies = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HarmlessProjectionOwnerContext> ownerContextByScreen =
        new();
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private bool disposed;

    internal SmapiHarmlessProjectionHost(
        IModHelper helper,
        IMonitor monitor,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        SanitySmapiResourceService resourceService,
        IShadowProjectionConversionIntentSink conversionIntentSink
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.resourceService = resourceService
            ?? throw new ArgumentNullException(nameof(resourceService));
        ArgumentNullException.ThrowIfNull(conversionIntentSink);
        resourceProvider = resourceService;
        scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        shadowCoordinator = new ShadowCreatureHarmlessProjectionCoordinator(
            new ShadowCreatureHarmlessProjectionIndex(),
            lifecycle,
            conversionIntentSink,
            new SessionShadowProjectionCorrelationSource()
        );

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resourceService.VisualResourcesInvalidating += OnVisualResourcesInvalidating;
        resourceService.WorldResourcesReleasing += OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal int ActiveCount => scheduler.Index.Count + shadowCoordinator.Index.Count;

    /// <summary>
    /// This is the sole future registration seam. Keeping policy and renderer registration atomic
    /// prevents an active species from entering the scheduler without an owner-local draw path.
    /// </summary>
    internal bool RegisterSpecies(
        HarmlessProjectionPolicy policy,
        IHarmlessProjectionWorldRenderer renderer,
        out string reason
    )
    {
        return RegisterSpecies(policy, renderer, null, out reason);
    }

    internal bool RegisterSpecies(
        HarmlessProjectionPolicy policy,
        IHarmlessProjectionWorldRenderer renderer,
        IHarmlessProjectionSpeciesBehavior? behavior,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(renderer);
        if (disposed)
        {
            reason = "projection.host-disposed";
            return false;
        }
        if (renderersBySpecies.ContainsKey(policy.SpeciesId))
        {
            reason = "projection.species-duplicate";
            return false;
        }
        if (!scheduler.RegisterPolicy(policy, behavior, out reason))
            return false;

        renderersBySpecies.Add(policy.SpeciesId, renderer);
        if (behavior is not null)
        {
            behaviorsBySpecies.Add(policy.SpeciesId, behavior);
            if (behavior is IHarmlessProjectionSpawnGate spawnGate)
                spawnGatesBySpecies.Add(policy.SpeciesId, spawnGate);
        }
        reason = "projection.species-registered";
        return true;
    }

    /// <summary>
    /// Registers only one of the two stage-08 shared-pool appearances. This seam is deliberately
    /// separate from RegisterSpecies so ordinary policies cannot acquire a shadow permit.
    /// </summary>
    internal bool RegisterShadowCreatureSpecies(
        ShadowCreatureHarmlessProjectionPolicy policy,
        IShadowCreatureHarmlessProjectionWorldRenderer renderer,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(renderer);
        if (disposed)
        {
            reason = "projection.host-disposed";
            return false;
        }
        if (shadowRenderersBySpecies.ContainsKey(policy.SpeciesId))
        {
            reason = "shadow-projection.species-duplicate";
            return false;
        }
        if (!shadowCoordinator.RegisterPolicy(policy, out reason))
            return false;

        shadowRenderersBySpecies.Add(policy.SpeciesId, renderer);
        reason = "shadow-projection.species-registered";
        return true;
    }

    internal bool RequestSoftExit(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        HarmlessProjectionCleanupReason reason,
        IHarmlessProjectionExitHook? hook,
        out string resultReason
    )
    {
        return scheduler.RequestSoftExit(
            owner,
            speciesId,
            reason,
            hook,
            out resultReason
        );
    }

    public HarmlessProjectionSpawnResult TrySpawn(
        HarmlessProjectionSpawnRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (disposed)
            return HarmlessProjectionSpawnResult.Failed("projection.host-disposed");
        if (
            !TryGetCurrentOwner(
                out _,
                out var currentLocation,
                out var currentContext
            )
            || !request.Owner.Matches(currentContext)
        )
        {
            return HarmlessProjectionSpawnResult.Failed(
                "spawn.owner-context-invalid"
            );
        }

        if (
            spawnGatesBySpecies.TryGetValue(request.Policy.SpeciesId, out var spawnGate)
            && !spawnGate.CanSpawn(request, out var gateReason)
        )
        {
            return HarmlessProjectionSpawnResult.Failed(
                string.IsNullOrWhiteSpace(gateReason)
                    ? "spawn.species-gate-rejected"
                    : gateReason
            );
        }

        if (
            !TryLoadSpawnVisuals(
                request.Policy,
                out var resource,
                out var visualStateResources,
                out var visualFailure
            )
        )
        {
            return HarmlessProjectionSpawnResult.Failed(visualFailure);
        }

        var point = spawnPointSelector.Select(
            request.OwnerStandingWorldPixel,
            request.Policy,
            new SmapiHarmlessProjectionMapCapability(currentLocation),
            random
        );
        if (!point.Success || !point.WorldPixel.HasValue)
            return HarmlessProjectionSpawnResult.Failed(point.Reason);

        long expiresAtMinute;
        try
        {
            expiresAtMinute = checked(
                request.GameMinute + request.Policy.HardTtlMinutes
            );
        }
        catch (OverflowException)
        {
            return HarmlessProjectionSpawnResult.Failed(
                "spawn.ttl-deadline-overflow"
            );
        }

        return HarmlessProjectionSpawnResult.Spawned(
            new HarmlessProjectionInstance(
                request.Owner,
                request.Policy,
                point.WorldPixel.Value,
                request.GameMinute,
                expiresAtMinute,
                request.Policy.InitialStateId,
                resource,
                visualStateResources
            )
        );
    }

    public ShadowCreatureHarmlessProjectionSpawnResult TrySpawn(
        ShadowCreatureHarmlessProjectionSpawnRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (disposed)
        {
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                "projection.host-disposed"
            );
        }
        if (
            !TryGetCurrentOwner(out _, out var currentLocation, out var currentContext)
            || !request.Owner.Matches(currentContext)
        )
        {
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                "shadow-projection.owner-context-invalid"
            );
        }

        var occupancy = shadowCoordinator.Index.CountForOwner(
            request.Owner.PlayerKey
        );
        if (
            !ShadowCreatureProjectionPermitGate.TryAuthorize(
                request.Policy.SpeciesId,
                request.Owner.PlayerKey,
                request.GameMinute,
                occupancy,
                request.Permit,
                out var permitReason
            )
        )
        {
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(permitReason);
        }

        SanitySlotResourceResult resource;
        try
        {
            resource = resourceProvider.LoadVisualSlot(
                request.Policy.IdleVisualSlotId,
                frameIndex: 0
            );
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "sanity.resource.consumer-facade-unavailable",
                $"Shadow harmless projection resource facade failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                "sanity.resource.consumer-facade-unavailable"
            );
        }
        if (!request.Policy.TryValidateResource(resource, out var visualReason))
        {
            if (!resource.Success)
                LogUnavailableResource(request.Policy.IdleVisualSlotId, resource);
            else
            {
                LogFailureOnce(
                    string.Concat(
                        "shadow-resource-contract|",
                        request.Policy.IdleVisualSlotId
                    ),
                    $"Shadow harmless projection visual failed closed (profile={request.Policy.VisualProfileId}, reason={visualReason})."
                );
            }
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(visualReason);
        }

        var point = spawnPointSelector.Select(
            request.OwnerStandingWorldPixel,
            request.Policy,
            new SmapiHarmlessProjectionMapCapability(currentLocation),
            random
        );
        if (!point.Success || !point.WorldPixel.HasValue)
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(point.Reason);

        return ShadowCreatureHarmlessProjectionSpawnResult.Spawned(
            new ShadowCreatureHarmlessProjectionInstance(
                request.CorrelationId,
                request.Owner,
                request.Policy,
                point.WorldPixel.Value,
                request.GameMinute,
                resource
            )
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        scheduler.CleanupAll(HarmlessProjectionCleanupReason.WorldCleanup);
        shadowCoordinator.CleanupAll(
            HarmlessProjectionCleanupReason.WorldCleanup,
            clearOwnerPhases: true,
            clearConversionEvidence: true
        );
        ownerContextByScreen.Clear();
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged -= OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resourceService.VisualResourcesInvalidating -= OnVisualResourcesInvalidating;
        resourceService.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.Display.RenderedWorld -= OnRenderedWorld;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        var shadowTransition = shadowCoordinator.ApplyStateEvent(
            stateEvent,
            timeApi.Time
        );
        if (
            shadowTransition.Reason.EndsWith("invalid", StringComparison.Ordinal)
            || shadowTransition.Reason.EndsWith("unsupported", StringComparison.Ordinal)
        )
        {
            LogFailureOnce(
                string.Concat("shadow-state|", shadowTransition.Reason),
                $"Shadow harmless projection state transition failed closed ({shadowTransition.Reason})."
            );
        }

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.TierEntered:
                scheduler.SetTierActive(stateEvent.PlayerKey, stateEvent.TierId, true);
                break;
            case SanityStateEventKind.TierExited:
                scheduler.SetTierActive(stateEvent.PlayerKey, stateEvent.TierId, false);
                break;
            case SanityStateEventKind.SystemDisabled:
                scheduler.CleanupAll(HarmlessProjectionCleanupReason.ConfigDisabled);
                ownerContextByScreen.Clear();
                break;
            case SanityStateEventKind.OwnerInvalidated:
                scheduler.CleanupOwner(
                    stateEvent.PlayerKey,
                    HarmlessProjectionCleanupReason.OwnerInvalidated
                );
                RemoveCachedOwner(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.WorldCleanup:
                // SanitySystemLifecycleCoordinator publishes the precise session/world boundary
                // before this generic state-machine event, so the first cleanup reason is retained.
                break;
            case SanityStateEventKind.SystemEnabled:
                break;
            default:
                LogFailureOnce(
                    string.Concat("state-event|", stateEvent.Kind),
                    $"Harmless projection host received an unsupported Sanity state event ({stateEvent.Kind})."
                );
                break;
        }
    }

    private void OnEventOwnerCoverageChanged(
        SanityEventOwnerCoverageChanged change
    )
    {
        if (disposed || !change.Active)
            return;

        // Event coverage belongs to one participant screen. A split-screen event must not
        // erase projections owned by another local player.
        scheduler.CleanupOwner(
            change.Key.PlayerKey,
            HarmlessProjectionCleanupReason.EventOverride
        );
        shadowCoordinator.CleanupOwner(
            change.Key.PlayerKey,
            HarmlessProjectionCleanupReason.EventOverride,
            forgetOwnerPhase: true
        );
        ownerContextByScreen.Remove(change.Key.ScreenId);
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;

        if (boundary == SanityWorldBoundary.DayStarted)
        {
            scheduler.CleanupAll(
                HarmlessProjectionCleanupReason.DayStartedRecovery
            );
            shadowCoordinator.CleanupAll(
                HarmlessProjectionCleanupReason.DayStartedRecovery,
                clearOwnerPhases: true,
                clearConversionEvidence: true
            );
            ownerContextByScreen.Clear();
            return;
        }

        if (
            boundary == SanityWorldBoundary.Warp
            && TryGetCurrentOwner(out _, out _, out var owner)
        )
        {
            scheduler.CleanupOwner(
                owner.PlayerKey,
                HarmlessProjectionCleanupReason.OwnerWarped
            );
            shadowCoordinator.CleanupOwner(
                owner.PlayerKey,
                HarmlessProjectionCleanupReason.OwnerWarped,
                forgetOwnerPhase: true
            );
            ownerContextByScreen.Remove(owner.ScreenId);
        }
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (disposed)
            return;

        scheduler.CleanupAll(
            boundary == SanitySessionBoundary.ReturnedToTitle
                ? HarmlessProjectionCleanupReason.ReturnedTitle
                : HarmlessProjectionCleanupReason.WorldCleanup
        );
        shadowCoordinator.CleanupAll(
            boundary == SanitySessionBoundary.ReturnedToTitle
                ? HarmlessProjectionCleanupReason.ReturnedTitle
                : HarmlessProjectionCleanupReason.WorldCleanup,
            clearOwnerPhases: true,
            clearConversionEvidence: true
        );
        ownerContextByScreen.Clear();
    }

    private void OnVisualResourcesInvalidating()
    {
        if (disposed)
            return;

        scheduler.InvalidateResources();
        shadowCoordinator.CleanupAll(
            HarmlessProjectionCleanupReason.ResourceInvalidated,
            clearOwnerPhases: false,
            clearConversionEvidence: false
        );
        loggedFailures.Clear();
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (disposed)
            return;

        var cleanupReason = reason switch
        {
            SanityResourceReleaseReason.ReturnedToTitle =>
                HarmlessProjectionCleanupReason.ReturnedTitle,
            SanityResourceReleaseReason.SystemDisabled =>
                HarmlessProjectionCleanupReason.ConfigDisabled,
            SanityResourceReleaseReason.ContentInvalidated =>
                HarmlessProjectionCleanupReason.ResourceInvalidated,
            _ => HarmlessProjectionCleanupReason.WorldCleanup,
        };
        scheduler.CleanupAll(cleanupReason);
        var clearsSession = reason is SanityResourceReleaseReason.ReturnedToTitle
            or SanityResourceReleaseReason.WorldCleanup
            or SanityResourceReleaseReason.Dispose;
        shadowCoordinator.CleanupAll(
            cleanupReason,
            clearOwnerPhases: clearsSession
                || reason == SanityResourceReleaseReason.SystemDisabled,
            clearConversionEvidence: clearsSession
        );
        ownerContextByScreen.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(60))
            CleanupInvalidScreens();
        if (
            !TryGetCurrentOwner(
                out var player,
                out _,
                out var currentOwner
            )
        )
        {
            return;
        }

        if (
            scheduler.Index.CleanupMismatchedLocation(
                currentOwner,
                HarmlessProjectionCleanupReason.LocationInvalid
            ) > 0
        )
        {
            scheduler.RetryActivePolicies(currentOwner.PlayerKey);
        }
        shadowCoordinator.CleanupMismatchedLocation(
            currentOwner,
            HarmlessProjectionCleanupReason.LocationInvalid
        );
        var standingPixel = player.StandingPixel;
        var standingWorldPixel = new HarmlessProjectionWorldPoint(
            standingPixel.X,
            standingPixel.Y
        );
        var update = scheduler.UpdateOwner(
            currentOwner,
            standingWorldPixel,
            timeApi.Time,
            this
        );
        var elapsedMilliseconds = (int)Math.Min(
            int.MaxValue,
            Math.Max(0d, Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds)
        );
        var shadowUpdate = shadowCoordinator.UpdateOwner(
            currentOwner,
            standingWorldPixel,
            timeApi.Time,
            elapsedMilliseconds,
            this
        );
        if (shadowUpdate.Status == ShadowCreatureProjectionUpdateStatus.Unavailable)
        {
            LogFailureOnce(
                string.Concat("shadow-scheduler|", shadowUpdate.Reason),
                $"Shadow harmless projection scheduler failed closed ({shadowUpdate.Reason})."
            );
        }
        UpdateSpeciesBehaviors(
            currentOwner,
            standingWorldPixel,
            elapsedMilliseconds
        );
        if (
            update.Status == HarmlessProjectionSchedulerStatus.Unavailable
            || update.Reason.StartsWith("spawn.factory-threw-", StringComparison.Ordinal)
        )
        {
            LogFailureOnce(
                string.Concat("scheduler|", update.Reason),
                $"Harmless projection scheduler failed closed ({update.Reason})."
            );
        }
    }

    private void UpdateSpeciesBehaviors(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        int elapsedMilliseconds
    )
    {
        if (
            !scheduler.Index.TryGetContextInstances(owner, out var instances)
            || instances is null
        )
        {
            return;
        }

        List<KeyValuePair<string, HarmlessProjectionCleanupReason>>? cleanup = null;
        foreach (var instance in instances)
        {
            if (
                instance.IsCleanedUp
                || !behaviorsBySpecies.TryGetValue(instance.SpeciesId, out var behavior)
            )
            {
                continue;
            }

            HarmlessProjectionSpeciesUpdateResult result;
            try
            {
                result = behavior.Update(
                    instance,
                    owner,
                    ownerStandingWorldPixel,
                    elapsedMilliseconds
                );
            }
            catch (Exception exception)
            {
                result = new HarmlessProjectionSpeciesUpdateResult(
                    HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                    HarmlessProjectionCleanupReason.OwnerInvalidated,
                    "projection.behavior-threw"
                );
                LogFailureOnce(
                    string.Concat("behavior|", instance.SpeciesId),
                    $"Harmless projection behavior failed closed (species={instance.SpeciesId}, {exception.GetType().Name}: {exception.Message})."
                );
            }

            if (result.Status == HarmlessProjectionSpeciesUpdateStatus.Unavailable)
            {
                LogFailureOnce(
                    string.Concat("behavior-unavailable|", instance.SpeciesId, "|", result.Reason),
                    $"Harmless projection behavior became unavailable (species={instance.SpeciesId}, reason={result.Reason})."
                );
            }
            if (!result.ShouldCleanup)
                continue;

            cleanup ??=
                new List<KeyValuePair<string, HarmlessProjectionCleanupReason>>();
            cleanup.Add(
                new KeyValuePair<string, HarmlessProjectionCleanupReason>(
                    instance.SpeciesId,
                    result.CleanupReason!.Value
                )
            );
        }

        if (cleanup is null)
            return;
        foreach (var pair in cleanup)
        {
            scheduler.RequestSoftExit(
                owner,
                pair.Key,
                pair.Value,
                null,
                out _
            );
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (disposed || !TryGetCurrentOwner(out _, out _, out var owner))
            return;

        List<string>? failedSpecies = null;
        if (
            scheduler.Index.TryGetContextInstances(owner, out var instances)
            && instances is not null
        )
        {
            foreach (var instance in instances)
            {
                if (
                    instance.IsCleanedUp
                    || !renderersBySpecies.TryGetValue(
                        instance.SpeciesId,
                        out var renderer
                    )
                )
                {
                    continue;
                }

                var worldPixel = new Vector2(
                    (float)instance.SpawnWorldPixel.X,
                    (float)instance.SpawnWorldPixel.Y
                );
                var screenPixel = Game1.GlobalToLocal(Game1.viewport, worldPixel);
                try
                {
                    renderer.Draw(e.SpriteBatch, instance, screenPixel);
                }
                catch (Exception exception)
                {
                    failedSpecies ??= new List<string>();
                    failedSpecies.Add(instance.SpeciesId);
                    LogFailureOnce(
                        string.Concat("renderer|", instance.SpeciesId),
                        $"Harmless projection renderer failed closed (species={instance.SpeciesId}, {exception.GetType().Name}: {exception.Message})."
                    );
                }
            }
        }

        if (failedSpecies is not null)
        {
            foreach (var speciesId in failedSpecies)
            {
                scheduler.RequestSoftExit(
                    owner,
                    speciesId,
                    HarmlessProjectionCleanupReason.OwnerInvalidated,
                    null,
                    out _
                );
            }
        }

        if (
            !shadowCoordinator.Index.TryGetContextInstances(
                owner,
                out var shadowInstances
            )
            || shadowInstances is null
        )
        {
            return;
        }

        List<string>? failedCorrelations = null;
        foreach (var instance in shadowInstances)
        {
            if (
                instance.IsCleanedUp
                || !shadowRenderersBySpecies.TryGetValue(
                    instance.SpeciesId,
                    out var renderer
                )
            )
            {
                continue;
            }

            var worldPixel = new Vector2(
                (float)instance.SpawnWorldPixel.X,
                (float)instance.SpawnWorldPixel.Y
            );
            var screenPixel = Game1.GlobalToLocal(Game1.viewport, worldPixel);
            try
            {
                renderer.Draw(e.SpriteBatch, instance, screenPixel);
            }
            catch (Exception exception)
            {
                failedCorrelations ??= new List<string>();
                failedCorrelations.Add(instance.CorrelationId);
                LogFailureOnce(
                    string.Concat("shadow-renderer|", instance.SpeciesId),
                    $"Shadow harmless projection renderer failed closed (species={instance.SpeciesId}, {exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        if (failedCorrelations is null)
            return;
        foreach (var correlationId in failedCorrelations)
        {
            shadowCoordinator.Index.TryRemove(
                correlationId,
                HarmlessProjectionCleanupReason.OwnerInvalidated,
                out _
            );
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (disposed)
            return;

        scheduler.CleanupAll(HarmlessProjectionCleanupReason.DayEnding);
        shadowCoordinator.CleanupAll(
            HarmlessProjectionCleanupReason.DayEnding,
            clearOwnerPhases: true,
            clearConversionEvidence: true
        );
        ownerContextByScreen.Clear();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private bool TryGetCurrentOwner(
        out Farmer player,
        out GameLocation location,
        out HarmlessProjectionOwnerContext owner
    )
    {
        player = Game1.player;
        location = Game1.currentLocation;
        owner = null!;
        var screenId = Context.ScreenId;
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
            ownerContextByScreen.TryGetValue(screenId, out var cached)
            && string.Equals(cached.PlayerKey, playerKey, StringComparison.Ordinal)
            && ReferenceEquals(cached.LocationReference, location)
            && string.Equals(
                cached.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            owner = cached;
            return true;
        }

        owner = new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location,
            location.NameOrUniqueName
        );
        ownerContextByScreen[screenId] = owner;
        return true;
    }

    private void CleanupInvalidScreens()
    {
        List<KeyValuePair<int, HarmlessProjectionOwnerContext>>? invalidScreens =
            null;
        foreach (var pair in ownerContextByScreen)
        {
            if (Context.HasScreenId(pair.Key))
                continue;
            invalidScreens ??=
                new List<KeyValuePair<int, HarmlessProjectionOwnerContext>>();
            invalidScreens.Add(pair);
        }
        if (invalidScreens is null)
            return;

        scheduler.Index.CleanupInvalidScreens(
            Context.HasScreenId,
            HarmlessProjectionCleanupReason.ScreenInvalid
        );
        shadowCoordinator.CleanupInvalidScreens(
            Context.HasScreenId,
            HarmlessProjectionCleanupReason.ScreenInvalid
        );
        foreach (var invalid in invalidScreens)
        {
            var hasValidReplacement = false;
            foreach (var candidate in ownerContextByScreen)
            {
                if (
                    candidate.Key != invalid.Key
                    && Context.HasScreenId(candidate.Key)
                    && string.Equals(
                        candidate.Value.PlayerKey,
                        invalid.Value.PlayerKey,
                        StringComparison.Ordinal
                    )
                )
                {
                    hasValidReplacement = true;
                    break;
                }
            }

            if (hasValidReplacement)
                scheduler.RetryActivePolicies(invalid.Value.PlayerKey);
            else
            {
                scheduler.CleanupOwner(
                    invalid.Value.PlayerKey,
                    HarmlessProjectionCleanupReason.ScreenInvalid
                );
                shadowCoordinator.CleanupOwner(
                    invalid.Value.PlayerKey,
                    HarmlessProjectionCleanupReason.ScreenInvalid,
                    forgetOwnerPhase: true
                );
            }
            ownerContextByScreen.Remove(invalid.Key);
        }
    }

    private void RemoveCachedOwner(string playerKey)
    {
        List<int>? screens = null;
        foreach (var pair in ownerContextByScreen)
        {
            if (!string.Equals(pair.Value.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            screens ??= new List<int>();
            screens.Add(pair.Key);
        }
        if (screens is null)
            return;
        foreach (var screenId in screens)
            ownerContextByScreen.Remove(screenId);
    }

    private bool TryLoadSpawnVisuals(
        HarmlessProjectionPolicy policy,
        out SanitySlotResourceResult? initialResource,
        out IReadOnlyDictionary<string, SanitySlotResourceResult>? visualStateResources,
        out string reason
    )
    {
        initialResource = null;
        visualStateResources = null;
        if (policy.VisualStates.Count == 0)
        {
            SanitySlotResourceResult resource;
            try
            {
                resource = resourceProvider.LoadVisualSlot(policy.VisualSlotId);
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "sanity.resource.consumer-facade-unavailable",
                    $"Harmless projection resource facade failed closed ({exception.GetType().Name}: {exception.Message})."
                );
                reason = "sanity.resource.consumer-facade-unavailable";
                return false;
            }

            if (!resource.Success)
            {
                LogUnavailableResource(policy.VisualSlotId, resource);
                reason = resource.Diagnostic.Code;
                return false;
            }
            if (
                resource.VisualPreview is null
                || resource.PhysicalResource?.Kind != SanityPhysicalResourceKind.Texture
                || !resource.VisualPreview.OwnerLocalOnly
            )
            {
                LogFailureOnce(
                    string.Concat("resource-contract|", policy.VisualSlotId),
                    $"Harmless projection visual failed its owner-local texture contract (slot={policy.VisualSlotId})."
                );
                reason = "spawn.visual-resource-contract-invalid";
                return false;
            }

            initialResource = resource;
            reason = "spawn.visual-resource-ready";
            return true;
        }

        var resources = new Dictionary<string, SanitySlotResourceResult>(
            StringComparer.Ordinal
        );
        ISanityPhysicalResource? sharedTexture = null;
        foreach (var state in policy.VisualStates)
        {
            SanitySlotResourceResult resource;
            try
            {
                resource = resourceProvider.LoadVisualSlot(state.VisualSlotId, frameIndex: 0);
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "sanity.resource.consumer-facade-unavailable",
                    $"Harmless projection resource facade failed closed ({exception.GetType().Name}: {exception.Message})."
                );
                reason = "sanity.resource.consumer-facade-unavailable";
                return false;
            }
            if (!state.TryValidateResource(resource, out reason))
            {
                if (!resource.Success)
                    LogUnavailableResource(state.VisualSlotId, resource);
                else
                {
                    LogFailureOnce(
                        string.Concat("resource-state-contract|", state.VisualSlotId),
                        $"Harmless projection animation metadata failed closed (profile={policy.VisualProfileId}, state={state.StateId}, reason={reason})."
                    );
                }
                return false;
            }
            if (sharedTexture is not null && !ReferenceEquals(sharedTexture, resource.PhysicalResource))
            {
                reason = "spawn.visual-profile-texture-not-shared";
                LogFailureOnce(
                    string.Concat("resource-profile-cache|", policy.VisualProfileId),
                    $"Harmless projection profile did not resolve through one loader-owned texture (profile={policy.VisualProfileId})."
                );
                return false;
            }

            sharedTexture = resource.PhysicalResource;
            resources.Add(state.StateId, resource);
            if (string.Equals(state.StateId, policy.InitialStateId, StringComparison.Ordinal))
                initialResource = resource;
        }

        if (initialResource is null)
        {
            reason = "spawn.initial-visual-state-missing";
            return false;
        }
        visualStateResources = resources;
        reason = "spawn.visual-profile-ready";
        return true;
    }

    private void LogUnavailableResource(string slotId, SanitySlotResourceResult resource)
    {
        LogFailureOnce(
            string.Concat("resource|", resource.Diagnostic.Code),
            $"Harmless projection visual is unavailable (slot={slotId}, code={resource.Diagnostic.Code}, reason={resource.Diagnostic.Reason})."
        );
    }

    private void LogFailureOnce(string key, string message)
    {
        if (
            loggedFailures.Count >= MaximumLoggedFailures
            || !loggedFailures.Add(key)
        )
        {
            return;
        }
        monitor.Log(message, LogLevel.Error);
    }

    private sealed class SystemHarmlessProjectionRandom : IHarmlessProjectionRandom
    {
        private readonly Random random = new();

        public double NextUnitDouble()
        {
            return random.NextDouble();
        }
    }

    private sealed class SmapiHarmlessProjectionMapCapability
        : IHarmlessProjectionMapCapability
    {
        private readonly GameLocation location;

        internal SmapiHarmlessProjectionMapCapability(GameLocation location)
        {
            this.location = location
                ?? throw new ArgumentNullException(nameof(location));
        }

        public bool IsTileOnMap(int tileX, int tileY)
        {
            return location.isTileOnMap(new Vector2(tileX, tileY));
        }

        public bool IsTileLocationOpen(int tileX, int tileY)
        {
            return location.isTileLocationOpen(new Vector2(tileX, tileY));
        }

        public bool IsTilePassable(int tileX, int tileY)
        {
            return location.isTilePassable(new Vector2(tileX, tileY));
        }

        public bool IsAnchorVisible(HarmlessProjectionWorldPoint worldPixel)
        {
            if (!worldPixel.IsFinite)
                return false;
            var tileX = (int)Math.Floor(
                worldPixel.X / HarmlessProjectionSpawnPointSelector.TileSize
            );
            var tileY = (int)Math.Floor(
                worldPixel.Y / HarmlessProjectionSpawnPointSelector.TileSize
            );
            return location.isTileOnMap(new Vector2(tileX, tileY));
        }
    }
}
