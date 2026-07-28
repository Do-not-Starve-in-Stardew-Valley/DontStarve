#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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

internal sealed class HostileAttackRuntimeDefinition
{
    private readonly HashSet<int> activeFrames;

    private HostileAttackRuntimeDefinition(
        string assetBindingId,
        string animationProfileId,
        HostileAttackMotionPolicy motion,
        long spawnDurationMilliseconds,
        long tauntDurationMilliseconds,
        int attackFrameCount,
        int attackFrameDurationMilliseconds,
        IReadOnlyList<int> activeFrames,
        HostileAttackPoint actorOriginSourcePx,
        HostileAttackPoint hurtPivotSourcePx,
        HostileAttackRectangle hurtBoxSourcePx,
        double hurtDrawScale,
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
        ActorOriginSourcePx = actorOriginSourcePx;
        HurtPivotSourcePx = hurtPivotSourcePx;
        HurtBoxSourcePx = hurtBoxSourcePx;
        HurtDrawScale = hurtDrawScale;
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

        definition = new HostileAttackRuntimeDefinition(
            metadata.AssetBindingId,
            metadata.AnimationProfileId,
            motion,
            spawnDuration,
            tauntDuration,
            metadata.Attack.FrameCount,
            metadata.Attack.FrameDurationMilliseconds,
            metadata.Collision.AttackActiveFrames,
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
}
