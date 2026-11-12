#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using DontStarve.Player.Combat.InvincibilityFrames;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal sealed class HostileAttackMotionPolicy
{
    private const int SupportedContractVersion = 1;
    private readonly double[] frameAdvanceTiles;

    private HostileAttackMotionPolicy(
        string policyId,
        int contractVersion,
        double totalAdvanceTiles,
        IReadOnlyList<double> advances,
        bool resetAfterAnimation
    )
    {
        PolicyId = policyId;
        ContractVersion = contractVersion;
        TotalAdvanceTiles = totalAdvanceTiles;
        frameAdvanceTiles = advances.ToArray();
        ResetAfterAnimation = resetAfterAnimation;
    }

    internal string PolicyId { get; }
    internal int ContractVersion { get; }
    internal double TotalAdvanceTiles { get; }
    internal int FrameCount => frameAdvanceTiles.Length;
    internal bool ResetAfterAnimation { get; }

    internal static bool TryCreate(
        SanityHostileAttackMotionDefinition? source,
        out HostileAttackMotionPolicy? policy,
        out string reason
    )
    {
        policy = null;
        if (
            source is null
            || string.IsNullOrWhiteSpace(source.PolicyId)
            || source.ContractVersion != SupportedContractVersion
            || !double.IsFinite(source.TotalAdvanceTiles)
            || source.TotalAdvanceTiles <= 0d
            || source.FrameAdvanceTiles.Count == 0
            || source.FrameAdvanceTiles.Any(
                value => !double.IsFinite(value) || value < 0d
            )
            || Math.Abs(
                source.FrameAdvanceTiles.Sum() - source.TotalAdvanceTiles
            ) > 0.000001d
            || !source.ResetAfterAnimation
        )
        {
            reason = "hostile-shadow.attack-motion-policy-invalid";
            return false;
        }

        policy = new HostileAttackMotionPolicy(
            source.PolicyId,
            source.ContractVersion,
            source.TotalAdvanceTiles,
            source.FrameAdvanceTiles,
            source.ResetAfterAnimation
        );
        reason = "hostile-shadow.attack-motion-policy-available";
        return true;
    }

    /// <summary>
    /// Returns cumulative displacement from the frozen attack origin. The first frame after the
    /// animation is the reset boundary, so callers never accumulate floating-point drift.
    /// </summary>
    internal bool TryGetCumulativeWorldAdvance(
        int frameNumber,
        double tileSizePixels,
        out double worldAdvancePixels
    )
    {
        worldAdvancePixels = 0d;
        if (
            frameNumber <= 0
            || frameNumber > FrameCount + 1
            || !double.IsFinite(tileSizePixels)
            || tileSizePixels <= 0d
        )
        {
            return false;
        }
        if (frameNumber == FrameCount + 1)
            return ResetAfterAnimation;

        var cumulativeTiles = 0d;
        for (var index = 0; index < frameNumber; index++)
            cumulativeTiles += frameAdvanceTiles[index];
        worldAdvancePixels = cumulativeTiles * tileSizePixels;
        return double.IsFinite(worldAdvancePixels);
    }
}

/// <summary>
/// One independent hit window inside a single attack instance. Each window owns its
/// invincibility policy; the hit ledger claims players per window, so different windows may
/// hit the same player again while one window never does (20261113 冻结决议 5)。
/// </summary>
internal sealed class HostileAttackHitWindow
{
    internal const string LegacyWindowId =
        SanityHostileAttackHitWindowRules.LegacyWindowId;

    internal HostileAttackHitWindow(
        string windowId,
        InvincibilityFramePolicy policy,
        IReadOnlyList<int> activeFrames
    )
    {
        WindowId = windowId;
        Policy = policy;
        ActiveFrames = activeFrames;
    }

    internal string WindowId { get; }
    internal InvincibilityFramePolicy Policy { get; }
    internal IReadOnlyList<int> ActiveFrames { get; }
}

internal sealed class HostileAttackRuntimeDefinition
{
    private readonly HashSet<int> activeFrames;
    private readonly Dictionary<int, HostileAttackHitWindow> hitWindowByFrame;

    private HostileAttackRuntimeDefinition(
        string assetBindingId,
        string animationProfileId,
        HostileAttackMotionPolicy motion,
        long spawnDurationMilliseconds,
        long tauntDurationMilliseconds,
        int attackFrameCount,
        int attackFrameDurationMilliseconds,
        IReadOnlyList<int> activeFrames,
        IReadOnlyList<HostileAttackHitWindow> hitWindows,
        HostileAttackPoint actorOriginSourcePx,
        HostileAttackPoint hurtPivotSourcePx,
        HostileAttackRectangle hurtBoxSourcePx,
        double hurtDrawScale,
        SanityHostilePushBoxDefinition pushBox,
        HostileAttackPoint pushBoxPivotSourcePx,
        double pushBoxDrawScale,
        HostileAttackPoint attackPivotSourcePx,
        HostileAttackRectangle attackBoxSourcePx,
        double attackDrawScale,
        SanityHostileAnimationStateDefinition? hitResponseVisual
    )
    {
        AssetBindingId = assetBindingId;
        AnimationProfileId = animationProfileId;
        Motion = motion;
        SpawnDurationMilliseconds = spawnDurationMilliseconds;
        TauntDurationMilliseconds = tauntDurationMilliseconds;
        AttackFrameCount = attackFrameCount;
        AttackFrameDurationMilliseconds = attackFrameDurationMilliseconds;
        this.activeFrames = new HashSet<int>(activeFrames);
        // 窗口帧互斥由 SanityHostileAttackHitWindowRules 保证；这里用 Add 而不是索引赋值，
        // 违反互斥时立即抛错而不是静默覆盖（fail-fast，绝不把重复帧编码成期望行为）。
        hitWindowByFrame = new Dictionary<int, HostileAttackHitWindow>();
        foreach (var hitWindow in hitWindows)
        {
            foreach (var frame in hitWindow.ActiveFrames)
                hitWindowByFrame.Add(frame, hitWindow);
        }
        ActorOriginSourcePx = actorOriginSourcePx;
        HurtPivotSourcePx = hurtPivotSourcePx;
        HurtBoxSourcePx = hurtBoxSourcePx;
        HurtDrawScale = hurtDrawScale;
        PushBox = pushBox;
        PushBoxPivotSourcePx = pushBoxPivotSourcePx;
        PushBoxDrawScale = pushBoxDrawScale;
        AttackPivotSourcePx = attackPivotSourcePx;
        AttackBoxSourcePx = attackBoxSourcePx;
        AttackDrawScale = attackDrawScale;
        HitResponseVisual = hitResponseVisual;
    }

    internal string AssetBindingId { get; }
    internal string AnimationProfileId { get; }
    internal HostileAttackMotionPolicy Motion { get; }
    internal long SpawnDurationMilliseconds { get; }
    internal long TauntDurationMilliseconds { get; }
    internal int AttackFrameCount { get; }
    internal int AttackFrameDurationMilliseconds { get; }
    internal HostileAttackPoint ActorOriginSourcePx { get; }
    internal HostileAttackPoint HurtPivotSourcePx { get; }
    internal HostileAttackRectangle HurtBoxSourcePx { get; }
    internal double HurtDrawScale { get; }
    internal SanityHostilePushBoxDefinition PushBox { get; }
    internal HostileAttackPoint PushBoxPivotSourcePx { get; }
    internal double PushBoxDrawScale { get; }
    internal SanityResourceRectangle PushBoxSourcePx => PushBox.SourcePx;
    internal string PushBoxGroupId => PushBox.GroupId;
    internal double PushForce => PushBox.PushForce;
    internal HostileAttackPoint AttackPivotSourcePx { get; }
    internal HostileAttackRectangle AttackBoxSourcePx { get; }
    internal double AttackDrawScale { get; }
    /// <summary>
    /// Optional presentation metadata only. HitTeleport/Dying timing and branching never consume
    /// the animation ID, row name, or availability of this definition.
    /// </summary>
    internal SanityHostileAnimationStateDefinition? HitResponseVisual { get; }

    internal bool IsActiveFrame(int frameNumber)
    {
        return activeFrames.Contains(frameNumber);
    }

    /// <summary>
    /// Resolves the hit window that owns an active frame. Every active frame belongs to exactly
    /// one window (implicit legacy window for old metadata), so an active frame without a window
    /// is an invariant violation the caller must treat as fail-closed.
    /// </summary>
    internal bool TryGetWindowForFrame(
        int frameNumber,
        out HostileAttackHitWindow? window
    )
    {
        return hitWindowByFrame.TryGetValue(frameNumber, out window);
    }

    internal static bool TryCreate(
        SanityHostileAttackMetadataDefinition? metadata,
        ShadowMonsterRuntimeProfile? profile,
        out HostileAttackRuntimeDefinition? definition,
        out string reason
    )
    {
        definition = null;
        reason = string.Empty;
        if (
            metadata is null
            || profile is null
            || !string.Equals(
                metadata.AssetBindingId,
                profile.AssetBindingId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                metadata.AnimationProfileId,
                profile.AnimationProfileId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                metadata.AttackMotionPolicyId,
                profile.AttackMotionPolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                metadata.Collision.CoordinateSpace,
                "ActorOriginRelativeSourcePx",
                StringComparison.Ordinal
            )
            || metadata.Spawn.FrameCount <= 0
            || metadata.Spawn.FrameDurationMilliseconds <= 0
            || metadata.Taunt.FrameCount <= 0
            || metadata.Taunt.FrameDurationMilliseconds <= 0
            || metadata.Attack.FrameCount <= 0
            || metadata.Attack.FrameDurationMilliseconds <= 0
            || metadata.Idle.DrawScale <= 0d
            || metadata.Attack.DrawScale <= 0d
            || metadata.Collision.HurtBoxSourcePx.Width <= 0
            || metadata.Collision.HurtBoxSourcePx.Height <= 0
            || !metadata.Collision.PushBox.IsValid
            || metadata.Collision.AttackActiveFrames.Count == 0
            || !metadata.Collision.AttackActiveFrames.SequenceEqual(
                metadata.Attack.HitFrames
            )
            || metadata.Collision.AttackActiveFrames.Any(
                frame => frame < 1 || frame > metadata.Attack.FrameCount
            )
            || !HostileAttackMotionPolicy.TryCreate(
                metadata.Motion,
                out var motion,
                out reason
            )
            || motion!.FrameCount != metadata.Attack.FrameCount
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "hostile-shadow.attack-runtime-metadata-invalid";
            return false;
        }

        long spawnDuration;
        long tauntDuration;
        try
        {
            spawnDuration = checked(
                (long)metadata.Spawn.FrameCount
                    * metadata.Spawn.FrameDurationMilliseconds
            );
            tauntDuration = checked(
                (long)metadata.Taunt.FrameCount
                    * metadata.Taunt.FrameDurationMilliseconds
            );
        }
        catch (OverflowException)
        {
            reason = "hostile-shadow.attack-animation-duration-overflow";
            return false;
        }

        if (
            !SanityHostileAttackHitWindowRules.TryCreateWindows(
                metadata.Collision.AttackActiveFrames,
                metadata.Attack.FrameCount,
                metadata.Collision.AttackHitWindows,
                out var windowSources,
                out reason
            )
        )
        {
            return false;
        }

        var hitWindows = new List<HostileAttackHitWindow>();
        foreach (var windowSource in windowSources)
        {
            if (!TryConvertPolicy(windowSource, out var policy, out reason))
            {
                return false;
            }

            hitWindows.Add(
                new HostileAttackHitWindow(
                    windowSource.WindowId,
                    policy,
                    windowSource.ActiveFrames
                )
            );
        }

        definition = new HostileAttackRuntimeDefinition(
            metadata.AssetBindingId,
            metadata.AnimationProfileId,
            motion,
            spawnDuration,
            tauntDuration,
            metadata.Attack.FrameCount,
            metadata.Attack.FrameDurationMilliseconds,
            metadata.Collision.AttackActiveFrames,
            hitWindows,
            new HostileAttackPoint(
                metadata.Collision.ActorOriginSourcePx.X,
                metadata.Collision.ActorOriginSourcePx.Y
            ),
            new HostileAttackPoint(
                metadata.Idle.PivotSourcePx.X,
                metadata.Idle.PivotSourcePx.Y
            ),
            new HostileAttackRectangle(
                metadata.Collision.HurtBoxSourcePx.X,
                metadata.Collision.HurtBoxSourcePx.Y,
                metadata.Collision.HurtBoxSourcePx.Width,
                metadata.Collision.HurtBoxSourcePx.Height
            ),
            metadata.Idle.DrawScale,
            metadata.Collision.PushBox,
            new HostileAttackPoint(
                metadata.Idle.PivotSourcePx.X,
                metadata.Idle.PivotSourcePx.Y
            ),
            metadata.Idle.DrawScale,
            new HostileAttackPoint(
                metadata.Attack.PivotSourcePx.X,
                metadata.Attack.PivotSourcePx.Y
            ),
            new HostileAttackRectangle(
                metadata.Collision.AttackBoxSourcePx.X,
                metadata.Collision.AttackBoxSourcePx.Y,
                metadata.Collision.AttackBoxSourcePx.Width,
                metadata.Collision.AttackBoxSourcePx.Height
            ),
            metadata.Attack.DrawScale,
            metadata.HitResponseVisual
        );
        reason = "hostile-shadow.attack-runtime-metadata-available";
        return true;
    }

    private static bool TryConvertPolicy(
        SanityHostileAttackHitWindowDefinition source,
        out InvincibilityFramePolicy policy,
        out string reason
    )
    {
        // 资源层只携带字符串/整数，这里是唯一的策略值对象转换点；
        // 未知名称一律 fail-closed，绝不降级成 Normal（那会静默放大无敌帧）。
        switch (source.IFramePolicyMode)
        {
            case "Normal":
                policy = InvincibilityFramePolicy.Normal;
                break;
            case "Strong":
                policy = InvincibilityFramePolicy.Strong(
                    source.IFramePolicyDurationMilliseconds
                );
                break;
            case "Weak":
                policy = InvincibilityFramePolicy.Weak(
                    source.IFramePolicyDurationMilliseconds
                );
                break;
            default:
                policy = InvincibilityFramePolicy.Normal;
                reason = "hostile-shadow.attack-runtime-hit-window-policy-unknown";
                return false;
        }

        reason = "hostile-shadow.attack-runtime-hit-window-policy-available";
        return true;
    }
}
