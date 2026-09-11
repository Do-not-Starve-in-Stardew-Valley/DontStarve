#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
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
using StardewValley.GameData;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// SMAPI lifecycle/context adapter around the pure host authority and the real location-owned
/// Monster runtime. Physical spawning remains fail-closed unless the startup net roundtrip probe,
/// exact peer-version gate, shared visual contract, and location materialization all succeed.
/// </summary>
internal sealed class SmapiHostileShadowHost
    : IShadowProjectionConversionIntentSink,
        IHostileShadowLocationOccupancyProvider,
        IShadowCreaturePushBoxIntentSink,
        IHostileShadowConversionRequestHandler,
        IHostileShadowAggroHintHandler,
        IHostileShadowAttackHitHandler,
        IHostileShadowPhysicalCapabilityHandler,
        IHostileShadowPushBoxIntentHandler,
        IHostileShadowPushBoxResultHandler,
        IHostileShadowProjectionPushBoxBridge,
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
    // DIAG-20260807: ds_spawn 中心偏移需要读取碰撞元数据（ActorOrigin/Pivot/HurtBox）。
    private readonly SanitySmapiResourceService resources;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool worldMutationSuspended = true;
    private bool disposed;
    // DIAG-20260809: 绑定投影（危险实体隐藏态外观）管理。脱战时生成绑定投影并把实体隐藏
    // （不渲染/无敌/行为禁用），危险实体位置低频跟随投影；低理智恢复或投影被清时解除/连带清除。
    private SmapiHarmlessProjectionHost? projectionHost;
    private readonly Dictionary<long, string> bindingCorrelationByEntity = new();
    private readonly Dictionary<string, long> bindingEntityByCorrelation =
        new(StringComparer.Ordinal);
    private readonly Random bindingRandom = new();
    private readonly HostileShadowCheckSchedule<long> bindingRollSchedule =
        new(BindingRollIntervalMinutes);
    private readonly HostileShadowCheckSchedule<long> overCapCheckSchedule =
        new(OverCapTrimIntervalMinutes);
    private readonly HostileShadowCheckSchedule<string> overCapProjectionCheckSchedule =
        new(OverCapTrimIntervalMinutes, StringComparer.Ordinal);
    private readonly HostileShadowNaturalSpawnRequestIds naturalSpawnRequestIds = new();
    private int bindingAlignTickCounter;
    private const long OverCapTrimIntervalMinutes = 10;
    // DIAG-20260809: 脱战参数（主策划 17:03 设计稿）：san 回升 >17.5%（Danger 退出）后，
    // 每 10 游戏分钟对每只影怪 roll 25% 脱战；绑定投影位置对齐低频 10 tick。
    private const long BindingRollIntervalMinutes = 10;
    private const double BindingRollChance = 0.25d;
    private const int BindingAlignCadenceTicks = 10;
    // DIAG-20260810: 脱战恐吓时长（毫秒）。roll 中后先播恐吓动画（期间无敌），
    // 恐吓结束才隐藏+绑定投影——恐吓是“留有余地”的视觉缓冲。
    private const double RetreatTauntMilliseconds = 1200d;
    // DIAG-20260810: 恐吓中的实体（entityId → 恐吓开始真实毫秒）。
    private readonly Dictionary<long, double> pendingRetreats = new();
    // 超限清理只登记危险影怪的延迟生命周期：先完成恐吓，再由绑定投影完成约 1 秒淡出。
    private readonly HashSet<long> pendingOverCapRetreats = new();
    // entityId -> true means the entity was already in its own Taunt action, so over-cap cleanup
    // must wait for that action to finish without starting a second Taunt.
    private readonly Dictionary<long, bool> pendingOverCapActions = new();
    private readonly HashSet<long> pendingOverCapBindingFades = new();
    private readonly HashSet<string> pendingOverCapProjectionFades =
        new(StringComparer.Ordinal);
    private const int OverCapFadeOutMilliseconds = 1000;
    // TimeApi may publish a forward catch-up sequence one minute at a time. Keep only the last
    // minute until the normal UpdateTicked phase; a non-unit OnSync clears it and rebases once.
    private long pendingScheduledShadowCheckMinute = -1;
    // DIAG-20260811: 切图快速刷新——切图时记录旧地图危险影怪物种分布，
    // 新地图内每 2 秒刷 1 只（数量 = min(旧地图数量, 当前密度档上限)）；
    // 旧地图影怪留原地冻结、不计入新地图上限（上限按所在地图计算）。
    private readonly Dictionary<string, Queue<string>> pendingFastSpawnsByPlayer =
        new(StringComparer.Ordinal);
    private double fastSpawnNextDueMilliseconds;
    private const double FastSpawnIntervalMilliseconds = 2000d;
    // DIAG-20260812: 玩家最近所在地图（每 tick 记录）——Warp 事件触发时
    // currentLocation 可能已切到新地图，需用旧位置统计切图前影怪分布。
    private readonly Dictionary<string, string> lastLocationByPlayer =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> naturalRefreshDiagnosticStates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fastRefreshDiagnosticStates =
        new(StringComparer.Ordinal);

    private const long PushBoxIntentFreshnessTicks = 30;
    private const int MaximumRemotePushBoxParticipants =
        HostileShadowProtocol.MaximumPushBoxEntriesPerBatch;
    private readonly Dictionary<string, RemotePushBoxState> remotePushBoxStates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastRemotePushBoxBatchByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastRemotePushBoxLocationByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostileShadowCrowdParticipant>
        localPushBoxParticipants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShadowCreaturePushBoxIntent>
        localPushBoxIntents = new(StringComparer.Ordinal);
    private readonly List<ShadowCreaturePushBoxIntent> localPushBoxIntentBuffer =
        new(HostileShadowProtocol.MaximumPushBoxEntriesPerBatch);
    private readonly HashSet<string> activeLocalPushBoxKeys =
        new(StringComparer.Ordinal);
    private readonly List<string> staleLocalPushBoxKeys = new();
    private readonly List<string> staleRemotePushBoxKeys = new();
    private readonly Dictionary<string, HostileAttackRuntimeDefinition>
        projectionPushBoxDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<ShadowProjectionPushBoxResultEntry>>
        pushBoxResultEntriesByPlayer = new();
    private long lastAppliedPushBoxResultBatchNonce;

    private sealed class RemotePushBoxState
    {
        internal RemotePushBoxState(
            long senderPlayerId,
            string ownerPlayerKey,
            string locationId,
            string speciesId,
            string correlationId,
            HostileShadowCrowdParticipant participant
        )
        {
            SenderPlayerId = senderPlayerId;
            OwnerPlayerKey = ownerPlayerKey;
            LocationId = locationId;
            SpeciesId = speciesId;
            CorrelationId = correlationId;
            Participant = participant;
        }

        internal long SenderPlayerId { get; }
        internal string OwnerPlayerKey { get; }
        internal string LocationId { get; set; }
        internal string SpeciesId { get; }
        internal string CorrelationId { get; }
        internal HostileShadowCrowdParticipant Participant { get; }
        internal long Revision { get; set; }
        internal long BatchNonce { get; set; }
        internal long LastReceivedTick { get; set; }
        internal double AuthoritativePositionX { get; set; }
        internal double AuthoritativePositionY { get; set; }
        internal bool HasAuthoritativePosition { get; set; }
        internal double NormalTargetPositionX { get; set; }
        internal double NormalTargetPositionY { get; set; }
    }

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
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));

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
        if (!multiplayer.BindPushBoxHandlers(this, this, out var pushBoxTransportReason))
        {
            monitor.Log(
                $"Hostile shadow PushBox transport binding failed ({pushBoxTransportReason}).",
                LogLevel.Error
            );
        }

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        timeApi.OnUpdate.Add(OnMinuteUpdate);
        timeApi.OnSync.Add(OnTimeSynchronized);
        // DIAG-20260809: 绑定投影对齐/连带清除的 tick 挂点。
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // DIAG-20260807：主策划裁定——不使用 Data/Monsters 注入的原版概率掉落显示
        // （反复尝试未生效，且会与之后 CP 包给怪物添加掉落物产生叠加/覆盖冲突）。
        // 掉落显示一律走 objectsToDrop（TryMaterialize 静态写 2 个虚空精华，纯展示；
        // 实际掉落走 HostileShadowSettlement 自有结算，不受影响）。不注册任何
        // Data/Monsters 占位条目，避免抑制/覆盖 CP 或其他 mod 的掉落配置。

        LogOnce(
            physical.Reason,
            physical.IsAvailable ? LogLevel.Debug : LogLevel.Error
        );
    }

    internal HostileShadowAuthority Authority => authority;

    internal bool IsPlayerTargeted(Farmer player)
    {
        if (
            disposed
            || player is null
            || player.currentLocation is null
        )
        {
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID);
        return world.IsPlayerTargeted(playerKey, player.currentLocation);
    }

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
        // DIAG-20260828: Sanity's initial tier event may have been published before this host's
        // SaveLoaded session reset. Reconcile from the current snapshot immediately before
        // building the conversion envelope so a valid low-Sanity projection is not retried forever
        // against a missing local Danger revision (including client-local projection requests).
        SynchronizeDangerEpochFromCurrentTierState(intent.PlayerKey);
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
                out _
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
        // DIAG-20260809: 转化语义（主策划 16:36 定稿）——Danger 档（≤15%）内两种影怪
        // 都能从无害转化而来；10% 档只约束自然刷新（interval），不约束转化（转化 1:1
        // 替换无害投影，密度额度已由投影侧 cap 管理）。此前按 terrorbeakActive 选档
        // 导致 15%~10% 区间转化恐怖尖喙被拒（budget.terrorbeak-requires-hostile10）。

        return TrySpawn(
            request.CorrelationId,
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            player,
            bindingId,
            "hostile-shadow.spawn.owner-projection-conversion",
            // DIAG-20260812: 原地转化——请求带投影原位时在无害影怪原位置生成
            // 危险实体（不再瞬移到玩家脚下）；无位置（联机旧消息）回退玩家位置。
            request.HasPosition ? (float?)request.PositionX : null,
            request.HasPosition ? (float?)request.PositionY : null
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

    /// <summary>
    /// DIAG-20260806: 测试命令 ds_spawn clear 用。清理全部 authority 实体
    /// （通过 Removed delta 联动物理实体移除），释放占用与转换纪元。
    /// </summary>
    public int DebugClearAll()
    {
        if (disposed || !authority.IsHostSessionActive)
            return 0;
        return authority.CleanupAll(
            "hostile-shadow.debug-clear-all"
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
        timeApi.OnSync.Remove(OnTimeSynchronized);
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        multiplayer.Dispose();
        sessionLifecycleCoordinator.Dispose();
        authority.EndSession(HostileShadowCleanupReasonIds.Disposed);
        ClearPushBoxState();
        world.Dispose();
        loggedReasons.Clear();
        pendingOverCapRetreats.Clear();
        pendingOverCapActions.Clear();
        pendingOverCapBindingFades.Clear();
        pendingOverCapProjectionFades.Clear();
        ClearShadowCheckSchedules();
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
        naturalRefreshDiagnosticStates.Clear();
        fastRefreshDiagnosticStates.Clear();
        ClearShadowCheckSchedules();
        pendingOverCapProjectionFades.Clear();
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
            SynchronizeDangerEpochsForOnlinePlayers();
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

        if (
            HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                Game1.activeClickableMenu is not null,
                Game1.IsMultiplayer,
                Game1.paused,
                Game1.game1.IsActive
            )
        )
        {
            return;
        }

        // Do not perform the roll inside this callback: TimeApi can call it repeatedly while
        // synchronizing a large forward time jump. The normal tick consumes only the final
        // minute published for that frame.
        pendingScheduledShadowCheckMinute = gameMinute;
    }

    private void OnTimeSynchronized(long gameMinute, long delta)
    {
        if (disposed)
            return;

        if (delta is 0 or 1)
            return;

        // TimeApi owns the clock and may have already published a forward catch-up sequence.
        // Rebase once at the final time instead of replaying every skipped 10-minute window.
        pendingScheduledShadowCheckMinute = -1;
        RebaseShadowCheckSchedules(gameMinute);
        monitor.Log(
            string.Concat(
                "Hostile shadow scheduled checks rebased after time sync: minute=",
                gameMinute.ToString(CultureInfo.InvariantCulture),
                ", delta=",
                delta.ToString(CultureInfo.InvariantCulture)
            ),
            LogLevel.Debug
        );
    }

    private void AdvanceNaturalIntervalSpawns(int elapsedMilliseconds)
    {
        if (
            disposed
            || worldMutationSuspended
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !authority.IsHostSessionActive
            || !authority.IsEnabled
            || !Context.IsWorldReady
        )
        {
            return;
        }

        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            var locationId = player.currentLocation?.NameOrUniqueName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(locationId))
            {
                LogNaturalRefreshState(
                    playerKey,
                    locationId,
                    "location-unavailable",
                    "current location unavailable"
                );
                continue;
            }
            if (
                !TryGetDangerTier(
                    playerKey,
                    out var terrorbeakActive,
                    out var dangerReason
                )
            )
            {
                LogNaturalRefreshState(
                    playerKey,
                    locationId,
                    dangerReason,
                    "danger tier is inactive or unavailable"
                );
                continue;
            }

            var poolTier = terrorbeakActive
                ? SanityShadowPoolTier.Hostile10
                : SanityShadowPoolTier.Hostile15;
            if (
                !HostileShadowSpeciesBindingPolicy.TrySelectIntervalBinding(
                    poolTier,
                    out var bindingId,
                    out var selectionReason
                )
                || !HostileShadowSpeciesBindingPolicy.TryResolveSpecies(
                    bindingId,
                    out var requestedSpecies,
                    out _
                )
            )
            {
                LogOnce(selectionReason, LogLevel.Warn);
                continue;
            }

            var evaluation = lifecycle.EvaluateHostileShadowBudgetRealTime(
                playerKey,
                timeApi.Time,
                CountCurrentMapLockedShadowOccupancy(playerKey, locationId),
                elapsedMilliseconds,
                requestedSpecies
            );
            if (
                evaluation.Status
                != SanityShadowBudgetEvaluationStatus.PermitGranted
            )
            {
                LogNaturalRefreshState(
                    playerKey,
                    locationId,
                    string.Concat(
                        "budget-",
                        evaluation.Status.ToString(),
                        ":",
                        evaluation.Reason
                    ),
                    string.Concat(
                        "occupancy=",
                        evaluation.Occupancy.ToString(CultureInfo.InvariantCulture),
                        ", cap=",
                        evaluation.Cap.ToString(CultureInfo.InvariantCulture),
                        ", reason=",
                        evaluation.Reason
                    )
                );
                continue;
            }

            LogNaturalRefreshState(
                playerKey,
                locationId,
                "permit-granted",
                string.Concat(
                    "occupancy=",
                    evaluation.Occupancy.ToString(CultureInfo.InvariantCulture),
                    ", cap=",
                    evaluation.Cap.ToString(CultureInfo.InvariantCulture)
                )
            );

            if (
                !naturalSpawnRequestIds.TryNext(
                    authority.SessionId,
                    playerKey,
                    out var requestId
                )
            )
            {
                LogOnce(
                    "hostile-shadow.interval-request-id-unavailable",
                    LogLevel.Error
                );
                continue;
            }
            var result = TrySpawn(
                requestId,
                HostileShadowSpawnOrigin.Interval,
                player,
                bindingId,
                "hostile-shadow.spawn.interval",
                preEvaluatedBudget: evaluation
            );
            if (
                result.Status is HostileShadowSpawnStatus.Unavailable
                    or HostileShadowSpawnStatus.Rejected
            )
            {
                LogOnce(result.Reason, LogLevel.Warn);
            }
            LogNaturalSpawnResult(playerKey, locationId, result);
        }
    }

    /// <summary>
    /// DIAG-20260809: 脱战 roll——Danger 档全退出（san>17.5%）且每只影怪自己的
    /// 10 游戏分钟检查时间已到时，roll 25% 脱战（隐藏+绑定投影）。
    /// </summary>
    private void RollBindings(long gameMinute)
    {
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
            {
                bindingRollSchedule.Remove(entityId);
                overCapCheckSchedule.Remove(entityId);
                continue;
            }
            if (state.Health <= 0)
            {
                bindingRollSchedule.Remove(entityId);
                overCapCheckSchedule.Remove(entityId);
                continue;
            }
            if (!world.TryGetEntitySpawnGameMinute(entityId, out var spawnGameMinute))
                continue;

            bindingRollSchedule.Register(entityId, spawnGameMinute);
            overCapCheckSchedule.Register(entityId, spawnGameMinute);
            if (bindingCorrelationByEntity.ContainsKey(entityId))
                continue;
            if (
                !bindingRollSchedule.TryConsumeIfDue(
                    entityId,
                    gameMinute,
                    spawnGameMinute
                )
            )
                continue;
            if (!CanRetreatForCurrentTarget(entityId, state))
                continue;
            if (bindingRandom.NextDouble() >= BindingRollChance)
                continue;
            BeginRetreat(entityId, state);
        }
    }

    /// <summary>
    /// DIAG-20260810: 单只影怪脱战编排：恐吓（无敌+播恐吓动画，留有余地）→
    /// 恐吓结束（RetreatTauntMilliseconds）→ BindNow 隐藏+绑定投影。
    /// </summary>
    private bool BeginRetreat(long entityId, ShadowStateSnapshot state)
    {
        if (projectionHost is null)
            return false;
        if (
            !world.TryBeginRetreat(
                entityId,
                out var monster,
                out var retreatReason
            )
            || monster is null
        )
        {
            LogOnce(retreatReason, LogLevel.Warn);
            return false;
        }
        pendingRetreats[entityId] =
            Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        LogOnce("hostile-shadow.retreat-started", LogLevel.Debug);
        return true;
    }

    /// <summary>DIAG-20260810: 恐吓结束——进入绑定隐藏态并生成绑定投影。</summary>
    private void CompleteRetreat(long entityId)
    {
        pendingRetreats.Remove(entityId);
        var overCapCleanup = pendingOverCapRetreats.Remove(entityId);
        if (projectionHost is null)
            return;
        if (!authority.TryGetEntity(entityId, out var state) || state is null)
        {
            world.TryExitRetreat(entityId, out _);
            return;
        }
        if (!BindNow(entityId, state) || !overCapCleanup)
            return;
        BeginOverCapBindingFade(entityId);
    }

    /// <summary>
    /// DIAG-20260809: 单只影怪立即脱战：进入绑定隐藏态 → 在实体受击框中心
    /// 生成同朝向绑定投影；投影生成失败则回滚隐藏。调试命令（ds_spawn bind）
    /// 直接调用本方法；自然脱战经恐吓流程（BeginRetreat→CompleteRetreat）后调用。
    /// </summary>
    private bool BindNow(long entityId, ShadowStateSnapshot state)
    {
        if (projectionHost is null)
            return false;
        if (!projectionHost.TryGetBindingOwner(out var owner))
        {
            LogOnce("hostile-shadow.binding-owner-unavailable", LogLevel.Warn);
            world.TryExitRetreat(entityId, out _);
            return false;
        }
        if (
            !world.TryBeginBinding(
                entityId,
                "pending",
                out var monster,
                out var bindReason
            )
            || monster is null
        )
        {
            LogOnce(bindReason, LogLevel.Warn);
            world.TryExitRetreat(entityId, out _);
            return false;
        }
        var speciesId = ResolveProjectionSpeciesId(state.AssetBindingId);
        if (speciesId is null)
        {
            world.TryExitBinding(entityId, out _);
            return false;
        }
        var facingId = monster.modData.TryGetValue(
            HostileShadowMonster.MovementFacingModDataKey,
            out var rawFacing
        )
            ? rawFacing
            : string.Empty;
        // DIAG-20260810: 绑定投影位置=实体受击框中心（此前用 Position 左上角导致
        // 投影贴图锚点落在怪物左上角、整体偏左上——14 号点位偏移根因）。
        var hurtBox = monster.GetBoundingBox();
        if (
            !projectionHost.TrySpawnBindingProjection(
                owner,
                speciesId,
                hurtBox.Center.X,
                hurtBox.Center.Y,
                facingId,
                out var correlationId,
                out var spawnReason
            )
        )
        {
            world.TryExitBinding(entityId, out _);
            LogOnce(spawnReason, LogLevel.Warn);
            return false;
        }
        bindingCorrelationByEntity[entityId] = correlationId;
        bindingEntityByCorrelation[correlationId] = entityId;
        authority.SetBindingState(
            entityId,
            true,
            correlationId,
            facingId,
            out _
        );
        // 高理智目标触发随机脱战后，绑定投影进入高理智淡出；这只影响已经完成
        // 脱战的实体，不会把所有危险影怪在 ShadowCreatures 档退出时一起带走。
        // 调试召唤与自然召唤都经过同一脱战绑定流程；绑定完成后按当前目标是否
        // 仍处于 ShadowCreatures 档决定是否进入高理智淡出。
        if (!IsShadowCreaturesActiveFor(GetRetreatSubjectPlayerKey(entityId, state)))
        {
            projectionHost.BeginBindingFadeOut(correlationId);
        }
        LogOnce("hostile-shadow.binding-started", LogLevel.Debug);
        return true;
    }

    /// <summary>
    /// DIAG-20260809: 低理智恢复——全部绑定影怪解除隐藏、恢复危险形态（贴图恢复、
    /// 进入正常索敌→恐吓→追击程序；血量留在实体上不会回满）。
    /// </summary>
    private void RestoreAllBindings()
    {
        if (bindingCorrelationByEntity.Count == 0)
            return;
        var entityIds = new List<long>(bindingCorrelationByEntity.Keys);
        entityIds.Sort();
        foreach (var entityId in entityIds)
        {
            if (
                !bindingCorrelationByEntity.TryGetValue(
                    entityId,
                    out var correlationId
                )
            )
            {
                continue;
            }
            if (!world.TryExitBinding(entityId, out var exitReason))
            {
                LogOnce(exitReason, LogLevel.Warn);
                continue;
            }
            authority.SetBindingState(
                entityId,
                false,
                string.Empty,
                string.Empty,
                out _
            );
            if (
                projectionHost is not null
                && !projectionHost.TryRemoveBindingProjection(
                    correlationId,
                    out var removeReason
                )
            )
            {
                LogOnce(removeReason, LogLevel.Warn);
            }
            bindingEntityByCorrelation.Remove(correlationId);
            bindingCorrelationByEntity.Remove(entityId);
            bindingRollSchedule.RebaseKey(entityId, timeApi.Time);
        }
        LogOnce("hostile-shadow.bindings-restored", LogLevel.Debug);
    }

    private void RestoreBindingsForPlayer(string playerKey)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return;
        var entityIds = new List<long>();
        foreach (var entityId in bindingCorrelationByEntity.Keys)
        {
            if (
                authority.TryGetEntity(entityId, out var state)
                && state is not null
                && IsRetreatSubjectForPlayer(entityId, state, playerKey)
            )
            {
                entityIds.Add(entityId);
            }
        }
        entityIds.Sort();
        foreach (var entityId in entityIds)
        {
            if (!bindingCorrelationByEntity.TryGetValue(entityId, out var correlationId))
                continue;
            if (!world.TryExitBinding(entityId, out var exitReason))
            {
                LogOnce(exitReason, LogLevel.Warn);
                continue;
            }
            authority.SetBindingState(entityId, false, string.Empty, string.Empty, out _);
            if (
                projectionHost is not null
                && !projectionHost.TryRemoveBindingProjection(correlationId, out var removeReason)
            )
            {
                LogOnce(removeReason, LogLevel.Warn);
            }
            bindingEntityByCorrelation.Remove(correlationId);
            bindingCorrelationByEntity.Remove(entityId);
            bindingRollSchedule.RebaseKey(entityId, timeApi.Time);
        }
    }

    /// <summary>
    /// DIAG-20260809: 绑定投影对齐/连带清除 tick。每 10 tick 把绑定投影位置低频推给
    /// 危险实体；绑定投影被清（高 san 淡出/驱赶/远离/切图）时连带清除隐藏实体。
    /// </summary>
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            disposed
            || worldMutationSuspended
            || !Context.IsWorldReady
        )
        {
            return;
        }

        if (
            HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                Game1.activeClickableMenu is not null,
                Game1.IsMultiplayer,
                Game1.paused,
                Game1.game1.IsActive
            )
        )
            return;

        // Natural refreshes and cut-map fast refreshes use real elapsed milliseconds. They must
        // continue during CJB's time-freeze mode, but must stop during the hard pause/menu/focus
        // pause just like monster behavior and animation do.
        TrackWarpAndQueueFastSpawns();
        var elapsedMilliseconds = (int)Math.Min(
            int.MaxValue,
            Math.Max(0d, Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds)
        );
        AdvanceNaturalIntervalSpawns(elapsedMilliseconds);

        var scheduledCheckMinute = pendingScheduledShadowCheckMinute;
        pendingScheduledShadowCheckMinute = -1;
        if (scheduledCheckMinute >= 0)
        {
            // These are two independent clocks. Each shadow consumes its own due check, so one
            // shadow's retreat roll cannot advance or suppress another shadow's over-cap roll.
            TrimOverCap(scheduledCheckMinute);
            if (projectionHost is not null)
                RollBindings(scheduledCheckMinute);
        }

        if (
            projectionHost is null
            || (
                bindingCorrelationByEntity.Count == 0
                && pendingRetreats.Count == 0
                && pendingOverCapActions.Count == 0
                && pendingOverCapBindingFades.Count == 0
                && pendingFastSpawnsByPlayer.Count == 0
            )
        )
        {
            return;
        }

        // DIAG-20260810: 恐吓完成检查——恐吓动画播完后进入绑定隐藏态。
        if (pendingRetreats.Count > 0)
        {
            var nowMilliseconds =
                Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
            List<long>? ready = null;
            foreach (var pair in pendingRetreats)
            {
                if (nowMilliseconds - pair.Value >= RetreatTauntMilliseconds)
                {
                    ready ??= new List<long>();
                    ready.Add(pair.Key);
                }
            }
            if (ready is not null)
            {
                foreach (var entityId in ready)
                    CompleteRetreat(entityId);
            }
        }

        AdvancePendingOverCapActions();

        // DIAG-20260811: 切图快速刷新推进（每 2 秒刷 1 只，陆续出现）。
        AdvanceFastSpawns();
        AdvanceOverCapBindingFades();

        bindingAlignTickCounter++;
        if (bindingAlignTickCounter < BindingAlignCadenceTicks)
            return;
        bindingAlignTickCounter = 0;

        List<long>? lost = null;
        foreach (var pair in bindingCorrelationByEntity)
        {
            if (
                !projectionHost.TryGetBindingProjectionPosition(
                    pair.Value,
                    out var positionX,
                    out var positionY
                )
            )
            {
                lost ??= new List<long>();
                lost.Add(pair.Key);
                continue;
            }
            world.ApplyBindingAnchor(pair.Key, positionX, positionY);
        }
        if (lost is null)
            return;
        foreach (var entityId in lost)
        {
            if (
                !bindingCorrelationByEntity.TryGetValue(
                    entityId,
                    out var correlationId
                )
            )
            {
                continue;
            }
            bindingEntityByCorrelation.Remove(correlationId);
            bindingCorrelationByEntity.Remove(entityId);
            if (world.TryExitBinding(entityId, out _))
            {
                authority.SetBindingState(
                    entityId,
                    false,
                    string.Empty,
                    string.Empty,
                    out _
                );
            }
            authority.CleanupEntity(
                entityId,
                "hostile-shadow.cleanup.binding-projection-lost"
            );
            bindingRollSchedule.Remove(entityId);
            overCapCheckSchedule.Remove(entityId);
        }
        LogOnce(
            "hostile-shadow.binding-projection-lost-cleanup",
            LogLevel.Debug
        );
    }

    private bool CanRetreatForCurrentTarget(
        long entityId,
        ShadowStateSnapshot state
    )
    {
        var subject = GetRetreatSubjectPlayerKey(entityId, state);
        // 脱战判定只对当前确实锁定的玩家生效。没有目标时不能退回 owner，
        // 否则无目标影怪会被误当成“正在追踪高理智玩家”。
        return SanityPlayerKey.IsCanonical(subject) && !TryGetDangerTier(subject, out _);
    }

    private string GetRetreatSubjectPlayerKey(
        long entityId,
        ShadowStateSnapshot state
    )
    {
        if (world.TryGetActiveAggroLockPlayerKey(entityId, out var aggroLockPlayerKey))
            return aggroLockPlayerKey;

        if (
            SanityPlayerKey.IsCanonical(state.TargetPlayerKey)
            && IsPlayerOnlineOnLocation(state.TargetPlayerKey, state.LocationId)
        )
        {
            return state.TargetPlayerKey;
        }
        return string.Empty;
    }

    private static bool IsPlayerOnlineOnLocation(
        string playerKey,
        string locationId
    )
    {
        foreach (var player in Game1.getOnlineFarmers())
        {
            if (
                string.Equals(
                    SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                    playerKey,
                    StringComparison.Ordinal
                )
                && player.currentLocation is not null
                && string.Equals(
                    player.currentLocation.NameOrUniqueName,
                    locationId,
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>DIAG-20260812: 指定玩家当前是否处于 ShadowCreatures 档（理智 ≤50%）。
    /// 未激活 = 高理智（>50%），脱变后的绑定投影应进入高理智淡出消失链条。</summary>
    private bool IsShadowCreaturesActiveFor(string playerKey)
    {
        if (
            !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
        )
        {
            return false;
        }
        foreach (var tierId in tier.ActiveTierIds)
        {
            if (
                string.Equals(
                    tierId,
                    SanityTierIds.ShadowCreatures,
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }
        }
        return false;
    }

    private static string? ResolveProjectionSpeciesId(string bindingId)
    {
        if (
            string.Equals(
                bindingId,
                ShadowMonsterAssetBindingIds.CreeperFear,
                StringComparison.Ordinal
            )
        )
        {
            return ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId;
        }
        if (
            string.Equals(
                bindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            )
        )
        {
            return ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId;
        }
        return null;
    }

    /// <summary>
    /// DIAG-20260809: 注入无害投影宿主（ModEntry 在两者构造后调用）。
    /// 绑定投影的生成/删除/位置查询全部经由此引用。
    /// </summary>
    internal bool BindProjectionHost(
        SmapiHarmlessProjectionHost host,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        if (projectionHost is not null)
        {
            reason = "hostile-shadow.binding-host-already-bound";
            return false;
        }
        projectionHost = host;
        if (!world.BindProjectionPushBoxBridge(this, out reason))
        {
            projectionHost = null;
            return false;
        }
        reason = "hostile-shadow.binding-host-bound";
        return true;
    }

    public bool SubmitShadowCreaturePushBoxIntent(
        string ownerPlayerKey,
        string locationId,
        long batchNonce,
        IReadOnlyList<ShadowCreaturePushBoxIntent> intents,
        out string reason
    )
    {
        return multiplayer.SubmitPushBoxIntent(
            ownerPlayerKey,
            locationId,
            batchNonce,
            intents,
            out reason
        );
    }

    public bool HandlePushBoxIntent(
        ShadowProjectionPushBoxIntentMessage message,
        long senderPlayerId,
        out string reason
    )
    {
        reason = string.Empty;
        if (disposed || !Game1.IsMasterGame)
        {
            reason = "hostile-shadow.push-box-intent-host-unavailable";
            return false;
        }

        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(senderPlayerId);
        var sender = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        var expectedLocationId = sender?.currentLocation?.NameOrUniqueName ?? string.Empty;
        if (
            !HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                expectedPlayerKey,
                expectedLocationId,
                authority.SessionId,
                out reason
            )
        )
        {
            return false;
        }
        PruneRemotePushBoxStates();
        var ownerLocationChanged =
            lastRemotePushBoxLocationByOwner.TryGetValue(
                message!.OwnerPlayerKey,
                out var lastLocationId
            )
            && !string.Equals(lastLocationId, message.LocationId, StringComparison.Ordinal);
        if (!ownerLocationChanged)
        {
            foreach (var state in remotePushBoxStates.Values)
            {
                if (
                    string.Equals(
                        state.OwnerPlayerKey,
                        message.OwnerPlayerKey,
                        StringComparison.Ordinal
                    )
                    && !string.Equals(
                        state.LocationId,
                        message.LocationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    ownerLocationChanged = true;
                    break;
                }
            }
        }
        if (
            lastRemotePushBoxBatchByOwner.TryGetValue(
                message!.OwnerPlayerKey,
                out var lastBatchNonce
            )
            && !ownerLocationChanged
            && message.BatchNonce <= lastBatchNonce
        )
        {
            reason = "hostile-shadow.push-box-intent-batch-stale";
            return false;
        }

        var pending = new List<(
            ShadowProjectionPushBoxIntentEntry Entry,
            string StableId,
            RemotePushBoxState? Existing,
            double CurrentX,
            double CurrentY,
            HostileShadowPushBoxWorldRectangle WorldBox,
            HostileAttackRuntimeDefinition Definition
        )>(message.Entries.Count);
        var activeRemoteStableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in message.Entries)
        {
            var stableId = CreateProjectionStableId(
                message.OwnerPlayerKey,
                entry.CorrelationId
            );
            activeRemoteStableIds.Add(stableId);
            RemotePushBoxState? existing = null;
            if (
                remotePushBoxStates.TryGetValue(stableId, out var candidate)
                && string.Equals(
                    candidate.LocationId,
                    message.LocationId,
                    StringComparison.Ordinal
                )
            )
            {
                existing = candidate;
            }
            if (existing is not null)
            {
                if (
                    existing.SenderPlayerId != senderPlayerId
                    || !string.Equals(
                        existing.SpeciesId,
                        entry.SpeciesId,
                        StringComparison.Ordinal
                    )
                )
                {
                    reason = "hostile-shadow.push-box-intent-owner-or-location-conflict";
                    return false;
                }
                if (entry.Revision <= existing.Revision)
                {
                    reason = "hostile-shadow.push-box-intent-revision-stale";
                    return false;
                }
            }

            if (!TryGetProjectionPushBoxDefinition(entry.SpeciesId, out var definition, out reason))
                return false;

            var currentX = existing?.HasAuthoritativePosition == true
                ? existing.AuthoritativePositionX
                : entry.CurrentPositionX;
            var currentY = existing?.HasAuthoritativePosition == true
                ? existing.AuthoritativePositionY
                : entry.CurrentPositionY;
            if (
                !HostileShadowPushBoxGeometry.TryCreateWorldBox(
                    definition,
                    currentX,
                    currentY,
                    out var worldBox
                )
            )
            {
                reason = "hostile-shadow.push-box-intent-authoritative-geometry-invalid";
                return false;
            }

            pending.Add(
                (
                    entry,
                    stableId,
                    existing,
                    currentX,
                    currentY,
                    worldBox,
                    definition
                )
            );
        }

        var retainedCount = 0;
        var newCount = 0;
        foreach (var pair in remotePushBoxStates)
        {
            var state = pair.Value;
            if (
                string.Equals(
                    state.OwnerPlayerKey,
                    message.OwnerPlayerKey,
                    StringComparison.Ordinal
                )
                && (
                    ownerLocationChanged
                    || !string.Equals(
                        state.LocationId,
                        message.LocationId,
                        StringComparison.Ordinal
                    )
                    || !activeRemoteStableIds.Contains(pair.Key)
                )
            )
            {
                continue;
            }
            retainedCount++;
        }
        foreach (var item in pending)
        {
            if (item.Existing is null)
                newCount++;
        }
        if (retainedCount + newCount > MaximumRemotePushBoxParticipants)
        {
            reason = "hostile-shadow.push-box-intent-cache-cap-reached";
            return false;
        }
        RemoveRemotePushBoxStatesForOwner(
            message.OwnerPlayerKey,
            message.LocationId,
            activeRemoteStableIds
        );

        foreach (var item in pending)
        {
            var target = new HostileShadowPushBoxPoint(
                item.Entry.NormalTargetPositionX,
                item.Entry.NormalTargetPositionY
            );
            var current = new HostileShadowPushBoxPoint(item.CurrentX, item.CurrentY);
            var movement = new HostileShadowPushBoxPoint(
                target.X - current.X,
                target.Y - current.Y
            );
            if (item.Existing is { } existing)
            {
                existing.Participant.Update(
                    current,
                    target,
                    movement,
                    item.WorldBox,
                    isActive: true,
                    isBinding: false,
                    isBindingRepresentative: false
                );
                existing.Revision = item.Entry.Revision;
                existing.BatchNonce = message.BatchNonce;
                existing.LastReceivedTick = (long)Game1.ticks;
                existing.NormalTargetPositionX = target.X;
                existing.NormalTargetPositionY = target.Y;
                continue;
            }

            var participant = new HostileShadowCrowdParticipant(
                item.StableId,
                message.LocationId,
                item.Definition.PushBoxGroupId,
                current,
                target,
                movement,
                item.WorldBox,
                item.Definition.PushForce
            );
            var state = new RemotePushBoxState(
                senderPlayerId,
                message.OwnerPlayerKey,
                message.LocationId,
                item.Entry.SpeciesId,
                item.Entry.CorrelationId,
                participant
            )
            {
                Revision = item.Entry.Revision,
                BatchNonce = message.BatchNonce,
                LastReceivedTick = (long)Game1.ticks,
                AuthoritativePositionX = current.X,
                AuthoritativePositionY = current.Y,
                HasAuthoritativePosition = true,
                NormalTargetPositionX = target.X,
                NormalTargetPositionY = target.Y,
            };
            remotePushBoxStates.Add(item.StableId, state);
        }

        lastRemotePushBoxBatchByOwner[message.OwnerPlayerKey] = message.BatchNonce;
        lastRemotePushBoxLocationByOwner[message.OwnerPlayerKey] = message.LocationId;
        reason = "hostile-shadow.push-box-intent-accepted";
        return true;
    }

    public bool HandlePushBoxResult(
        ShadowProjectionPushBoxResultMessage message,
        out string reason
    )
    {
        reason = string.Empty;
        if (disposed || Game1.IsMasterGame || projectionHost is null)
        {
            reason = "hostile-shadow.push-box-result-client-unavailable";
            return false;
        }
        if (
            message is null
            || !HostileShadowProtocol.IsFreshPushBoxHostTick(
                message.HostTick,
                (long)Game1.ticks
            )
        )
        {
            reason = "hostile-shadow.push-box-result-host-tick-stale-or-future";
            return false;
        }
        if (message.BatchNonce <= lastAppliedPushBoxResultBatchNonce)
        {
            reason = "hostile-shadow.push-box-result-batch-stale";
            return false;
        }

        foreach (var entry in message.Entries)
        {
            if (
                !projectionHost.TryApplyShadowPushBoxResult(
                    message.OwnerPlayerKey,
                    message.LocationId,
                    entry.CorrelationId,
                    entry.SpeciesId,
                    entry.Revision,
                    entry.FinalPositionX,
                    entry.FinalPositionY,
                    out reason
                )
            )
            {
                return false;
            }
        }

        lastAppliedPushBoxResultBatchNonce = message.BatchNonce;
        reason = "hostile-shadow.push-box-result-applied";
        return true;
    }

    bool IHostileShadowProjectionPushBoxBridge.HasActivePushBoxParticipants =>
        projectionHost?.HasActiveShadowPushBoxParticipants == true
        || remotePushBoxStates.Count > 0;

    void IHostileShadowProjectionPushBoxBridge.AppendPushBoxParticipants(
        List<HostileShadowCrowdParticipant> participants
    )
    {
        if (disposed || !Game1.IsMasterGame || projectionHost is null)
            return;

        PruneRemotePushBoxStates();
        activeLocalPushBoxKeys.Clear();
        localPushBoxIntentBuffer.Clear();
        projectionHost.CopyShadowPushBoxIntents(localPushBoxIntentBuffer);
        foreach (var intent in localPushBoxIntentBuffer)
        {
            var stableId = CreateProjectionStableId(
                intent.OwnerPlayerKey,
                intent.CorrelationId
            );
            activeLocalPushBoxKeys.Add(stableId);
            localPushBoxIntents[stableId] = intent;
            if (!TryGetProjectionPushBoxDefinition(intent.SpeciesId, out var definition, out var reason))
            {
                LogOnce(reason, LogLevel.Warn);
                continue;
            }
            if (
                !HostileShadowPushBoxGeometry.TryCreateWorldBox(
                    definition,
                    intent.CurrentPositionX,
                    intent.CurrentPositionY,
                    out var worldBox
                )
            )
            {
                LogOnce(
                    "hostile-shadow.push-box-local-geometry-invalid",
                    LogLevel.Warn
                );
                continue;
            }

            if (!localPushBoxParticipants.TryGetValue(stableId, out var participant))
            {
                participant = new HostileShadowCrowdParticipant(
                    stableId,
                    intent.LocationId,
                    definition.PushBoxGroupId,
                    default,
                    default,
                    default,
                    default,
                    definition.PushForce
                );
                localPushBoxParticipants.Add(stableId, participant);
            }
            var current = new HostileShadowPushBoxPoint(
                intent.CurrentPositionX,
                intent.CurrentPositionY
            );
            var target = new HostileShadowPushBoxPoint(
                intent.NormalTargetPositionX,
                intent.NormalTargetPositionY
            );
            participant.Update(
                current,
                target,
                new HostileShadowPushBoxPoint(target.X - current.X, target.Y - current.Y),
                worldBox,
                isActive: true,
                isBinding: false,
                isBindingRepresentative: false
            );
            participants.Add(participant);
        }

        staleLocalPushBoxKeys.Clear();
        foreach (var key in localPushBoxParticipants.Keys)
        {
            if (!activeLocalPushBoxKeys.Contains(key))
                staleLocalPushBoxKeys.Add(key);
        }
        foreach (var key in staleLocalPushBoxKeys)
        {
            localPushBoxParticipants.Remove(key);
            localPushBoxIntents.Remove(key);
        }

        foreach (var state in remotePushBoxStates.Values)
            participants.Add(state.Participant);
    }

    void IHostileShadowProjectionPushBoxBridge.ApplyPushBoxResolution(
        HostileShadowCrowdCollisionResolution resolution
    )
    {
        if (disposed || !Game1.IsMasterGame || projectionHost is null)
            return;

        foreach (var resolved in resolution.Entries)
        {
            if (localPushBoxIntents.TryGetValue(resolved.StableId, out var localIntent))
            {
                if (
                    !projectionHost.TryApplyShadowPushBoxResult(
                        localIntent.OwnerPlayerKey,
                        localIntent.LocationId,
                        localIntent.CorrelationId,
                        localIntent.SpeciesId,
                        localIntent.Revision,
                        resolved.FinalPosition.X,
                        resolved.FinalPosition.Y,
                        out var localReason
                    )
                    && !localReason.EndsWith("not-applicable", StringComparison.Ordinal)
                )
                {
                    LogOnce(localReason, LogLevel.Warn);
                }
                continue;
            }

            if (!remotePushBoxStates.TryGetValue(resolved.StableId, out var remote))
                continue;
            remote.AuthoritativePositionX = resolved.FinalPosition.X;
            remote.AuthoritativePositionY = resolved.FinalPosition.Y;
            remote.HasAuthoritativePosition = true;
            if (!pushBoxResultEntriesByPlayer.TryGetValue(remote.SenderPlayerId, out var resultEntries))
            {
                resultEntries = new List<ShadowProjectionPushBoxResultEntry>();
                pushBoxResultEntriesByPlayer.Add(remote.SenderPlayerId, resultEntries);
            }
            resultEntries.Add(
                new ShadowProjectionPushBoxResultEntry
                {
                    CorrelationId = remote.CorrelationId,
                    SpeciesId = remote.SpeciesId,
                    Revision = remote.Revision,
                    FinalPositionX = resolved.FinalPosition.X,
                    FinalPositionY = resolved.FinalPosition.Y,
                }
            );
        }

        foreach (var pair in pushBoxResultEntriesByPlayer)
        {
            if (pair.Value.Count == 0)
                continue;
            var ownerPlayerKey = string.Empty;
            var locationId = string.Empty;
            var batchNonce = 0L;
            foreach (var state in remotePushBoxStates.Values)
            {
                if (state.SenderPlayerId != pair.Key)
                    continue;
                ownerPlayerKey = state.OwnerPlayerKey;
                locationId = state.LocationId;
                batchNonce = Math.Max(batchNonce, state.BatchNonce);
            }
            if (
                string.IsNullOrWhiteSpace(ownerPlayerKey)
                || !HostileShadowProtocol.IsValidLocationId(locationId)
                || batchNonce <= 0
            )
            {
                pair.Value.Clear();
                continue;
            }
            multiplayer.SendPushBoxResult(
                new ShadowProjectionPushBoxResultMessage
                {
                    SessionId = authority.SessionId,
                    OwnerPlayerKey = ownerPlayerKey,
                    LocationId = locationId,
                    CapabilityId = HostileShadowProtocol.ShadowPushBoxCapabilityId,
                    BatchNonce = batchNonce,
                    HostTick = Game1.ticks,
                    Entries = new List<ShadowProjectionPushBoxResultEntry>(pair.Value),
                },
                pair.Key
            );
            pair.Value.Clear();
        }
    }

    /// <summary>
    /// DIAG-20260809: 测试命令专用——立即对全部在册危险影怪强制脱战
    /// （跳过 10 游戏分钟/25% 判定，方便 F5 验证绑定流程）。
    /// </summary>
    internal int DebugForceBindings()
    {
        if (disposed || projectionHost is null)
            return 0;
        var count = 0;
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (bindingCorrelationByEntity.ContainsKey(entityId))
                continue;
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
                continue;
            if (state.Health <= 0)
                continue;
            // DIAG-20260811: 调试命令也走完整脱战流程（恐吓→无敌→隐藏+绑定投影），
            // 与自然脱战表现一致；不直接 BindNow（会跳过恐吓动画）。
            BeginRetreat(entityId, state);
            count++;
        }
        return count;
    }

    /// <summary>DIAG-20260809: 测试命令专用——立即恢复全部绑定（解除隐藏+删投影）。</summary>
    internal int DebugForceRestoreBindings()
    {
        var count = bindingCorrelationByEntity.Count;
        RestoreAllBindings();
        return count;
    }

    /// <summary>
    /// DIAG-20260804: 测试命令 ds_spawn 专用。在指定世界坐标请求真实影怪实体；
    /// 位置可指定。调试召唤只保留系统、主机、物理能力、资源物化和全局硬上限检查；
    /// 不占用自然刷新预算，也不修改自然刷新计时。
    /// </summary>
    public HostileShadowSpawnResult DebugSpawnAt(
        string speciesId,
        float positionX,
        float positionY,
        out string reason
    )
    {
        reason = string.Empty;
        if (
            disposed
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !authority.IsHostSessionActive
        )
        {
            reason = "hostile-shadow.debug-host-unavailable";
            return Failure(HostileShadowSpawnStatus.Unavailable, reason);
        }
        // DIAG-20260806: ds_spawn 是纯测试命令，但不隐式清理已有实体；
        // 明确执行 ds_spawn clear 才会清理测试残留。DebugCommand 本身已经绕过自然预算、
        // 共享上限和转换锁，避免把“是否清场”与“能否生成”耦合在一起。
        var player = Game1.player;
        var location = player?.currentLocation;
        if (
            player is null
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            reason = "hostile-shadow.debug-location-unavailable";
            return Failure(HostileShadowSpawnStatus.Unavailable, reason);
        }
        if (!TryResolveBindingForSpecies(speciesId, out var bindingId))
        {
            reason = "hostile-shadow.debug-species-invalid";
            return Failure(HostileShadowSpawnStatus.Rejected, reason);
        }
        var resolved = profiles.Resolve(bindingId);
        if (!resolved.Success || resolved.Profile is null)
        {
            reason = resolved.Reason;
            return Failure(HostileShadowSpawnStatus.Unavailable, reason);
        }
        // DIAG-20260807: 让怪物中心（受击框几何中心）对准鼠标——鼠标点哪，怪物中心就在哪。
        // 怪物 Position 是贴图左上角锚点，直接把鼠标坐标当 Position 会让怪物身体出现在
        // 鼠标右下方。复用统一中心反算，避免调试生成与投影转换各自维护一套偏移语义。
        if (
            resources.TryGetHostileAttackMetadata(
                bindingId,
                out var debugMetadata,
                out _
            )
            && HostileAttackRuntimeDefinition.TryCreate(
                debugMetadata,
                resolved.Profile,
                out var debugDefinition,
                out _
            )
            && HostileAttackCollisionResolver.TryResolvePivotForHurtBoxCenter(
                debugDefinition,
                positionX,
                positionY,
                out var resolvedPositionX,
                out var resolvedPositionY
            )
        )
        {
            positionX = (float)resolvedPositionX;
            positionY = (float)resolvedPositionY;
        }
        var capability = world.CurrentCapability;
        if (!capability.IsAvailable)
        {
            reason = capability.Reason;
            return Failure(HostileShadowSpawnStatus.Unavailable, reason);
        }
        if (!world.TryPrepareBinding(bindingId, out var visualReason))
        {
            reason = visualReason;
            return Failure(HostileShadowSpawnStatus.Unavailable, reason);
        }

        var requestId = string.Concat(
            "debug-",
            Guid.NewGuid().ToString("N")
        );
        if (
            !HostileShadowSpeciesBindingPolicy.TryResolveSpecies(
                bindingId,
                out var requestedSpecies,
                out var speciesReason
            )
        )
        {
            reason = speciesReason;
            return Failure(HostileShadowSpawnStatus.Rejected, reason);
        }
        var result = authority.TrySpawn(
            new HostileShadowSpawnCommand(
                requestId,
                HostileShadowSpawnOrigin.DebugCommand,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                location.NameOrUniqueName,
                positionX,
                positionY,
                timeApi.Time,
                resolved.Profile,
                "hostile-shadow.spawn.debug-command"
            )
        );
        if (!result.Spawned || !result.EntityId.HasValue)
        {
            reason = result.Reason;
            return result;
        }

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
            reason = string.IsNullOrWhiteSpace(materializationReason)
                ? "hostile-shadow.debug-materialization-failed"
                : materializationReason;
            return authority.FailSpawnMaterialization(
                requestId,
                result.EntityId.Value,
                reason
            );
        }
        RegisterHostileShadowSchedules(result.EntityId.Value);
        reason = "hostile-shadow.debug-spawned";
        return result;
    }

    /// <summary>
    /// Converts the shared mod centre-point contract into the hostile entity Position anchor.
    /// Projection conversion requests carry the harmless projection centre; the physical
    /// Monster.Position field is the pivot used by the hostile hurt-box geometry.
    /// </summary>
    private bool TryResolveHostilePositionFromCenter(
        string bindingId,
        ShadowMonsterRuntimeProfile profile,
        double centerWorldX,
        double centerWorldY,
        out double positionX,
        out double positionY,
        out string reason
    )
    {
        positionX = 0d;
        positionY = 0d;
        if (!resources.TryGetHostileAttackMetadata(bindingId, out var metadata, out reason))
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.conversion-center-metadata-unavailable";
            return false;
        }
        if (
            !HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                profile,
                out var definition,
                out reason
            )
            || definition is null
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.conversion-center-definition-invalid";
            return false;
        }
        if (
            !HostileAttackCollisionResolver.TryResolvePivotForHurtBoxCenter(
                definition,
                centerWorldX,
                centerWorldY,
                out positionX,
                out positionY
            )
        )
        {
            reason = "hostile-shadow.conversion-center-resolution-failed";
            return false;
        }

        reason = "hostile-shadow.conversion-center-resolved";
        return true;
    }

    private HostileShadowSpawnResult TrySpawn(
        string requestId,
        HostileShadowSpawnOrigin origin,
        Farmer player,
        string bindingId,
        string reason,
        float? positionX = null,
        float? positionY = null,
        SanityShadowBudgetEvaluationResult? preEvaluatedBudget = null,
        int? currentLocationCap = null
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

        double spawnPositionX = positionX ?? player.Position.X;
        double spawnPositionY = positionY ?? player.Position.Y;
        if (
            origin == HostileShadowSpawnOrigin.OwnerProjectionConversion
            && positionX is { } requestedCenterX
            && positionY is { } requestedCenterY
        )
        {
            if (
                !TryResolveHostilePositionFromCenter(
                    bindingId,
                    resolved.Profile,
                    requestedCenterX,
                    requestedCenterY,
                    out spawnPositionX,
                    out spawnPositionY,
                    out var centerReason
                )
            )
            {
                return Failure(
                    HostileShadowSpawnStatus.Unavailable,
                    centerReason
                );
            }
        }

        var result = authority.TrySpawn(
            new HostileShadowSpawnCommand(
                requestId,
                origin,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                location.NameOrUniqueName,
                // DIAG-20260812: 支持指定生成位置（原地转化用）；未提供回退玩家位置。
                spawnPositionX,
                spawnPositionY,
                timeApi.Time,
                resolved.Profile,
                reason,
                currentLocationCap
            ),
            preEvaluatedBudget: preEvaluatedBudget
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
        RegisterHostileShadowSchedules(result.EntityId.Value);
        return result;
    }

    private bool TryGetDangerTier(
        string playerKey,
        out bool terrorbeakActive
    )
    {
        return TryGetDangerTier(playerKey, out terrorbeakActive, out _);
    }

    private bool TryGetDangerTier(
        string playerKey,
        out bool terrorbeakActive,
        out string reason
    )
    {
        terrorbeakActive = false;
        reason = "hostile-shadow.natural.danger-tier-unavailable";
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
        if (!dangerActive)
        {
            reason = "hostile-shadow.natural.danger-inactive";
            return false;
        }
        reason = "hostile-shadow.natural.danger-active";
        return true;
    }

    private void SynchronizeDangerEpochsForOnlinePlayers()
    {
        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            if (SanityPlayerKey.IsCanonical(playerKey))
                SynchronizeDangerEpochFromCurrentTierState(playerKey);
        }
    }

    private void SynchronizeDangerEpochFromCurrentTierState(string playerKey)
    {
        if (
            !TryGetCurrentTierFlags(
                playerKey,
                out var tierRevision,
                out _,
                out var dangerTierActive
            )
        )
        {
            return;
        }

        if (
            !authority.SynchronizeDangerEpoch(
                playerKey,
                dangerTierActive,
                tierRevision,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
        }
    }

    private bool TryGetCurrentTierFlags(
        string playerKey,
        out long tierRevision,
        out bool shadowTierActive,
        out bool dangerTierActive
    )
    {
        tierRevision = -1;
        shadowTierActive = false;
        dangerTierActive = false;
        if (
            !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
            || tier.Revision < 0
        )
        {
            return false;
        }

        tierRevision = tier.Revision;
        foreach (var tierId in tier.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.ShadowCreatures, StringComparison.Ordinal))
                shadowTierActive = true;
            else if (string.Equals(tierId, SanityTierIds.Danger, StringComparison.Ordinal))
                dangerTierActive = true;
        }
        return true;
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        authority.ObserveStateEvent(stateEvent);
        // ShadowCreatures 档只影响无害投影的自然生命周期。危险影怪即使目标理智
        // 高于 50% 也继续正常战斗；危险影怪的脱战只由 RollBindings 的目标判定触发。
        if (
            string.Equals(
                stateEvent.TierId,
                SanityTierIds.ShadowCreatures,
                StringComparison.Ordinal
            )
        )
        {
            if (stateEvent.Kind == SanityStateEventKind.TierEntered)
                RestoreBindingsForPlayer(stateEvent.PlayerKey);
            return;
        }
        // DIAG-20260809: Danger 档变化驱动绑定生命周期：
        // - 进入（san≤15%）→ 全部绑定影怪恢复危险形态（解除隐藏+删绑定投影）；
        // - 退出（san>17.5%）→ 武装脱战计时（之后每 10 游戏分钟 roll 25%）。
        if (
            !string.Equals(
                stateEvent.TierId,
                SanityTierIds.Danger,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }
        if (stateEvent.Kind == SanityStateEventKind.TierEntered)
        {
            RestoreBindingsForPlayer(stateEvent.PlayerKey);
            return;
        }

        if (stateEvent.Kind == SanityStateEventKind.TierExited)
        {
            // A cut-map queue is a compensation for the danger state that created it. If the
            // player leaves Danger before the queue is consumed, discard the remaining entries;
            // re-entering Danger must not resurrect an old transition's spawns.
            if (pendingFastSpawnsByPlayer.Remove(stateEvent.PlayerKey))
            {
                LogFastRefreshState(
                    stateEvent.PlayerKey,
                    "discarded-danger-exited",
                    "pending cut-map compensation abandoned after Danger exit"
                );
            }
        }
    }

    /// <summary>
    /// DIAG-20260810: 超过当前地图、当前玩家的共享上限时，每只 Creeper/Terrorbeak
    /// 从自己的生成时间起每 10 游戏分钟独立 roll 50%。绑定实体和绑定投影只作为一个
    /// 候选；选中后仍走“完成当前动作→一次恐吓→约 1 秒淡出”的延迟生命周期。
    /// </summary>
    private void TrimOverCap(long gameMinute)
    {
        if (projectionHost is null)
            return;

        PrunePendingOverCapProjectionFades();

        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            if (
                !lifecycle.TryGetShadowBudgetTotalCap(playerKey, out var cap)
                || cap <= 0
            )
            {
                continue;
            }

            // 上限永远只看玩家当前所在地图；影怪本体仍留在原地图，不会因切图被删。
            var locationId = player.currentLocation?.NameOrUniqueName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(locationId))
                continue;

            var total = CountOverCapShadowsForPlayerAtLocation(playerKey, locationId);
            var availableOverCap = Math.Max(
                0,
                total
                    - cap
                    - CountPendingOverCapSelectionsForPlayerAtLocation(
                        playerKey,
                        locationId
                    )
            );

            // 先处理危险实体。每只实体到自己的时间才会消耗一次独立判断；没有空余
            // 超限名额时仍消费时间点，但不再掷概率，避免一次回收过量。
            foreach (var entityId in world.GetOrderedEntityIds())
            {
                if (
                    !world.TryGetEntityLocationId(entityId, out var entityLocationId)
                    || !string.Equals(
                        entityLocationId,
                        locationId,
                        StringComparison.Ordinal
                    )
                    || !authority.TryGetEntity(entityId, out var state)
                    || state is null
                    || !string.Equals(
                        state.OwnerPlayerKey,
                        playerKey,
                        StringComparison.Ordinal
                    )
                    || state.Health <= 0
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
                    || !IsOverCapAssetBinding(state.AssetBindingId)
                    || !world.TryGetEntitySpawnGameMinute(
                        entityId,
                        out var spawnGameMinute
                    )
                )
                {
                    continue;
                }

                overCapCheckSchedule.Register(entityId, spawnGameMinute);
                if (
                    !overCapCheckSchedule.TryConsumeIfDue(
                        entityId,
                        gameMinute,
                        spawnGameMinute
                    )
                    || availableOverCap <= 0
                    || bindingRandom.NextDouble() >= 0.5d
                )
                {
                    continue;
                }

                if (TryBeginOverCapCleanup(entityId, state))
                    availableOverCap--;
            }

            // 独立无害投影同样从自己的 SpawnedAtMinute 起算；绑定投影跳过，因为它由
            // 上面的绑定实体候选代表，不能因为一组绑定再次独立 roll。
            foreach (var instance in projectionHost.SnapshotShadowInstancesForOwner(playerKey))
            {
                if (
                    instance.IsCleanedUp
                    || instance.IsBindingProjection
                    || instance.BehaviorState
                        == ShadowCreatureHarmlessProjectionInstance
                            .ShadowCreatureProjectionBehaviorState.FadingOut
                    || !string.Equals(
                        instance.Owner.LocationNameOrUniqueName,
                        locationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                overCapProjectionCheckSchedule.Register(
                    instance.CorrelationId,
                    instance.SpawnedAtMinute
                );
                if (
                    !overCapProjectionCheckSchedule.TryConsumeIfDue(
                        instance.CorrelationId,
                        gameMinute,
                        instance.SpawnedAtMinute
                    )
                    || availableOverCap <= 0
                    || bindingRandom.NextDouble() >= 0.5d
                    || !projectionHost.BeginOverCapShadowProjectionFade(
                        playerKey,
                        locationId,
                        instance.CorrelationId,
                        OverCapFadeOutMilliseconds
                    )
                )
                {
                    continue;
                }

                pendingOverCapProjectionFades.Add(instance.CorrelationId);
                availableOverCap--;
            }
        }
    }

    private void PrunePendingOverCapProjectionFades()
    {
        if (projectionHost is null || pendingOverCapProjectionFades.Count == 0)
            return;

        List<string>? stale = null;
        foreach (var correlationId in pendingOverCapProjectionFades)
        {
            if (projectionHost.IsShadowProjectionActive(correlationId))
                continue;
            stale ??= new List<string>();
            stale.Add(correlationId);
        }
        if (stale is null)
            return;
        foreach (var correlationId in stale)
            pendingOverCapProjectionFades.Remove(correlationId);
    }

    private int CountOverCapShadowsForPlayerAtLocation(
        string playerKey,
        string locationId
    )
    {
        if (projectionHost is null)
            return 0;

        var harmless = projectionHost.CountOverCapShadowProjectionsForOwnerAtLocation(
            playerKey,
            locationId
        );
        var hostile = 0;
        foreach (var pair in world.CountEntitiesBySpeciesAtLocation(playerKey, locationId))
        {
            if (IsOverCapAssetBinding(pair.Key))
                hostile += pair.Value;
        }

        // The hidden entity and its binding projection are one shadow in the shared pool.
        var boundPairs = 0;
        foreach (var entityId in bindingCorrelationByEntity.Keys)
        {
            if (
                world.TryGetEntityLocationId(entityId, out var entityLocationId)
                && string.Equals(
                    entityLocationId,
                    locationId,
                    StringComparison.Ordinal
                )
                && authority.TryGetEntity(entityId, out var boundState)
                && boundState is not null
                && string.Equals(
                    boundState.OwnerPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                && IsOverCapAssetBinding(boundState.AssetBindingId)
            )
            {
                boundPairs++;
            }
        }

        return Math.Max(0, harmless + hostile - boundPairs);
    }

    private int CountPendingOverCapSelectionsForPlayerAtLocation(
        string playerKey,
        string locationId
    )
    {
        var count = 0;
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (
                !world.TryGetEntityLocationId(entityId, out var entityLocationId)
                || !string.Equals(
                    entityLocationId,
                    locationId,
                    StringComparison.Ordinal
                )
                || !authority.TryGetEntity(entityId, out var state)
                || state is null
                || !string.Equals(
                    state.OwnerPlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                || !IsOverCapAssetBinding(state.AssetBindingId)
                || (
                    !pendingOverCapRetreats.Contains(entityId)
                    && !pendingOverCapActions.ContainsKey(entityId)
                    && !pendingOverCapBindingFades.Contains(entityId)
                )
            )
            {
                continue;
            }
            count++;
        }

        if (projectionHost is null)
            return count;
        foreach (var instance in projectionHost.SnapshotShadowInstancesForOwner(playerKey))
        {
            if (
                pendingOverCapProjectionFades.Contains(instance.CorrelationId)
                && !instance.IsCleanedUp
                && string.Equals(
                    instance.Owner.LocationNameOrUniqueName,
                    locationId,
                    StringComparison.Ordinal
                )
            )
            {
                count++;
            }
        }
        return count;
    }

    private bool TryBeginOverCapCleanup(
        long entityId,
        ShadowStateSnapshot state
    )
    {
        if (bindingCorrelationByEntity.TryGetValue(entityId, out var correlationId))
        {
            if (pendingOverCapBindingFades.Contains(entityId))
                return false;
            if (!projectionHost!.BeginBindingFadeOut(correlationId, OverCapFadeOutMilliseconds))
                return false;

            pendingOverCapBindingFades.Add(entityId);
            return true;
        }

        if (
            pendingOverCapRetreats.Contains(entityId)
            || pendingOverCapActions.ContainsKey(entityId)
            || pendingOverCapBindingFades.Contains(entityId)
        )
        {
            return false;
        }

        if (pendingRetreats.ContainsKey(entityId))
        {
            pendingOverCapRetreats.Add(entityId);
            return true;
        }
        if (string.Equals(state.StateId, HostileShadowStateIds.Taunt, StringComparison.Ordinal))
        {
            // The current Taunt is already the requested warning; do not play it twice.
            pendingOverCapActions[entityId] = true;
            return true;
        }
        if (IsOverCapActionInProgress(state.StateId))
        {
            // Finish the current action first, then begin the one requested over-cap Taunt.
            pendingOverCapActions[entityId] = false;
            return true;
        }
        if (BeginRetreat(entityId, state))
        {
            pendingOverCapRetreats.Add(entityId);
            return true;
        }
        return false;
    }

    private void RebaseShadowCheckSchedules(long gameMinute)
    {
        var entityIds = world.GetOrderedEntityIds();
        overCapCheckSchedule.Clear();
        bindingRollSchedule.Clear();
        foreach (var entityId in entityIds)
        {
            overCapCheckSchedule.RebaseKey(entityId, gameMinute);
            bindingRollSchedule.RebaseKey(entityId, gameMinute);
        }

        overCapProjectionCheckSchedule.Clear();
        if (projectionHost is null)
            return;
        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            foreach (var instance in projectionHost.SnapshotShadowInstancesForOwner(playerKey))
            {
                if (
                    instance.IsCleanedUp
                    || instance.IsBindingProjection
                    || !IsOverCapProjectionSpecies(instance.SpeciesId)
                )
                {
                    continue;
                }
                overCapProjectionCheckSchedule.RebaseKey(
                    instance.CorrelationId,
                    gameMinute
                );
            }
        }
    }

    private void ClearShadowCheckSchedules()
    {
        bindingRollSchedule.Clear();
        overCapCheckSchedule.Clear();
        overCapProjectionCheckSchedule.Clear();
        pendingScheduledShadowCheckMinute = -1;
    }

    private void RegisterHostileShadowSchedules(long entityId)
    {
        if (!world.TryGetEntitySpawnGameMinute(entityId, out var spawnGameMinute))
            return;

        bindingRollSchedule.Register(entityId, spawnGameMinute);
        overCapCheckSchedule.Register(entityId, spawnGameMinute);
    }

    private void AdvancePendingOverCapActions()
    {
        if (pendingOverCapActions.Count == 0)
            return;

        var pending = new List<KeyValuePair<long, bool>>(pendingOverCapActions);
        foreach (var pair in pending)
        {
            var entityId = pair.Key;
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
            {
                pendingOverCapActions.Remove(entityId);
                continue;
            }
            if (
                state.Health <= 0
                || string.Equals(state.StateId, HostileShadowStateIds.Dying, StringComparison.Ordinal)
                || string.Equals(state.StateId, HostileShadowStateIds.Despawn, StringComparison.Ordinal)
            )
            {
                pendingOverCapActions.Remove(entityId);
                continue;
            }
            if (string.Equals(state.StateId, HostileShadowStateIds.Taunt, StringComparison.Ordinal))
            {
                // A finite action can naturally transition into Taunt before this deferred
                // cleanup is resumed. That Taunt is already the warning we need; remember to
                // skip starting another one when it completes.
                pendingOverCapActions[entityId] = true;
                continue;
            }
            if (IsOverCapActionInProgress(state.StateId))
            {
                continue;
            }

            pendingOverCapActions.Remove(entityId);
            if (pair.Value)
            {
                if (BindNow(entityId, state))
                    BeginOverCapBindingFade(entityId);
                continue;
            }

            if (BeginRetreat(entityId, state))
                pendingOverCapRetreats.Add(entityId);
        }
    }

    private void BeginOverCapBindingFade(long entityId)
    {
        if (
            projectionHost is not null
            && bindingCorrelationByEntity.TryGetValue(entityId, out var correlationId)
            && projectionHost.BeginBindingFadeOut(
                correlationId,
                OverCapFadeOutMilliseconds
            )
        )
        {
            pendingOverCapBindingFades.Add(entityId);
        }
    }

    /// <summary>
    /// DIAG-20260811: 切图时按指定地点统计危险影怪物种分布，按 min(旧地图数量,
    /// 当前密度档上限) 填充快速刷新队列（新地图内每 2 秒刷 1 只）。
    /// </summary>
    private void QueueFastSpawnsForLocation(
        string playerKey,
        string locationId
    )
    {
        if (
            disposed
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !authority.IsHostSessionActive
        )
        {
            return;
        }
        var bySpecies = world.CountEntitiesBySpeciesAtLocation(playerKey, locationId);
        var sourceCount = 0;
        foreach (var count in bySpecies.Values)
            sourceCount += count;
        if (sourceCount == 0)
        {
            LogFastRefreshState(
                playerKey,
                string.Concat("source-empty:", locationId),
                string.Concat(
                    "source-location=",
                    locationId,
                    ", source-count=0, reason=no-eligible-owner-entities"
                )
            );
            return;
        }
        if (
            !lifecycle.TryGetShadowBudgetTotalCap(playerKey, out var cap)
            || cap <= 0
        )
        {
            LogFastRefreshState(
                playerKey,
                string.Concat("source-cap-unavailable:", locationId),
                string.Concat(
                    "source-location=",
                    locationId,
                    ", source-count=",
                    sourceCount.ToString(CultureInfo.InvariantCulture),
                    ", reason=budget-cap-unavailable"
                )
            );
            return;
        }

        var queue = new Queue<string>();
        var remaining = cap;
        foreach (var pair in bySpecies)
        {
            if (remaining <= 0)
                break;
            var count = Math.Min(pair.Value, remaining);
            for (var i = 0; i < count; i++)
                queue.Enqueue(pair.Key);
            remaining -= count;
        }
        if (queue.Count == 0)
            return;
        pendingFastSpawnsByPlayer[playerKey] = queue;
        fastSpawnNextDueMilliseconds =
            Game1.currentGameTime.TotalGameTime.TotalMilliseconds
            + FastSpawnIntervalMilliseconds;
        LogFastRefreshState(
            playerKey,
            string.Concat("queued:", locationId, ":", queue.Count),
            string.Concat(
                "source-location=",
                locationId,
                ", source-count=",
                sourceCount.ToString(CultureInfo.InvariantCulture),
                ", queue-count=",
                queue.Count.ToString(CultureInfo.InvariantCulture),
                ", cap=",
                cap.ToString(CultureInfo.InvariantCulture)
            )
        );
    }

    /// <summary>
    /// DIAG-20260812: 每 tick 检测玩家所在地图变化（切图）。Warp 事件触发时
    /// currentLocation 可能已是新地图，无法统计旧地图影怪——用上次记录的位置
    /// 触发快速刷新队列（旧地图影怪留原地冻结、新地图按 min(旧数量,上限) 补刷）。
    /// </summary>
    private void TrackWarpAndQueueFastSpawns()
    {
        if (
            disposed
            || !Context.IsWorldReady
            || !Game1.IsMasterGame
        )
        {
            return;
        }
        var player = Game1.player;
        if (
            player is null
            || player.currentLocation is null
            || string.IsNullOrWhiteSpace(player.currentLocation.NameOrUniqueName)
        )
        {
            return;
        }
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        var locationId = player.currentLocation.NameOrUniqueName;
        if (
            lastLocationByPlayer.TryGetValue(playerKey, out var lastLocation)
            && !string.Equals(lastLocation, locationId, StringComparison.Ordinal)
        )
        {
            // 切图：用旧位置统计危险影怪分布并填快速刷新队列。
            QueueFastSpawnsForLocation(playerKey, lastLocation);
        }
        lastLocationByPlayer[playerKey] = locationId;
    }

    /// <summary>
    /// DIAG-20260811: 快速刷新推进——每 2 秒从队列取一只在玩家附近生成；它使用独立
    /// 的快速补足授权，不推进自然刷新计时，也不因物种不是当前自然刷物种而被拒绝。
    /// </summary>
    private void AdvanceFastSpawns()
    {
        if (pendingFastSpawnsByPlayer.Count == 0)
            return;
        var nowMilliseconds =
            Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        if (nowMilliseconds < fastSpawnNextDueMilliseconds)
            return;

        // 快照遍历（foreach 迭代中 Remove 字典会抛 InvalidOperationException）。
        var playerKeys = new List<string>(pendingFastSpawnsByPlayer.Keys);
        foreach (var playerKey in playerKeys)
        {
            if (
                !pendingFastSpawnsByPlayer.TryGetValue(
                    playerKey,
                    out var queue
                )
                || queue.Count == 0
            )
            {
                continue;
            }
            Farmer? target = null;
            foreach (var farmer in Game1.getOnlineFarmers())
            {
                var farmerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                    farmer.UniqueMultiplayerID
                );
                if (
                    string.Equals(
                        farmerKey,
                        playerKey,
                        StringComparison.Ordinal
                    )
                )
                {
                    target = farmer;
                    break;
                }
            }
            if (
                target is null
                || target.currentLocation is null
                || string.IsNullOrWhiteSpace(target.currentLocation.NameOrUniqueName)
            )
            {
                LogFastRefreshState(
                    playerKey,
                    "destination-unavailable",
                    "destination player or location unavailable; queue discarded"
                );
                pendingFastSpawnsByPlayer.Remove(playerKey);
                break;
            }
            var targetLocationId = target.currentLocation.NameOrUniqueName;
            if (!TryGetDangerTier(playerKey, out _, out var dangerReason))
            {
                LogFastRefreshState(
                    playerKey,
                    string.Concat("discarded-danger-inactive:", targetLocationId),
                    string.Concat(
                        "destination-location=",
                        targetLocationId,
                        ", reason=",
                        dangerReason,
                        ", queue-count=",
                        queue.Count.ToString(CultureInfo.InvariantCulture)
                    )
                );
                pendingFastSpawnsByPlayer.Remove(playerKey);
                continue;
            }
            var currentMapLockedOccupancy =
                CountCurrentMapLockedShadowOccupancy(playerKey, targetLocationId);
            if (
                !lifecycle.TryGetShadowBudgetTotalCap(playerKey, out var totalCap)
                || totalCap <= 0
            )
            {
                LogFastRefreshState(
                    playerKey,
                    string.Concat("destination-cap-unavailable:", targetLocationId),
                    string.Concat(
                        "destination-location=",
                        targetLocationId,
                        ", occupancy=",
                        currentMapLockedOccupancy.ToString(CultureInfo.InvariantCulture),
                        ", reason=budget-cap-unavailable, queue-count=",
                        queue.Count.ToString(CultureInfo.InvariantCulture)
                    )
                );
                break;
            }
            if (currentMapLockedOccupancy >= totalCap)
            {
                // The destination map is full for this player's locked hostile shadows. Keep
                // the source entry so a later vacancy can still receive the command-created
                // shadow; this does not touch off-map entity lifetime or its despawn timer.
                LogFastRefreshState(
                    playerKey,
                    string.Concat("destination-at-cap:", targetLocationId),
                    string.Concat(
                        "destination-location=",
                        targetLocationId,
                        ", occupancy=",
                        currentMapLockedOccupancy.ToString(CultureInfo.InvariantCulture),
                        ", cap=",
                        totalCap.ToString(CultureInfo.InvariantCulture),
                        ", queue-count=",
                        queue.Count.ToString(CultureInfo.InvariantCulture)
                    )
                );
                break;
            }

            var bindingId = queue.Peek();
            var requestId = string.Concat(
                "hostile-shadow.fast-spawn.",
                authority.SessionId,
                ".",
                playerKey,
                ".",
                nowMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
            var result = TrySpawn(
                requestId,
                HostileShadowSpawnOrigin.WarpFastCompensation,
                target,
                bindingId,
                "hostile-shadow.spawn.fast-spawn",
                currentLocationCap: totalCap
            );
            if (
                result.Status is HostileShadowSpawnStatus.Unavailable
                    or HostileShadowSpawnStatus.Rejected
            )
            {
                LogOnce(result.Reason, LogLevel.Warn);
            }
            LogFastRefreshState(
                playerKey,
                string.Concat(
                    "attempt:",
                    result.Status.ToString(),
                    ":queue-before=",
                    queue.Count.ToString(CultureInfo.InvariantCulture)
                ),
                string.Concat(
                    "destination-location=",
                    targetLocationId,
                    ", occupancy=",
                    currentMapLockedOccupancy.ToString(CultureInfo.InvariantCulture),
                    ", cap=",
                    totalCap.ToString(CultureInfo.InvariantCulture),
                    ", status=",
                    result.Status.ToString(),
                    ", spawned=",
                    result.Spawned.ToString(),
                    ", reason=",
                    result.Reason,
                    ", queue-count-before=",
                    queue.Count.ToString(CultureInfo.InvariantCulture)
                )
            );
            if (result.Spawned)
                queue.Dequeue();
            else if (
                result.Status is not HostileShadowSpawnStatus.Waiting
                    and not HostileShadowSpawnStatus.AtCap
            )
            {
                // Invalid command/resource/session failures are terminal for this transition
                // entry. Waiting/AtCap remain retryable and are intentionally not dequeued.
                queue.Dequeue();
            }
            if (queue.Count == 0)
                pendingFastSpawnsByPlayer.Remove(playerKey);
            break; // 每 tick 只刷一只（陆续出现）
        }
        fastSpawnNextDueMilliseconds =
            nowMilliseconds + FastSpawnIntervalMilliseconds;
    }

    /// <summary>
    /// 绑定组合的超限淡出由投影协调器完成；投影从索引消失后才解除隐藏并清理危险实体。
    /// 这样不会把绑定组合拆成“先删实体/再删投影”的瞬时硬删除。
    /// </summary>
    private void AdvanceOverCapBindingFades()
    {
        if (projectionHost is null || pendingOverCapBindingFades.Count == 0)
            return;

        var pending = new List<long>(pendingOverCapBindingFades);
        foreach (var entityId in pending)
        {
            if (
                !bindingCorrelationByEntity.TryGetValue(
                    entityId,
                    out var correlationId
                )
            )
            {
                pendingOverCapBindingFades.Remove(entityId);
                continue;
            }
            if (
                projectionHost.TryGetBindingProjectionPosition(
                    correlationId,
                    out _,
                    out _
                )
            )
            {
                continue;
            }

            bindingEntityByCorrelation.Remove(correlationId);
            bindingCorrelationByEntity.Remove(entityId);
            if (world.TryExitBinding(entityId, out _))
            {
                authority.SetBindingState(
                    entityId,
                    false,
                    string.Empty,
                    string.Empty,
                    out _
                );
            }
            authority.CleanupEntity(
                entityId,
                "hostile-shadow.cleanup.over-cap-fade-completed"
            );
            bindingRollSchedule.Remove(entityId);
            overCapCheckSchedule.Remove(entityId);
            pendingOverCapBindingFades.Remove(entityId);
        }
    }

    /// <summary>
    /// Refresh capacity is deliberately local to the current map and target lock. Off-map
    /// entities remain alive and keep their own despawn lifecycle, but cannot block this map's
    /// natural or cut-map refresh permit.
    /// </summary>
    private int CountCurrentMapLockedShadowOccupancy(
        string playerKey,
        string locationId
    )
    {
        return world.CountLockedEntitiesForOwnerAtLocation(playerKey, locationId);
    }

    int IHostileShadowLocationOccupancyProvider.CountHostileShadowsForOwnerAtLocation(
        string playerKey,
        string locationId
    )
    {
        return CountHostileShadowsForOwnerAtLocation(playerKey, locationId);
    }

    private int CountHostileShadowsForOwnerAtLocation(
        string playerKey,
        string locationId
    )
    {
        var hostile = world.CountEntitiesForOwnerAtLocation(playerKey, locationId);
        if (hostile == 0)
            return 0;

        var boundPairs = 0;
        foreach (var entityId in bindingCorrelationByEntity.Keys)
        {
            if (
                world.TryGetEntityLocationId(entityId, out var entityLocationId)
                && string.Equals(entityLocationId, locationId, StringComparison.Ordinal)
                && authority.TryGetEntity(entityId, out var state)
                && state is not null
                && string.Equals(state.OwnerPlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                boundPairs++;
            }
        }
        return Math.Max(0, hostile - boundPairs);
    }

    /// <summary>
    /// 旧的批量强制脱战辅助。当前设计不再由 ShadowCreatures 档退出触发；自然脱战
    /// 只由每只影怪当前目标的定时概率判定进入。
    /// </summary>
    private void ForceRetreatAll()
    {
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (
                bindingCorrelationByEntity.ContainsKey(entityId)
                || pendingRetreats.ContainsKey(entityId)
            )
            {
                continue;
            }
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
                continue;
            if (state.Health <= 0)
                continue;
            BeginRetreat(entityId, state);
        }
    }

    private void ForceRetreatForPlayer(string playerKey)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return;
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (
                bindingCorrelationByEntity.ContainsKey(entityId)
                || pendingRetreats.ContainsKey(entityId)
            )
            {
                continue;
            }
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
                continue;
            if (
                state.Health <= 0
                || !IsRetreatSubjectForPlayer(entityId, state, playerKey)
            )
                continue;
            BeginRetreat(entityId, state);
        }
    }

    private bool IsRetreatSubjectForPlayer(
        long entityId,
        ShadowStateSnapshot state,
        string playerKey
    )
    {
        return string.Equals(
            GetRetreatSubjectPlayerKey(entityId, state),
            playerKey,
            StringComparison.Ordinal
        );
    }

    private bool TryGetProjectionPushBoxDefinition(
        string speciesId,
        out HostileAttackRuntimeDefinition definition,
        out string reason
    )
    {
        definition = null!;
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
        {
            reason = "hostile-shadow.push-box-species-not-authorized";
            return false;
        }
        if (projectionPushBoxDefinitions.TryGetValue(speciesId, out definition!))
        {
            reason = "hostile-shadow.push-box-definition-cached";
            return true;
        }
        if (!TryResolveBindingForSpecies(speciesId, out var bindingId))
        {
            reason = "hostile-shadow.push-box-species-binding-invalid";
            return false;
        }
        var resolvedProfile = profiles.Resolve(bindingId);
        if (!resolvedProfile.Success || resolvedProfile.Profile is null)
        {
            reason = resolvedProfile.Reason;
            return false;
        }
        if (!resources.TryGetHostileAttackMetadata(bindingId, out var metadata, out reason))
            return false;
        if (
            !HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                resolvedProfile.Profile,
                out var created,
                out reason
            )
            || created is null
        )
        {
            return false;
        }

        definition = created;
        projectionPushBoxDefinitions.Add(speciesId, definition);
        reason = "hostile-shadow.push-box-definition-ready";
        return true;
    }

    private void RemoveRemotePushBoxStatesForOwner(
        string ownerPlayerKey,
        string locationId,
        HashSet<string> activeStableIds
    )
    {
        staleRemotePushBoxKeys.Clear();
        foreach (var pair in remotePushBoxStates)
        {
            var state = pair.Value;
            if (
                !string.Equals(
                    state.OwnerPlayerKey,
                    ownerPlayerKey,
                    StringComparison.Ordinal
                )
                || (
                    string.Equals(state.LocationId, locationId, StringComparison.Ordinal)
                    && activeStableIds.Contains(pair.Key)
                )
            )
            {
                continue;
            }
            staleRemotePushBoxKeys.Add(pair.Key);
        }
        foreach (var key in staleRemotePushBoxKeys)
            remotePushBoxStates.Remove(key);
    }

    private void PruneRemotePushBoxStates()
    {
        if (remotePushBoxStates.Count == 0)
            return;
        var now = (long)Game1.ticks;
        staleRemotePushBoxKeys.Clear();
        foreach (var pair in remotePushBoxStates)
        {
            if (now >= pair.Value.LastReceivedTick
                && now - pair.Value.LastReceivedTick > PushBoxIntentFreshnessTicks)
            {
                staleRemotePushBoxKeys.Add(pair.Key);
            }
        }
        foreach (var key in staleRemotePushBoxKeys)
            remotePushBoxStates.Remove(key);
    }

    private static string CreateProjectionStableId(
        string ownerPlayerKey,
        string correlationId
    )
    {
        return string.Concat("projection:", ownerPlayerKey, ":", correlationId);
    }

    private void ClearPushBoxState()
    {
        remotePushBoxStates.Clear();
        lastRemotePushBoxBatchByOwner.Clear();
        lastRemotePushBoxLocationByOwner.Clear();
        localPushBoxParticipants.Clear();
        localPushBoxIntents.Clear();
        activeLocalPushBoxKeys.Clear();
        localPushBoxIntentBuffer.Clear();
        staleLocalPushBoxKeys.Clear();
        staleRemotePushBoxKeys.Clear();
        foreach (var entries in pushBoxResultEntriesByPlayer.Values)
            entries.Clear();
        pushBoxResultEntriesByPlayer.Clear();
        projectionPushBoxDefinitions.Clear();
        lastAppliedPushBoxResultBatchNonce = 0;
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
            // DIAG-20260811: 切图记录旧地图危险影怪分布，新地图内快速刷出
            // min(旧地图数量, 当前密度档上限) 只（影怪跟随玩家体验）。
            // 触发时 currentLocation 可能已切新图——优先用上次记录的位置。
            var player = Game1.player;
            if (
                player is not null
                && player.currentLocation is not null
                && !string.IsNullOrWhiteSpace(
                    player.currentLocation.NameOrUniqueName
                )
            )
            {
                var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                );
                var locationId =
                    player.currentLocation.NameOrUniqueName;
                if (
                    lastLocationByPlayer.TryGetValue(
                        playerKey,
                        out var lastLocation
                    )
                    && !string.Equals(
                        lastLocation,
                        locationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    locationId = lastLocation;
                }
                QueueFastSpawnsForLocation(playerKey, locationId);
            }
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
        naturalSpawnRequestIds.Reset();
        multiplayer.ClearSession();
        sessionLifecycleCoordinator.ClearSession();
        ClearPushBoxState();
        world.ClearSession();
        pendingOverCapRetreats.Clear();
        pendingOverCapActions.Clear();
        pendingOverCapBindingFades.Clear();
        pendingOverCapProjectionFades.Clear();
        ClearShadowCheckSchedules();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        worldMutationSuspended = true;
        pendingOverCapRetreats.Clear();
        pendingOverCapActions.Clear();
        pendingOverCapBindingFades.Clear();
        pendingOverCapProjectionFades.Clear();
        ClearShadowCheckSchedules();
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host)
        {
            authority.CleanupAll(HostileShadowCleanupReasonIds.DayEnding);
        }
        multiplayer.OnDayEnding();
        sessionLifecycleCoordinator.DayEnding();
        ClearPushBoxState();
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

    private static bool IsOverCapAssetBinding(string assetBindingId)
    {
        return string.Equals(
                assetBindingId,
                ShadowMonsterAssetBindingIds.CreeperFear,
                StringComparison.Ordinal
            )
            || string.Equals(
                assetBindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            );
    }

    private static bool IsOverCapProjectionSpecies(string speciesId)
    {
        return string.Equals(
                speciesId,
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
                StringComparison.Ordinal
            )
            || string.Equals(
                speciesId,
                ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId,
                StringComparison.Ordinal
            );
    }

    private static bool IsOverCapActionInProgress(string stateId)
    {
        return string.Equals(
                stateId,
                HostileShadowStateIds.Spawn,
                StringComparison.Ordinal
            )
            || string.Equals(
                stateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            || string.Equals(
                stateId,
                HostileShadowStateIds.HitTeleport,
                StringComparison.Ordinal
            );
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

    private void LogNaturalRefreshState(
        string playerKey,
        string locationId,
        string state,
        string detail
    )
    {
        var key = string.Concat(playerKey, "|", locationId);
        if (
            naturalRefreshDiagnosticStates.TryGetValue(key, out var previous)
            && string.Equals(previous, state, StringComparison.Ordinal)
        )
        {
            return;
        }
        if (
            !naturalRefreshDiagnosticStates.ContainsKey(key)
            && naturalRefreshDiagnosticStates.Count >= MaximumLoggedReasons
        )
        {
            return;
        }
        naturalRefreshDiagnosticStates[key] = state;
        monitor.Log(
            string.Concat(
                "Hostile shadow natural refresh: player=",
                playerKey,
                ", location=",
                locationId,
                ", state=",
                state,
                ", ",
                detail
            ),
            LogLevel.Debug
        );
    }

    private void LogNaturalSpawnResult(
        string playerKey,
        string locationId,
        HostileShadowSpawnResult result
    )
    {
        monitor.Log(
            string.Concat(
                "Hostile shadow natural spawn result: player=",
                playerKey,
                ", location=",
                locationId,
                ", status=",
                result.Status.ToString(),
                ", spawned=",
                result.Spawned.ToString(),
                ", occupancy=",
                result.Occupancy.ToString(CultureInfo.InvariantCulture),
                ", cap=",
                result.Cap.ToString(CultureInfo.InvariantCulture),
                ", reason=",
                result.Reason
            ),
            result.Spawned ? LogLevel.Debug : LogLevel.Warn
        );
    }

    private void LogFastRefreshState(
        string playerKey,
        string state,
        string detail
    )
    {
        var key = string.Concat(playerKey, "|", state);
        if (fastRefreshDiagnosticStates.ContainsKey(key))
            return;
        if (fastRefreshDiagnosticStates.Count >= MaximumLoggedReasons)
            return;
        fastRefreshDiagnosticStates[key] = detail;
        monitor.Log(
            string.Concat(
                "Hostile shadow fast refresh: player=",
                playerKey,
                ", state=",
                state,
                ", ",
                detail
            ),
            LogLevel.Debug
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

    private sealed class LifecycleBudgetAuthority
        : IHostileShadowBudgetAuthority,
            IHostileShadowFastSpawnBudgetAuthority
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

        public bool TryGetCurrentPoolAndTotalCap(
            string playerKey,
            out SanityShadowPoolTier poolTier,
            out int totalCap
        )
        {
            poolTier = SanityShadowPoolTier.Inactive;
            totalCap = 0;
            if (
                !lifecycle.TryGetShadowBudgetState(playerKey, out var snapshot)
                || snapshot is null
                || snapshot.PoolTier is not SanityShadowPoolTier.Hostile15
                    and not SanityShadowPoolTier.Hostile10
                || !lifecycle.TryGetShadowBudgetTotalCap(playerKey, out totalCap)
                || totalCap <= 0
            )
            {
                totalCap = 0;
                return false;
            }

            poolTier = snapshot.PoolTier;
            return true;
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
