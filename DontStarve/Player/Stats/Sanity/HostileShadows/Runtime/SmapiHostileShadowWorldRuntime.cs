#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal interface IHostileShadowProjectionPushBoxBridge
{
    bool HasActivePushBoxParticipants { get; }

    void AppendPushBoxParticipants(
        List<HostileShadowCrowdParticipant> participants
    );

    void ApplyPushBoxResolution(
        HostileShadowCrowdCollisionResolution resolution
    );
}

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
            long entityId,
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
            CrowdParticipant = new HostileShadowCrowdParticipant(
                entityId.ToString("D19", CultureInfo.InvariantCulture),
                location.NameOrUniqueName,
                attackDefinition.PushBoxGroupId,
                default,
                default,
                default,
                default,
                attackDefinition.PushForce
            );
            AppliedMovementFacingId = movementPresentation is null
                ? string.Empty
                : HostileShadowFacingIds.Down;
            AppliedMovementFrameIndex = movementPresentation is null ? -1 : 0;
        }

        internal HostileShadowMonster Monster { get; }
        internal GameLocation Location { get; }
        internal ShadowMonsterRuntimeProfile Profile { get; }
        internal long SpawnGameMinute { get; }
        internal long? NoTargetSinceGameMinute { get; set; }
        internal long NoTargetElapsedGameMinutes { get; set; }
        internal long NoTargetLastObservedGameMinute { get; set; }
        internal bool NoTargetClockRunning { get; set; }
        internal HostileAttackRuntimeDefinition AttackDefinition { get; }
        internal HostileAttackStateMachine AttackState { get; }
        internal HostileShadowMovementPresentationState? MovementPresentation { get; }
        internal HostileShadowHitResponseController HitResponse { get; }
        internal HostileShadowCrowdParticipant CrowdParticipant { get; }
        // These values mirror the initial materialization writes. The 60 Hz loop compares raw
        // state first so numeric formatting and NetDictionary writes occur only on transitions.
        internal string AppliedStateId { get; set; } = HostileShadowStateIds.Spawn;
        internal string AppliedHitTeleportVisualPhase { get; set; } =
            HostileShadowHitTeleportVisualPhaseIds.None;
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

        // DIAG-20260809: 受击拉仇恨锁定——非空时影怪锁定追击该玩家（不受检测半径限制），
        // 由 HandleIncomingHit 的仇恨传播设置；玩家切图/下线后在下个索敌节奏自动清除。
        internal string AggroLockPlayerKey { get; set; } = string.Empty;

        // DIAG-20260809: 无索敌游荡状态。锚点=生成点（首次）或最后脱战位置；
        // 到达目标后随机静息 3-5 秒，再在锚点 10 格半径内选新目标，半速移动过去。
        internal double WanderAnchorX;
        internal double WanderAnchorY;
        internal double WanderRemainingMilliseconds = 3000d;
        internal double WanderTargetX;
        internal double WanderTargetY;
        internal bool HasWanderTarget;
        internal bool HadTargetLastTick;

        // DIAG-20260809: 绑定隐藏态——危险实体被无害投影外观取代（不渲染/无敌/行为禁用）。
        // BindingCorrelationId 非空即隐藏；位置由宿主按绑定投影低频对齐（5-15 tick）。
        internal string BindingCorrelationId { get; set; } = string.Empty;
        internal bool IsBindingHidden => BindingCorrelationId.Length > 0;
        internal double BindingAnchorX;
        internal double BindingAnchorY;
        internal int BindingAlignCooldownTicks;
        internal string BindingFacingId { get; set; } = string.Empty;
        // DIAG-20260810: 脱战恐吓标记——恐吓阶段无敌/停止行为/播恐吓动画，
        // 恐吓结束由宿主调用 TryBeginBinding 进入绑定隐藏态。
        internal bool IsRetreating { get; set; }
        // DIAG-20260810: 受击框中心相对 Position（贴图左上角）的偏移。
        // 绑定投影位置=实体受击框中心，实体 Position=投影位置-偏移（恢复瞬间贴图不跳）。
        internal double BindingCenterOffsetX;
        internal double BindingCenterOffsetY;

        // Stage 04 movement plan. These fields are reused for every host tick so the two-phase
        // runtime does not allocate one plan or participant object per entity.
        internal bool CrowdPlanActive;
        internal bool CrowdPlanIsRetreating;
        internal bool CrowdPlanIsBinding;
        internal bool CrowdPlanIsBindingAlignment;
        internal bool CrowdPlanUsesHitResponse;
        internal bool CrowdPlanHasTarget;
        internal bool CrowdPlanIsWandering;
        internal bool CrowdPlanMovementPositionChanged;
        internal bool CrowdPlanRemovalRequested;
        internal HostileShadowKnockbackStep CrowdPlanKnockback;
        internal double CrowdPlanCurrentPositionX;
        internal double CrowdPlanCurrentPositionY;
        internal double CrowdPlanNormalPositionX;
        internal double CrowdPlanNormalPositionY;
        internal double CrowdPlanFinalPositionX;
        internal double CrowdPlanFinalPositionY;
        internal double CrowdPlanTargetX;
        internal double CrowdPlanTargetY;
        internal string CrowdPlanCleanupReason { get; set; } = string.Empty;
        internal HostileAttackStateDecision CrowdPlanAttackDecision;
        internal HostileShadowHitResponseDecision CrowdPlanHitResponse;
    }

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly HostileShadowAuthority authority;
    private readonly SanitySmapiResourceService resourceService;
    private readonly HostileShadowMonsterRenderer renderer;
    private readonly SmapiHostileAttackCombatService attackCombat;
    private readonly Func<HostileShadowMonster, int, int, int, Farmer?, int>
        incomingHitHandler;
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
    // DIAG-20260809: 游荡随机源（独立于游戏 RNG，避免扰动其他系统；仅主机使用）。
    private readonly Random wanderRandom = new();
    private readonly HostileShadowLifecycleReceiptStore lifecycleReceipts = new();
    private readonly HostileShadowPhysicalEntityCapability serializationCapability;
    private readonly HostileShadowCrowdCollisionResolver crowdCollisionResolver = new();
    private readonly List<HostileShadowCrowdParticipant> crowdParticipants = new(
        HostileShadowAuthority.MaximumEntities
    );
    private readonly Dictionary<string, HostileShadowCrowdCollisionResolutionEntry>
        crowdResolutions = new(StringComparer.Ordinal);
    private IHostileShadowProjectionPushBoxBridge? projectionPushBoxBridge;
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
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
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

    internal bool BindProjectionPushBoxBridge(
        IHostileShadowProjectionPushBoxBridge bridge,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (disposed || projectionPushBoxBridge is not null)
        {
            reason = disposed
                ? "hostile-shadow.push-box-bridge-disposed"
                : "hostile-shadow.push-box-bridge-already-bound";
            return false;
        }

        projectionPushBoxBridge = bridge;
        // ModEntry constructs this runtime before the projection host. Re-subscribing here makes
        // the projection intent capture run before the shared world solve without changing any
        // SMAPI event source or adding a second update loop.
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        reason = "hostile-shadow.push-box-bridge-bound";
        return true;
    }

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

    /// <summary>
    /// DIAG-20260809: 进入绑定隐藏态。实体不渲染/无敌/行为禁用，位置改由宿主按绑定投影
    /// 低频对齐；返回怪物供宿主读取位置/朝向以生成绑定投影。
    /// </summary>
    internal bool TryBeginBinding(
        long entityId,
        string correlationId,
        out HostileShadowMonster? monster,
        out string reason
    )
    {
        monster = null;
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            reason = "hostile-shadow.binding-correlation-invalid";
            return false;
        }
        if (!entries.TryGetValue(entityId, out var entry))
        {
            reason = "hostile-shadow.binding-entry-missing";
            return false;
        }
        if (entry.IsBindingHidden)
        {
            reason = "hostile-shadow.binding-already-hidden";
            return false;
        }
        entry.BindingCorrelationId = correlationId;
        entry.BindingFacingId = entry.AppliedMovementFacingId;
        entry.BindingAnchorX = entry.Monster.Position.X;
        entry.BindingAnchorY = entry.Monster.Position.Y;
        entry.BindingAlignCooldownTicks = 0;
        // DIAG-20260810: 恐吓结束进入绑定——清除恐吓标记。
        if (entry.IsRetreating)
        {
            entry.IsRetreating = false;
            entry.Monster.modData.Remove(HostileShadowMonster.RetreatingModDataKey);
        }
        // DIAG-20260810: 记录受击框中心相对 Position 的偏移（绑定投影位置=中心）。
        var hurtBox = entry.Monster.GetBoundingBox();
        entry.BindingCenterOffsetX = hurtBox.Center.X - entry.Monster.Position.X;
        entry.BindingCenterOffsetY = hurtBox.Center.Y - entry.Monster.Position.Y;
        entry.Monster.modData[HostileShadowMonster.BindingHiddenModDataKey] = "1";
        monster = entry.Monster;
        reason = "hostile-shadow.binding-started";
        return true;
    }

    /// <summary>
    /// DIAG-20260810: 进入脱战恐吓阶段。实体立即无敌（HandleIncomingHit 守卫）、
    /// 停止行为并播恐吓动画；恐吓结束由宿主调用 TryBeginBinding 隐藏并绑定投影。
    /// </summary>
    internal bool TryBeginRetreat(
        long entityId,
        out HostileShadowMonster? monster,
        out string reason
    )
    {
        monster = null;
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        if (!entries.TryGetValue(entityId, out var entry))
        {
            reason = "hostile-shadow.retreat-entry-missing";
            return false;
        }
        if (entry.IsBindingHidden)
        {
            reason = "hostile-shadow.retreat-already-hidden";
            return false;
        }
        if (entry.IsRetreating)
        {
            reason = "hostile-shadow.retreat-already-active";
            return false;
        }
        entry.IsRetreating = true;
        entry.Monster.modData[HostileShadowMonster.RetreatingModDataKey] = "1";
        // 恐吓动画（渲染器按 Taunt 时序本地推进）；行为由 60Hz 循环 IsRetreating 分支接管。
        entry.Monster.modData[HostileShadowMonster.StateModDataKey] =
            HostileShadowStateIds.Taunt;
        entry.Monster.modData[HostileShadowMonster.HitTeleportVisualPhaseModDataKey] =
            HostileShadowHitTeleportVisualPhaseIds.None;
        entry.AppliedStateId = HostileShadowStateIds.Taunt;
        entry.AppliedHitTeleportVisualPhase = HostileShadowHitTeleportVisualPhaseIds.None;
        monster = entry.Monster;
        reason = "hostile-shadow.retreat-started";
        return true;
    }

    /// <summary>DIAG-20260810: 退出恐吓阶段（恐吓流程失败回滚时清理标记）。</summary>
    internal bool TryExitRetreat(long entityId, out string reason)
    {
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        if (!entries.TryGetValue(entityId, out var entry))
        {
            reason = "hostile-shadow.retreat-entry-missing";
            return false;
        }
        if (!entry.IsRetreating)
        {
            reason = "hostile-shadow.retreat-not-active";
            return false;
        }
        entry.IsRetreating = false;
        entry.Monster.modData.Remove(HostileShadowMonster.RetreatingModDataKey);
        reason = "hostile-shadow.retreat-exited";
        return true;
    }

    /// <summary>
    /// DIAG-20260809: 退出绑定隐藏态（低理智恢复）。实体恢复渲染/受击/行为，
    /// 并在恢复瞬间用绑定投影位置校准一次，保证视觉连续。
    /// </summary>
    internal bool TryExitBinding(long entityId, out string reason)
    {
        if (disposed)
        {
            reason = "hostile-shadow.world-runtime-disposed";
            return false;
        }
        if (!entries.TryGetValue(entityId, out var entry))
        {
            reason = "hostile-shadow.binding-entry-missing";
            return false;
        }
        if (!entry.IsBindingHidden)
        {
            reason = "hostile-shadow.binding-not-hidden";
            return false;
        }
        if (entry.IsRetreating)
        {
            entry.IsRetreating = false;
            entry.Monster.modData.Remove(HostileShadowMonster.RetreatingModDataKey);
        }
        entry.Monster.Position = new Vector2(
            (float)entry.BindingAnchorX,
            (float)entry.BindingAnchorY
        );
        if (entry.BindingFacingId.Length > 0)
        {
            // DIAG-20260809: 恢复瞬间校准朝向（隐藏前朝向），后续由常规索敌/移动覆盖。
            entry.Monster.modData[
                HostileShadowMonster.MovementFacingModDataKey
            ] = entry.BindingFacingId;
        }
        entry.BindingCorrelationId = string.Empty;
        entry.BindingFacingId = string.Empty;
        entry.Monster.modData.Remove(HostileShadowMonster.BindingHiddenModDataKey);
        reason = "hostile-shadow.binding-exited";
        return true;
    }

    /// <summary>
    /// DIAG-20260809: 宿主低频设置绑定锚点（绑定投影的当前位置）。实际移动在
    /// 60Hz 循环按 5-15 tick 冷却应用，避免每帧同步。
    /// </summary>
    internal void ApplyBindingAnchor(long entityId, double x, double y)
    {
        if (disposed || !entries.TryGetValue(entityId, out var entry))
            return;
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return;
        // DIAG-20260810: 投影位置=实体受击框中心——换算成 Position（左上角）锚点，
        // 恢复瞬间贴图中心与投影中心重合（视觉连续）。
        entry.BindingAnchorX = x - entry.BindingCenterOffsetX;
        entry.BindingAnchorY = y - entry.BindingCenterOffsetY;
    }

    /// <summary>DIAG-20260809: 当前隐藏绑定的实体 id 集合（脱战/恢复编排用）。</summary>
    internal IReadOnlyList<long> GetBindingHiddenEntityIds()
    {
        var result = new List<long>();
        foreach (var pair in entries)
        {
            if (pair.Value.IsBindingHidden)
                result.Add(pair.Key);
        }
        return result;
    }

    /// <summary>DIAG-20260811: 指定地点内危险影怪物种分布（AssetBindingId → 数量），切图快速刷新用。</summary>
    internal Dictionary<string, int> CountEntitiesBySpeciesAtLocation(
        string playerKey,
        string locationId
    )
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(locationId))
            return result;
        foreach (var pair in entries)
        {
            if (
                !string.Equals(
                    pair.Value.Location.NameOrUniqueName,
                    locationId,
                    StringComparison.Ordinal
                )
                || !authority.TryGetEntity(pair.Key, out var state)
                || state is null
                || !string.Equals(
                    state.OwnerPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    state.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            if (
                !pair.Value.Monster.modData.TryGetValue(
                    HostileShadowMonster.AssetBindingModDataKey,
                    out var bindingId
                )
                || string.IsNullOrWhiteSpace(bindingId)
            )
            {
                continue;
            }
            result.TryGetValue(bindingId, out var count);
            result[bindingId] = count + 1;
        }
        return result;
    }

    /// <summary>
    /// Refresh capacity is local to the current map and only counts this owner's hostile
    /// entities which are currently locked to that owner. Off-map entities and entities which
    /// have not acquired a target must not suppress the current-map refresh permit.
    /// </summary>
    internal int CountLockedEntitiesForOwnerAtLocation(
        string playerKey,
        string locationId
    )
    {
        if (
            string.IsNullOrWhiteSpace(playerKey)
            || string.IsNullOrWhiteSpace(locationId)
        )
        {
            return 0;
        }

        var count = 0;
        foreach (var pair in entries)
        {
            if (
                !authority.TryGetEntity(pair.Key, out var state)
                || state is null
                || !string.Equals(
                    state.OwnerPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    state.TargetPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    state.LocationId,
                    locationId,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    state.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            count++;
        }

        return count;
    }

    /// <summary>DIAG-20260811: 指定玩家在指定地点的危险影怪数量（上限按所在地图计算）。</summary>
    internal int CountEntitiesForOwnerAtLocation(
        string playerKey,
        string locationId
    )
    {
        if (
            string.IsNullOrWhiteSpace(playerKey)
            || string.IsNullOrWhiteSpace(locationId)
        )
        {
            return 0;
        }
        var count = 0;
        foreach (var pair in entries)
        {
            if (
                !string.Equals(
                    pair.Value.Location.NameOrUniqueName,
                    locationId,
                    StringComparison.Ordinal
                )
                || !authority.TryGetEntity(pair.Key, out var state)
                || state is null
                || !string.Equals(
                    state.OwnerPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>DIAG-20260811: 查询实体所在地点（超限清理按所在地图过滤用）。</summary>
    internal bool TryGetEntityLocationId(long entityId, out string locationId)
    {
        if (entries.TryGetValue(entityId, out var entry))
        {
            locationId = entry.Location.NameOrUniqueName;
            return !string.IsNullOrWhiteSpace(locationId);
        }
        locationId = string.Empty;
        return false;
    }

    internal bool TryGetEntitySpawnGameMinute(
        long entityId,
        out long spawnGameMinute
    )
    {
        if (entries.TryGetValue(entityId, out var entry))
        {
            spawnGameMinute = entry.SpawnGameMinute;
            return spawnGameMinute >= 0;
        }

        spawnGameMinute = 0;
        return false;
    }

    internal bool TryGetActiveAggroLockPlayerKey(
        long entityId,
        out string playerKey
    )
    {
        playerKey = string.Empty;
        if (
            !entries.TryGetValue(entityId, out var entry)
            || !SanityPlayerKey.IsCanonical(entry.AggroLockPlayerKey)
        )
        {
            return false;
        }

        if (!SanityPlayerKey.TryParseCanonicalPlayerId(
                entry.AggroLockPlayerKey,
                out var playerId
            ))
        {
            return false;
        }
        var player = Game1.GetPlayer(playerId, onlyOnline: true);
        if (
            player is null
            || player.currentLocation is null
            || !string.Equals(
                player.currentLocation.NameOrUniqueName,
                entry.Location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        playerKey = entry.AggroLockPlayerKey;
        return true;
    }

    /// <summary>
    /// Reports the mod-owned targeting label for a player on the current location. Both the
    /// ordinary target and the recent-attacker aggro lock count; hidden/bound presentation does not
    /// erase a live label until the targeting runtime clears it.
    /// </summary>
    internal bool IsPlayerTargeted(string playerKey, GameLocation location)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey) || location is null)
            return false;

        foreach (var entry in entries.Values)
        {
            if (!ReferenceEquals(entry.Location, location))
                continue;
            if (
                string.Equals(entry.TargetPlayerKey, playerKey, StringComparison.Ordinal)
                || string.Equals(entry.AggroLockPlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>DIAG-20260809: 当前在册实体 id 列表（脱战 roll 用，稳定顺序）。</summary>
    internal IReadOnlyList<long> GetOrderedEntityIds()
    {
        var result = new List<long>(entries.Count);
        foreach (var entityId in orderedEntityIds)
        {
            if (entries.ContainsKey(entityId))
                result.Add(entityId);
        }
        return result;
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
            // Authority chooses Spawn for standalone entities and Taunt for a harmless-projection
            // conversion. Preserve that initial presentation on the physical monster so a
            // conversion cannot replay the hostile Spawn animation.
            monster.modData[HostileShadowMonster.StateModDataKey] = state.StateId;
            monster.modData[HostileShadowMonster.HitTeleportVisualPhaseModDataKey] =
                state.HitTeleportVisualPhase;
            // DIAG-20260807: 记录配置档位的攻击力，供 LookupAnythingDisplayFake 在 Lookup
            // 构造 Subject 时临时写回 DamageToFarmer（显示用）；本体 DamageToFarmer 保持 0
            // 禁接触伤害。
            monster.modData[HostileShadowMonster.DisplayDamageModDataKey] =
                profile.BaseDamage.ToString(CultureInfo.InvariantCulture);
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
            // DIAG-20260807: 补真实贴图 Sprite（原版字段）。游戏内绘制仍走自定义渲染器
            // draw override，Sprite 仅供 Lookup Anything 等第三方读取显示贴图（原版怪物
            // 都有有效 Sprite）；加载失败时保持构造函数里的占位 Sprite，不阻断物化。
            try
            {
                var idleSlot = attackMetadata!.Idle.AnimationId;
                var spriteResource = resourceService.LoadVisualSlot(idleSlot, 0);
                if (
                    spriteResource.Success
                    && spriteResource.PhysicalResource
                        is XnaSanityTextureResource spriteTexture
                    && spriteResource.VisualPreview is { } spritePreview
                )
                {
                    // 1.6 的 AnimatedSprite：Texture 是只读属性（getter 会按 textureName
                    // 走内容加载），但 spriteTexture 是 public 字段；用无参构造 + 直接赋
                    // spriteTexture，textureName 保持 null 时 loadTexture 短路返回，
                    // 不会尝试加载任何内容。SourceRect 显式转换（preview 用的是 mod 自有
                    // SanityResourceRectangle，不是 XNA Rectangle）。
                    var sprite = new AnimatedSprite();
                    sprite.spriteTexture = spriteTexture.Texture;
                    sprite.SpriteWidth = spritePreview.SourceRectangle.Width;
                    sprite.SpriteHeight = spritePreview.SourceRectangle.Height;
                    sprite.SourceRect = new Rectangle(
                        spritePreview.SourceRectangle.X,
                        spritePreview.SourceRectangle.Y,
                        spritePreview.SourceRectangle.Width,
                        spritePreview.SourceRectangle.Height
                    );
                    sprite.SetOwner(monster);
                    monster.Sprite = sprite;
                }
            }
            catch
            {
                // 贴图缺失时保持占位 Sprite，不阻断物化。
            }
            // DIAG-20260807: 按 profile 的稳定显示名 i18n key 设置实例 Name。
            // Character.Name 是 NetString，写入后会随实体同步；Lookup Anything 读取它时即可
            // 显示当前语言，而不需要把本地化文本写入 gameplay profile。
            monster.Name = helper.Translation.Get(profile.DisplayNameKey).ToString();
            // DIAG-20260807 修正：恢复 DamageToFarmer=profile.BaseDamage 与
            // resilience=profile.Defense（Lookup 显示用，跟随配置档位不写死）。
            // 接触伤害已禁用：原版触发点是 Monster.MovePosition → isCollidingPosition
            // (damagesFarmer)，本类 update 不调 base.update/MovePosition 且已加 MovePosition
            // 空实现兜底；原版防御扣减在 Monster.takeDamage（damage-resilience），本类
            // takeDamage override 不调 base，故 resilience 只影响显示、不会与原版防御双扣。
            // DIAG-20260807：DamageToFarmer 是原版接触伤害字段（怪物移动撞玩家按此扣血）。
        // MovePosition 空实现仍挡不住实测的 10 点伤害（存在其他触发路径），直接置 0 彻底
        // 禁用。注意：本 mod 影怪攻击走自有攻击框系统（伤害=profile.BaseDamage），与此字段
        // 无关；但 Lookup Anything 的攻击力显示读此字段，置 0 后显示为 0（恢复显示需另找
        // Lookup 读取源，暂缓）。
        monster.DamageToFarmer = 0;
            monster.resilience.Value = profile.Defense;
            if (profile.DropTable is { } dropTable)
            {
                // DIAG-20260807 修正：ItemRegistry.GetData 不认识项目自定义语义 id
                // （stardew.item.void-essence），返回 null 导致 Add 从未执行、Lookup 显示无掉落。
                // 虚空精华标准 id 是 (O)769，直接写死；若以后 DropTable 增加其他掉落需改这里。
                // 00:20 主策划裁定：弃用 Data/Monsters 注入（原版概率掉落显示反复改不好，且
                // 会与 CP 包掉落冲突）；改回静态双写 2 个虚空精华（保底 1 + 概率 1，纯 Lookup
                // 展示，均正常色）。实际掉落走 HostileShadowSettlement 自有结算，不受影响。
                monster.objectsToDrop.Add("(O)769");
                monster.objectsToDrop.Add("(O)769");
            }
            if (combatImmunity is not null)
                monster.ApplyCombatImmunity(combatImmunity);
            else
                monster.ApplyDeclaredCombatImmunities(profile.ImmunityTags);
            location.characters.Add(monster);
            if (!location.characters.Contains(monster))
                throw new InvalidOperationException("location-character-add-not-observed");

            var attackState = new HostileAttackStateMachine(
                attackDefinition,
                transitionPolicy,
                state.StateId
            );
            AddPhysical(
                state.EntityId,
                new PhysicalEntry(
                    state.EntityId,
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
                new HostileShadowHitResponseController(
                    attackState,
                    initialHitTeleportVisualPhase: state.HitTeleportVisualPhase
                )
                )
            );
            // DIAG-20260809: 游荡锚点初始 = 生成位置（脱战后更新为最后脱战位置）。
            if (entries.TryGetValue(state.EntityId, out var materializedEntry))
            {
                materializedEntry.WanderAnchorX = state.PositionX;
                materializedEntry.WanderAnchorY = state.PositionY;
                ObserveShadowCreatureSfx(state.EntityId, materializedEntry, state);
            }
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
        entry.AggroLockPlayerKey = request.AttackerPlayerKey;
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
        int xTrajectory,
        int yTrajectory,
        Farmer? attacker
    )
    {
        // DIAG-20260806: 逐条守卫加去重黄字——传送后"框在但无伤害"时，需要精确知道是哪条守卫拒绝。
        // LogOnce 按原因去重（同原因只打一次），不会刷屏。
        if (disposed)
        {
            LogOnce("hostile-shadow.hit-guard-disposed", LogLevel.Warn);
            return 0;
        }
        if (
            monster.modData.TryGetValue(
                HostileShadowMonster.BindingHiddenModDataKey,
                out var bindingHiddenRaw
            )
            && string.Equals(bindingHiddenRaw, "1", StringComparison.Ordinal)
        )
        {
            // DIAG-20260809: 绑定隐藏态无敌——投影外观期间实体不可被攻击。
            LogOnce("hostile-shadow.hit-guard-binding-hidden", LogLevel.Warn);
            return 0;
        }
        if (
            monster.modData.TryGetValue(
                HostileShadowMonster.RetreatingModDataKey,
                out var retreatingRaw
            )
            && string.Equals(retreatingRaw, "1", StringComparison.Ordinal)
        )
        {
            // DIAG-20260811: 脱战恐吓阶段无敌——不扣血、不产生受击跳字。
            LogOnce("hostile-shadow.hit-guard-retreating", LogLevel.Warn);
            return 0;
        }
        if (!Game1.IsMasterGame)
        {
            LogOnce("hostile-shadow.hit-guard-not-master", LogLevel.Warn);
            return 0;
        }
        if (!authority.IsHostSessionActive)
        {
            LogOnce("hostile-shadow.hit-guard-session-inactive", LogLevel.Warn);
            return 0;
        }
        if (damage <= 0)
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-damage-nonpositive (damage=",
                    damage,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (attacker is null || attacker.currentLocation is null)
        {
            LogOnce(
                "hostile-shadow.hit-guard-attacker-invalid",
                LogLevel.Warn
            );
            return 0;
        }
        if (
            !monster.modData.TryGetValue(
                HostileShadowMonster.EntityIdModDataKey,
                out var serializedEntityId
            )
            || !long.TryParse(
                serializedEntityId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var entityId
            )
        )
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-entity-id-missing (entityIdRaw=",
                    serializedEntityId ?? "<null>",
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (!entries.TryGetValue(entityId, out var entry))
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-entry-missing (entity=",
                    entityId,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (!ReferenceEquals(entry.Monster, monster))
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-monster-identity-mismatch (entity=",
                    entityId,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (!ReferenceEquals(entry.Location, attacker.currentLocation))
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-location-mismatch (entity=",
                    entityId,
                    ", entryLocation=",
                    entry.Location.NameOrUniqueName,
                    ", attackerLocation=",
                    attacker.currentLocation.NameOrUniqueName,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (!entry.Location.characters.Contains(monster))
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-not-in-characters (entity=",
                    entityId,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (entry.PendingLethalDamage)
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-pending-lethal (entity=",
                    entityId,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }

        // DIAG-20260806: 将最可疑的“实体/血量一致性”守卫拆出单独检查并记录去重诊断，
        // 用于定位“受击传送后无法被攻击”（玩家命中但无效果）的精确拒绝原因。
        if (!authority.TryGetEntity(entityId, out var state) || state is null)
        {
            LogOnce(
                "hostile-shadow.hit-guard-entity-missing",
                LogLevel.Warn
            );
            return 0;
        }
        if (state.Health != monster.Health)
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-health-mismatch (entity=",
                    entityId,
                    ", stateHealth=",
                    state.Health,
                    ", monsterHealth=",
                    monster.Health,
                    ", stateId=",
                    state.StateId,
                    ", rev=",
                    state.Revision,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (monster.Health <= 0)
        {
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-monster-health-nonpositive (health=",
                    monster.Health,
                    ", stateId=",
                    state.StateId,
                    ")"
                ),
                LogLevel.Warn
            );
            return 0;
        }
        if (
            string.Equals(
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
            LogOnce(
                string.Concat(
                    "hostile-shadow.hit-guard-terminal-state (entity=",
                    entityId,
                    ", stateId=",
                    state.StateId,
                    ")"
                ),
                LogLevel.Warn
            );
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

        // Stardew Monster.takeDamage divides the raw GameLocation trajectory by three before
        // writing the velocity. Keep that input on the custom hit bridge; the host movement plan
        // will consume the resulting velocity exactly once on the next world tick.
        monster.ApplyIncomingHitTrajectory(xTrajectory, yTrajectory);

        // The hit entity itself also changes aggro to the attacking player. When it was chasing
        // or attacking another player, this is the special multiplayer handoff: keep the complete
        // HitTeleport presentation, then resume directly in Chase for the attacker. It must not be
        // confused with ordinary target reacquisition, whose next contact still Taunts first.
        if (
            !damageDecision.PendingDying
            && !string.Equals(
                entry.TargetPlayerKey,
                attackerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            var hadExistingTarget = SanityPlayerKey.IsCanonical(entry.TargetPlayerKey);
            var wasChasingOrAttacking =
                string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Chase,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Attack,
                    StringComparison.Ordinal
                );
            bool handoffPrepared;
            string targetHandoffReason;
            if (hadExistingTarget && wasChasingOrAttacking)
            {
                handoffPrepared = entry.AttackState.RequestTargetHandoffAfterHit(
                    out targetHandoffReason
                );
            }
            else
            {
                handoffPrepared = entry.AttackState.RequestTargetReacquisition(
                    out targetHandoffReason
                );
            }
            if (!handoffPrepared)
            {
                LogOnce(targetHandoffReason, LogLevel.Warn);
            }
            entry.TargetPlayerKey = attackerPlayerKey;
        }
        // DIAG-20260809: 受击拉仇恨传播——被击中影怪附近 30 格（1920px）内、同地点、无索敌的
        // 其他影怪，仇恨转移到攻击者（AggroLockPlayerKey 锁定，不受检测半径限制）。
        PropagateAggroToNearby(entityId, entry, attackerPlayerKey);
        // DIAG-20260902: 不在自定义受击桥里写固定无敌时间。原版
        // GameLocation.damageMonster 会在 takeDamage 返回后按武器路径设置
        // 450 / 2（普通武器）、450 / 3（普通匕首）或在匕首多段攻击时关闭本次计时。
        // 影怪不调用 Monster.update，因此倒计时仍由 HostileShadowMonster.update 递减。
        var previousHealth = monster.Health;
        var proposedRevision = authority.Revision + 1;

        entry.RecentAttackerPlayerKey = attackerPlayerKey;
        entry.AggroLockPlayerKey = attackerPlayerKey;
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
            DeferHitResponseSynchronizationFailure(failure);
            return damageDecision.AppliedDamage;
        }
        if (!decision.Valid)
        {
            monster.Health = previousHealth;
            entry.PendingLethalDamage = false;
            entry.PendingLethalAttackerPlayerKey = string.Empty;
            pendingLethalEntityIds.Remove(entityId);
            LogOnce(decision.Reason, LogLevel.Error);
            DeferHitResponseSynchronizationFailure(decision.Reason);
            return 0;
        }

        monster.Position = new Vector2(
            (float)decision.PositionX,
            (float)decision.PositionY
        );
        ApplyMonsterState(entry);
        if (!TrySynchronizeHitResponse(entityId, entry, state, decision))
        {
            DeferHitResponseSynchronizationFailure(decision.Reason);
            return damageDecision.AppliedDamage;
        }
        if (!damageDecision.PendingDying && damageDecision.AppliedDamage > 0)
        {
            DontStarve.ModEntry.ActiveShadowCreatureSfx?.NotifyHostileHit(
                authority.SessionId,
                entityId,
                entry.Profile.AssetBindingId,
                monster.Position.X,
                monster.Position.Y,
                proposedRevision,
                state.LocationId,
                ClassifyHitSource(attacker)
            );
        }
        if (decision.RemovalRequested)
            authority.CleanupEntity(entityId, decision.Reason);
        return damageDecision.AppliedDamage;
    }

    private static ShadowCreatureSfxHitSource ClassifyHitSource(Farmer attacker)
    {
        if (attacker.CurrentTool is not StardewValley.Tools.MeleeWeapon weapon)
            return ShadowCreatureSfxHitSource.Other;

        return ShadowCreatureSfxHitSourceClassifier.From(
            isMeleeWeapon: true,
            isScythe: weapon.isScythe(),
            weaponType: weapon.type.Value
        );
    }

    /// <summary>
    /// DIAG-20260809: 受击拉仇恨传播。以被击中影怪为中心，30 格半径内、同地点、无索敌、
    /// 非终态的其他影怪，仇恨锁定到攻击者（锁定目标不受 20 格检测半径限制，玩家切图/下线
    /// 后在下个索敌节奏自动清除）。
    /// </summary>
    private void PropagateAggroToNearby(
        long hitEntityId,
        PhysicalEntry hitEntry,
        string attackerPlayerKey
    )
    {
        const double radiusPixels = 30d * Game1.tileSize;
        var radiusSquared = radiusPixels * radiusPixels;
        var hitX = hitEntry.Monster.Position.X;
        var hitY = hitEntry.Monster.Position.Y;
        foreach (var pair in entries)
        {
            var otherId = pair.Key;
            var other = pair.Value;
            if (otherId == hitEntityId)
                continue;
            if (!ReferenceEquals(other.Location, hitEntry.Location))
                continue;
            if (other.TargetPlayerKeyIsCanonical)
                continue;
            var otherStateId = other.AttackState.StateId;
            if (
                string.Equals(
                    otherStateId,
                    HostileShadowStateIds.Dying,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    otherStateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    otherStateId,
                    HostileShadowStateIds.HitTeleport,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            var dx = other.Monster.Position.X - hitX;
            var dy = other.Monster.Position.Y - hitY;
            if ((dx * dx) + (dy * dy) > radiusSquared)
                continue;

            if (
                !other.AttackState.BeginTargetReacquisition(
                    out var reacquisitionReason
                )
            )
            {
                LogOnce(reacquisitionReason, LogLevel.Warn);
                continue;
            }

            // 物理层锁定 + 权威同步。仇恨转移也必须经过 Idle -> Taunt -> Chase，不能只
            // 替换 TargetPlayerKey 后继续沿用旧 Chase 状态。
            other.AggroLockPlayerKey = attackerPlayerKey;
            other.RecentAttackerPlayerKey = attackerPlayerKey;
            other.TargetPlayerKey = attackerPlayerKey;
            ApplyMonsterState(other);
            if (authority.TryGetEntity(otherId, out var otherState) && otherState is not null)
            {
                authority.TryUpdate(
                    new HostileShadowStateUpdate(
                        otherId,
                        otherState.LocationId,
                        other.AttackState.StateId,
                        attackerPlayerKey,
                        otherState.PositionX,
                        otherState.PositionY,
                        otherState.Health,
                        "hostile-shadow.aggro-propagated",
                        other.AttackState.StateId == HostileShadowStateIds.Attack
                            ? otherState.AttackInstanceId
                            : string.Empty,
                        other.AttackState.StateId == HostileShadowStateIds.Attack
                            ? otherState.AttackInstanceRevision
                            : 0,
                        other.AttackState.StateId == HostileShadowStateIds.Attack
                            ? otherState.AttackFrameNumber
                            : 0
                    ),
                    out _
                );
            }
        }
    }

    /// <summary>Returns a finite preferred position, or a finite fallback position.</summary>
    private static bool TryResolveFinitePosition(
        double preferredX,
        double preferredY,
        double fallbackX,
        double fallbackY,
        out double positionX,
        out double positionY
    )
    {
        if (double.IsFinite(preferredX) && double.IsFinite(preferredY))
        {
            positionX = preferredX;
            positionY = preferredY;
            return true;
        }

        if (double.IsFinite(fallbackX) && double.IsFinite(fallbackY))
        {
            positionX = fallbackX;
            positionY = fallbackY;
            return true;
        }

        positionX = double.NaN;
        positionY = double.NaN;
        return false;
    }

    /// <summary>
    /// DIAG-20260809: 无索敌游荡推进。到达目标后随机静息 3-5 秒，再在锚点（生成点/最后脱战
    /// 位置）10 格半径内选随机目标点，以半速朝其移动（到达 0.5 格内停下）。返回是否处于游荡中
    /// （用于帧推进与 WanderActive 标记）。索敌到玩家时主循环不再调用本方法。
    /// </summary>
    private bool TryAdvanceWander(
        PhysicalEntry entry,
        double elapsedSeconds,
        double currentPositionX,
        double currentPositionY,
        ref double normalPositionX,
        ref double normalPositionY,
        ref bool movementPositionChanged
    )
    {
        // 锚点异常时只允许回退到影怪当前坐标；当前坐标也无效则保持 Idle，绝不使用玩家
        // 坐标、地图中心或人为构造的 (0,0)。
        if (
            !TryResolveFinitePosition(
                entry.WanderAnchorX,
                entry.WanderAnchorY,
                currentPositionX,
                currentPositionY,
                out var anchorX,
                out var anchorY
            )
        )
        {
            return false;
        }
        entry.WanderAnchorX = anchorX;
        entry.WanderAnchorY = anchorY;

        if (!entry.HasWanderTarget)
        {
            entry.WanderRemainingMilliseconds -= elapsedSeconds * 1000d;
            if (entry.WanderRemainingMilliseconds > 0d)
                return false;

            // 选新目标：锚点 10 格半径内随机方向/距离（含 0，可原地停一个周期）。
            var angle = wanderRandom.NextDouble() * Math.PI * 2d;
            var radiusPixels = wanderRandom.NextDouble() * (10d * Game1.tileSize);
            entry.WanderTargetX =
                anchorX + Math.Cos(angle) * radiusPixels;
            entry.WanderTargetY =
                anchorY + Math.Sin(angle) * radiusPixels;
            entry.HasWanderTarget = true;
        }

        // 半速移动（MovementSpeed × 0.5）；动画半速在帧推进处（elapsedMs × 0.5）。
        var movement = HostileShadowTargetingEngine.AdvancePosition(
            currentPositionX,
            currentPositionY,
            entry.Monster.StandingPixel.X,
            entry.Monster.StandingPixel.Y,
            entry.WanderTargetX,
            entry.WanderTargetY,
            entry.Profile.MovementSpeed * 0.5d,
            Game1.tileSize * 0.5d,
            elapsedSeconds
        );
        if (!movement.Valid)
            return false;
        movementPositionChanged =
            currentPositionX != movement.PositionX
            || currentPositionY != movement.PositionY;
        normalPositionX = movement.PositionX;
        normalPositionY = movement.PositionY;
        if (
            string.Equals(
                movement.Reason,
                "hostile-shadow.movement-at-stop-distance",
                StringComparison.Ordinal
            )
        )
        {
            // 到达目标点后才开始随机静息；移动期间绝不消耗该倒计时。
            entry.HasWanderTarget = false;
            entry.WanderRemainingMilliseconds =
                3000d + (wanderRandom.NextDouble() * 2000d);
        }
        return true;
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
                DeferHitResponseSynchronizationFailure(
                    "hostile-shadow.revision-overflow"
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
                DeferHitResponseSynchronizationFailure(decision.Reason);
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
            if (
                authority.TryGetEntity(entityId, out var confirmed)
                && confirmed is not null
                && string.Equals(
                    confirmed.StateId,
                    HostileShadowStateIds.Dying,
                    StringComparison.Ordinal
                )
                && confirmed.Health == 0
            )
            {
                DontStarve.ModEntry.ActiveShadowCreatureSfx?.ConfirmHostileDeath(
                    authority.SessionId,
                    entityId,
                    entry.Profile.AssetBindingId,
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y,
                    confirmed.Revision,
                    confirmed.LocationId
                );
            }
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
        ) with
        {
            // Ring.onMonsterSlay uses Monster.Tile (the standing pixel from the actual hurt box),
            // not the entity's raw top-left Position. Freeze that tile into the host settlement so
            // a delayed death confirmation and a replay use the same Napalm center.
            ExplosionTileX = entry.Monster.Tile.X,
            ExplosionTileY = entry.Monster.Tile.Y,
        };
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
                    decision.Reason,
                    hitTeleportVisualPhase: entry.HitResponse.HitTeleportVisualPhase
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
        projectionPushBoxBridge = null;
        peerGate.Dispose();
        HostileShadowMonsterHitBridge.Clear(incomingHitHandler);
        HostileShadowMonsterVisualBridge.Clear(renderer);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            disposed
            || (
                entries.Count == 0
                && (
                    projectionPushBoxBridge is null
                    || !projectionPushBoxBridge.HasActivePushBoxParticipants
                )
            )
            || !Context.IsWorldReady
            || !Game1.IsMasterGame
        )
        {
            return;
        }

        // Physical hostile entities remain frozen during hard pause. Creation and conversion are
        // driven by the host budget path and do not depend on this behavior tick.
        if (
            HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                Game1.activeClickableMenu is not null,
                Game1.IsMultiplayer,
                Game1.paused,
                Game1.game1.IsActive
            )
        )
            return;

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
                    farmer.StandingPixel.Y,
                    IsDangerActive(
                        SanityPlayerKey.FromUniqueMultiplayerId(
                            farmer.UniqueMultiplayerID
                        )
                    )
                )
            );
        }

        var result = playerIndex.Rebuild(samples);
        if (!result.Success)
            LogOnce(result.Reason, LogLevel.Warn);
    }

    private bool IsDangerActive(string playerKey)
    {
        if (
            !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
        )
            return false;
        foreach (var tierId in tier.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.Danger, StringComparison.Ordinal))
                return true;
        }
        return false;
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
            if (entry.IsBindingHidden || entry.IsRetreating)
            {
                // DIAG-20260809: 绑定隐藏态——跳过索敌/快照/TTL（行为禁用，
                // 位置由 AdvanceCachedTargets 按绑定投影低频对齐；投影消失时连带清除）。
                // DIAG-20260810: 恐吓阶段同样跳过（无敌/停止行为/播恐吓动画）。
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
            // DIAG-20260809: 设计稿“切图后不计入消失倒计时”——owner 玩家不在影怪所在地图时
            // 冻结 TTL（自然消失倒计时视为无限），玩家返回同地点后恢复计时。
            // 影怪留在旧地图（不跟随、不清除），玩家返回后自然进入索敌范围。
            var hasPlayersOnLocation = playerIndex.HasPlayers(
                entry.Location.NameOrUniqueName
            );
            AdvanceNoTargetClock(entry, hasPlayersOnLocation, timeApi.Time);
            var decision = HostileShadowTargetingEngine.Evaluate(
                new HostileShadowTargetingInput
                {
                    EntityId = entityId,
                    OwnerPlayerKey = state.OwnerPlayerKey,
                    LocationId = state.LocationId,
                    AggroLockPlayerKey = entry.AggroLockPlayerKey,
                    RecentAttackerPlayerKey = entry.RecentAttackerPlayerKey,
                    LockedTargetPlayerKey = entry.TargetPlayerKey,
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
                    NoTargetSinceGameMinute = entry.NoTargetSinceGameMinute,
                    NoTargetElapsedGameMinutes = entry.NoTargetElapsedGameMinutes,
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
                // Natural disappearance is not a kill: enter the shared Dying animation path
                // with no settlement receipt, then let AdvanceCachedTargets remove the entity
                // after the complete death animation. This keeps no-target cleanup loot-free.
                entry.Monster.Health = 0;
                entry.TargetPlayerKey = string.Empty;
                entry.AggroLockPlayerKey = string.Empty;
                entry.RecentAttackerPlayerKey = string.Empty;
                var naturalDying = entry.HitResponse.BeginNaturalDying(
                    entry.Monster.Position.X,
                    entry.Monster.Position.Y
                );
                if (
                    !naturalDying.Valid
                    || !TrySynchronizeHitResponse(
                        entityId,
                        entry,
                        state,
                        naturalDying
                    )
                )
                {
                    LogOnce(naturalDying.Reason, LogLevel.Warn);
                    continue;
                }
                ApplyMonsterState(entry);
                continue;
            }

            var previousTargetPlayerKey = entry.TargetPlayerKey;
            if (string.IsNullOrEmpty(decision.TargetPlayerKey))
            {
                if (!entry.NoTargetSinceGameMinute.HasValue)
                {
                    entry.NoTargetSinceGameMinute = timeApi.Time;
                    entry.NoTargetElapsedGameMinutes = 0;
                    entry.NoTargetLastObservedGameMinute = timeApi.Time;
                    entry.NoTargetClockRunning = hasPlayersOnLocation;
                }
            }
            else
            {
                entry.NoTargetSinceGameMinute = null;
                entry.NoTargetElapsedGameMinutes = 0;
                entry.NoTargetLastObservedGameMinute = 0;
                entry.NoTargetClockRunning = false;
            }

            // DIAG-20260809: 受击拉仇恨锁定——锁定玩家仍在线同地点时强制保持目标（不受
            // 20 格检测半径限制）；玩家切图/下线则清除锁定，回到常规索敌。
            if (!string.IsNullOrEmpty(entry.AggroLockPlayerKey))
            {
                if (
                    playerIndex.TryGetPlayer(
                        entry.AggroLockPlayerKey,
                        out var lockedTarget
                    )
                    && lockedTarget is not null
                    && string.Equals(
                        lockedTarget.LocationId,
                        entry.Location.NameOrUniqueName,
                        StringComparison.Ordinal
                    )
                )
                {
                    decision = new HostileShadowTargetingDecision(
                        true,
                        false,
                        HostileShadowStateIds.Chase,
                        entry.AggroLockPlayerKey,
                        HostileShadowTargetSource.RecentAttacker,
                        entry.Monster.Position.X,
                        entry.Monster.Position.Y,
                        "hostile-shadow.target-aggro-lock"
                    );
                }
                else
                {
                    entry.AggroLockPlayerKey = string.Empty;
                }
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
            {
                if (
                    ShouldBeginTargetReacquisition(
                        entry,
                        previousTargetPlayerKey,
                        decision.TargetPlayerKey
                    )
                    && !entry.AttackState.BeginTargetReacquisition(
                        out var reacquisitionReason
                    )
                )
                {
                    LogOnce(reacquisitionReason, LogLevel.Warn);
                }
                entry.TargetPlayerKey = decision.TargetPlayerKey;
            }
            else if (entry.AttackState.CurrentInstance is { } attack)
                entry.TargetPlayerKey = attack.TargetPlayerKey;

            ApplyMonsterState(entry);
            // DIAG-20260806: 常规同步的 TryUpdate 此前静默吞掉失败（out _）——若传送后
            // authority 状态与物理实体脱节，这里会持续失败且无任何日志，导致攻击/受击全失效。
            // 失败时记录一次去重黄字，便于实机定位。
            if (
                !authority.TryUpdate(
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
                    out var syncReason
                )
            )
            {
                LogOnce(
                    string.Concat(
                        "hostile-shadow.routine-sync-rejected (",
                        syncReason,
                        ", stateId=",
                        entry.AttackState.StateId,
                        ", health=",
                        entry.Monster.Health,
                        ")"
                    ),
                    LogLevel.Warn
                );
            }
        }
    }

    private static void AdvanceNoTargetClock(
        PhysicalEntry entry,
        bool hasPlayersOnLocation,
        long currentGameMinute
    )
    {
        if (!entry.NoTargetSinceGameMinute.HasValue)
            return;

        if (!hasPlayersOnLocation)
        {
            entry.NoTargetClockRunning = false;
            entry.NoTargetLastObservedGameMinute = currentGameMinute;
            return;
        }

        if (!entry.NoTargetClockRunning)
        {
            entry.NoTargetClockRunning = true;
            entry.NoTargetLastObservedGameMinute = currentGameMinute;
            return;
        }

        if (currentGameMinute > entry.NoTargetLastObservedGameMinute)
        {
            entry.NoTargetElapsedGameMinutes = checked(
                entry.NoTargetElapsedGameMinutes
                    + currentGameMinute
                    - entry.NoTargetLastObservedGameMinute
            );
        }
        entry.NoTargetLastObservedGameMinute = currentGameMinute;
    }

    private static bool ShouldBeginTargetReacquisition(
        PhysicalEntry entry,
        string previousTargetPlayerKey,
        string nextTargetPlayerKey
    )
    {
        var hadPreviousTarget = SanityPlayerKey.IsCanonical(previousTargetPlayerKey);
        var hasNextTarget = SanityPlayerKey.IsCanonical(nextTargetPlayerKey);
        if (
            hadPreviousTarget
            && !string.Equals(
                previousTargetPlayerKey,
                nextTargetPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        // An aggro event can write a new target before the fixed update observes the preceding
        // target loss. If the state is still Chase, force the handoff through Idle so the incoming
        // player receives the same first-contact Taunt.
        return !hadPreviousTarget
            && hasNextTarget
            && string.Equals(
                entry.AttackState.StateId,
                HostileShadowStateIds.Chase,
                StringComparison.Ordinal
            );
    }

    private HostileShadowKnockbackStep PlanKnockbackStep(
        PhysicalEntry entry
    )
    {
        var velocityX = entry.Monster.xVelocity;
        var velocityY = entry.Monster.yVelocity;
        if (velocityX == 0f && velocityY == 0f)
            return HostileShadowKnockbackStep.None;

        if (entry.Monster.Slipperiness == -1)
            return HostileShadowKnockbackStep.None;

        var blocked = IsKnockbackBlocked(entry, velocityX, velocityY);
        return HostileShadowKnockbackPolicy.Plan(
            velocityX,
            velocityY,
            entry.Monster.Slipperiness,
            blocked,
            stunned: entry.Monster.stunTime.Value > 0
        );
    }

    private static bool IsKnockbackBlocked(
        PhysicalEntry entry,
        float velocityX,
        float velocityY
    )
    {
        var boundingBox = entry.Monster.GetBoundingBox();
        var startX = boundingBox.X;
        var startY = boundingBox.Y;
        var destinationX = startX + (int)velocityX;
        var destinationY = startY - (int)velocityY;
        var subdivisions = 1;
        if (!entry.Monster.isGlider.Value)
        {
            if (
                boundingBox.Width > 0
                && Math.Abs((int)velocityX) > boundingBox.Width
            )
            {
                subdivisions = Math.Max(
                    subdivisions,
                    (int)Math.Ceiling(
                        Math.Abs((float)(int)velocityX) / boundingBox.Width
                    )
                );
            }
            if (
                boundingBox.Height > 0
                && Math.Abs((int)velocityY) > boundingBox.Height
            )
            {
                subdivisions = Math.Max(
                    subdivisions,
                    (int)Math.Ceiling(
                        Math.Abs((float)(int)velocityY) / boundingBox.Height
                    )
                );
            }
        }

        for (var index = 1; index <= subdivisions; index++)
        {
            var candidate = boundingBox;
            candidate.X = (int)(
                startX
                + ((destinationX - startX) * (double)index / subdivisions)
            );
            candidate.Y = (int)(
                startY
                + ((destinationY - startY) * (double)index / subdivisions)
            );
            if (
                entry.Location.isCollidingPosition(
                    candidate,
                    Game1.viewport,
                    isFarmer: false,
                    damagesFarmer: entry.Monster.DamageToFarmer,
                    glider: entry.Monster.isGlider.Value,
                    character: entry.Monster
                )
            )
            {
                return true;
            }
        }
        return false;
    }

    private void AdvanceCachedTargets(bool snapshotCadence)
    {
        var entityCount = CaptureEntityIterationOrder();
        crowdParticipants.Clear();
        crowdResolutions.Clear();
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
            ResetCrowdPlan(entry);
            var currentPositionX = (double)entry.Monster.Position.X;
            var currentPositionY = (double)entry.Monster.Position.Y;
            var knockbackStep = PlanKnockbackStep(entry);
            if (!knockbackStep.Valid)
            {
                LogOnce(
                    "hostile-shadow.knockback-state-invalid",
                    LogLevel.Error
                );
                entry.Monster.ClearKnockbackVelocity();
                knockbackStep = HostileShadowKnockbackStep.None;
            }
            if (entry.IsRetreating)
            {
                // DIAG-20260810: 脱战恐吓——无敌/停止行为/保持恐吓动画，
                // 由宿主在恐吓结束后调用 TryBeginBinding 进入绑定隐藏态。
                entry.Monster.ClearKnockbackVelocity();
                PrepareRetreatCrowdPlan(
                    entry,
                    currentPositionX,
                    currentPositionY
                );
                continue;
            }
            if (entry.IsBindingHidden)
            {
                // DIAG-20260809: 绑定隐藏态——行为禁用（不索敌/不攻击/不游荡），
                // 仅低频位置对齐（5-15 tick），对齐变化时发一次位置同步。
                entry.Monster.ClearKnockbackVelocity();
                PrepareBindingCrowdPlan(
                    entry,
                    entityId,
                    currentPositionX,
                    currentPositionY
                );
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

            // 目标切换必须先于 Chase -> Idle 和游荡判断处理。否则丢目标这一 tick 会跳过
            // TryAdvanceWander，下一 tick 又已把 HadTargetLastTick 写成 false，只能错误地继续
            // 使用生成点。
            var hadTargetLastTick = entry.HadTargetLastTick;
            if (hasTarget)
            {
                // 重新索敌后，旧的游荡目的地立即失效；本轮沿既有索敌/恐吓/追击链推进。
                entry.HasWanderTarget = false;
            }
            else if (hadTargetLastTick)
            {
                entry.HasWanderTarget = false;
                if (
                    TryResolveFinitePosition(
                        currentPositionX,
                        currentPositionY,
                        state.PositionX,
                        state.PositionY,
                        out var anchorX,
                        out var anchorY
                    )
                )
                {
                    entry.WanderAnchorX = anchorX;
                    entry.WanderAnchorY = anchorY;
                    entry.WanderRemainingMilliseconds =
                        3000d + (wanderRandom.NextDouble() * 2000d);
                }
            }
            entry.HadTargetLastTick = hasTarget;

            if (
                string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Despawn,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
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
                entry.AttackState.ObserveTargetPresence(hasTarget);
                PrepareHitResponseCrowdPlan(
                    entry,
                    currentPositionX,
                    currentPositionY,
                    hasTarget,
                    knockbackStep
                );
                continue;
            }

            // A knockback slice owns this tick's movement. The AI position and wander movement
            // must not be added to it, or the same hit would move the entity twice.
            var normalPositionX = currentPositionX + knockbackStep.OffsetX;
            var normalPositionY = currentPositionY + knockbackStep.OffsetY;
            var movementPositionChanged = knockbackStep.Active
                && (
                    knockbackStep.OffsetX != 0d
                    || knockbackStep.OffsetY != 0d
                );
            if (
                hasTarget
                && !knockbackStep.Active
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
                        currentPositionX != movement.PositionX
                        || currentPositionY != movement.PositionY;
                    normalPositionX = movement.PositionX;
                    normalPositionY = movement.PositionY;
                }
            }

            // DIAG-20260809: 无索敌游荡——Idle 且无目标时，到达目标后静息 3-5 秒，再在锚点
            // （生成点/最后脱战位置）10 格半径内选随机目标点，半速移动过去；一旦索敌到玩家（hasTarget=true）
            // 本分支立即不执行，游荡目标自然作废，进入既有 索敌→恐吓→追击 链。
            var isWanderingNow = false;
            if (
                !hasTarget
                && !knockbackStep.Active
                && string.Equals(
                    entry.AttackState.StateId,
                    HostileShadowStateIds.Idle,
                    StringComparison.Ordinal
                )
            )
            {
                isWanderingNow = TryAdvanceWander(
                    entry,
                    FixedUpdateSeconds,
                    currentPositionX,
                    currentPositionY,
                    ref normalPositionX,
                    ref normalPositionY,
                    ref movementPositionChanged
                );
            }
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.WanderActiveModDataKey,
                isWanderingNow ? "1" : string.Empty
            );

            var inAttackRange = hasTarget
                && WithinRange(
                    entry.Monster.StandingPixel.X
                        + (normalPositionX - currentPositionX),
                    entry.Monster.StandingPixel.Y
                        + (normalPositionY - currentPositionY),
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
                normalPositionX,
                normalPositionY,
                entry.Monster.StandingPixel.X
                    + (normalPositionX - currentPositionX),
                entry.Monster.StandingPixel.Y
                    + (normalPositionY - currentPositionY),
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
            PrepareNormalCrowdPlan(
                entry,
                currentPositionX,
                currentPositionY,
                decision.PositionX,
                decision.PositionY,
                movementPositionChanged,
                isWanderingNow,
                hasTarget,
                targetX,
                targetY,
                decision,
                knockbackStep
            );
        }

        projectionPushBoxBridge?.AppendPushBoxParticipants(crowdParticipants);

        ResolveCrowdPlans();
        for (var index = 0; index < entityCount; index++)
        {
            var entityId = entityIterationBuffer[index];
            if (entries.TryGetValue(entityId, out var entry))
                ApplyCrowdPlan(entityId, entry, snapshotCadence);
        }
    }

    private void ObserveShadowCreatureSfx(
        long entityId,
        PhysicalEntry entry,
        ShadowStateSnapshot? stateOverride = null
    )
    {
        var service = DontStarve.ModEntry.ActiveShadowCreatureSfx;
        if (service is null)
            return;
        var state = stateOverride;
        if (state is null && (!authority.TryGetEntity(entityId, out state) || state is null))
            return;
        service.ObserveHostile(
            authority.SessionId,
            entityId,
            entry.Profile.AssetBindingId,
            entry.AttackState.StateId,
            entry.Monster.Position.X,
            entry.Monster.Position.Y,
            entry.Monster.Health,
            entry.Monster.MaxHealth,
            state.AttackInstanceId,
            state.Revision,
            state.LocationId
        );
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
            // DIAG-20260809: 游荡中（有游荡目标）保留移动帧推进，不 Reset；
            // 到达目标/索敌后 HasWanderTarget=false，自动回到 Idle 帧。
            && !entry.HasWanderTarget
        )
        {
            movementPresentation.Reset();
        }
        ApplyAttackModDataIfChanged(
            entry,
            entry.AttackState.StateId,
            instance?.InstanceId ?? string.Empty,
            instance?.Revision ?? 0,
            instance?.FrameNumber ?? 0,
            string.Equals(
                entry.AttackState.StateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            )
                ? entry.HitResponse.HitTeleportVisualPhase
                : HostileShadowHitTeleportVisualPhaseIds.None
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

    private static void ResetCrowdPlan(PhysicalEntry entry)
    {
        entry.CrowdPlanActive = false;
        entry.CrowdPlanIsRetreating = false;
        entry.CrowdPlanIsBinding = false;
        entry.CrowdPlanIsBindingAlignment = false;
        entry.CrowdPlanUsesHitResponse = false;
        entry.CrowdPlanHasTarget = false;
        entry.CrowdPlanIsWandering = false;
        entry.CrowdPlanMovementPositionChanged = false;
        entry.CrowdPlanRemovalRequested = false;
        entry.CrowdPlanKnockback = default;
        entry.CrowdPlanCurrentPositionX = 0d;
        entry.CrowdPlanCurrentPositionY = 0d;
        entry.CrowdPlanNormalPositionX = 0d;
        entry.CrowdPlanNormalPositionY = 0d;
        entry.CrowdPlanFinalPositionX = 0d;
        entry.CrowdPlanFinalPositionY = 0d;
        entry.CrowdPlanTargetX = 0d;
        entry.CrowdPlanTargetY = 0d;
        entry.CrowdPlanCleanupReason = string.Empty;
        entry.CrowdPlanAttackDecision = default;
        entry.CrowdPlanHitResponse = default;
    }

    private void PrepareRetreatCrowdPlan(
        PhysicalEntry entry,
        double currentPositionX,
        double currentPositionY
    )
    {
        entry.CrowdPlanActive = true;
        entry.CrowdPlanIsRetreating = true;
        entry.CrowdPlanCurrentPositionX = currentPositionX;
        entry.CrowdPlanCurrentPositionY = currentPositionY;
        entry.CrowdPlanNormalPositionX = currentPositionX;
        entry.CrowdPlanNormalPositionY = currentPositionY;
        entry.CrowdPlanFinalPositionX = currentPositionX;
        entry.CrowdPlanFinalPositionY = currentPositionY;
        UpdateCrowdParticipant(
            entry,
            currentPositionX,
            currentPositionY,
            currentPositionX,
            currentPositionY,
            isBinding: false
        );
    }

    private void PrepareBindingCrowdPlan(
        PhysicalEntry entry,
        long entityId,
        double currentPositionX,
        double currentPositionY
    )
    {
        entry.BindingAlignCooldownTicks--;
        var align = entry.BindingAlignCooldownTicks <= 0;
        if (align)
        {
            entry.BindingAlignCooldownTicks =
                5 + (int)(entityId % 11L);
        }

        var normalPositionX = currentPositionX;
        var normalPositionY = currentPositionY;
        if (
            align
            && double.IsFinite(entry.BindingAnchorX)
            && double.IsFinite(entry.BindingAnchorY)
        )
        {
            normalPositionX = entry.BindingAnchorX;
            normalPositionY = entry.BindingAnchorY;
        }
        else
        {
            align = false;
        }

        entry.CrowdPlanActive = true;
        entry.CrowdPlanIsBinding = true;
        entry.CrowdPlanIsBindingAlignment = align;
        entry.CrowdPlanCurrentPositionX = currentPositionX;
        entry.CrowdPlanCurrentPositionY = currentPositionY;
        entry.CrowdPlanNormalPositionX = normalPositionX;
        entry.CrowdPlanNormalPositionY = normalPositionY;
        entry.CrowdPlanFinalPositionX = normalPositionX;
        entry.CrowdPlanFinalPositionY = normalPositionY;
        UpdateCrowdParticipant(
            entry,
            currentPositionX,
            currentPositionY,
            normalPositionX,
            normalPositionY,
            isBinding: true
        );
    }

    private void PrepareHitResponseCrowdPlan(
        PhysicalEntry entry,
        double currentPositionX,
        double currentPositionY,
        bool hasTarget,
        HostileShadowKnockbackStep knockbackStep
    )
    {
        var responseInputPositionX = currentPositionX + knockbackStep.OffsetX;
        var responseInputPositionY = currentPositionY + knockbackStep.OffsetY;
        var response = entry.HitResponse.Advance(
            responseInputPositionX,
            responseInputPositionY,
            FixedUpdateSeconds * 1000d,
            hasTarget
        );
        if (!response.Valid)
        {
            LogOnce(response.Reason, LogLevel.Error);
            DeferHitResponseSynchronizationFailure(response.Reason);
            return;
        }

        entry.CrowdPlanActive = true;
        entry.CrowdPlanKnockback = knockbackStep;
        entry.CrowdPlanUsesHitResponse = true;
        entry.CrowdPlanHasTarget = hasTarget;
        entry.CrowdPlanCurrentPositionX = currentPositionX;
        entry.CrowdPlanCurrentPositionY = currentPositionY;
        entry.CrowdPlanNormalPositionX = response.PositionX;
        entry.CrowdPlanNormalPositionY = response.PositionY;
        entry.CrowdPlanFinalPositionX = response.PositionX;
        entry.CrowdPlanFinalPositionY = response.PositionY;
        entry.CrowdPlanRemovalRequested = response.RemovalRequested;
        entry.CrowdPlanCleanupReason = response.StateId == HostileShadowStateIds.Dying
            ? HostileShadowCleanupReasonIds.DyingCompleted
            : response.Reason;
        entry.CrowdPlanHitResponse = response;
        UpdateCrowdParticipant(
            entry,
            currentPositionX,
            currentPositionY,
            response.PositionX,
            response.PositionY,
            isBinding: false
        );
    }

    private void PrepareNormalCrowdPlan(
        PhysicalEntry entry,
        double currentPositionX,
        double currentPositionY,
        double normalPositionX,
        double normalPositionY,
        bool movementPositionChanged,
        bool isWanderingNow,
        bool hasTarget,
        double targetX,
        double targetY,
        HostileAttackStateDecision decision,
        HostileShadowKnockbackStep knockbackStep
    )
    {
        entry.CrowdPlanActive = true;
        entry.CrowdPlanKnockback = knockbackStep;
        entry.CrowdPlanHasTarget = hasTarget;
        entry.CrowdPlanIsWandering = isWanderingNow;
        entry.CrowdPlanMovementPositionChanged = movementPositionChanged;
        entry.CrowdPlanCurrentPositionX = currentPositionX;
        entry.CrowdPlanCurrentPositionY = currentPositionY;
        entry.CrowdPlanNormalPositionX = normalPositionX;
        entry.CrowdPlanNormalPositionY = normalPositionY;
        entry.CrowdPlanFinalPositionX = normalPositionX;
        entry.CrowdPlanFinalPositionY = normalPositionY;
        entry.CrowdPlanTargetX = targetX;
        entry.CrowdPlanTargetY = targetY;
        entry.CrowdPlanAttackDecision = decision;
        UpdateCrowdParticipant(
            entry,
            currentPositionX,
            currentPositionY,
            normalPositionX,
            normalPositionY,
            isBinding: false
        );
    }

    private void UpdateCrowdParticipant(
        PhysicalEntry entry,
        double currentPositionX,
        double currentPositionY,
        double normalPositionX,
        double normalPositionY,
        bool isBinding
    )
    {
        var movementVector = new HostileShadowPushBoxPoint(
            normalPositionX - currentPositionX,
            normalPositionY - currentPositionY
        );
        var worldBox = default(HostileShadowPushBoxWorldRectangle);
        HostileShadowPushBoxGeometry.TryCreateWorldBox(
            entry.AttackDefinition,
            currentPositionX,
            currentPositionY,
            out worldBox
        );
        entry.CrowdParticipant.Update(
            new HostileShadowPushBoxPoint(currentPositionX, currentPositionY),
            new HostileShadowPushBoxPoint(normalPositionX, normalPositionY),
            movementVector,
            worldBox,
            isActive: true,
            isBinding,
            isBindingRepresentative: isBinding
        );
        crowdParticipants.Add(entry.CrowdParticipant);
    }

    private void ResolveCrowdPlans()
    {
        if (crowdParticipants.Count == 0)
            return;

        var resolution = crowdCollisionResolver.Resolve(crowdParticipants);
        projectionPushBoxBridge?.ApplyPushBoxResolution(resolution);
        foreach (var resolved in resolution.Entries)
        {
            if (resolved.IsFinite)
                crowdResolutions[resolved.StableId] = resolved;
        }
        if (resolution.PairFailureCount > 0)
            LogOnce(resolution.Reason, LogLevel.Warn);
    }

    private void ApplyCrowdPlan(
        long entityId,
        PhysicalEntry entry,
        bool snapshotCadence
    )
    {
        if (!entry.CrowdPlanActive)
            return;

        var finalPositionX = entry.CrowdPlanNormalPositionX;
        var finalPositionY = entry.CrowdPlanNormalPositionY;
        if (
            crowdResolutions.TryGetValue(
                entry.CrowdParticipant.StableId,
                out var resolved
            )
            && resolved.FinalPosition.IsFinite
        )
        {
            finalPositionX = resolved.FinalPosition.X;
            finalPositionY = resolved.FinalPosition.Y;
        }
        if (!double.IsFinite(finalPositionX) || !double.IsFinite(finalPositionY))
        {
            LogOnce(
                "hostile-shadow.push-box-final-position-invalid",
                LogLevel.Error
            );
            return;
        }

        var correctionX = finalPositionX - entry.CrowdPlanNormalPositionX;
        var correctionY = finalPositionY - entry.CrowdPlanNormalPositionY;
        if (
            !double.IsFinite(correctionX)
            || !double.IsFinite(correctionY)
        )
        {
            LogOnce(
                "hostile-shadow.push-box-correction-invalid",
                LogLevel.Error
            );
            return;
        }

        if (
            !entry.CrowdPlanUsesHitResponse
            && !entry.CrowdPlanIsRetreating
            && !entry.CrowdPlanIsBinding
            && string.Equals(
                entry.CrowdPlanAttackDecision.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && entry.AttackState.CurrentInstance is not null
            && (correctionX != 0d || correctionY != 0d)
            && !entry.AttackState.TryTranslateCurrentAttackOrigin(
                correctionX,
                correctionY,
                out var attackOriginReason
            )
        )
        {
            LogOnce(attackOriginReason, LogLevel.Error);
            finalPositionX = entry.CrowdPlanNormalPositionX;
            finalPositionY = entry.CrowdPlanNormalPositionY;
            correctionX = 0d;
            correctionY = 0d;
        }

        entry.CrowdPlanFinalPositionX = finalPositionX;
        entry.CrowdPlanFinalPositionY = finalPositionY;
        entry.Monster.Position = new Vector2(
            (float)finalPositionX,
            (float)finalPositionY
        );
        // Position and velocity are committed together. This is the single consumer of the
        // planned vanilla knockback slice; no client-side update or AI movement may consume it a
        // second time.
        entry.Monster.ApplyKnockbackStep(entry.CrowdPlanKnockback);

        if (entry.CrowdPlanUsesHitResponse)
        {
            ApplyHitResponseCrowdPlan(entityId, entry);
            return;
        }
        if (entry.CrowdPlanIsRetreating)
        {
            ApplyRetreatCrowdPlan(entityId, entry, snapshotCadence);
            return;
        }
        if (entry.CrowdPlanIsBinding)
        {
            ApplyBindingCrowdPlan(entityId, entry, snapshotCadence);
            return;
        }

        ApplyNormalCrowdPlan(
            entityId,
            entry,
            snapshotCadence,
            correctionX,
            correctionY
        );
    }

    private void ApplyNormalCrowdPlan(
        long entityId,
        PhysicalEntry entry,
        bool snapshotCadence,
        double correctionX,
        double correctionY
    )
    {
        var decision = entry.CrowdPlanAttackDecision;
        if (
            entry.MovementPresentation is { } movementPresentation
            && !movementPresentation.TryAdvance(
                (
                    entry.CrowdPlanHasTarget
                    && string.Equals(
                        decision.StateId,
                        HostileShadowStateIds.Chase,
                        StringComparison.Ordinal
                    )
                )
                    || entry.CrowdPlanIsWandering,
                entry.CrowdPlanMovementPositionChanged,
                entry.Monster.StandingPixel.X,
                entry.Monster.StandingPixel.Y,
                entry.CrowdPlanIsWandering
                    ? entry.WanderTargetX
                    : entry.CrowdPlanTargetX,
                entry.CrowdPlanIsWandering
                    ? entry.WanderTargetY
                    : entry.CrowdPlanTargetY,
                // DIAG-20260809: 游荡动画半速（elapsedMs × 0.5）。
                FixedUpdateSeconds
                    * 1000d
                    * (entry.CrowdPlanIsWandering ? 0.5d : 1d),
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
            return;
        }

        if (
            !authority.TryGetEntity(entityId, out var state)
            || state is null
        )
        {
            RemovePhysical(entityId);
            return;
        }

        var finalPositionChanged =
            entry.CrowdPlanCurrentPositionX != entry.CrowdPlanFinalPositionX
            || entry.CrowdPlanCurrentPositionY != entry.CrowdPlanFinalPositionY;
        var mustSynchronize = decision.StateChanged
            || decision.AttackFrameChanged
            || (
                finalPositionChanged
                && (
                    snapshotCadence
                    || string.Equals(
                        decision.StateId,
                        HostileShadowStateIds.Attack,
                        StringComparison.Ordinal
                    )
                )
            );
        if (
            mustSynchronize
            && string.Equals(
                decision.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && !TryAlignAttackInstanceRevision(entry, state, out var revisionReason)
        )
        {
            LogOnce(revisionReason, LogLevel.Error);
            return;
        }

        ApplyMonsterState(entry);
        if (mustSynchronize)
        {
            var attackInstance = entry.AttackState.CurrentInstance;
            if (
                !authority.TryUpdate(
                    new HostileShadowStateUpdate(
                        entityId,
                        state.LocationId,
                        decision.StateId,
                        entry.TargetPlayerKey,
                        entry.CrowdPlanFinalPositionX,
                        entry.CrowdPlanFinalPositionY,
                        entry.Monster.Health,
                        decision.Reason,
                        attackInstance?.InstanceId ?? string.Empty,
                        attackInstance?.Revision ?? 0,
                        attackInstance?.FrameNumber ?? 0
                    ),
                    out var updateReason
                )
            )
            {
                LogOnce(updateReason, LogLevel.Warn);
                return;
            }
            authority.TryGetEntity(entityId, out state);
        }

        if (
            state is not null
            && string.Equals(
                state.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
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
        ObserveShadowCreatureSfx(entityId, entry);
    }

    private bool TryAlignAttackInstanceRevision(
        PhysicalEntry entry,
        ShadowStateSnapshot current,
        out string reason
    )
    {
        if (entry.AttackState.CurrentInstance is not { } instance)
        {
            reason = "hostile-shadow.attack-instance-missing-for-sync";
            return false;
        }

        var revision = 0L;
        if (
            string.Equals(
                current.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && string.Equals(
                current.AttackInstanceId,
                instance.InstanceId,
                StringComparison.Ordinal
            )
        )
        {
            revision = current.AttackInstanceRevision;
        }
        else if (authority.Revision < long.MaxValue)
        {
            revision = authority.Revision + 1;
        }

        reason = string.Empty;
        if (
            revision <= 0
            || !entry.AttackState.TrySetCurrentAttackRevision(
                revision,
                out reason
            )
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.attack-instance-revision-invalid";
            return false;
        }

        reason = "hostile-shadow.attack-instance-revision-aligned";
        return true;
    }

    private void ApplyHitResponseCrowdPlan(
        long entityId,
        PhysicalEntry entry
    )
    {
        var response = entry.CrowdPlanHitResponse;
        var finalPositionChanged =
            entry.CrowdPlanCurrentPositionX != entry.CrowdPlanFinalPositionX
            || entry.CrowdPlanCurrentPositionY != entry.CrowdPlanFinalPositionY;
        ApplyMonsterState(entry);
        if (
            !authority.TryGetEntity(entityId, out var state)
            || state is null
        )
        {
            RemovePhysical(entityId);
            return;
        }

        if (
            response.StateChanged
            || response.PositionChanged
            || finalPositionChanged
        )
        {
            var synchronize = response with
            {
                PositionX = entry.CrowdPlanFinalPositionX,
                PositionY = entry.CrowdPlanFinalPositionY,
                PositionChanged = response.PositionChanged || finalPositionChanged,
            };
            if (
                response.RemovalRequested
                && !response.StateChanged
                && !response.PositionChanged
            )
            {
                if (
                    !TrySynchronizePositionOnly(
                        entityId,
                        state,
                        entry.CrowdPlanFinalPositionX,
                        entry.CrowdPlanFinalPositionY,
                        response.Reason,
                        out var positionReason
                    )
                )
                {
                    DeferHitResponseSynchronizationFailure(positionReason);
                    return;
                }
            }
            else if (
                !TrySynchronizeHitResponse(
                    entityId,
                    entry,
                    state,
                    synchronize
                )
            )
            {
                DeferHitResponseSynchronizationFailure(response.Reason);
                return;
            }
        }

        if (response.RemovalRequested)
        {
            authority.CleanupEntity(
                entityId,
                entry.CrowdPlanCleanupReason
            );
        }
        ObserveShadowCreatureSfx(entityId, entry);
    }

    private void ApplyRetreatCrowdPlan(
        long entityId,
        PhysicalEntry entry,
        bool snapshotCadence
    )
    {
        SetModDataIfChanged(
            entry.Monster,
            HostileShadowMonster.StateModDataKey,
            HostileShadowStateIds.Taunt
        );
        SetModDataIfChanged(
            entry.Monster,
            HostileShadowMonster.HitTeleportVisualPhaseModDataKey,
            HostileShadowHitTeleportVisualPhaseIds.None
        );
        entry.AppliedStateId = HostileShadowStateIds.Taunt;
        entry.AppliedHitTeleportVisualPhase = HostileShadowHitTeleportVisualPhaseIds.None;
        var finalPositionChanged =
            entry.CrowdPlanCurrentPositionX != entry.CrowdPlanFinalPositionX
            || entry.CrowdPlanCurrentPositionY != entry.CrowdPlanFinalPositionY;
        if (finalPositionChanged && snapshotCadence)
        {
            if (
                !authority.TryGetEntity(entityId, out var state)
                || state is null
            )
            {
                RemovePhysical(entityId);
                return;
            }
            if (
                !TrySynchronizePositionOnly(
                    entityId,
                    state,
                    entry.CrowdPlanFinalPositionX,
                    entry.CrowdPlanFinalPositionY,
                    "hostile-shadow.retreat-push-box",
                    out var reason
                )
            )
            {
                LogOnce(reason, LogLevel.Warn);
                return;
            }
        }
        ObserveShadowCreatureSfx(entityId, entry);
    }

    private void ApplyBindingCrowdPlan(
        long entityId,
        PhysicalEntry entry,
        bool snapshotCadence
    )
    {
        var normalPositionChanged =
            entry.CrowdPlanCurrentPositionX != entry.CrowdPlanNormalPositionX
            || entry.CrowdPlanCurrentPositionY != entry.CrowdPlanNormalPositionY;
        var finalPositionChanged =
            entry.CrowdPlanCurrentPositionX != entry.CrowdPlanFinalPositionX
            || entry.CrowdPlanCurrentPositionY != entry.CrowdPlanFinalPositionY;
        var shouldAlign = entry.CrowdPlanIsBindingAlignment
            && (normalPositionChanged || finalPositionChanged);
        if (shouldAlign || (finalPositionChanged && snapshotCadence))
        {
            if (
                !authority.TryGetEntity(entityId, out var state)
                || state is null
            )
            {
                RemovePhysical(entityId);
                return;
            }
            var stateId = shouldAlign
                ? HostileShadowStateIds.Idle
                : state.StateId;
            var targetPlayerKey = shouldAlign
                ? string.Empty
                : state.TargetPlayerKey;
            if (
                !TrySynchronizePositionOnly(
                    entityId,
                    state,
                    entry.CrowdPlanFinalPositionX,
                    entry.CrowdPlanFinalPositionY,
                    shouldAlign
                        ? "hostile-shadow.binding-anchor-align"
                        : "hostile-shadow.binding-push-box",
                    out var reason,
                    stateId,
                    targetPlayerKey
                )
            )
            {
                LogOnce(reason, LogLevel.Warn);
                return;
            }
        }
        ObserveShadowCreatureSfx(entityId, entry);
    }

    private bool TrySynchronizePositionOnly(
        long entityId,
        ShadowStateSnapshot current,
        double positionX,
        double positionY,
        string reason,
        out string failureReason,
        string? stateIdOverride = null,
        string? targetPlayerKeyOverride = null
    )
    {
        var stateId = stateIdOverride ?? current.StateId;
        var targetPlayerKey = targetPlayerKeyOverride ?? current.TargetPlayerKey;
        if (
            !authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    current.LocationId,
                    stateId,
                    targetPlayerKey,
                    positionX,
                    positionY,
                    current.Health,
                    reason,
                    stateId == HostileShadowStateIds.Attack
                        ? current.AttackInstanceId
                        : string.Empty,
                    stateId == HostileShadowStateIds.Attack
                        ? current.AttackInstanceRevision
                        : 0,
                    stateId == HostileShadowStateIds.Attack
                        ? current.AttackFrameNumber
                        : 0
                ),
                out failureReason
            )
        )
        {
            return false;
        }

        failureReason = "hostile-shadow.position-synchronized";
        return true;
    }

    private static void ApplyAttackModDataIfChanged(
        PhysicalEntry entry,
        string stateId,
        string attackInstanceId,
        long attackInstanceRevision,
        int attackFrameNumber,
        string hitTeleportVisualPhase
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
                entry.AppliedHitTeleportVisualPhase,
                hitTeleportVisualPhase,
                StringComparison.Ordinal
            )
        )
        {
            SetModDataIfChanged(
                entry.Monster,
                HostileShadowMonster.HitTeleportVisualPhaseModDataKey,
                hitTeleportVisualPhase
            );
            entry.AppliedHitTeleportVisualPhase = hitTeleportVisualPhase;
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
            state.AttackFrameNumber,
            state.HitTeleportVisualPhase
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
            ObserveShadowCreatureSfx(state.EntityId, entry, state);
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
        DontStarve.ModEntry.ActiveShadowCreatureSfx?.RemoveOwner(
            authority.SessionId,
            entityId.ToString(CultureInfo.InvariantCulture)
        );
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

    private void DeferHitResponseSynchronizationFailure(string detail)
    {
        // A transient authority/receipt mismatch must not turn a living shadow into a cleanup.
        // The physical state remains registered and the next bounded host update can retry the
        // same transition or reconcile it as a duplicate.
        LogOnce("hostile-shadow.hit-response-sync-deferred", LogLevel.Warn);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            LogOnce(
                string.Concat("hostile-shadow.hit-response-sync-detail-", detail),
                LogLevel.Debug
            );
        }
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
