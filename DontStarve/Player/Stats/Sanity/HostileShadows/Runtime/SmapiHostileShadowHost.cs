#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using DontStarve.Config;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// SMAPI lifecycle/context adapter around the pure host authority and the real location-owned
/// Monster runtime. Physical spawning remains fail-closed unless the startup net roundtrip probe,
/// exact peer-version gate, shared visual contract, and location materialization all succeed.
/// </summary>
internal sealed class SmapiHostileShadowHost
    : IShadowProjectionConversionIntentSink,
        IHostileShadowConversionRequestHandler,
        IHostileShadowAggroHintHandler,
        IHostileShadowAttackHitHandler,
        IHostileShadowPhysicalCapabilityHandler,
        IDisposable
{
    private const int MaximumLoggedReasons = 64;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly HostileShadowRuntimeProfileProvider profiles;
    private readonly HostileShadowAuthority authority;
    private readonly HostileShadowSessionLifecycleCoordinator sessionLifecycleCoordinator;
    private readonly SmapiHostileShadowWorldRuntime world;
    private readonly SmapiHostileShadowMultiplayerCoordinator multiplayer;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool worldMutationSuspended = true;
    private bool disposed;

    internal SmapiHostileShadowHost(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        string modVersion,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        Func<HostileShadowConfigFingerprintSnapshot> configFingerprintProvider,
        TypedConfigResolver? config,
        bool systemEnabled,
        SanitySmapiResourceService resources
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        profiles = LoadProfiles(helper, config);
        var physical = HostileShadowNetSerializationProbe.Probe();
        authority = new HostileShadowAuthority(
            new LifecycleBudgetAuthority(lifecycle),
            new SmapiEntityIdSource(helper.Multiplayer),
            physical
        );
        authority.SetEnabled(systemEnabled);
        sessionLifecycleCoordinator = new HostileShadowSessionLifecycleCoordinator();
        world = new SmapiHostileShadowWorldRuntime(
            helper,
            monitor,
            modId,
            modVersion,
            timeApi,
            lifecycle,
            authority,
            resources,
            physical
        );
        multiplayer = new SmapiHostileShadowMultiplayerCoordinator(
            helper,
            monitor,
            modId,
            timeApi,
            () => lifecycle.SessionId,
            sessionLifecycleCoordinator,
            configFingerprintProvider,
            UnavailableDarkHandLeaseTargetAuthority.Instance,
            authority,
            this,
            this,
            this,
            this
        );

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        timeApi.OnUpdate.Add(OnMinuteUpdate);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        LogOnce(
            physical.Reason,
            physical.IsAvailable ? LogLevel.Debug : LogLevel.Error
        );
    }

    internal HostileShadowAuthority Authority => authority;

    // Task 11 must consume task 07's one session/nonce/lease authority. Exposing this exact
    // instance prevents the world-interaction coordinator from silently creating a second lane.
    internal DarkHandInteractionLeaseAuthority DarkHandLeaseAuthority =>
        sessionLifecycleCoordinator.LeaseAuthority;

    internal bool BindDarkHandInteractionHandler(
        IDarkHandInteractionTransportHandler handler,
        out string reason
    )
    {
        if (disposed)
        {
            reason = "dark-hand.transport-host-disposed";
            return false;
        }
        return multiplayer.BindDarkHandInteractionHandler(handler, out reason);
    }

    internal bool RequestDarkHandInteractionLease(
        string targetId,
        string operationId,
        long observedTargetRevision,
        out string reason
    ) =>
        multiplayer.RequestDarkHandInteractionLease(
            targetId,
            operationId,
            observedTargetRevision,
            out reason
        );

    internal bool CommitDarkHandInteraction(
        string targetId,
        string operationId,
        out string reason
    ) =>
        multiplayer.CommitDarkHandInteraction(targetId, operationId, out reason);

    internal bool TryTakeDarkHandCommitResult(
        string targetId,
        string operationId,
        out DarkHandInteractionCommitResult? result
    ) =>
        multiplayer.TryTakeDarkHandCommitResult(
            targetId,
            operationId,
            out result
        );

    public ShadowProjectionConversionSubmissionResult Record(
        ShadowProjectionConversionIntent intent
    )
    {
        if (disposed)
        {
            return new ShadowProjectionConversionSubmissionResult(
                ShadowProjectionConversionSubmissionStatus.Failed,
                "shadow-conversion.host-disposed"
            );
        }
        return multiplayer.SubmitConversionIntent(intent);
    }

    public HostileShadowSpawnResult HandleConversionRequest(
        ShadowProjectionConversionRequest request,
        long senderPlayerId
    )
    {
        if (
            disposed
            || worldMutationSuspended
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !authority.IsHostSessionActive
        )
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                "hostile-shadow.conversion-host-unavailable"
            );
        }
        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        if (
            player is null
            || player.UniqueMultiplayerID != senderPlayerId
            || !string.Equals(
                request.PlayerKey,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                StringComparison.Ordinal
            )
            || !TryGetDangerTier(
                request.PlayerKey,
                out var terrorbeakActive
            )
        )
        {
            return Failure(
                HostileShadowSpawnStatus.Rejected,
                "hostile-shadow.conversion-player-or-tier-invalid"
            );
        }
        if (!TryResolveBindingForSpecies(request.SpeciesId, out var bindingId))
        {
            return Failure(
                HostileShadowSpawnStatus.Rejected,
                "hostile-shadow.conversion-species-invalid"
            );
        }
        var poolTier = terrorbeakActive
            ? SanityShadowPoolTier.Hostile10
            : SanityShadowPoolTier.Hostile15;
        if (
            !HostileShadowSpeciesBindingPolicy.TryResolveSpecies(
                bindingId,
                out var requestedSpecies,
                out var bindingReason
            )
        )
        {
            return Failure(
                HostileShadowSpawnStatus.Rejected,
                bindingReason
            );
        }
        if (
            !SanityShadowPoolEligibilityPolicy.TryAuthorize(
                poolTier,
                requestedSpecies,
                out var eligibilityReason
            )
        )
        {
            return Failure(HostileShadowSpawnStatus.Rejected, eligibilityReason);
        }

        return TrySpawn(
            request.CorrelationId,
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            player,
            bindingId,
            "hostile-shadow.spawn.owner-projection-conversion"
        );
    }

    public bool HandleAggroHint(
        ShadowAggroHintRequest request,
        long senderPlayerId,
        out string reason
    )
    {
        return world.TryRecordAggroHint(
            request,
            senderPlayerId,
            out reason
        );
    }

    public bool HandleAttackHit(
        ShadowAttackHitRequest request,
        long senderPlayerId,
        out HostileAttackReceipt receipt,
        out string reason
    )
    {
        return world.TryHandleAttackHit(
            request,
            senderPlayerId,
            out receipt,
            out reason
        );
    }

    public HostileShadowPhysicalEntityCapability GetLocalPhysicalCapability()
    {
        return world.GetLocalVisibilityCapability();
    }

    public bool HandlePhysicalCapabilityReport(
        ShadowPhysicalCapabilityReport report,
        long senderPlayerId,
        out string reason
    )
    {
        return world.RecordPeerVisibilityCapability(
            report,
            senderPlayerId,
            out reason
        );
    }

    internal int SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            var removed = authority.SetEnabled(false);
            multiplayer.OnSystemEnabledChanged(false);
            sessionLifecycleCoordinator.SetEnabled(false);
            return removed;
        }
        authority.SetEnabled(true);
        sessionLifecycleCoordinator.SetEnabled(true);
        multiplayer.OnSystemEnabledChanged(true);
        return 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged -= OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        timeApi.OnUpdate.Remove(OnMinuteUpdate);
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        multiplayer.Dispose();
        sessionLifecycleCoordinator.Dispose();
        authority.EndSession(HostileShadowCleanupReasonIds.Disposed);
        world.Dispose();
        loggedReasons.Clear();
        worldMutationSuspended = true;
    }

    private HostileShadowRuntimeProfileProvider LoadProfiles(
        IModHelper helper,
        TypedConfigResolver? config
    )
    {
        var load = new ShadowMonsterProfileLoader(
            new DirectoryShadowMonsterProfileFileSource(helper.DirectoryPath)
        ).LoadAtStartupOnce();
        foreach (var issue in load.Issues)
        {
            LogOnce(
                string.Concat(
                    issue.Code,
                    ":",
                    issue.FilePath,
                    ":",
                    issue.Key,
                    ":",
                    issue.Reason
                ),
                LogLevel.Error
            );
        }

        var facts = ResolveVersionFacts();
        var capability = load.Catalog is null
            ? new ShadowMonsterProfileAdapterCapability(
                ShadowMonsterProfileAdapterCapabilityStatus.Unavailable,
                ShadowMonsterProfileAdapterShape.Unknown,
                "hostile-shadow.profile-catalog-unavailable",
                null
            )
            : ShadowMonsterProfileVersionAdapterFactory.Resolve(
                load.Catalog.Schema,
                facts
            );
        LogOnce(
            capability.Reason,
            capability.IsAvailable ? LogLevel.Debug : LogLevel.Error
        );

        return new HostileShadowRuntimeProfileProvider(
            load.Catalog,
            capability,
            config,
            Game1.tileSize,
            load.Success
                ? capability.Reason
                : "hostile-shadow.profile-load-failed"
        );
    }

    private static ShadowMonsterProfileVersionFacts ResolveVersionFacts()
    {
        string monstersReturnType = string.Empty;
        MethodInfo? monstersMethod = null;
        foreach (var method in typeof(DataLoader).GetMethods(
            BindingFlags.Public | BindingFlags.Static
        ))
        {
            if (!string.Equals(method.Name, "Monsters", StringComparison.Ordinal))
                continue;
            if (monstersMethod is not null)
            {
                monstersMethod = null;
                monstersReturnType = "ambiguous";
                break;
            }
            monstersMethod = method;
        }
        if (monstersMethod is not null)
        {
            monstersReturnType =
                ShadowMonsterProfileVersionFactIds.DescribeMonstersReturnType(
                    monstersMethod.ReturnType
                );
        }

        var stardew17Type = typeof(Game1).Assembly.GetType(
            "StardewValley.GameData.Monsters.MonsterData",
            throwOnError: false
        );
        return new ShadowMonsterProfileVersionFacts(
            typeof(Game1).Assembly.GetName().Version?.ToString() ?? string.Empty,
            monstersReturnType,
            stardew17Type is not null,
            stardew17Type?.FullName
        );
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        loggedReasons.Clear();
        world.OnSessionStarted();
        var reason = string.Empty;
        if (
            !sessionLifecycleCoordinator.BeginSession(
                lifecycle.SessionId,
                lifecycle.IsEnabled,
                out reason
            )
        )
        {
            LogOnce(reason, LogLevel.Error);
            return;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host)
        {
            if (
                !authority.BeginHostSession(
                    lifecycle.SessionId,
                    lifecycle.IsEnabled,
                    out reason
                )
            )
            {
                LogOnce(reason, LogLevel.Error);
                return;
            }
            if (!world.BeginSettlementSession(lifecycle.SessionId, out reason))
            {
                LogOnce(reason, LogLevel.Error);
                authority.EndSession(HostileShadowCleanupReasonIds.WorldCleanup);
                return;
            }
        }
        else
        {
            authority.EndSession(HostileShadowCleanupReasonIds.WorldCleanup);
        }
        worldMutationSuspended = false;
        multiplayer.OnSessionStarted();
    }

    private void OnMinuteUpdate(long gameMinute)
    {
        if (
            disposed
            || worldMutationSuspended
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !authority.IsHostSessionActive
            || !authority.IsEnabled
        )
        {
            return;
        }

        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            if (!TryGetDangerTier(playerKey, out var terrorbeakActive))
                continue;

            var poolTier = terrorbeakActive
                ? SanityShadowPoolTier.Hostile10
                : SanityShadowPoolTier.Hostile15;
            if (
                !HostileShadowSpeciesBindingPolicy.TrySelectIntervalBinding(
                    poolTier,
                    out var bindingId,
                    out var selectionReason
                )
            )
            {
                LogOnce(selectionReason, LogLevel.Warn);
                continue;
            }
            var requestId = string.Concat(
                "hostile-shadow.interval.",
                authority.SessionId,
                ".",
                playerKey,
                ".",
                gameMinute.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
            var result = TrySpawn(
                requestId,
                HostileShadowSpawnOrigin.Interval,
                player,
                bindingId,
                "hostile-shadow.spawn.interval"
            );
            if (
                result.Status is HostileShadowSpawnStatus.Unavailable
                    or HostileShadowSpawnStatus.Rejected
            )
            {
                LogOnce(result.Reason, LogLevel.Warn);
            }
        }
    }

    private HostileShadowSpawnResult TrySpawn(
        string requestId,
        HostileShadowSpawnOrigin origin,
        Farmer player,
        string bindingId,
        string reason
    )
    {
        var location = player.currentLocation;
        if (
            location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                "hostile-shadow.owner-location-unavailable"
            );
        }
        var resolved = profiles.Resolve(bindingId);
        if (!resolved.Success || resolved.Profile is null)
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                resolved.Reason
            );
        }
        var capability = world.CurrentCapability;
        if (!capability.IsAvailable)
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                capability.Reason
            );
        }
        if (!world.TryPrepareBinding(bindingId, out var visualReason))
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                visualReason
            );
        }

        var result = authority.TrySpawn(
            new HostileShadowSpawnCommand(
                requestId,
                origin,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                location.NameOrUniqueName,
                player.Position.X,
                player.Position.Y,
                timeApi.Time,
                resolved.Profile,
                reason
            )
        );
        if (!result.Spawned || !result.EntityId.HasValue)
            return result;

        var materializationReason = string.Empty;
        if (
            !authority.TryGetEntity(result.EntityId.Value, out var state)
            || state is null
            || !world.TryMaterialize(
                state,
                resolved.Profile,
                location,
                timeApi.Time,
                out materializationReason
            )
        )
        {
            return authority.FailSpawnMaterialization(
                requestId,
                result.EntityId.Value,
                string.IsNullOrWhiteSpace(materializationReason)
                    ? "hostile-shadow.physical-materialization-failed"
                    : materializationReason
            );
        }
        return result;
    }

    private bool TryGetDangerTier(
        string playerKey,
        out bool terrorbeakActive
    )
    {
        terrorbeakActive = false;
        if (
            !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
        )
        {
            return false;
        }
        var dangerActive = false;
        foreach (var tierId in tier.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.Danger, StringComparison.Ordinal))
                dangerActive = true;
            else if (
                string.Equals(
                    tierId,
                    SanityTierIds.Terrorbeak,
                    StringComparison.Ordinal
                )
            )
            {
                terrorbeakActive = true;
            }
        }
        return dangerActive;
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        authority.ObserveStateEvent(stateEvent);
    }

    private void OnEventOwnerCoverageChanged(
        SanityEventOwnerCoverageChanged change
    )
    {
        authority.SetEventOverride(change.Key.PlayerKey, change.Active);
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        // A warp only changes the owner context; stage 04 exclusively owns off-map movement and
        // target lifecycle. DayEnding already clears session entities before DayStarted rebuilds.
        if (boundary == SanityWorldBoundary.Warp)
        {
            multiplayer.OnLocalWarp();
            return;
        }
        if (boundary == SanityWorldBoundary.DayStarted)
        {
            sessionLifecycleCoordinator.DayStarted();
            multiplayer.OnDayStarted();
            worldMutationSuspended = false;
        }
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        worldMutationSuspended = true;
        authority.EndSession(
            boundary == SanitySessionBoundary.ReturnedToTitle
                ? HostileShadowCleanupReasonIds.ReturnedToTitle
                : HostileShadowCleanupReasonIds.WorldCleanup
        );
        multiplayer.ClearSession();
        sessionLifecycleCoordinator.ClearSession();
        world.ClearSession();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        worldMutationSuspended = true;
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host)
        {
            authority.CleanupAll(HostileShadowCleanupReasonIds.DayEnding);
        }
        multiplayer.OnDayEnding();
        sessionLifecycleCoordinator.DayEnding();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private static bool TryResolveBindingForSpecies(
        string speciesId,
        out string bindingId
    )
    {
        if (
            string.Equals(
                speciesId,
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
                StringComparison.Ordinal
            )
        )
        {
            bindingId = ShadowMonsterAssetBindingIds.CreeperFear;
            return true;
        }
        if (
            string.Equals(
                speciesId,
                ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId,
                StringComparison.Ordinal
            )
        )
        {
            bindingId = ShadowMonsterAssetBindingIds.Terrorbeak;
            return true;
        }
        bindingId = string.Empty;
        return false;
    }

    private static HostileShadowSpawnResult Failure(
        HostileShadowSpawnStatus status,
        string reason
    )
    {
        return new HostileShadowSpawnResult(
            status,
            reason,
            null,
            null,
            0,
            0
        );
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
        monitor.Log($"Hostile shadow host: {reason}", level);
    }

    private sealed class LifecycleBudgetAuthority : IHostileShadowBudgetAuthority
    {
        private readonly SanitySystemLifecycleCoordinator lifecycle;

        internal LifecycleBudgetAuthority(
            SanitySystemLifecycleCoordinator lifecycle
        )
        {
            this.lifecycle = lifecycle;
        }

        public SanityShadowBudgetEvaluationResult Evaluate(
            string playerKey,
            long gameMinute,
            int occupancy,
            SanityShadowSpecies requestedSpecies
        )
        {
            return lifecycle.EvaluateHostileShadowBudget(
                playerKey,
                gameMinute,
                occupancy,
                requestedSpecies
            );
        }
    }

    private sealed class SmapiEntityIdSource : IHostileShadowEntityIdSource
    {
        private readonly IMultiplayerHelper multiplayer;

        internal SmapiEntityIdSource(IMultiplayerHelper multiplayer)
        {
            this.multiplayer = multiplayer;
        }

        public long Next()
        {
            return multiplayer.GetNewID();
        }
    }
}
