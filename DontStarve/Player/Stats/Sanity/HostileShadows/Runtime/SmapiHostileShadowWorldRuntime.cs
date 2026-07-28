#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Owns the host's real GameLocation.characters instances and their bounded targeting loop. The
/// location reference is frozen at spawn: owner warp never migrates or teleports the entity.
/// </summary>
internal sealed class SmapiHostileShadowWorldRuntime : IDisposable
{
    private const long MinutesPerGameHour = 60;
    private const double FixedUpdateSeconds =
        1d / HostileShadowTargetingLimits.NominalTicksPerSecond;
    private const int MaximumLoggedReasons = 64;

    private sealed class PhysicalEntry
    {
        private string targetPlayerKey = string.Empty;

        internal PhysicalEntry(
            HostileShadowMonster monster,
            GameLocation location,
            ShadowMonsterRuntimeProfile profile,
            long spawnGameMinute,
            HostileAttackRuntimeDefinition attackDefinition,
            HostileAttackStateMachine attackState,
            HostileShadowMovementPresentationState? movementPresentation,
            HostileShadowHitResponseController hitResponse
        )
        {
            Monster = monster;
            Location = location;
            Profile = profile;
            SpawnGameMinute = spawnGameMinute;
            AttackDefinition = attackDefinition;
            AttackState = attackState;
            MovementPresentation = movementPresentation;
            HitResponse = hitResponse;
            AppliedMovementFacingId = movementPresentation is null
                ? string.Empty
                : HostileShadowFacingIds.Down;
            AppliedMovementFrameIndex = movementPresentation is null ? -1 : 0;
        }

        internal HostileShadowMonster Monster { get; }
        internal GameLocation Location { get; }
        internal ShadowMonsterRuntimeProfile Profile { get; }
        internal long SpawnGameMinute { get; }
        internal HostileAttackRuntimeDefinition AttackDefinition { get; }
        internal HostileAttackStateMachine AttackState { get; }
        internal HostileShadowMovementPresentationState? MovementPresentation { get; }
        internal HostileShadowHitResponseController HitResponse { get; }
        // These values mirror the initial materialization writes. The 60 Hz loop compares raw
        // state first so numeric formatting and NetDictionary writes occur only on transitions.
        internal string AppliedStateId { get; set; } = HostileShadowStateIds.Spawn;
        internal string AppliedAttackInstanceId { get; set; } = string.Empty;
        internal long AppliedAttackInstanceRevision { get; set; }
        internal int AppliedAttackFrameNumber { get; set; }
        internal string AppliedMovementFacingId { get; set; }
        internal int AppliedMovementFrameIndex { get; set; }
        internal string RecentAttackerPlayerKey { get; set; } = string.Empty;
        internal string TargetPlayerKey
        {
            get => targetPlayerKey;
            set
            {
                if (string.Equals(targetPlayerKey, value, StringComparison.Ordinal))
                    return;
                targetPlayerKey = value;
                TargetPlayerKeyIsCanonical = SanityPlayerKey.IsCanonical(value);
            }
        }
        internal bool TargetPlayerKeyIsCanonical { get; private set; }
        internal bool PendingLethalDamage { get; set; }
        internal string PendingLethalAttackerPlayerKey { get; set; } = string.Empty;
    }

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly HostileShadowAuthority authority;
    private readonly SanitySmapiResourceService resourceService;
    private readonly HostileShadowMonsterRenderer renderer;
    private readonly SmapiHostileAttackCombatService attackCombat;
    private readonly Func<HostileShadowMonster, int, Farmer?, int> incomingHitHandler;
    private readonly HostileShadowPeerVisibilityGate peerGate;
    private readonly HostileShadowSettlementService settlements;
    private readonly HostileShadowLocationPlayerIndex playerIndex = new();
    private readonly Dictionary<long, PhysicalEntry> entries = new();
    private readonly List<long> orderedEntityIds = new(
        HostileShadowAuthority.MaximumEntities
    );
    private readonly long[] entityIterationBuffer = new long[
        HostileShadowAuthority.MaximumEntities
    ];
    private readonly HashSet<long> pendingLethalEntityIds = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private readonly HostileShadowLifecycleReceiptStore lifecycleReceipts = new();
    private readonly HostileShadowPhysicalEntityCapability serializationCapability;
    private bool disposed;

    internal SmapiHostileShadowWorldRuntime(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        string modVersion,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        HostileShadowAuthority authority,
        SanitySmapiResourceService resources,
        HostileShadowPhysicalEntityCapability serializationCapability
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        resourceService = resources
            ?? throw new ArgumentNullException(nameof(resources));
        var settlementEffects = new SmapiHostileShadowSettlementEffects(
            lifecycle ?? throw new ArgumentNullException(nameof(lifecycle))
        );
        settlements = new HostileShadowSettlementService(
            new StableHostileShadowSettlementRandom(),
            settlementEffects,
            settlementEffects,
            settlementEffects
        );

        renderer = new HostileShadowMonsterRenderer(resources);
        attackCombat = new SmapiHostileAttackCombatService(
            authority,
            LogOnce
        );
        incomingHitHandler = HandleIncomingHit;
        HostileShadowMonsterVisualBridge.Configure(renderer);
        HostileShadowMonsterHitBridge.Configure(incomingHitHandler);
        this.serializationCapability = serializationCapability;
        peerGate = new HostileShadowPeerVisibilityGate(
            helper,
            monitor,
            modId,
            modVersion,
            authority,
            serializationCapability
        );

        authority.DeltaProduced += OnAuthorityDelta;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
    }

    internal HostileShadowPhysicalEntityCapability SerializationCapability =>
        serializationCapability;

    internal HostileShadowPhysicalEntityCapability CurrentCapability =>
        peerGate.CurrentCapability;

    internal int Count => entries.Count;

    internal void OnSessionStarted()
    {
        if (disposed)
            return;
        loggedReasons.Clear();
        settlements.ClearSession();
        RemoveAllPhysical();
        pendingLethalEntityIds.Clear();
        lifecycleReceipts.Clear();
        if (Game1.IsMasterGame)
            RemoveOrphansAtWorldBoundary();
        peerGate.RefreshForSession();

        // Clients never construct a second projection. They only prime the loader-owned frames
        // that the network-created Monster.draw path will borrow.
        if (!TryPrimeSharedVisuals(out var reason))
            LogOnce(reason, LogLevel.Warn);
    }

    internal bool BeginSettlementSession(string sessionId, out string reason)
    {
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        return settlements.BeginSession(sessionId, out reason);
    }

    internal HostileShadowPhysicalEntityCapability GetLocalVisibilityCapability()
    {
        if (!serializationCapability.IsAvailable)
            return serializationCapability;
        return TryPrimeSharedVisuals(out var reason)
            ? new HostileShadowPhysicalEntityCapability(
                HostileShadowPhysicalEntityCapabilityStatus.Available,
                "hostile-shadow.local-shared-visibility-capability-ready"
            )
            : new HostileShadowPhysicalEntityCapability(
                HostileShadowPhysicalEntityCapabilityStatus.Unavailable,
                reason
            );
    }

    internal bool RecordPeerVisibilityCapability(
        ShadowPhysicalCapabilityReport report,
        long senderPlayerId,
        out string reason
    )
    {
        return peerGate.Record(report, senderPlayerId, out reason);
    }

    internal bool TryPrepareBinding(string assetBindingId, out string reason)
    {
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        var capability = CurrentCapability;
        if (!capability.IsAvailable)
        {
            reason = capability.Reason;
            return false;
        }
        return renderer.TryPrepareBinding(assetBindingId, out reason);
    }

    internal bool TryMaterialize(
        ShadowStateSnapshot state,
        ShadowMonsterRuntimeProfile profile,
        GameLocation location,
        long spawnGameMinute,
        out string reason
    )
    {
        reason = string.Empty;
        if (
            disposed
            || !Game1.IsMasterGame
            || state is null
            || profile is null
            || location is null
            || spawnGameMinute < 0
            || entries.ContainsKey(state.EntityId)
            || !string.Equals(
                state.LocationId,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
            || !resourceService.TryGetHostileAttackMetadata(
                profile.AssetBindingId,
                out var attackMetadata,
                out reason
            )
            || !HostileAttackRuntimeDefinition.TryCreate(
                attackMetadata,
                profile,
                out var attackDefinition,
                out reason
            )
            || attackDefinition is null
            || !HostileAttackTransitionPolicyFactory.TryCreate(
                profile,
                authority.SessionId,
                state.EntityId,
                out var transitionPolicy,
                out reason
            )
            || transitionPolicy is null
            || !TryPrepareBinding(profile.AssetBindingId, out reason)
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.physical-materialization-input-invalid";
            return false;
        }

        HostileShadowCombatImmunityPolicy? combatImmunity = null;
        if (
            string.Equals(
                profile.AssetBindingId,
                ShadowMonsterAssetBindingIds.CreeperFear,
                StringComparison.Ordinal
            )
        )
        {
            if (
                !CreeperFearCombatImmunityPolicy.TryCreate(
                    profile,
                    out var creeperFearImmunity,
                    out reason
                )
            )
            {
                return false;
            }
            combatImmunity = creeperFearImmunity;
        }
        else if (
            string.Equals(
                profile.AssetBindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            )
        )
        {
            if (
                !TerrorbeakCombatImmunityPolicy.TryCreate(
                    profile,
                    out var terrorbeakImmunity,
                    out reason
                )
            )
            {
                return false;
            }
            combatImmunity = terrorbeakImmunity;
        }

        HostileShadowMonster? monster = null;
        try
        {
            monster = new HostileShadowMonster
            {
                Position = new Vector2((float)state.PositionX, (float)state.PositionY),
                Health = state.Health,
                MaxHealth = state.MaxHealth,
                currentLocation = location,
            };
            monster.modData[HostileShadowMonster.EntityIdModDataKey] =
                state.EntityId.ToString(CultureInfo.InvariantCulture);
            monster.modData[HostileShadowMonster.AssetBindingModDataKey] =
                state.AssetBindingId;
            monster.modData[HostileShadowMonster.StateModDataKey] =
                HostileShadowStateIds.Spawn;
            monster.modData[HostileShadowMonster.AttackInstanceModDataKey] =
                string.Empty;
            monster.modData[
                HostileShadowMonster.AttackInstanceRevisionModDataKey
            ] = "0";
            monster.modData[HostileShadowMonster.AttackFrameModDataKey] = "0";
            if (
                HostileShadowMovementPresentationBindings.Supports(
                    profile.AssetBindingId
                )
            )
            {
                monster.modData[HostileShadowMonster.MovementFacingModDataKey] =
                    HostileShadowFacingIds.Down;
                monster.modData[HostileShadowMonster.MovementFrameModDataKey] = "0";
            }
            if (combatImmunity is not null)
                monster.ApplyCombatImmunity(combatImmunity);
            location.characters.Add(monster);
            if (!location.characters.Contains(monster))
                throw new InvalidOperationException("location-character-add-not-observed");

            var attackState = new HostileAttackStateMachine(
                attackDefinition,
                transitionPolicy
            );
            AddPhysical(
                state.EntityId,
                new PhysicalEntry(
                    monster,
                    location,
                    profile,
                    spawnGameMinute,
                    attackDefinition,
                    attackState,
                    HostileShadowMovementPresentationBindings.Supports(
                        profile.AssetBindingId
                    )
                        ? new HostileShadowMovementPresentationState(
                            attackMetadata!.Chase.FrameCount,
                            attackMetadata.Chase.FrameDurationMilliseconds
                        )
                        : null,
                    new HostileShadowHitResponseController(attackState)
                )
            );
            reason = "hostile-shadow.physical-entity-materialized";
            return true;
        }
        catch (Exception exception)
        {
            if (monster is not null)
                location.characters.Remove(monster);
            reason = string.Concat(
                "hostile-shadow.physical-materialization-threw-",
                exception.GetType().Name
            );
            return false;
        }
    }

    internal bool TryRecordAggroHint(
        ShadowAggroHintRequest request,
        long senderPlayerId,
        out string reason
    )
    {
        reason = string.Empty;
        if (
            disposed
            || !Game1.IsMasterGame
            || !authority.IsHostSessionActive
            || !HostileShadowProtocol.IsValidAggroHintRequest(
                request,
                SanityPlayerKey.FromUniqueMultiplayerId(senderPlayerId),
                authority.SessionId,
                out reason
            )
            || !authority.TryGetEntity(request.EntityId, out var state)
            || state is null
            || state.Revision != request.KnownEntityRevision
            || !entries.TryGetValue(request.EntityId, out var entry)
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.aggro-hint-state-or-revision-invalid";
            return false;
        }

        var attacker = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        if (
            attacker is null
            || attacker.currentLocation is null
            || !string.Equals(
                request.LocationId,
                state.LocationId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                attacker.currentLocation.NameOrUniqueName,
                state.LocationId,
                StringComparison.Ordinal
            )
            || !WithinRange(
                entry.Monster.StandingPixel.X,
                entry.Monster.StandingPixel.Y,
                attacker.StandingPixel.X,
                attacker.StandingPixel.Y,
                entry.Profile.DetectionRadiusPixels
            )
        )
        {
            reason = "hostile-shadow.aggro-hint-sender-location-or-range-invalid";
            return false;
        }

        entry.RecentAttackerPlayerKey = request.AttackerPlayerKey;
        reason = "hostile-shadow.aggro-hint-accepted-for-host-recompute";
        return true;
    }

    internal bool TryHandleAttackHit(
        ShadowAttackHitRequest request,
        long senderPlayerId,
        out HostileAttackReceipt receipt,
        out string reason
    )
    {
        reason = string.Empty;
        var senderPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            senderPlayerId
        );
        if (
            disposed
            || !Game1.IsMasterGame
            || !authority.IsHostSessionActive
            || !HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                senderPlayerKey,
                authority.SessionId,
                out reason
            )
            || !authority.TryGetEntity(request.EntityId, out var state)
            || state is null
            || !entries.TryGetValue(request.EntityId, out var entry)
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.attack-hit-state-unavailable";
            receipt = SmapiHostileAttackCombatService.Rejected(request, reason);
            return false;
        }

        var farmer = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        if (
            farmer is null
            || farmer.currentLocation is null
            || !string.Equals(
                farmer.currentLocation.NameOrUniqueName,
                state.LocationId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.attack-hit-sender-location-invalid";
            receipt = SmapiHostileAttackCombatService.Rejected(request, reason);
            return false;
        }

        var applied = attackCombat.TryProcessHit(
            entry.Monster,
            entry.Profile,
            entry.AttackDefinition,
            entry.AttackState,
            state,
            farmer,
            request,
            out receipt
        );
        reason = receipt.Result.Reason;
        return applied;
    }

    private int HandleIncomingHit(
        HostileShadowMonster monster,
        int damage,
        Farmer? attacker
    )
    {
        if (
            disposed
            || !Game1.IsMasterGame
            || !authority.IsHostSessionActive
            || damage <= 0
            || attacker is null
            || attacker.currentLocation is null
            || !monster.modData.TryGetValue(
                HostileShadowMonster.EntityIdModDataKey,
                out var serializedEntityId
            )
            || !long.TryParse(
                serializedEntityId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var entityId
            )
            || !entries.TryGetValue(entityId, out var entry)
            || !ReferenceEquals(entry.Monster, monster)
            || !ReferenceEquals(entry.Location, attacker.currentLocation)
            || !entry.Location.characters.Contains(monster)
            || !authority.TryGetEntity(entityId, out var state)
            || state is null
            || state.Health != monster.Health
            || monster.Health <= 0
            || entry.PendingLethalDamage
            || string.Equals(
                state.StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
            || string.Equals(
                state.StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            )
            || authority.Revision == long.MaxValue
        )
        {
            return 0;
        }

        var attackerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            attacker.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(attackerPlayerKey))
            return 0;

        var damageDecision = HostileShadowIncomingDamagePolicy.Evaluate(
            monster.Health,
            damage,
            entry.Profile.Defense
        );
        if (!damageDecision.Valid)
            return 0;
        var previousHealth = monster.Health;
        var proposedRevision = authority.Revision + 1;

        entry.RecentAttackerPlayerKey = attackerPlayerKey;
        entry.PendingLethalDamage = damageDecision.PendingDying;
        entry.PendingLethalAttackerPlayerKey = entry.PendingLethalDamage
            ? attackerPlayerKey
            : string.Empty;
        // Keep a positive physical sentinel for the remainder of GameLocation.damageMonster.
        // That method otherwise calls vanilla onMonsterKilled after this override returns.
        monster.Health = damageDecision.PhysicalHealthAfter;
        if (entry.PendingLethalDamage)
            pendingLethalEntityIds.Add(entityId);

        var randomSeed = HostileShadowTeleportSeed.Derive(
            authority.SessionId,
            entityId,
            proposedRevision
        );
        HostileShadowHitResponseDecision decision;
        try
        {
            decision = entry.HitResponse.HandleHit(
                new HostileShadowHitResponseInput
                {
                    SessionId = authority.SessionId,
                    EntityId = entityId,
                    ProposedRevision = proposedRevision,
                    LocationId = state.LocationId,
                    PositionX = monster.Position.X,
                    PositionY = monster.Position.Y,
                    TileSizePixels = Game1.tileSize,
                    Health = monster.Health,
                    AttackerPlayerKey = attackerPlayerKey,
                    Map = new SmapiHostileShadowTeleportMap(entry.Location),
                    Random = new HostileShadowTeleportRandom(randomSeed),
                }
            );
        }
        catch (Exception exception)
        {
            var failure = string.Concat(
                "hostile-shadow.hit-teleport-map-evaluation-threw-",
                exception.GetType().Name
            );
            LogOnce(failure, LogLevel.Error);
            authority.CleanupEntity(
                entityId,
                HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
            );
            return 0;
        }
        if (!decision.Valid)
        {
            monster.Health = previousHealth;
            entry.PendingLethalDamage = false;
            entry.PendingLethalAttackerPlayerKey = string.Empty;
            pendingLethalEntityIds.Remove(entityId);
            LogOnce(decision.Reason, LogLevel.Error);
            authority.CleanupEntity(
                entityId,
                HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
            );
            return 0;
        }

        monster.Position = new Vector2(
            (float)decision.PositionX,
            (float)decision.PositionY
        );
        ApplyMonsterState(entry);
        if (!TrySynchronizeHitResponse(entityId, entry, state, decision))
        {
            authority.CleanupEntity(
                entityId,
                HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
            );
            return damageDecision.AppliedDamage;
        }
        if (decision.RemovalRequested)
            authority.CleanupEntity(entityId, decision.Reason);
        return damageDecision.AppliedDamage;
    }

    private void ResolvePendingLethalDamage()
    {
        if (pendingLethalEntityIds.Count == 0)
            return;
        var entityIds = new List<long>(pendingLethalEntityIds);
        entityIds.Sort();
        foreach (var entityId in entityIds)
        {
            if (
                !entries.TryGetValue(entityId, out var entry)
                || !entry.PendingLethalDamage
                || !authority.TryGetEntity(entityId, out var state)
                || state is null
            )
            {
                pendingLethalEntityIds.Remove(entityId);
                continue;
            }
            if (authority.Revision == long.MaxValue)
            {
                authority.CleanupEntity(
                    entityId,
                    HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
                );
                pendingLethalEntityIds.Remove(entityId);
                continue;
            }

            // The sentinel has already kept vanilla kill/reward code out. On the next host tick,
            // write true zero first and only then publish Dying.
            entry.Monster.Health = 0;
            var decision = entry.HitResponse.HandleHit(
                new HostileShadowHitResponseInput
                {
                    SessionId = authority.SessionId,
                    EntityId = entityId,
                    ProposedRevision = authority.Revision + 1,
                    LocationId = state.LocationId,
                    PositionX = entry.Monster.Position.X,
                    PositionY = entry.Monster.Position.Y,
                    TileSizePixels = Game1.tileSize,
                    Health = 0,
                    AttackerPlayerKey = entry.PendingLethalAttackerPlayerKey,
                }
            );
            if (
                !decision.Valid
                || !TrySynchronizeHitResponse(
                    entityId,
                    entry,
                    state,
                    decision
                )
            )
            {
                LogOnce(decision.Reason, LogLevel.Error);
                authority.CleanupEntity(
                    entityId,
                    HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
                );
                pendingLethalEntityIds.Remove(entityId);
                continue;
            }
            SettleDying(entityId, entry);
            entry.PendingLethalDamage = false;
            entry.PendingLethalAttackerPlayerKey = string.Empty;
            pendingLethalEntityIds.Remove(entityId);
            entry.Monster.Position = new Vector2(
                (float)decision.PositionX,
                (float)decision.PositionY
            );
            ApplyMonsterState(entry);
        }
    }

    private void SettleDying(long entityId, PhysicalEntry entry)
    {
        if (
            !Game1.IsMasterGame
            || !authority.TryGetEntity(entityId, out var state)
            || state is null
            || !string.Equals(
                state.StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
            || state.Health != 0
            || !lifecycleReceipts.TryGetDying(
                authority.SessionId,
                entityId,
                state.Revision,
                out var dyingReceipt
            )
        )
        {
            LogOnce(
                "hostile-shadow.settlement-confirmed-dying-receipt-missing",
                LogLevel.Error
            );
            return;
        }

        var request = HostileShadowSettlementRequest.Capture(
            dyingReceipt,
            SanityAuthorityRole.Host,
            state.StateId,
            state.Health,
            state.LocationId,
            entry.Monster.Position.X,
            entry.Monster.Position.Y,
            entry.Profile
        );
        var result = settlements.Resolve(request);
        if (
            result.Status
                is not HostileShadowSettlementStatus.Settled
                    and not HostileShadowSettlementStatus.Duplicate
        )
        {
            // Settlement failures are terminal for this death: retrying after a possibly-created
            // debris item would duplicate loot. The stable receipt/reason remains diagnostic.
            LogOnce(result.Reason, LogLevel.Error);
            if (result.Receipt is { } failedReceipt)
            {
                LogOnce(failedReceipt.Drop.Reason, LogLevel.Error);
                if (
                    failedReceipt.SanityReward.Status
                    == HostileShadowSanityRewardStatus.Rejected
                )
                {
                    LogOnce(failedReceipt.SanityReward.Reason, LogLevel.Error);
                }
            }
        }
        else if (
            result.Receipt is { } settledReceipt
            && settledReceipt.LastHitter.Status
                == HostileShadowLastHitterStatus.Invalid
        )
        {
            LogOnce(settledReceipt.LastHitter.Reason, LogLevel.Debug);
        }
    }

    private bool TrySynchronizeHitResponse(
        long entityId,
        PhysicalEntry entry,
        ShadowStateSnapshot current,
        HostileShadowHitResponseDecision decision
    )
    {
        var targetPlayerKey = string.Equals(
            decision.StateId,
            HostileShadowStateIds.Despawn,
            StringComparison.Ordinal
        )
            ? string.Empty
            : entry.TargetPlayerKey;
        if (
            !authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    current.LocationId,
                    decision.StateId,
                    targetPlayerKey,
                    decision.PositionX,
                    decision.PositionY,
                    entry.Monster.Health,
                    decision.Reason
                ),
                out var reason
            )
            || !authority.TryGetEntity(entityId, out var updated)
            || updated is null
        )
        {
            LogOnce(reason, LogLevel.Error);
            return false;
        }

        if (decision.Receipt is { } receipt)
        {
            if (
                (decision.Status
                        is HostileShadowHitResponseDecisionStatus.Started
                            or HostileShadowHitResponseDecisionStatus.RemovalRequested)
                && receipt.Revision != updated.Revision
            )
            {
                LogOnce(
                    "hostile-shadow.lifecycle-receipt-revision-mismatch",
                    LogLevel.Error
                );
                return false;
            }
            var record = lifecycleReceipts.Record(receipt);
            if (
                record.Status
                    is HostileShadowLifecycleReceiptRecordStatus.Conflict
                    or HostileShadowLifecycleReceiptRecordStatus.Rejected
            )
            {
                LogOnce(record.Reason, LogLevel.Error);
                return false;
            }
        }
        ApplyMonsterState(entry, updated);
        return true;
    }

    internal void ClearSession()
    {
        RemoveAllPhysical();
        pendingLethalEntityIds.Clear();
        playerIndex.Rebuild(Array.Empty<HostileShadowPlayerSample>());
        renderer.Clear();
        lifecycleReceipts.Clear();
        settlements.ClearSession();
        peerGate.ClearSession();
        loggedReasons.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        authority.DeltaProduced -= OnAuthorityDelta;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        resourceService.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        ClearSession();
        peerGate.Dispose();
        HostileShadowMonsterHitBridge.Clear(incomingHitHandler);
        HostileShadowMonsterVisualBridge.Clear(renderer);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            disposed
            || entries.Count == 0
            || !Context.IsWorldReady
            || !Game1.IsMasterGame
        )
        {
            return;
        }

        ResolvePendingLethalDamage();
        if (e.IsMultipleOf(HostileShadowTargetingLimits.RefreshCadenceTicks))
        {
            RebuildPlayerIndex();
            RefreshTargetsAndSnapshots();
        }
        AdvanceCachedTargets(
            e.IsMultipleOf(HostileShadowTargetingLimits.RefreshCadenceTicks)
        );
    }

    private void RebuildPlayerIndex()
    {
        var samples = new List<HostileShadowPlayerSample>(
            Math.Min(
                HostileShadowTargetingLimits.MaximumPlayers,
                Game1.getOnlineFarmers().Count
            )
        );
        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (
                farmer.currentLocation is null
                || samples.Count >= HostileShadowTargetingLimits.MaximumPlayers
            )
            {
                if (samples.Count >= HostileShadowTargetingLimits.MaximumPlayers)
                {
                    playerIndex.Rebuild(null);
                    LogOnce(
                        "hostile-shadow.player-index.sample-invalid-or-capacity",
                        LogLevel.Warn
                    );
                    return;
                }
                continue;
            }
            samples.Add(
                new HostileShadowPlayerSample(
                    SanityPlayerKey.FromUniqueMultiplayerId(
                        farmer.UniqueMultiplayerID
                    ),
                    farmer.currentLocation.NameOrUniqueName,
                    farmer.StandingPixel.X,
                    farmer.StandingPixel.Y
                )
            );
        }

        var result = playerIndex.Rebuild(samples);
        if (!result.Success)
            LogOnce(result.Reason, LogLevel.Warn);
    }

    private void RefreshTargetsAndSnapshots()
    {
        var entityCount = CaptureEntityIterationOrder();
        for (var index = 0; index < entityCount; index++)
        {
            var entityId = entityIterationBuffer[index];
            if (
                !entries.TryGetValue(entityId, out var entry)
                || !authority.TryGetEntity(entityId, out var state)
                || state is null
            )
            {
                RemovePhysical(entityId);
                continue;
            }
            if (!entry.Location.characters.Contains(entry.Monster))
            {
                authority.CleanupEntity(
                    entityId,
                    HostileShadowCleanupReasonIds.PhysicalEntityMissing
                );
                continue;
            }
            if (
                string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Dying,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            var naturalTtlMinutes = Math.Max(
                1L,
                checked(
                    (long)Math.Round(
                        entry.Profile.NaturalDespawnGameHours
                            * MinutesPerGameHour,
                        MidpointRounding.AwayFromZero
                    )
                )
            );
            var decision = HostileShadowTargetingEngine.Evaluate(
                new HostileShadowTargetingInput
                {
                    EntityId = entityId,
                    OwnerPlayerKey = state.OwnerPlayerKey,
                    LocationId = state.LocationId,
                    RecentAttackerPlayerKey = entry.RecentAttackerPlayerKey,
                    PositionX = entry.Monster.Position.X,
                    PositionY = entry.Monster.Position.Y,
                    StandingX = entry.Monster.StandingPixel.X,
                    StandingY = entry.Monster.StandingPixel.Y,
                    MovementSpeed = entry.Profile.MovementSpeed,
                    DetectionRadiusPixels = entry.Profile.DetectionRadiusPixels,
                    StopDistancePixels = entry.Profile.AttackRangePixels,
                    SpawnGameMinute = entry.SpawnGameMinute,
                    CurrentGameMinute = timeApi.Time,
                    NaturalTtlMinutes = naturalTtlMinutes,
                    ElapsedSeconds = 0d,
                },
                playerIndex
            );
            if (!decision.Valid)
            {
                LogOnce(decision.Reason, LogLevel.Warn);
                continue;
            }
            if (decision.NaturalTtlExpired)
            {
                authority.CleanupEntity(
                    entityId,
                    HostileShadowCleanupReasonIds.Natural
                );
                continue;
            }

            var attackLocked = string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Attack,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.HitTeleport,
                    StringComparison.Ordinal
                );
            if (
                !attackLocked
                && decision.TargetSource
                    != HostileShadowTargetSource.RecentAttacker
            )
            {
                entry.RecentAttackerPlayerKey = string.Empty;
            }
            if (!attackLocked)
                entry.TargetPlayerKey = decision.TargetPlayerKey;
            else if (entry.AttackState.CurrentInstance is { } attack)
                entry.TargetPlayerKey = attack.TargetPlayerKey;

            ApplyMonsterState(entry);
            authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    state.LocationId,
                    entry.AttackState.StateId,
                    entry.TargetPlayerKey,
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y,
                    entry.Monster.Health,
                    decision.Reason,
                    entry.AttackState.CurrentInstance?.InstanceId
                        ?? string.Empty,
                    entry.AttackState.CurrentInstance?.Revision ?? 0,
                    entry.AttackState.CurrentInstance?.FrameNumber ?? 0
                ),
                out _
            );
        }
    }

    private void AdvanceCachedTargets(bool snapshotCadence)
    {
        var entityCount = CaptureEntityIterationOrder();
        for (var index = 0; index < entityCount; index++)
        {
            var entityId = entityIterationBuffer[index];
            if (
                !entries.TryGetValue(entityId, out var entry)
                || !authority.TryGetEntity(entityId, out var state)
                || state is null
            )
            {
                RemovePhysical(entityId);
                continue;
            }

            HostileShadowPlayerSample? target = null;
            var hasTarget = entry.TargetPlayerKeyIsCanonical
                && playerIndex.TryGetPlayer(
                    entry.TargetPlayerKey,
                    out target
                )
                && target is not null
                && string.Equals(
                    target.LocationId,
                    entry.Location.NameOrUniqueName,
                    StringComparison.Ordinal
                );
            var targetX = hasTarget ? target!.StandingX : 0d;
            var targetY = hasTarget ? target!.StandingY : 0d;
            if (
                string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.HitTeleport,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Dying,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                var response = entry.HitResponse.Advance(
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y,
                    FixedUpdateSeconds * 1000d,
                    hasTarget
                );
                if (!response.Valid)
                {
                    LogOnce(response.Reason, LogLevel.Error);
                    authority.CleanupEntity(
                        entityId,
                        HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
                    );
                    continue;
                }
                entry.Monster.Position = new Vector2(
                    (float)response.PositionX,
                    (float)response.PositionY
                );
                ApplyMonsterState(entry);
                if (
                    (response.StateChanged || response.PositionChanged)
                    && !TrySynchronizeHitResponse(
                        entityId,
                        entry,
                        state,
                        response
                    )
                )
                {
                    authority.CleanupEntity(
                        entityId,
                        HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed
                    );
                    continue;
                }
                if (response.RemovalRequested)
                {
                    authority.CleanupEntity(
                        entityId,
                        string.Equals(
                            response.StateId,
                            HostileShadowStateIds.Dying,
                            StringComparison.Ordinal
                        )
                            ? HostileShadowCleanupReasonIds.DyingCompleted
                            : response.Reason
                    );
                }
                continue;
            }
            var movementPositionChanged = false;
            if (
                hasTarget
                && string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Chase,
                    StringComparison.Ordinal
                )
            )
            {
                var movement = HostileShadowTargetingEngine.AdvancePosition(
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y,
                    entry.Monster.StandingPixel.X,
                    entry.Monster.StandingPixel.Y,
                    targetX,
                    targetY,
                    entry.Profile.MovementSpeed,
                    entry.Profile.AttackRangePixels,
                    FixedUpdateSeconds
                );
                if (movement.Valid)
                {
                    movementPositionChanged =
                        entry.Monster.Position.X != (float)movement.PositionX
                        || entry.Monster.Position.Y != (float)movement.PositionY;
                    entry.Monster.Position = new Vector2(
                        (float)movement.PositionX,
                        (float)movement.PositionY
                    );
                }
            }

            var inAttackRange = hasTarget
                && WithinRange(
                    entry.Monster.StandingPixel.X,
                    entry.Monster.StandingPixel.Y,
                    targetX,
                    targetY,
                    entry.Profile.AttackRangePixels
                );
            var proposedRevision = authority.Revision == long.MaxValue
                ? 0
                : authority.Revision + 1;
            var attackInput = HostileAttackStateInput.Capture(
                authority.SessionId,
                entityId,
                proposedRevision,
                state.LocationId,
                entry.TargetPlayerKey,
                hasTarget,
                inAttackRange,
                entry.Monster.Position.X,
                entry.Monster.Position.Y,
                entry.Monster.StandingPixel.X,
                entry.Monster.StandingPixel.Y,
                targetX,
                targetY,
                Game1.tileSize,
                entry.Profile.AttackIntervalSeconds
            );
            var decision = entry.AttackState.Advance(
                in attackInput,
                FixedUpdateSeconds * 1000d
            );
            if (!decision.Valid)
            {
                LogOnce(decision.Reason, LogLevel.Warn);
                continue;
            }

            entry.Monster.Position = new Vector2(
                (float)decision.PositionX,
                (float)decision.PositionY
            );
            if (
                entry.MovementPresentation is { } movementPresentation
                && !movementPresentation.TryAdvance(
                    hasTarget
                        && string.Equals(
                            decision.StateId,
                            HostileShadowStateIds.Chase,
                            StringComparison.Ordinal
                        ),
                    movementPositionChanged,
                    entry.Monster.StandingPixel.X,
                    entry.Monster.StandingPixel.Y,
                    targetX,
                    targetY,
                    FixedUpdateSeconds * 1000d,
                    out _
                )
            )
            {
                LogOnce(
                    "hostile-shadow.movement-presentation-input-invalid",
                    LogLevel.Error
                );
                authority.CleanupEntity(
                    entityId,
                    HostileShadowCleanupReasonIds.ResourceInvalidated
                );
                continue;
            }
            ApplyMonsterState(entry);
            var mustSynchronize = decision.StateChanged
                || decision.AttackFrameChanged
                || (
                    decision.PositionChanged
                    && (
                        snapshotCadence
                        || string.Equals(
                            decision.StateId,
                            HostileShadowStateIds.Attack,
                            StringComparison.Ordinal
                        )
                    )
                );
            if (mustSynchronize)
            {
                if (
                    !authority.TryUpdate(
                        new HostileShadowStateUpdate(
                            entityId,
                            state.LocationId,
                            decision.StateId,
                            entry.TargetPlayerKey,
                            decision.PositionX,
                            decision.PositionY,
                            entry.Monster.Health,
                            decision.Reason,
                            decision.AttackInstanceId,
                            decision.AttackInstanceRevision,
                            decision.AttackFrameNumber
                        ),
                        out var updateReason
                    )
                )
                {
                    LogOnce(updateReason, LogLevel.Warn);
                    continue;
                }
                authority.TryGetEntity(entityId, out state);
            }

            if (
                state is not null
                && entry.AttackState.CurrentInstance is { } instance
                && entry.AttackDefinition.IsActiveFrame(instance.FrameNumber)
            )
            {
                attackCombat.ProcessCurrentHits(
                    entry.Monster,
                    entry.Profile,
                    entry.AttackDefinition,
                    entry.AttackState,
                    state
                );
            }
        }
    }

    private static void ApplyMonsterState(PhysicalEntry entry)
    {
        var instance = entry.AttackState.CurrentInstance;
        if (
            entry.MovementPresentation is { } movementPresentation
            &&
            !string.Equals(
                entry.AttackState.StateId,
                HostileShadowStateIds.Chase,
                StringComparison.Ordinal
            )
        )
        {
            movementPresentation.Reset();
        }
        ApplyAttackModDataIfChanged(
            entry,
            entry.AttackState.StateId,
            instance?.InstanceId ?? string.Empty,
            instance?.Revision ?? 0,
            instance?.FrameNumber ?? 0
        );
        if (entry.MovementPresentation is { } presentation)
        {
            if (
                !string.Equals(
                    entry.AppliedMovementFacingId,
                    presentation.FacingId,
                    StringComparison.Ordinal
                )
            )
            {
                SetModDataIfChanged(
                    entry.Monster,
                    HostileShadowMonster.MovementFacingModDataKey,
                    presentation.FacingId
                );
                entry.AppliedMovementFacingId = presentation.FacingId;
            }
            if (entry.AppliedMovementFrameIndex != presentation.FrameIndex)
            {
                SetModDataIfChanged(
                    entry.Monster,
                    HostileShadowMonster.MovementFrameModDataKey,
                    SerializeMovementFrame(presentation.FrameIndex)
                );
                entry.AppliedMovementFrameIndex = presentation.FrameIndex;
            }
        }
    }

    private static void ApplyAttackModDataIfChanged(
        PhysicalEntry entry,
        string stateId,
        string attackInstanceId,
        long attackInstanceRevision,
        int attackFrameNumber
    )
    {
        if (
            !string.Equals(
                entry.AppliedStateId,
                stateId,
                StringComparison.Ordinal
            )
        )
        {
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.StateModDataKey,
                stateId
            );
            entry.AppliedStateId = stateId;
        }
        if (
            !string.Equals(
                entry.AppliedAttackInstanceId,
                attackInstanceId,
                StringComparison.Ordinal
            )
        )
        {
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.AttackInstanceModDataKey,
                attackInstanceId
            );
            entry.AppliedAttackInstanceId = attackInstanceId;
        }
        if (entry.AppliedAttackInstanceRevision != attackInstanceRevision)
        {
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.AttackInstanceRevisionModDataKey,
                attackInstanceRevision.ToString(CultureInfo.InvariantCulture)
            );
            entry.AppliedAttackInstanceRevision = attackInstanceRevision;
        }
        if (entry.AppliedAttackFrameNumber != attackFrameNumber)
        {
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.AttackFrameModDataKey,
                attackFrameNumber.ToString(CultureInfo.InvariantCulture)
            );
            entry.AppliedAttackFrameNumber = attackFrameNumber;
        }
    }

    private static string SerializeMovementFrame(int frameIndex)
    {
        return frameIndex switch
        {
            0 => "0",
            1 => "1",
            2 => "2",
            3 => "3",
            _ => frameIndex.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static void SetModDataIfChanged(
        HostileShadowMonster monster,
        string key,
        string value
    )
    {
        if (
            !monster.modData.TryGetValue(key, out var current)
            || !string.Equals(current, value, StringComparison.Ordinal)
        )
        {
            monster.modData[key] = value;
        }
    }

    private static void ApplyMonsterState(
        PhysicalEntry entry,
        ShadowStateSnapshot state
    )
    {
        ApplyAttackModDataIfChanged(
            entry,
            state.StateId,
            state.AttackInstanceId,
            state.AttackInstanceRevision,
            state.AttackFrameNumber
        );
    }

    private void OnAuthorityDelta(ShadowStateDeltaMessage message)
    {
        if (message.Change.Kind == ShadowStateDeltaKind.Removed)
        {
            RemovePhysical(message.Change.EntityId);
            return;
        }
        if (
            message.Change.Kind == ShadowStateDeltaKind.Updated
            && message.Change.State is { } state
            && entries.TryGetValue(state.EntityId, out var entry)
        )
        {
            if (
                string.Equals(
                    state.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                var transition = entry.AttackState.TransitionToExternalState(
                    HostileShadowStateIds.Despawn,
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y
                );
                if (!transition.Valid)
                    LogOnce(transition.Reason, LogLevel.Error);
                if (
                    HostileShadowLifecycleReceipt.TryCreate(
                        message.SessionId,
                        state.EntityId,
                        state.Revision,
                        HostileShadowLifecycleTransitionKind.Despawn,
                        message.Change.Reason,
                        null,
                        string.Empty,
                        out var receipt
                    )
                )
                {
                    var recorded = lifecycleReceipts.Record(receipt);
                    if (
                        recorded.Status
                            is HostileShadowLifecycleReceiptRecordStatus.Conflict
                            or HostileShadowLifecycleReceiptRecordStatus.Rejected
                    )
                    {
                        LogOnce(recorded.Reason, LogLevel.Error);
                    }
                }
            }
            ApplyMonsterState(entry, state);
        }
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        renderer.Clear();
        peerGate.OnResourcesReleasing(reason);
        if (Game1.IsMasterGame && authority.IsHostSessionActive)
        {
            authority.CleanupAll(
                reason == SanityResourceReleaseReason.SystemDisabled
                    ? HostileShadowCleanupReasonIds.SystemDisabled
                    : reason == SanityResourceReleaseReason.ReturnedToTitle
                        ? HostileShadowCleanupReasonIds.ReturnedToTitle
                        : HostileShadowCleanupReasonIds.ResourceInvalidated
            );
        }
        else
        {
            RemoveAllPhysical();
        }
    }

    private bool TryPrimeSharedVisuals(out string reason)
    {
        foreach (var bindingId in ShadowMonsterAssetBindingIds.All)
        {
            if (!renderer.TryPrepareBinding(bindingId, out reason))
                return false;
        }
        reason = "hostile-shadow.local-shared-visibility-capability-ready";
        return true;
    }

    private void AddPhysical(long entityId, PhysicalEntry entry)
    {
        var insertionIndex = orderedEntityIds.BinarySearch(entityId);
        if (
            entityId <= 0
            || entry is null
            || insertionIndex >= 0
            || orderedEntityIds.Count >= entityIterationBuffer.Length
        )
        {
            throw new InvalidOperationException(
                "hostile-shadow.physical-iteration-order-add-invalid"
            );
        }

        entries.Add(entityId, entry);
        try
        {
            // Entity mutation is rare and capped at 256. Paying the ordered insert here keeps the
            // 60 Hz movement/attack loop deterministic without sorting or allocating per tick.
            orderedEntityIds.Insert(~insertionIndex, entityId);
        }
        catch
        {
            entries.Remove(entityId);
            throw;
        }
    }

    private int CaptureEntityIterationOrder()
    {
        // Reuse one cap-sized buffer to preserve the old per-call snapshot behavior when authority
        // callbacks synchronously remove entries; the hot loop never allocates or re-sorts IDs.
        orderedEntityIds.CopyTo(entityIterationBuffer, 0);
        return orderedEntityIds.Count;
    }

    private void RemovePhysical(long entityId)
    {
        var orderIndex = orderedEntityIds.BinarySearch(entityId);
        if (!entries.Remove(entityId, out var entry))
        {
            if (orderIndex >= 0)
            {
                orderedEntityIds.RemoveAt(orderIndex);
                LogOnce(
                    "hostile-shadow.physical-iteration-order-entry-missing",
                    LogLevel.Error
                );
            }
            return;
        }
        if (orderIndex >= 0)
            orderedEntityIds.RemoveAt(orderIndex);
        else
        {
            LogOnce(
                "hostile-shadow.physical-iteration-order-id-missing",
                LogLevel.Error
            );
        }
        pendingLethalEntityIds.Remove(entityId);
        entry.Location.characters.Remove(entry.Monster);
    }

    private void RemoveAllPhysical()
    {
        if (entries.Count == 0)
        {
            orderedEntityIds.Clear();
            return;
        }
        var entityCount = CaptureEntityIterationOrder();
        for (var index = 0; index < entityCount; index++)
            RemovePhysical(entityIterationBuffer[index]);
    }

    private static void RemoveOrphansAtWorldBoundary()
    {
        if (Game1.locations is null)
            return;
        foreach (var location in Game1.locations)
        {
            if (location is null || location.characters.Count == 0)
                continue;
            var remove = new List<HostileShadowMonster>();
            foreach (var character in location.characters)
            {
                if (character is HostileShadowMonster monster)
                    remove.Add(monster);
            }
            foreach (var monster in remove)
                location.characters.Remove(monster);
        }
    }

    private sealed class SmapiHostileShadowTeleportMap
        : IHostileShadowTeleportMap
    {
        private readonly GameLocation location;

        internal SmapiHostileShadowTeleportMap(GameLocation location)
        {
            this.location = location
                ?? throw new ArgumentNullException(nameof(location));
        }

        public bool IsLocationValid(string expectedLocationId)
        {
            return Context.IsWorldReady
                && string.Equals(
                    location.NameOrUniqueName,
                    expectedLocationId,
                    StringComparison.Ordinal
                )
                && ReferenceEquals(
                    Game1.getLocationFromName(expectedLocationId),
                    location
                );
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
    }

    private static bool WithinRange(
        double leftX,
        double leftY,
        double rightX,
        double rightY,
        double range
    )
    {
        if (!double.IsFinite(range) || range <= 0d)
            return false;
        var x = rightX - leftX;
        var y = rightY - leftY;
        return (x * x) + (y * y) <= range * range;
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
        monitor.Log($"Hostile shadow world runtime: {reason}", level);
    }
}
