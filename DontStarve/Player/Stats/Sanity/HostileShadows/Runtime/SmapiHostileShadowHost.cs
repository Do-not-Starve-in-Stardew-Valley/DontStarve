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
    private readonly Dictionary<long, long> lastBindingRollMinuteByEntity = new();
    private int bindingAlignTickCounter;
    // DIAG-20260810: 统一上限池超限清理节奏（每游戏内 10 分钟 50% 消失）。
    private long lastOverCapTrimMinute = -1;
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

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        timeApi.OnUpdate.Add(OnMinuteUpdate);
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

        // DIAG-20260810: 统一上限池超限清理（每游戏内 10 分钟对超限部分逐只 50% 消失）。
        if (gameMinute - lastOverCapTrimMinute >= OverCapTrimIntervalMinutes)
        {
            lastOverCapTrimMinute = gameMinute;
            TrimOverCap(gameMinute);
        }

        // DIAG-20260809: 每 10 游戏分钟检查一次；每只影怪是否可脱战由它当前锁定的
        // 玩家单独决定，不能用某个玩家或全局 Danger 状态替代。
        if (projectionHost is not null)
        {
            RollBindings(gameMinute);
        }

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
                authority.GetOwnerOccupancy(playerKey),
                elapsedMilliseconds,
                requestedSpecies
            );
            if (
                evaluation.Status
                != SanityShadowBudgetEvaluationStatus.PermitGranted
            )
            {
                continue;
            }

            var requestId = string.Concat(
                "hostile-shadow.interval.",
                authority.SessionId,
                ".",
                playerKey,
                ".",
                timeApi.Time.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
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
        }
    }

    /// <summary>
    /// DIAG-20260809: 脱战 roll——Danger 档全退出（san>17.5%）且距上次 ≥10 游戏分钟时，
    /// 对每只在册危险影怪 roll 25% 脱战（隐藏+绑定投影）。
    /// </summary>
    private void RollBindings(long gameMinute)
    {
        foreach (var entityId in world.GetOrderedEntityIds())
        {
            if (bindingCorrelationByEntity.ContainsKey(entityId))
                continue;
            if (!authority.TryGetEntity(entityId, out var state) || state is null)
                continue;
            if (state.Health <= 0)
                continue;
            if (
                lastBindingRollMinuteByEntity.TryGetValue(entityId, out var lastRoll)
                && gameMinute - lastRoll < BindingRollIntervalMinutes
            )
                continue;
            lastBindingRollMinuteByEntity[entityId] = gameMinute;
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
    private void BeginRetreat(long entityId, ShadowStateSnapshot state)
    {
        if (projectionHost is null)
            return;
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
            return;
        }
        pendingRetreats[entityId] =
            Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        LogOnce("hostile-shadow.retreat-started", LogLevel.Debug);
    }

    /// <summary>DIAG-20260810: 恐吓结束——进入绑定隐藏态并生成绑定投影。</summary>
    private void CompleteRetreat(long entityId)
    {
        pendingRetreats.Remove(entityId);
        if (projectionHost is null)
            return;
        if (!authority.TryGetEntity(entityId, out var state) || state is null)
        {
            world.TryExitRetreat(entityId, out _);
            return;
        }
        BindNow(entityId, state);
    }

    /// <summary>
    /// DIAG-20260809: 单只影怪立即脱战：进入绑定隐藏态 → 在实体受击框中心
    /// 生成同朝向绑定投影；投影生成失败则回滚隐藏。调试命令（ds_spawn bind）
    /// 直接调用本方法；自然脱战经恐吓流程（BeginRetreat→CompleteRetreat）后调用。
    /// </summary>
    private void BindNow(long entityId, ShadowStateSnapshot state)
    {
        if (projectionHost is null)
            return;
        if (!projectionHost.TryGetBindingOwner(out var owner))
        {
            LogOnce("hostile-shadow.binding-owner-unavailable", LogLevel.Warn);
            world.TryExitRetreat(entityId, out _);
            return;
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
            return;
        }
        var speciesId = ResolveProjectionSpeciesId(state.AssetBindingId);
        if (speciesId is null)
        {
            world.TryExitBinding(entityId, out _);
            return;
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
            return;
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

        if (
            projectionHost is null
            || (
                bindingCorrelationByEntity.Count == 0
                && pendingRetreats.Count == 0
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

        // DIAG-20260811: 切图快速刷新推进（每 2 秒刷 1 只，陆续出现）。
        AdvanceFastSpawns();

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
        reason = "hostile-shadow.binding-host-bound";
        return true;
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
        // 鼠标右下方。用 Position=0 时的受击框中心作为偏移量，减去后框中心落在鼠标处。
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
            && HostileAttackCollisionResolver.TryCreateWorldHurtBox(
                debugDefinition,
                0d,
                0d,
                out var debugHurtBox
            )
        )
        {
            positionX -= (float)(debugHurtBox.X + debugHurtBox.Width / 2d);
            positionY -= (float)(debugHurtBox.Y + debugHurtBox.Height / 2d);
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
        reason = "hostile-shadow.debug-spawned";
        return result;
    }

    private HostileShadowSpawnResult TrySpawn(
        string requestId,
        HostileShadowSpawnOrigin origin,
        Farmer player,
        string bindingId,
        string reason,
        float? positionX = null,
        float? positionY = null,
        SanityShadowBudgetEvaluationResult? preEvaluatedBudget = null
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
                // DIAG-20260812: 支持指定生成位置（原地转化用）；未提供回退玩家位置。
                positionX ?? player.Position.X,
                positionY ?? player.Position.Y,
                timeApi.Time,
                resolved.Profile,
                reason
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
        }
    }

    /// <summary>
    /// DIAG-20260810: 统一上限池超限清理（7 号需求）。补偿+已刷（含指令生成、
    /// 绑定对算 1 只）超过当前密度档上限（BaseCap+TerrorbeakCap）时，每游戏内
    /// 10 分钟对超限部分逐只 50% 概率消失（无害投影移除/危险实体连带清理）；
    /// 每玩家单独计算。
    /// </summary>
    private void TrimOverCap(long gameMinute)
    {
        if (projectionHost is null)
            return;
        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                player.UniqueMultiplayerID
            );
            if (
                !lifecycle.TryGetShadowBudgetTotalCap(
                    playerKey,
                    out var cap
                )
                || cap <= 0
            )
            {
                continue;
            }
            // DIAG-20260811: 上限按所在地图计算（切图后旧地图影怪不计入新地图上限）。
            var locationId = player.currentLocation is null
                ? string.Empty
                : player.currentLocation.NameOrUniqueName;
            var harmless = projectionHost.CountShadowProjectionsForOwner(playerKey);
            var hostile = world.CountEntitiesForOwnerAtLocation(
                playerKey,
                locationId
            );
            // 绑定对算 1 只：只统计所在地图的绑定实体（绑定投影在册即绑定实体不另计）。
            var boundPairs = 0;
            foreach (var entityId in bindingCorrelationByEntity.Keys)
            {
                if (
                    world.TryGetEntityLocationId(
                        entityId,
                        out var entityLocationId
                    )
                    && string.Equals(
                        entityLocationId,
                        locationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    boundPairs++;
                }
            }
            var total = harmless + hostile - boundPairs;
            if (total <= cap)
                continue;
            var over = total - cap;
            var removed = 0;

            // 1) 绑定实体：连带绑定投影一起清（50% 概率，仅所在地图）。
            var boundEntityIds = new List<long>(bindingCorrelationByEntity.Keys);
            foreach (var entityId in boundEntityIds)
            {
                if (removed >= over)
                    break;
                if (
                    !world.TryGetEntityLocationId(
                        entityId,
                        out var boundLocationId
                    )
                    || !string.Equals(
                        boundLocationId,
                        locationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }
                if (bindingRandom.NextDouble() >= 0.5d)
                    continue;
                if (!bindingCorrelationByEntity.TryGetValue(entityId, out var correlationId))
                    continue;
                bindingEntityByCorrelation.Remove(correlationId);
                bindingCorrelationByEntity.Remove(entityId);
                if (projectionHost.TryRemoveBindingProjection(correlationId, out _))
                {
                    // 投影已移除（连带隐藏实体解除隐藏）。
                }
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
                    "hostile-shadow.cleanup.over-cap-bound"
                );
                removed++;
            }

            // 2) 未绑定危险实体（50% 概率，仅所在地图）。
            foreach (var entityId in world.GetOrderedEntityIds())
            {
                if (removed >= over)
                    break;
                if (bindingCorrelationByEntity.ContainsKey(entityId))
                    continue;
                if (
                    !world.TryGetEntityLocationId(
                        entityId,
                        out var entityLocationId
                    )
                    || !string.Equals(
                        entityLocationId,
                        locationId,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }
                if (bindingRandom.NextDouble() >= 0.5d)
                    continue;
                authority.CleanupEntity(
                    entityId,
                    "hostile-shadow.cleanup.over-cap"
                );
                removed++;
            }

            // 3) 无害投影（含指令生成，50% 概率）。
            if (removed < over)
            {
                projectionHost.TrimShadowProjectionsForOwner(
                    playerKey,
                    over - removed,
                    bindingRandom
                );
            }
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
        var bySpecies = world.CountEntitiesBySpeciesAtLocation(locationId);
        if (bySpecies.Count == 0)
            return;
        if (
            !lifecycle.TryGetShadowBudgetTotalCap(playerKey, out var cap)
            || cap <= 0
        )
        {
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
        LogOnce("hostile-shadow.fast-spawn-queued", LogLevel.Debug);
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
    /// DIAG-20260811: 快速刷新推进——每 2 秒从队列取一只在玩家附近生成（走常规
    /// TrySpawn 链路，不占密度预算；数量已按上限截断）。
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
                pendingFastSpawnsByPlayer.Remove(playerKey);
                break;
            }
            var bindingId = queue.Dequeue();
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
                HostileShadowSpawnOrigin.Interval,
                target,
                bindingId,
                "hostile-shadow.spawn.fast-spawn"
            );
            if (
                result.Status is HostileShadowSpawnStatus.Unavailable
                    or HostileShadowSpawnStatus.Rejected
            )
            {
                LogOnce(result.Reason, LogLevel.Warn);
            }
            if (queue.Count == 0)
                pendingFastSpawnsByPlayer.Remove(playerKey);
            break; // 每 tick 只刷一只（陆续出现）
        }
        fastSpawnNextDueMilliseconds =
            nowMilliseconds + FastSpawnIntervalMilliseconds;
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
