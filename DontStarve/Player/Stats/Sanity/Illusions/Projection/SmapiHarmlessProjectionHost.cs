#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
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
    // DIAG-20260809: 影怪无害投影 correlation 来源提为字段，供调度与调试召唤共用同一序号源。
    private readonly IShadowProjectionCorrelationSource correlationSource =
        new SessionShadowProjectionCorrelationSource();
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
            correlationSource
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

    /// <summary>
    /// DIAG-20260804: 测试命令 ds_spawn 专用。在指定世界坐标直接生成无害幻觉投影
    /// （mr-skitts/dark-hand/dark-watcher/eyes），绕过调度器与选点器；仍走完整视觉加载/TTL。
    /// </summary>
    internal string DebugSpawnProjectionAt(
        string speciesId,
        float positionX,
        float positionY
    )
    {
        if (disposed)
            return "projection.host-disposed";
        if (!TryGetCurrentOwner(
                out _, out var currentLocation, out var currentContext
            ))
        {
            return "spawn.owner-context-invalid";
        }
        HarmlessProjectionPolicy? policy = null;
        foreach (var candidate in scheduler.Policies)
        {
            if (
                string.Equals(
                    candidate.SpeciesId,
                    speciesId,
                    StringComparison.Ordinal
                )
            )
            {
                policy = candidate;
                break;
            }
        }
        if (policy is null)
            return "spawn.species-not-registered";
        if (
            spawnGatesBySpecies.TryGetValue(
                policy.SpeciesId,
                out var spawnGate
            )
            && !spawnGate.CanSpawn(
                new HarmlessProjectionSpawnRequest(
                    currentContext,
                    policy,
                    new HarmlessProjectionWorldPoint(
                        positionX,
                        positionY
                    ),
                    timeApi.Time
                ),
                out var gateReason
            )
        )
        {
            return string.IsNullOrWhiteSpace(gateReason)
                ? "spawn.species-gate-rejected"
                : gateReason;
        }
        if (
            !TryLoadSpawnVisuals(
                policy,
                out var resource,
                out var visualStateResources,
                out var visualFailure
            )
        )
        {
            return visualFailure;
        }

        long expiresAtMinute;
        try
        {
            expiresAtMinute = checked(timeApi.Time + policy.HardTtlMinutes);
        }
        catch (OverflowException)
        {
            return "spawn.ttl-deadline-overflow";
        }
        scheduler.Index.TryAdd(
            new HarmlessProjectionInstance(
                currentContext,
                policy,
                new HarmlessProjectionWorldPoint(positionX, positionY),
                timeApi.Time,
                expiresAtMinute,
                policy.InitialStateId,
                resource,
                visualStateResources
            ),
            out _
        );
        return "spawn.debug-spawned";
    }

    /// <summary>
    /// DIAG-20260809: 测试命令 ds_spawn harmless 专用。在指定世界坐标请求影怪的
    /// 非危险形态（无害投影，creeper-fear/terrorbeak）。位置可指定；调试生成绕过自然
    /// 预算、刷新计时、共享上限和转换锁，供测试命令稳定生成目标。
    /// </summary>
    internal string DebugSpawnShadowProjectionAt(
        string speciesId,
        float positionX,
        float positionY
    )
    {
        if (disposed)
            return "shadow-projection.host-disposed";
        if (!lifecycle.IsEnabled)
            return "shadow-projection.system-disabled";
        if (!TryGetCurrentOwner(out _, out _, out var currentContext))
        {
            return "spawn.owner-context-invalid";
        }
        return SpawnShadowProjectionCore(
            currentContext,
            speciesId,
            positionX,
            positionY
        );
    }

    /// <summary>
    /// DIAG-20260809: 无害投影指定位置生成核心。调试召唤绕过自然预算、刷新计时、
    /// 共享上限和转换锁，但仍经过宿主、物种、资源和索引完整性检查；驱赶补偿仍使用
    /// 独立的补偿通道，不经过此方法。
    /// </summary>
    private string SpawnShadowProjectionCore(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        float positionX,
        float positionY
    )
    {
        ShadowCreatureHarmlessProjectionPolicy? policy = null;
        foreach (var candidate in shadowCoordinator.Policies)
        {
            if (
                string.Equals(
                    candidate.SpeciesId,
                    speciesId,
                    StringComparison.Ordinal
                )
            )
            {
                policy = candidate;
                break;
            }
        }
        if (policy is null)
            return "spawn.species-not-registered";

        if (
            !TryLoadShadowProjectionVisuals(
                policy,
                out var resource,
                out var spawnResource,
                out var moveResource,
                out var visualReason
            )
        )
        {
            return visualReason;
        }

        string correlationId;
        try
        {
            correlationId = correlationSource.Next(
                owner.PlayerKey,
                policy.SpeciesId
            );
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "shadow-projection.debug-correlation-threw",
                $"Shadow harmless projection debug correlation failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            return "shadow-projection.correlation-threw";
        }
        if (
            string.IsNullOrWhiteSpace(correlationId)
            || shadowCoordinator.Index.ContainsCorrelation(correlationId)
        )
        {
            return "shadow-projection.correlation-invalid-or-duplicate";
        }

        var instance = new ShadowCreatureHarmlessProjectionInstance(
            correlationId,
            owner,
            policy,
            new HarmlessProjectionWorldPoint(positionX, positionY),
            timeApi.Time,
            resource,
            spawnResource,
            moveResource
        );
        if (
            !shadowCoordinator.TryRegisterDebugProjection(
                owner,
                instance,
                out var registrationReason
            )
        )
        {
            return registrationReason;
        }
        return "spawn.debug-spawned";
    }

    /// <summary>
    /// DIAG-20260809: 驱赶补偿——被驱赶消失的影怪立即在玩家附近 4-16 格补刷一只，
    /// 防止玩家反复驱赶导致场上无影怪（补偿不占预算 60 分钟 timer）。
    /// </summary>
    private void TrySpawnCompensationProjection(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        string speciesId
    )
    {
        if (disposed || Game1.player is null || Game1.player.currentLocation is null)
            return;
        // DIAG-20260811: 影怪总数达到上限后驱赶不补偿（超限部分由宿主每 10 分钟
        // 50% 消失处理，不再无限补刷）。投影侧计数近似（多人下危险实体占用未计入）。
        if (
            lifecycle.TryGetShadowBudgetTotalCap(
                owner.PlayerKey,
                out var totalCap
            )
            && totalCap > 0
            && shadowCoordinator.Index.CountForOwner(owner.PlayerKey) >= totalCap
        )
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2d;
            var distancePixels = (4d * 64d) + (Random.Shared.NextDouble() * 12d * 64d);
            var worldX = ownerStandingWorldPixel.X
                + (float)(Math.Cos(angle) * distancePixels);
            var worldY = ownerStandingWorldPixel.Y
                + (float)(Math.Sin(angle) * distancePixels);
            if (
                !Game1.player.currentLocation.isTileOnMap(
                    (int)Math.Floor(worldX / 64f),
                    (int)Math.Floor(worldY / 64f)
                )
            )
            {
                continue;
            }
            var reason = SpawnShadowProjectionCore(
                owner,
                speciesId,
                (float)worldX,
                (float)worldY
            );
            if (string.Equals(reason, "spawn.debug-spawned", StringComparison.Ordinal))
                return;
            LogFailureOnce(
                string.Concat("shadow-compensation|", speciesId),
                $"Shadow compensation spawn failed closed ({reason})."
            );
            return;
        }
    }

    /// <summary>
    /// DIAG-20260809: 测试命令 ds_spawn clear 专用。清理所有影怪无害投影（无 TTL，
    /// 只能靠此命令/切图/跨天清理）；保留档位相位与转换证据，便于继续测试。
    /// </summary>
    internal int DebugClearShadowProjections()
    {
        if (disposed)
            return 0;
        return shadowCoordinator.CleanupAll(
            HarmlessProjectionCleanupReason.OwnerInvalidated,
            clearOwnerPhases: false,
            clearConversionEvidence: false
        );
    }

    // DIAG-20260809: 绑定投影（危险实体隐藏态的外观）按 correlation 登记，
    // 供位置查询与解除绑定删除；与协调器 Index 保持同步（Index 清理后自动移除）。
    private readonly Dictionary<string, ShadowCreatureHarmlessProjectionInstance>
        bindingProjections = new(StringComparer.Ordinal);

    /// <summary>
    /// DIAG-20260809: 生成绑定投影（危险实体隐藏态的外观）。位置=实体当前位置，
    /// 朝向=实体隐藏前朝向；correlationId 由宿主生成并返回，供解除绑定时删除。
    /// </summary>
    internal bool TrySpawnBindingProjection(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        double positionX,
        double positionY,
        string facingId,
        out string correlationId,
        out string reason
    )
    {
        correlationId = string.Empty;
        if (disposed)
        {
            reason = "shadow-projection.host-disposed";
            return false;
        }
        if (owner is null)
        {
            reason = "spawn.owner-context-invalid";
            return false;
        }

        ShadowCreatureHarmlessProjectionPolicy? policy = null;
        foreach (var candidate in shadowCoordinator.Policies)
        {
            if (
                string.Equals(
                    candidate.SpeciesId,
                    speciesId,
                    StringComparison.Ordinal
                )
            )
            {
                policy = candidate;
                break;
            }
        }
        if (policy is null)
        {
            reason = "spawn.species-not-registered";
            return false;
        }
        if (
            !TryLoadShadowProjectionVisuals(
                policy,
                out var resource,
                out var spawnResource,
                out var moveResource,
                out var visualReason
            )
        )
        {
            reason = visualReason;
            return false;
        }

        string generated;
        try
        {
            generated = correlationSource.Next(
                owner.PlayerKey,
                policy.SpeciesId
            );
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "shadow-projection.binding-correlation-threw",
                $"Shadow harmless projection binding correlation failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            reason = "shadow-projection.correlation-threw";
            return false;
        }
        if (
            string.IsNullOrWhiteSpace(generated)
            || shadowCoordinator.Index.ContainsCorrelation(generated)
        )
        {
            reason = "shadow-projection.correlation-invalid-or-duplicate";
            return false;
        }

        var instance = new ShadowCreatureHarmlessProjectionInstance(
            generated,
            owner,
            policy,
            new HarmlessProjectionWorldPoint(positionX, positionY),
            timeApi.Time,
            resource,
            spawnResource,
            moveResource
        );
        instance.SetFacingId(facingId);
        // DIAG-20260811: 绑定投影 10 秒驱赶保护期（玩家能看到投影稳定出现）；
        // 保护期内静止且豁免近距驱赶，之后恢复正常行为（可游荡/可驱赶）。
        // SetBindingProjection 同时把动画状态切到静息——绑定投影不播生成动画
        // （危险影怪恐吓→消失→立刻出现非危险形态，无生成过渡）。
        instance.SetBindingProjection(10000);
        if (!shadowCoordinator.Index.TryAdd(instance, out var addReason))
        {
            reason = addReason;
            return false;
        }
        bindingProjections[generated] = instance;
        correlationId = generated;
        reason = "shadow-projection.binding-spawned";
        return true;
    }

    /// <summary>
    /// DIAG-20260809: 删除绑定投影（低理智恢复/连带清除时解除外观）。
    /// </summary>
    internal bool TryRemoveBindingProjection(
        string correlationId,
        out string reason
    )
    {
        if (disposed || string.IsNullOrWhiteSpace(correlationId))
        {
            reason = "shadow-projection.binding-remove-invalid";
            return false;
        }
        bindingProjections.Remove(correlationId);
        if (
            !shadowCoordinator.Index.TryRemove(
                correlationId,
                HarmlessProjectionCleanupReason.OwnerInvalidated,
                out _
            )
        )
        {
            reason = "shadow-projection.binding-remove-failed";
            return false;
        }
        reason = "shadow-projection.binding-removed";
        return true;
    }

    /// <summary>
    /// DIAG-20260810: 高理智场景——绑定投影生成后立即进入高理智淡出
    /// （危险影怪先脱战→隐藏→绑定投影，再走无害投影消失链条一起消失）。
    /// </summary>
    internal bool BeginBindingFadeOut(string correlationId)
    {
        if (
            disposed
            || !bindingProjections.TryGetValue(
                correlationId,
                out var instance
            )
            || instance is null
            || instance.IsCleanedUp
        )
        {
            return false;
        }
        instance.BeginFadeOut(
            ShadowCreatureHarmlessProjectionCatalog.HighSanFadeOutMilliseconds,
            ShadowCreatureHarmlessProjectionInstance
                .ShadowCreatureProjectionFadeOutKind.HighSan
        );
        return true;
    }

    /// <summary>DIAG-20260810: 指定 owner 的在册无害投影数（统一上限池超限清理用）。</summary>
    internal int CountShadowProjectionsForOwner(string playerKey)
    {
        return shadowCoordinator.Index.CountForOwner(playerKey);
    }

    /// <summary>
    /// DIAG-20260810: 对指定 owner 的无害投影逐只按 50% 概率移除（超限清理），
    /// 最多移除 maxToRemove 只；返回实际移除数。含指令生成（ds_spawn harmless）。
    /// </summary>
    internal int TrimShadowProjectionsForOwner(
        string playerKey,
        int maxToRemove,
        Random random
    )
    {
        if (disposed || maxToRemove <= 0)
            return 0;
        if (
            !TryGetCurrentOwner(out _, out _, out var currentContext)
            || !string.Equals(
                currentContext.PlayerKey,
                playerKey,
                StringComparison.Ordinal
            )
        )
        {
            return 0;
        }
        if (
            !shadowCoordinator.Index.TryGetContextInstances(
                currentContext,
                out var instances
            )
            || instances is null
        )
        {
            return 0;
        }
        var removed = 0;
        foreach (var instance in instances)
        {
            if (removed >= maxToRemove)
                break;
            if (instance.IsCleanedUp)
                continue;
            if (random.NextDouble() >= 0.5d)
                continue;
            if (
                shadowCoordinator.Index.TryRemove(
                    instance.CorrelationId,
                    HarmlessProjectionCleanupReason.OwnerInvalidated,
                    out _
                )
            )
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// DIAG-20260809: 读取绑定投影当前位置（危险实体低频对齐用）。
    /// </summary>
    internal bool TryGetBindingProjectionPosition(
        string correlationId,
        out double positionX,
        out double positionY
    )
    {
        positionX = 0d;
        positionY = 0d;
        if (
            disposed
            || !bindingProjections.TryGetValue(
                correlationId,
                out var instance
            )
            || instance is null
            || instance.IsCleanedUp
        )
        {
            return false;
        }
        positionX = instance.WorldPixel.X;
        positionY = instance.WorldPixel.Y;
        return true;
    }

    /// <summary>DIAG-20260809: 绑定投影存活检查——被协调器清掉的绑定投影同步移除登记。</summary>
    private void PruneDeadBindingProjections()
    {
        if (bindingProjections.Count == 0)
            return;
        List<string>? removed = null;
        foreach (var pair in bindingProjections)
        {
            if (!shadowCoordinator.Index.ContainsCorrelation(pair.Key))
            {
                removed ??= new List<string>();
                removed.Add(pair.Key);
            }
        }
        if (removed is null)
            return;
        foreach (var correlationId in removed)
            bindingProjections.Remove(correlationId);
    }

    /// <summary>
    /// DIAG-20260809: 加载影怪无害投影全部动画槽。主用三槽（spawn/move/idle）必须成功
    /// 且通过契约校验（fail-closed）；taunt/death 已登记为休眠动作，额外槽位只加载以防
    /// 意外状态请求，失败仅记录不阻断无害投影生成。
    /// </summary>
    private bool TryLoadShadowProjectionVisuals(
        ShadowCreatureHarmlessProjectionPolicy policy,
        out SanitySlotResourceResult? idleResource,
        out SanitySlotResourceResult? spawnResource,
        out SanitySlotResourceResult? moveResource,
        out string reason
    )
    {
        idleResource = null;
        spawnResource = null;
        moveResource = null;
        if (
            !TryLoadShadowProjectionSlot(
                policy,
                policy.IdleVisualSlotId,
                out idleResource,
                out reason
            )
        )
        {
            return false;
        }
        if (
            !TryLoadShadowProjectionSlot(
                policy,
                policy.SpawnVisualSlotId,
                out spawnResource,
                out reason
            )
        )
        {
            return false;
        }
        if (
            !TryLoadShadowProjectionSlot(
                policy,
                policy.MoveVisualSlotId,
                out moveResource,
                out reason
            )
        )
        {
            return false;
        }

        foreach (var slotId in policy.AllVisualSlotIds)
        {
            if (IsAppliedShadowProjectionSlot(policy, slotId))
            {
                continue;
            }
            SanitySlotResourceResult auxiliary;
            try
            {
                auxiliary = resourceProvider.LoadVisualSlot(slotId, frameIndex: 0);
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    string.Concat("shadow-aux-resource|", slotId),
                    $"Shadow harmless projection auxiliary resource threw (slot={slotId}, {exception.GetType().Name}: {exception.Message})."
                );
                continue;
            }
            if (!auxiliary.Success)
            {
                if (IsRegisteredDormantActionSlot(policy, slotId))
                {
                    LogFailureOnce(
                        string.Concat("shadow-dormant-action-resource|", slotId),
                        $"Shadow harmless dormant action resource is unavailable but remains registered (slot={slotId}, code={auxiliary.Diagnostic.Code})."
                    );
                }
                else
                {
                    LogUnavailableResource(slotId, auxiliary);
                }
            }
        }

        reason = "shadow-projection.visuals-ready";
        return true;
    }

    private static bool IsAppliedShadowProjectionSlot(
        ShadowCreatureHarmlessProjectionPolicy policy,
        string slotId
    )
    {
        foreach (var appliedSlotId in policy.AppliedVisualSlotIds)
        {
            if (string.Equals(appliedSlotId, slotId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsRegisteredDormantActionSlot(
        ShadowCreatureHarmlessProjectionPolicy policy,
        string slotId
    )
    {
        foreach (var dormantSlotId in policy.RegisteredDormantActionVisualSlotIds)
        {
            if (string.Equals(dormantSlotId, slotId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool TryLoadShadowProjectionSlot(
        ShadowCreatureHarmlessProjectionPolicy policy,
        string slotId,
        out SanitySlotResourceResult? resource,
        out string reason
    )
    {
        resource = null;
        SanitySlotResourceResult loaded;
        try
        {
            loaded = resourceProvider.LoadVisualSlot(slotId, frameIndex: 0);
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "sanity.resource.consumer-facade-unavailable",
                $"Shadow harmless projection resource facade failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            reason = "sanity.resource.consumer-facade-unavailable";
            return false;
        }
        if (!policy.TryValidateResource(loaded, slotId, out var visualReason))
        {
            if (!loaded.Success)
                LogUnavailableResource(slotId, loaded);
            else
            {
                LogFailureOnce(
                    string.Concat("shadow-resource-contract|", slotId),
                    $"Shadow harmless projection visual failed closed (profile={policy.VisualProfileId}, slot={slotId}, reason={visualReason})."
                );
            }
            reason = visualReason;
            return false;
        }
        resource = loaded;
        reason = "shadow-projection.slot-ready";
        return true;
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

        if (
            !TryLoadShadowProjectionVisuals(
                request.Policy,
                out var resource,
                out var spawnResource,
                out var moveResource,
                out var visualReason
            )
        )
        {
            return ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                visualReason
            );
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
                resource,
                spawnResource,
                moveResource
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
            // DIAG-20260811: 切图清影怪无害投影计入驱赶补偿（切图后新地图按同样物种
            // 补刷）；绑定投影除外（连带清除隐藏实体，不补偿）。绑定投影带“已有实体”
            // 标签，切图时实体随投影消失链条连带清除。
            if (
                shadowCoordinator.Index.TryGetContextInstances(
                    owner,
                    out var warpInstances
                )
                && warpInstances is not null
            )
            {
                foreach (var instance in warpInstances)
                {
                    if (
                        instance.IsCleanedUp
                        || instance.IsBindingProjection
                    )
                    {
                        continue;
                    }
                    shadowCoordinator.RecordCompensation(instance.SpeciesId);
                }
            }
            // DIAG-20260809: 切图只清投影表现、保留档位相位（与“切图不清 tier 状态机”裁定一致）；
            // 否则相位删除后没有事件能重建，非危险形态影怪切图后永久不刷。
            shadowCoordinator.CleanupOwner(
                owner.PlayerKey,
                HarmlessProjectionCleanupReason.OwnerWarped,
                forgetOwnerPhase: false
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

        var elapsedMilliseconds = (int)Math.Min(
            int.MaxValue,
            Math.Max(0d, Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds)
        );

        PruneDeadBindingProjections();
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
        // DIAG-20260809: 驱赶补偿——被驱赶消失的影怪立即在玩家附近 4-16 格补刷一只，
        // 防止玩家反复驱赶无害影怪导致场上无影怪（补偿走独立通道，不占预算 timer）。
        var compensations = shadowCoordinator.ConsumePendingCompensations();
        foreach (var compensationSpeciesId in compensations)
        {
            TrySpawnCompensationProjection(
                currentOwner,
                standingWorldPixel,
                compensationSpeciesId
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
                (float)instance.WorldPixel.X,
                (float)instance.WorldPixel.Y
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

    /// <summary>DIAG-20260809: 供影怪宿主读取当前绑定 owner 上下文（生成绑定投影用）。</summary>
    internal bool TryGetBindingOwner(out HarmlessProjectionOwnerContext owner)
    {
        owner = null!;
        if (disposed || !TryGetCurrentOwner(out _, out _, out var current))
            return false;
        owner = current;
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
