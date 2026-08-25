#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// The two 50% shadow appearances are the only harmless projection species allowed to consume the
/// task-family 02 shared owner permit. Ordinary projection policies intentionally cannot implement
/// this contract or carry a permit.
/// </summary>
internal sealed class ShadowCreatureHarmlessProjectionPolicy
    : IHarmlessProjectionPlacementPolicy
{
    internal ShadowCreatureHarmlessProjectionPolicy(
        string speciesId,
        string visualProfileId,
        string idleVisualSlotId,
        string spawnVisualSlotId,
        string moveVisualSlotId,
        string textureSlotId,
        int frameCount,
        int frameDurationMilliseconds,
        int spawnFrameDurationMilliseconds,
        int moveFrameDurationMilliseconds,
        string deathVisualSlotId,
        string tauntVisualSlotId,
        IReadOnlyList<string> allVisualSlotIds,
        SanityResourcePoint expectedPivotSourcePx,
        double expectedDrawScale
    )
    {
        SpeciesId = RequireId(speciesId, nameof(speciesId));
        VisualProfileId = RequireId(visualProfileId, nameof(visualProfileId));
        IdleVisualSlotId = RequireId(idleVisualSlotId, nameof(idleVisualSlotId));
        SpawnVisualSlotId = RequireId(spawnVisualSlotId, nameof(spawnVisualSlotId));
        MoveVisualSlotId = RequireId(moveVisualSlotId, nameof(moveVisualSlotId));
        DeathVisualSlotId = RequireId(deathVisualSlotId, nameof(deathVisualSlotId));
        TauntVisualSlotId = RequireId(tauntVisualSlotId, nameof(tauntVisualSlotId));
        TextureSlotId = RequireId(textureSlotId, nameof(textureSlotId));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        if (spawnFrameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(spawnFrameDurationMilliseconds));
        if (moveFrameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(moveFrameDurationMilliseconds));
        if (expectedDrawScale <= 0d || !double.IsFinite(expectedDrawScale))
            throw new ArgumentOutOfRangeException(nameof(expectedDrawScale));
        FrameCount = frameCount;
        FrameDurationMilliseconds = frameDurationMilliseconds;
        SpawnFrameDurationMilliseconds = spawnFrameDurationMilliseconds;
        MoveFrameDurationMilliseconds = moveFrameDurationMilliseconds;
        AllVisualSlotIds = CopySlotIds(allVisualSlotIds, nameof(allVisualSlotIds));
        if (
            !ContainsSlotId(AllVisualSlotIds, DeathVisualSlotId)
            || !ContainsSlotId(AllVisualSlotIds, TauntVisualSlotId)
        )
        {
            throw new ArgumentException(
                "Registered dormant action slots must be part of allVisualSlotIds.",
                nameof(allVisualSlotIds)
            );
        }
        AppliedVisualSlotIds = Array.AsReadOnly(
            new[]
            {
                SpawnVisualSlotId,
                MoveVisualSlotId,
                IdleVisualSlotId,
            }
        );
        RegisteredDormantActionVisualSlotIds = Array.AsReadOnly(
            new[]
            {
                TauntVisualSlotId,
                DeathVisualSlotId,
            }
        );
        ExpectedPivotSourcePx = expectedPivotSourcePx;
        ExpectedDrawScale = expectedDrawScale;
    }

    internal string SpeciesId { get; }

    internal string VisualProfileId { get; }

    internal string IdleVisualSlotId { get; }

    internal string SpawnVisualSlotId { get; }

    internal string MoveVisualSlotId { get; }

    internal string DeathVisualSlotId { get; }

    internal string TauntVisualSlotId { get; }

    internal string TextureSlotId { get; }

    internal int FrameCount { get; }

    internal int FrameDurationMilliseconds { get; }

    internal int SpawnFrameDurationMilliseconds { get; }

    internal int MoveFrameDurationMilliseconds { get; }

    /// <summary>该物种注册的全部动画槽，包括攻击、恐吓、死亡和可选消失槽。</summary>
    internal IReadOnlyList<string> AllVisualSlotIds { get; }

    /// <summary>无害状态实际允许应用的动画槽；休眠动作不在此集合中。</summary>
    internal IReadOnlyList<string> AppliedVisualSlotIds { get; }

    /// <summary>
    /// 已登记但无害状态不应用的动作槽。保留这组注册可避免意外状态请求落到未知资源，
    /// 同时由实例状态机保证它们不会被正常无害流程选中。
    /// </summary>
    internal IReadOnlyList<string> RegisteredDormantActionVisualSlotIds { get; }

    internal SanityResourcePoint ExpectedPivotSourcePx { get; }

    internal double ExpectedDrawScale { get; }

    public int MinimumDistanceTiles => 4;

    public int MaximumDistanceTiles => 16;

    public int CandidateAttemptLimit => HarmlessProjectionPolicy.MaximumCandidateAttempts;

    public HarmlessProjectionPlacementKind PlacementKind =>
        HarmlessProjectionPlacementKind.Ground;

    internal bool TryValidateResource(
        SanitySlotResourceResult? resource,
        string expectedSlotId,
        out string reason
    )
    {
        if (resource is null)
        {
            reason = "shadow-projection.visual-resource-result-null";
            return false;
        }
        if (!resource.Success)
        {
            reason = resource.Diagnostic.Code;
            return false;
        }

        var preview = resource.VisualPreview;
        if (
            resource.PhysicalResource?.Kind != SanityPhysicalResourceKind.Texture
            || preview is null
            || preview.Kind != SanityVisualPreviewKind.AnimationFrame
            || preview.FrameIndex != 0
            || preview.FrameCount != FrameCount
            || preview.SourceRectangle.Width <= 0
            || preview.SourceRectangle.Height <= 0
            || preview.PivotSourcePx != ExpectedPivotSourcePx
            || Math.Abs(preview.DrawScale - ExpectedDrawScale) > 0.0001d
            || !string.Equals(
                preview.RequestedSlotId,
                expectedSlotId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                preview.TextureSlotId,
                TextureSlotId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "shadow-projection.visual-contract-invalid";
            return false;
        }

        // These sheets are also future hostile appearance candidates, so their frozen metadata is
        // not marked owner-local-only. Locality comes from this mod-private index/render path; none
        // of the profile's gameplay-oriented metadata is consumed here.
        reason = "shadow-projection.visual-contract-valid";
        return true;
    }

    /// <summary>兼容旧调用：默认按静息槽校验。</summary>
    internal bool TryValidateResource(
        SanitySlotResourceResult? resource,
        out string reason
    )
    {
        return TryValidateResource(resource, IdleVisualSlotId, out reason);
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }

    private static IReadOnlyList<string> CopySlotIds(
        IReadOnlyList<string> slotIds,
        string parameterName
    )
    {
        if (slotIds is null || slotIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one visual slot is required.",
                parameterName
            );
        }

        var copy = new string[slotIds.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < slotIds.Count; index++)
        {
            var slotId = RequireId(slotIds[index], $"{parameterName}[{index}]");
            if (!seen.Add(slotId))
            {
                throw new ArgumentException(
                    "Visual slot IDs must be unique.",
                    parameterName
                );
            }
            copy[index] = slotId;
        }

        return Array.AsReadOnly(copy);
    }

    private static bool ContainsSlotId(
        IReadOnlyList<string> slotIds,
        string slotId
    )
    {
        foreach (var candidate in slotIds)
        {
            if (string.Equals(candidate, slotId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

internal static class ShadowCreatureHarmlessProjectionCatalog
{
    internal const string CreeperFearSpeciesId =
        "sanity.projection.creeper-fear";
    internal const string TerrorbeakSpeciesId = "sanity.projection.terrorbeak";

    internal const string CreeperFearProfileId =
        "sanity.animation.creeper-fear.profile";
    internal const string CreeperFearIdleVisualSlotId =
        "sanity.animation.creeper-fear.idle";
    internal const string CreeperFearTextureSlotId =
        "sanity.asset.creeper-fear.sprite";

    internal const string TerrorbeakProfileId =
        "sanity.animation.terrorbeak.profile";
    internal const string TerrorbeakIdleVisualSlotId =
        "sanity.animation.terrorbeak.idle";
    internal const string TerrorbeakTextureSlotId =
        "sanity.asset.terrorbeak.sprite";

    // DIAG-20260809: 影怪动画槽全集（sanity-assets.json 均已声明）。生成/移动/静息
    // 为主用三槽（fail-closed）；attack/death/taunt/despawn 为辅助槽（加载以防万一，
    // 失败仅记录不阻断生成）。
    internal const string CreeperFearSpawnVisualSlotId =
        "sanity.animation.creeper-fear.spawn";
    internal const string CreeperFearMoveVisualSlotId =
        "sanity.animation.creeper-fear.move";
    internal const string CreeperFearAttackVisualSlotId =
        "sanity.animation.creeper-fear.attack";
    internal const string CreeperFearDeathVisualSlotId =
        "sanity.animation.creeper-fear.death";
    internal const string CreeperFearTauntVisualSlotId =
        "sanity.animation.creeper-fear.taunt";
    internal const string CreeperFearDespawnVisualSlotId =
        "sanity.animation.creeper-fear.despawn";

    internal const string TerrorbeakSpawnVisualSlotId =
        "sanity.animation.terrorbeak.spawn";
    internal const string TerrorbeakMoveVisualSlotId =
        "sanity.animation.terrorbeak.move";
    internal const string TerrorbeakAttackVisualSlotId =
        "sanity.animation.terrorbeak.attack";
    internal const string TerrorbeakDeathVisualSlotId =
        "sanity.animation.terrorbeak.death";
    internal const string TerrorbeakTauntVisualSlotId =
        "sanity.animation.terrorbeak.taunt";

    // 四方向行走行选择（与 animations.json DirectionRows 一致：Down=0/Right=1/Up=2/Left=3）。
    internal const string FacingDown = "down";
    internal const string FacingRight = "right";
    internal const string FacingUp = "up";
    internal const string FacingLeft = "left";

    internal const int OwnerProximityPixels =
        HarmlessProjectionSpawnPointSelector.TileSize;

    private static readonly IReadOnlyList<ShadowCreatureHarmlessProjectionPolicy>
        FrozenPolicies = Array.AsReadOnly(
            new[]
            {
                new ShadowCreatureHarmlessProjectionPolicy(
                    CreeperFearSpeciesId,
                    CreeperFearProfileId,
                    CreeperFearIdleVisualSlotId,
                    CreeperFearSpawnVisualSlotId,
                    CreeperFearMoveVisualSlotId,
                    CreeperFearTextureSlotId,
                    frameCount: 4,
                    // Keep the harmless projection on the same cadence as the hostile profile.
                    frameDurationMilliseconds: 200,
                    spawnFrameDurationMilliseconds: 100,
                    moveFrameDurationMilliseconds: 100,
                    CreeperFearDeathVisualSlotId,
                    CreeperFearTauntVisualSlotId,
                    new[]
                    {
                        CreeperFearSpawnVisualSlotId,
                        CreeperFearMoveVisualSlotId,
                        CreeperFearIdleVisualSlotId,
                        CreeperFearAttackVisualSlotId,
                        CreeperFearDeathVisualSlotId,
                        CreeperFearTauntVisualSlotId,
                        CreeperFearDespawnVisualSlotId,
                    },
                    new SanityResourcePoint(32, 48),
                    expectedDrawScale: 4d
                ),
                new ShadowCreatureHarmlessProjectionPolicy(
                    TerrorbeakSpeciesId,
                    TerrorbeakProfileId,
                    TerrorbeakIdleVisualSlotId,
                    TerrorbeakSpawnVisualSlotId,
                    TerrorbeakMoveVisualSlotId,
                    TerrorbeakTextureSlotId,
                    frameCount: 4,
                    // DIAG-20260809: 静息帧时长 100→200ms，与 animations.json 一致
                    // （此前写错导致无害恐怖尖喙静息播放速度是有害版 2 倍）。
                    frameDurationMilliseconds: 200,
                    spawnFrameDurationMilliseconds: 100,
                    moveFrameDurationMilliseconds: 100,
                    TerrorbeakDeathVisualSlotId,
                    TerrorbeakTauntVisualSlotId,
                    new[]
                    {
                        TerrorbeakSpawnVisualSlotId,
                        TerrorbeakMoveVisualSlotId,
                        TerrorbeakIdleVisualSlotId,
                        TerrorbeakAttackVisualSlotId,
                        TerrorbeakDeathVisualSlotId,
                        TerrorbeakTauntVisualSlotId,
                    },
                    new SanityResourcePoint(24, 48),
                    expectedDrawScale: 4d
                ),
            }
        );

    internal static IReadOnlyList<ShadowCreatureHarmlessProjectionPolicy> Policies =>
        FrozenPolicies;

    // DIAG-20260809: 无害影怪行为参数（玩家可调，先集中在此；后续进配置）。
    internal const float HarmlessAlpha = 0.25f;                 // 无害形态透明度（危险 50% 区分）
    internal const double WanderRadiusPixels = 10d * 64d;       // 游荡范围：锚点 10 格
    internal const double PlayerFleeTriggerPixels = 2d * 64d;   // 玩家距影怪中心点 2 格触发驱赶
    internal const double FleeDistancePixels = 5d * 64d;        // 逃离距离 5 格
    internal const double PlayerFarFadePixels = 20d * 64d;      // 玩家远离 20 格强制淡出
    internal const int FleeFadeOutMilliseconds = 2000;           // 驱赶淡出 2 秒（与逃离 5 格 @160px/s 匹配，边逃边淡）
    internal const int FarFadeOutMilliseconds = 1000;           // 远离淡出 1 秒
    internal const int HighSanFadeOutMilliseconds = 1000;       // 高理智淡出 1 秒
    internal const int WanderRestMillisecondsMin = 3000;        // 游荡到达目标后的休息下限（与危险版 3-5 秒一致）
    internal const int WanderRestMillisecondsMax = 5000;        // 游荡到达目标后的休息上限
    internal const double WanderSpeedPixelsPerSecond = 80d;     // 游荡半速（危险版追击一半）
    internal const double FleeSpeedPixelsPerSecond = 160d;      // 驱赶逃离速度（游荡 2 倍）

    internal static bool IsPermitConsumer(string? speciesId)
    {
        return string.Equals(
                speciesId,
                CreeperFearSpeciesId,
                StringComparison.Ordinal
            )
            || string.Equals(
                speciesId,
                TerrorbeakSpeciesId,
                StringComparison.Ordinal
            );
    }
}

internal static class ShadowCreatureProjectionPermitGate
{
    internal static bool TryAuthorize(
        string speciesId,
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpawnPermit permit,
        out string reason
    )
    {
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
        {
            reason = "shadow-permit.species-not-authorized";
            return false;
        }
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !string.Equals(permit.PlayerKey, playerKey, StringComparison.Ordinal)
        )
        {
            reason = "shadow-permit.owner-mismatch";
            return false;
        }
        if (permit.PoolTier != SanityShadowPoolTier.Harmless50)
        {
            reason = "shadow-permit.pool-not-harmless";
            return false;
        }
        if (gameMinute < 0 || permit.IssuedAtMinute != gameMinute)
        {
            reason = "shadow-permit.minute-mismatch";
            return false;
        }
        if (
            occupancy < 0
            || permit.Occupancy != occupancy
            || permit.Cap <= occupancy
            || permit.NextDueMinute <= permit.IssuedAtMinute
        )
        {
            reason = "shadow-permit.occupancy-invalid";
            return false;
        }

        reason = "shadow-permit.authorized";
        return true;
    }
}

internal sealed class ShadowCreatureHarmlessProjectionInstance
{
    private long frameElapsedMilliseconds;
    // DIAG-20260809: 动画状态与朝向（生成过渡→静息/四方向行走）。
    private ShadowCreatureProjectionAnimationState animationState =
        ShadowCreatureProjectionAnimationState.Spawning;
    private string facingId = ShadowCreatureHarmlessProjectionCatalog.FacingDown;
    private SanitySlotResourceResult? spawnVisualResource;
    private SanitySlotResourceResult? moveVisualResource;
    private bool wanderResting;
    private int wanderRestRemainingMilliseconds;
    // DIAG-20260809: 行为化后实例变为可移动、可淡出的动态投影。
    // worldPixel 为当前世界坐标（初始=生成点）；alpha 默认 25%（与危险 50% 区分）。
    private HarmlessProjectionWorldPoint worldPixel;
    private float alpha = ShadowCreatureHarmlessProjectionCatalog.HarmlessAlpha;
    private ShadowCreatureProjectionBehaviorState behaviorState =
        ShadowCreatureProjectionBehaviorState.Wander;
    private HarmlessProjectionWorldPoint wanderAnchorPixel;
    private HarmlessProjectionWorldPoint wanderTargetPixel;
    private HarmlessProjectionWorldPoint fleeTargetPixel;
    private int fadeOutElapsedMilliseconds;
    private int fadeOutDurationMilliseconds;
    // DIAG-20260810: 绑定投影（危险实体隐藏态外观）标记与保护期。保护期内完全
    // 静止且豁免近距驱赶（玩家能看到投影稳定出现），保护期后恢复正常行为
    // （可游荡、可被驱赶）；远离 20 格/高理智清理不受保护期影响。
    private bool isBindingProjection;
    private int bindingProtectionRemainingMilliseconds;
    // DIAG-20260810: 淡出原因——驱赶(Flee)/远离(Far)/高理智(HighSan)，用于
    // 决定是否触发驱赶补偿（Flee/Far 补偿，HighSan 不补偿）。
    private ShadowCreatureProjectionFadeOutKind fadeOutKind =
        ShadowCreatureProjectionFadeOutKind.None;

    internal enum ShadowCreatureProjectionBehaviorState
    {
        Wander,
        Fleeing,
        FadingOut,
    }

    /// <summary>淡出原因（驱赶补偿判定用）。</summary>
    internal enum ShadowCreatureProjectionFadeOutKind
    {
        None,
        Flee,
        Far,
        HighSan,
    }

    internal enum ShadowCreatureProjectionBehaviorEvent
    {
        None,
        FleeTriggered,
        FadeOutCompleted,
    }

    /// <summary>
    /// 动画状态：生成过渡（播完自动进静息）→ 静息 / 四方向行走。
    /// Taunt/Death 虽在策略中登记为休眠动作，但故意不进入此应用状态机。
    /// </summary>
    internal enum ShadowCreatureProjectionAnimationState
    {
        Spawning,
        Idle,
        Moving,
    }

    internal ShadowCreatureHarmlessProjectionInstance(
        string correlationId,
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint spawnWorldPixel,
        long spawnedAtMinute,
        SanitySlotResourceResult? visualResource = null,
        SanitySlotResourceResult? spawnVisualResource = null,
        SanitySlotResourceResult? moveVisualResource = null
    )
    {
        CorrelationId = RequireId(correlationId, nameof(correlationId));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!spawnWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(spawnWorldPixel));
        if (spawnedAtMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(spawnedAtMinute));

        SpawnWorldPixel = spawnWorldPixel;
        SpawnedAtMinute = spawnedAtMinute;
        VisualResource = visualResource;
        this.spawnVisualResource = spawnVisualResource;
        this.moveVisualResource = moveVisualResource;
        worldPixel = spawnWorldPixel;
        wanderAnchorPixel = spawnWorldPixel;
        wanderTargetPixel = spawnWorldPixel;
    }

    internal string CorrelationId { get; }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal ShadowCreatureHarmlessProjectionPolicy Policy { get; }

    internal string SpeciesId => Policy.SpeciesId;

    internal HarmlessProjectionWorldPoint SpawnWorldPixel { get; }

    internal long SpawnedAtMinute { get; }

    internal SanitySlotResourceResult? VisualResource { get; }

    internal SanitySlotResourceResult? SpawnVisualResource => spawnVisualResource;

    internal SanitySlotResourceResult? MoveVisualResource => moveVisualResource;

    /// <summary>动画状态：生成过渡 → 静息 / 四方向行走。</summary>
    internal ShadowCreatureProjectionAnimationState AnimationState => animationState;

    /// <summary>当前朝向（move 四方向行选择用）。</summary>
    internal string FacingId => facingId;

    /// <summary>渲染器当前应使用的动画槽（Spawning→spawn、Moving→move、其余→idle）。</summary>
    internal string CurrentAnimationSlotId => animationState switch
    {
        ShadowCreatureProjectionAnimationState.Spawning => Policy.SpawnVisualSlotId,
        ShadowCreatureProjectionAnimationState.Moving => Policy.MoveVisualSlotId,
        _ => Policy.IdleVisualSlotId,
    };

    /// <summary>渲染器当前应使用的视觉资源（缺槽时回退静息资源）。</summary>
    internal SanitySlotResourceResult? CurrentVisualResource => animationState switch
    {
        ShadowCreatureProjectionAnimationState.Spawning
            when spawnVisualResource is not null => spawnVisualResource,
        ShadowCreatureProjectionAnimationState.Moving
            when moveVisualResource is not null => moveVisualResource,
        _ => VisualResource,
    };

    /// <summary>当前动画帧时长（生成/移动/静息各自节奏）。</summary>
    internal int CurrentAnimationFrameDurationMilliseconds => animationState switch
    {
        ShadowCreatureProjectionAnimationState.Spawning => Policy.SpawnFrameDurationMilliseconds,
        ShadowCreatureProjectionAnimationState.Moving => Policy.MoveFrameDurationMilliseconds,
        _ => Policy.FrameDurationMilliseconds,
    };

    internal int CurrentFrameIndex { get; private set; }

    internal HarmlessProjectionCleanupReason? CleanupReason { get; private set; }

    internal string CleanupReasonId => CleanupReason.HasValue
        ? HarmlessProjectionCleanupReasonIds.GetId(CleanupReason.Value)
        : string.Empty;

    internal bool IsCleanedUp => CleanupReason.HasValue;

    /// <summary>当前世界坐标（行为推进后与 SpawnWorldPixel 分离）。</summary>
    internal HarmlessProjectionWorldPoint WorldPixel => worldPixel;

    /// <summary>当前不透明度（0.25 默认，淡出时线性到 0）。</summary>
    internal float Alpha => alpha;

    internal ShadowCreatureProjectionBehaviorState BehaviorState => behaviorState;

    /// <summary>是否为绑定投影（危险实体隐藏态外观）。</summary>
    internal bool IsBindingProjection => isBindingProjection;

    /// <summary>淡出原因（驱赶补偿判定用）。</summary>
    internal ShadowCreatureProjectionFadeOutKind FadeOutKind => fadeOutKind;

    /// <summary>
    /// DIAG-20260810: 标记为绑定投影并设置驱赶保护期（毫秒）。保护期内只豁免
    /// “淡出消失”（被追会逃离但不消失、游荡照常）；保护期结束后恢复可驱赶。
    /// </summary>
    internal void SetBindingProjection(int protectionMilliseconds)
    {
        isBindingProjection = true;
        bindingProtectionRemainingMilliseconds = Math.Max(
            0,
            protectionMilliseconds
        );
        fadeOutKind = ShadowCreatureProjectionFadeOutKind.None;
        // DIAG-20260811: 绑定投影直接以静息形态出现（无生成过渡动画）。
        animationState = ShadowCreatureProjectionAnimationState.Idle;
    }

    /// <summary>高理智/远离触发淡出（不移动，透明度 1 秒内降到 0）。</summary>
    internal void BeginFadeOut(int durationMilliseconds)
    {
        BeginFadeOut(durationMilliseconds, ShadowCreatureProjectionFadeOutKind.None);
    }

    /// <summary>
    /// DIAG-20260810: 带原因的淡出。Flee/Far 淡出完成后触发驱赶补偿；
    /// HighSan（高理智）淡出不补偿。更快的淡出不降速（保留原原因）。
    /// </summary>
    internal void BeginFadeOut(
        int durationMilliseconds,
        ShadowCreatureProjectionFadeOutKind kind
    )
    {
        if (durationMilliseconds <= 0)
            durationMilliseconds = ShadowCreatureHarmlessProjectionCatalog.FarFadeOutMilliseconds;
        if (
            behaviorState == ShadowCreatureProjectionBehaviorState.FadingOut
            && fadeOutDurationMilliseconds <= durationMilliseconds
        )
        {
            if (
                kind == ShadowCreatureProjectionFadeOutKind.HighSan
                || fadeOutKind == ShadowCreatureProjectionFadeOutKind.None
            )
            {
                fadeOutKind = kind;
            }
            return;
        }
        if (kind != ShadowCreatureProjectionFadeOutKind.None)
            fadeOutKind = kind;
        behaviorState = ShadowCreatureProjectionBehaviorState.FadingOut;
        fadeOutDurationMilliseconds = durationMilliseconds;
        fadeOutElapsedMilliseconds = 0;
    }

    /// <summary>
    /// DIAG-20260809: 行为推进（协调器每 tick 调用）。处理游荡/驱赶/远离/淡出，
    /// 返回事件供协调器决定清理与驱赶补偿。
    /// </summary>
    internal ShadowCreatureProjectionBehaviorEvent AdvanceBehavior(
        int elapsedMilliseconds,
        HarmlessProjectionWorldPoint playerWorldPixel
    )
    {
        if (IsCleanedUp || elapsedMilliseconds <= 0)
            return ShadowCreatureProjectionBehaviorEvent.None;

        // DIAG-20260812: 绑定投影保护期——只豁免“淡出消失”，不豁免移动：
        // 被玩家追会正常逃离（10 秒内不消失），没被追正常静息/游荡循环；
        // 保护期结束恢复可驱赶（逃离中则立即开始淡出）。
        // 远离 20 格照常淡出（玩家走远则随消失链条连带清除隐藏实体）。
        if (
            isBindingProjection
            && bindingProtectionRemainingMilliseconds > 0
            && behaviorState != ShadowCreatureProjectionBehaviorState.FadingOut
        )
        {
            bindingProtectionRemainingMilliseconds = Math.Max(
                0,
                bindingProtectionRemainingMilliseconds - elapsedMilliseconds
            );
            var farX = playerWorldPixel.X - worldPixel.X;
            var farY = playerWorldPixel.Y - worldPixel.Y;
            var farFade = ShadowCreatureHarmlessProjectionCatalog.PlayerFarFadePixels;
            if ((farX * farX) + (farY * farY) > farFade * farFade)
            {
                BeginFadeOut(
                    ShadowCreatureHarmlessProjectionCatalog.FarFadeOutMilliseconds,
                    ShadowCreatureProjectionFadeOutKind.Far
                );
                return ShadowCreatureProjectionBehaviorEvent.None;
            }
            if (behaviorState != ShadowCreatureProjectionBehaviorState.Fleeing)
            {
                var dx = playerWorldPixel.X - worldPixel.X;
                var dy = playerWorldPixel.Y - worldPixel.Y;
                var trigger =
                    ShadowCreatureHarmlessProjectionCatalog.PlayerFleeTriggerPixels;
                if ((dx * dx) + (dy * dy) <= trigger * trigger)
                {
                    BeginFlee(playerWorldPixel, deferFadeOut: true);
                    return ShadowCreatureProjectionBehaviorEvent.FleeTriggered;
                }
                return AdvanceWandering(elapsedMilliseconds);
            }
            // 已在逃离（保护期内）：继续移动逃离，但不淡出消失。
            var previousX = worldPixel.X;
            var previousY = worldPixel.Y;
            MoveToward(
                fleeTargetPixel,
                ShadowCreatureHarmlessProjectionCatalog.FleeSpeedPixelsPerSecond,
                elapsedMilliseconds
            );
            SetAnimationState(ShadowCreatureProjectionAnimationState.Moving);
            UpdateFacingFromDelta(
                fleeTargetPixel.X - worldPixel.X,
                fleeTargetPixel.Y - worldPixel.Y
            );
            AdvanceFrame(elapsedMilliseconds);
            if (
                Math.Abs(worldPixel.X - previousX) < 0.001d
                && Math.Abs(worldPixel.Y - previousY) < 0.001d
            )
            {
                // 已到达逃离目标——先确认玩家是否仍在追：
                // 玩家已远离（超过触发范围）→ 停止逃离，回到游荡/静息；
                // 玩家仍贴近 → 重新选逃离方向（持续被追着跑）。
                var chaseX = playerWorldPixel.X - worldPixel.X;
                var chaseY = playerWorldPixel.Y - worldPixel.Y;
                var chaseTrigger =
                    ShadowCreatureHarmlessProjectionCatalog.PlayerFleeTriggerPixels;
                if ((chaseX * chaseX) + (chaseY * chaseY) > chaseTrigger * chaseTrigger)
                {
                    behaviorState = ShadowCreatureProjectionBehaviorState.Wander;
                    fadeOutKind = ShadowCreatureProjectionFadeOutKind.None;
                    return AdvanceWandering(elapsedMilliseconds);
                }
                BeginFlee(playerWorldPixel, deferFadeOut: true);
            }
            // 保护期到期 → 立即开始淡出（被追着跑满 10 秒后消失）。
            if (
                bindingProtectionRemainingMilliseconds == 0
                && fadeOutKind == ShadowCreatureProjectionFadeOutKind.None
            )
            {
                BeginFadeOut(
                    ShadowCreatureHarmlessProjectionCatalog.FleeFadeOutMilliseconds,
                    ShadowCreatureProjectionFadeOutKind.Flee
                );
            }
            return ShadowCreatureProjectionBehaviorEvent.None;
        }

        if (
            behaviorState != ShadowCreatureProjectionBehaviorState.FadingOut
            && behaviorState != ShadowCreatureProjectionBehaviorState.Fleeing
        )
        {
            var farX = playerWorldPixel.X - worldPixel.X;
            var farY = playerWorldPixel.Y - worldPixel.Y;
            var farFade = ShadowCreatureHarmlessProjectionCatalog.PlayerFarFadePixels;
            if ((farX * farX) + (farY * farY) > farFade * farFade)
            {
                BeginFadeOut(
                    ShadowCreatureHarmlessProjectionCatalog.FarFadeOutMilliseconds,
                    ShadowCreatureProjectionFadeOutKind.Far
                );
            }
        }

        switch (behaviorState)
        {
            case ShadowCreatureProjectionBehaviorState.FadingOut:
                // 静止淡出（远离/高理智）：保持静息动画推进，透明度随淡出递减。
                SetAnimationState(ShadowCreatureProjectionAnimationState.Idle);
                AdvanceFrame(elapsedMilliseconds);
                return AdvanceFadingOut(elapsedMilliseconds);
            case ShadowCreatureProjectionBehaviorState.Fleeing:
                MoveToward(
                    fleeTargetPixel,
                    ShadowCreatureHarmlessProjectionCatalog.FleeSpeedPixelsPerSecond,
                    elapsedMilliseconds
                );
                // 逃离：四方向行走动画（全速推进，朝向逃离方向）。
                SetAnimationState(ShadowCreatureProjectionAnimationState.Moving);
                UpdateFacingFromDelta(
                    fleeTargetPixel.X - worldPixel.X,
                    fleeTargetPixel.Y - worldPixel.Y
                );
                AdvanceFrame(elapsedMilliseconds);
                // DIAG-20260812: 保护期内的逃离不淡出（fadeOutKind=None 时只移动）。
                if (fadeOutKind == ShadowCreatureProjectionFadeOutKind.None)
                    return ShadowCreatureProjectionBehaviorEvent.None;
                return AdvanceFadingOut(elapsedMilliseconds);
            default:
                var dx = playerWorldPixel.X - worldPixel.X;
                var dy = playerWorldPixel.Y - worldPixel.Y;
                var trigger =
                    ShadowCreatureHarmlessProjectionCatalog.PlayerFleeTriggerPixels;
                if ((dx * dx) + (dy * dy) <= trigger * trigger)
                {
                    BeginFlee(playerWorldPixel);
                    return ShadowCreatureProjectionBehaviorEvent.FleeTriggered;
                }
                return AdvanceWandering(elapsedMilliseconds);
        }
    }

    internal ShadowCreatureProjectionBehaviorEvent AdvanceFadeOutOnly(
        int elapsedMilliseconds
    )
    {
        if (IsCleanedUp || elapsedMilliseconds <= 0)
        {
            return ShadowCreatureProjectionBehaviorEvent.None;
        }

        // During a hard pause movement and flee behavior stay frozen, but wall-clock lifecycle
        // still advances. A projection that was created just before the pause must be allowed to
        // finish its spawn animation so a later Danger transition can convert it instead of
        // leaving it permanently in Spawning.
        if (animationState == ShadowCreatureProjectionAnimationState.Spawning)
        {
            AdvanceFrame(elapsedMilliseconds);
            return ShadowCreatureProjectionBehaviorEvent.None;
        }
        if (behaviorState != ShadowCreatureProjectionBehaviorState.FadingOut)
            return ShadowCreatureProjectionBehaviorEvent.None;

        SetAnimationState(ShadowCreatureProjectionAnimationState.Idle);
        AdvanceFrame(elapsedMilliseconds);
        return AdvanceFadingOut(elapsedMilliseconds);
    }

    internal bool AdvanceFrame(int elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
            return false;
        if (IsCleanedUp)
            return false;

        var durationMilliseconds = CurrentAnimationFrameDurationMilliseconds;
        var cycleMilliseconds = checked(
            (long)Policy.FrameCount * durationMilliseconds
        );
        var elapsed = Math.Min(
            long.MaxValue - frameElapsedMilliseconds,
            (long)elapsedMilliseconds
        );
        frameElapsedMilliseconds += elapsed;
        if (
            animationState == ShadowCreatureProjectionAnimationState.Spawning
            && frameElapsedMilliseconds >= cycleMilliseconds
        )
        {
            // 生成动画播完 → 自动进入静息（从头播）。
            SetAnimationState(ShadowCreatureProjectionAnimationState.Idle);
            return true;
        }
        frameElapsedMilliseconds %= cycleMilliseconds;
        CurrentFrameIndex = (int)(
            frameElapsedMilliseconds / durationMilliseconds
        );
        return true;
    }

    internal bool TryMarkCleaned(HarmlessProjectionCleanupReason reason)
    {
        if (CleanupReason.HasValue)
            return false;
        CleanupReason = reason;
        return true;
    }

    /// <summary>
    /// DIAG-20260812: 从清理标记恢复（RecordDangerEntry 摘除后放回绑定投影用）。
    /// 绑定投影代表隐藏的危险实体，转化摘除会丢失绑定并连带清除实体。
    /// </summary>
    internal bool TryRestoreFromCleanup()
    {
        if (!CleanupReason.HasValue)
            return false;
        CleanupReason = null;
        return true;
    }

    private void BeginFlee(
        HarmlessProjectionWorldPoint playerWorldPixel,
        bool deferFadeOut = false
    )
    {
        behaviorState = ShadowCreatureProjectionBehaviorState.Fleeing;
        var dx = worldPixel.X - playerWorldPixel.X;
        var dy = worldPixel.Y - playerWorldPixel.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var awayX = distance > 0.01d ? dx / distance : 1d;
        var awayY = distance > 0.01d ? dy / distance : 0d;
        var fleeDistance = ShadowCreatureHarmlessProjectionCatalog.FleeDistancePixels;
        fleeTargetPixel = new HarmlessProjectionWorldPoint(
            worldPixel.X + (float)(awayX * fleeDistance),
            worldPixel.Y + (float)(awayY * fleeDistance)
        );
        // DIAG-20260812: 绑定投影保护期内的逃离只移动不淡出（deferFadeOut）；
        // 保护期结束才启动淡出，避免 10 秒内被驱赶消失。
        if (deferFadeOut)
        {
            fadeOutKind = ShadowCreatureProjectionFadeOutKind.None;
        }
        else
        {
            fadeOutKind = ShadowCreatureProjectionFadeOutKind.Flee;
            fadeOutDurationMilliseconds =
                ShadowCreatureHarmlessProjectionCatalog.FleeFadeOutMilliseconds;
            fadeOutElapsedMilliseconds = 0;
        }
        UpdateFacingFromDelta(awayX, awayY);
    }

    private ShadowCreatureProjectionBehaviorEvent AdvanceWandering(
        int elapsedMilliseconds
    )
    {
        if (wanderResting)
        {
            // 到达目标后的休息：不移动、播静息动画，3-5 秒（与危险版游荡一致）后再选新目标。
            SetAnimationState(ShadowCreatureProjectionAnimationState.Idle);
            wanderRestRemainingMilliseconds -= elapsedMilliseconds;
            if (wanderRestRemainingMilliseconds <= 0)
            {
                wanderTargetPixel = RandomPointNear(
                    wanderAnchorPixel,
                    ShadowCreatureHarmlessProjectionCatalog.WanderRadiusPixels
                );
                wanderResting = false;
            }
            AdvanceFrame(elapsedMilliseconds);
            return ShadowCreatureProjectionBehaviorEvent.None;
        }

        var previousX = worldPixel.X;
        var previousY = worldPixel.Y;
        MoveToward(
            wanderTargetPixel,
            ShadowCreatureHarmlessProjectionCatalog.WanderSpeedPixelsPerSecond,
            elapsedMilliseconds
        );
        if (
            Math.Abs(worldPixel.X - previousX) > 0.001d
            || Math.Abs(worldPixel.Y - previousY) > 0.001d
        )
        {
            // 移动中：四方向行走动画，半速推进（与危险版游荡动画一致）。
            SetAnimationState(ShadowCreatureProjectionAnimationState.Moving);
            UpdateFacingFromDelta(
                wanderTargetPixel.X - worldPixel.X,
                wanderTargetPixel.Y - worldPixel.Y
            );
            AdvanceFrame(elapsedMilliseconds >> 1);
        }
        else
        {
            // 到达目标：开始休息（随机 3-5 秒），播静息动画。
            SetAnimationState(ShadowCreatureProjectionAnimationState.Idle);
            wanderResting = true;
            wanderRestRemainingMilliseconds = Random.Shared.Next(
                ShadowCreatureHarmlessProjectionCatalog.WanderRestMillisecondsMin,
                ShadowCreatureHarmlessProjectionCatalog.WanderRestMillisecondsMax + 1
            );
            AdvanceFrame(elapsedMilliseconds);
        }
        return ShadowCreatureProjectionBehaviorEvent.None;
    }

    private ShadowCreatureProjectionBehaviorEvent AdvanceFadingOut(
        int elapsedMilliseconds
    )
    {
        fadeOutElapsedMilliseconds += elapsedMilliseconds;
        var duration = Math.Max(1, fadeOutDurationMilliseconds);
        var progress = Math.Min(
            1d,
            fadeOutElapsedMilliseconds / (double)duration
        );
        alpha = (float)(
            ShadowCreatureHarmlessProjectionCatalog.HarmlessAlpha * (1d - progress)
        );
        if (alpha <= 0f)
            return ShadowCreatureProjectionBehaviorEvent.FadeOutCompleted;
        return ShadowCreatureProjectionBehaviorEvent.None;
    }

    private void SetAnimationState(ShadowCreatureProjectionAnimationState state)
    {
        if (animationState == state)
            return;
        animationState = state;
        frameElapsedMilliseconds = 0;
        CurrentFrameIndex = 0;
    }

    private void UpdateFacingFromDelta(double deltaX, double deltaY)
    {
        if (
            !double.IsFinite(deltaX)
            || !double.IsFinite(deltaY)
            || (Math.Abs(deltaX) < 0.001d && Math.Abs(deltaY) < 0.001d)
        )
        {
            return;
        }
        facingId = Math.Abs(deltaX) > Math.Abs(deltaY)
            ? deltaX > 0d
                ? ShadowCreatureHarmlessProjectionCatalog.FacingRight
                : ShadowCreatureHarmlessProjectionCatalog.FacingLeft
            : deltaY > 0d
                ? ShadowCreatureHarmlessProjectionCatalog.FacingDown
                : ShadowCreatureHarmlessProjectionCatalog.FacingUp;
    }

    /// <summary>DIAG-20260809: 设置初始朝向（绑定投影恢复外观用，静息/生成阶段不影响帧行）。</summary>
    internal void SetFacingId(string facingIdValue)
    {
        if (string.IsNullOrWhiteSpace(facingIdValue))
            return;
        facingId = facingIdValue;
    }

    private static HarmlessProjectionWorldPoint RandomPointNear(
        HarmlessProjectionWorldPoint anchor,
        double radiusPixels
    )
    {
        // 行为随机使用进程级随机源即可（视觉行为，非结算路径）。
        var angle = Random.Shared.NextDouble() * Math.PI * 2d;
        var radius = Random.Shared.NextDouble() * radiusPixels;
        return new HarmlessProjectionWorldPoint(
            anchor.X + (float)(Math.Cos(angle) * radius),
            anchor.Y + (float)(Math.Sin(angle) * radius)
        );
    }

    private void MoveToward(
        HarmlessProjectionWorldPoint target,
        double speedPixelsPerSecond,
        int elapsedMilliseconds
    )
    {
        var dx = target.X - worldPixel.X;
        var dy = target.Y - worldPixel.Y;
        var distanceSquared = (dx * dx) + (dy * dy);
        if (distanceSquared <= 0.01d)
            return;
        var distance = Math.Sqrt(distanceSquared);
        var step = speedPixelsPerSecond * (elapsedMilliseconds / 1000d);
        if (step >= distance)
        {
            worldPixel = target;
            return;
        }
        worldPixel = new HarmlessProjectionWorldPoint(
            worldPixel.X + (float)((dx / distance) * step),
            worldPixel.Y + (float)((dy / distance) * step)
        );
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }
}

internal sealed class ShadowCreatureHarmlessProjectionSpawnRequest
{
    internal ShadowCreatureHarmlessProjectionSpawnRequest(
        string correlationId,
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        long gameMinute,
        SanityShadowSpawnPermit permit
    )
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        CorrelationId = correlationId;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!ownerStandingWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(ownerStandingWorldPixel));

        OwnerStandingWorldPixel = ownerStandingWorldPixel;
        GameMinute = gameMinute;
        Permit = permit;
    }

    internal string CorrelationId { get; }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal ShadowCreatureHarmlessProjectionPolicy Policy { get; }

    internal HarmlessProjectionWorldPoint OwnerStandingWorldPixel { get; }

    internal long GameMinute { get; }

    internal SanityShadowSpawnPermit Permit { get; }
}

internal sealed class ShadowCreatureHarmlessProjectionSpawnResult
{
    private ShadowCreatureHarmlessProjectionSpawnResult(
        bool success,
        string reason,
        ShadowCreatureHarmlessProjectionInstance? instance
    )
    {
        Success = success;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A stable result reason is required.", nameof(reason))
            : reason;
        Instance = instance;
    }

    internal bool Success { get; }

    internal string Reason { get; }

    internal ShadowCreatureHarmlessProjectionInstance? Instance { get; }

    internal static ShadowCreatureHarmlessProjectionSpawnResult Spawned(
        ShadowCreatureHarmlessProjectionInstance instance
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new ShadowCreatureHarmlessProjectionSpawnResult(
            true,
            "shadow-projection.spawned",
            instance
        );
    }

    internal static ShadowCreatureHarmlessProjectionSpawnResult Failed(
        string reason
    )
    {
        return new ShadowCreatureHarmlessProjectionSpawnResult(false, reason, null);
    }
}

internal interface IShadowCreatureHarmlessProjectionSpawnFactory
{
    ShadowCreatureHarmlessProjectionSpawnResult TrySpawn(
        ShadowCreatureHarmlessProjectionSpawnRequest request
    );
}

internal interface IShadowCreatureProjectionBudgetAuthority
{
    SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
        string playerKey,
        long gameMinute,
        int occupancy
    );
}

internal interface ISanityShadowRealTimeBudgetAuthority
{
    SanityShadowBudgetEvaluationResult EvaluateShadowBudgetRealTime(
        string playerKey,
        long gameMinute,
        int occupancy,
        int elapsedMilliseconds,
        SanityShadowSpecies? requestedSpecies
    );
}

internal interface IShadowProjectionCorrelationSource
{
    string Next(string playerKey, string speciesId);
}

internal sealed class SessionShadowProjectionCorrelationSource
    : IShadowProjectionCorrelationSource
{
    private readonly string sessionNonce = Guid.NewGuid().ToString("N");
    private long nextSequence;

    public string Next(string playerKey, string speciesId)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            throw new ArgumentException("A canonical player key is required.", nameof(playerKey));
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
            throw new ArgumentException("An authorized shadow species is required.", nameof(speciesId));

        nextSequence = checked(nextSequence + 1);
        return string.Concat(
            "sanity.shadow-conversion.",
            sessionNonce,
            ".",
            nextSequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)
        );
    }
}

internal sealed class ShadowProjectionConversionIntent
{
    internal const string ResponsibilityId = "future-host-authority";

    internal ShadowProjectionConversionIntent(
        string correlationId,
        string playerKey,
        string speciesId,
        long requestedAtMinute,
        double positionX = double.NaN,
        double positionY = double.NaN
    )
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        if (!SanityPlayerKey.IsCanonical(playerKey))
            throw new ArgumentException("A canonical player key is required.", nameof(playerKey));
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
            throw new ArgumentException("An authorized shadow species is required.", nameof(speciesId));

        CorrelationId = correlationId;
        PlayerKey = playerKey;
        SpeciesId = speciesId;
        RequestedAtMinute = requestedAtMinute;
        PositionX = positionX;
        PositionY = positionY;
    }

    internal string CorrelationId { get; }

    internal string PlayerKey { get; }

    internal string SpeciesId { get; }

    internal long RequestedAtMinute { get; }

    /// <summary>DIAG-20260812: 投影原位（原地转化用；NaN=未提供，回退玩家位置）。</summary>
    internal double PositionX { get; }

    internal double PositionY { get; }

    internal string AuthorityResponsibility => ResponsibilityId;

    /// <summary>
    /// A future reverse transition must create a new legal local candidate after a fresh budget
    /// decision. The removed visual instance is evidence only and can never be restored.
    /// </summary>
    internal bool RequiresFreshBudgetAndLegalSpawn => true;

    internal bool RestoresRemovedLocalInstance => false;
}

internal enum ShadowProjectionConversionSubmissionStatus
{
    Confirmed,
    Unconfirmed,
    Delayed,
    Rejected,
    Failed,
}

internal readonly record struct ShadowProjectionConversionSubmissionResult(
    ShadowProjectionConversionSubmissionStatus Status,
    string Reason
);

/// <summary>
/// This pure responsibility seam is not a multiplayer message, lease, receipt, or confirmation
/// signature. Task family 07 may later bridge it only after freezing the real host protocol.
/// </summary>
internal interface IShadowProjectionConversionIntentSink
{
    ShadowProjectionConversionSubmissionResult Record(
        ShadowProjectionConversionIntent intent
    );
}

internal sealed class UnavailableShadowProjectionConversionIntentSink
    : IShadowProjectionConversionIntentSink
{
    public ShadowProjectionConversionSubmissionResult Record(
        ShadowProjectionConversionIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new ShadowProjectionConversionSubmissionResult(
            ShadowProjectionConversionSubmissionStatus.Unconfirmed,
            "shadow-conversion.host-contract-not-frozen"
        );
    }
}

internal sealed class ShadowProjectionConversionEvidence
{
    internal ShadowProjectionConversionEvidence(
        ShadowProjectionConversionIntent intent,
        ShadowProjectionConversionSubmissionResult submission
    )
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        if (string.IsNullOrWhiteSpace(submission.Reason))
        {
            submission = new ShadowProjectionConversionSubmissionResult(
                ShadowProjectionConversionSubmissionStatus.Failed,
                "shadow-conversion.result-reason-missing"
            );
        }
        Submission = submission;
    }

    internal ShadowProjectionConversionIntent Intent { get; }

    internal ShadowProjectionConversionSubmissionResult Submission { get; }

    internal string LocalCleanupReasonId =>
        HarmlessProjectionCleanupReasonIds.ConversionRequested;
}
