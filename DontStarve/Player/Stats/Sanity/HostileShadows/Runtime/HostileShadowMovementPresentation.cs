#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal static class HostileShadowFacingIds
{
    internal const string Down = "Down";
    internal const string Right = "Right";
    internal const string Up = "Up";
    internal const string Left = "Left";

    internal static string FromFacing(HostileAttackFacing facing)
    {
        return facing switch
        {
            HostileAttackFacing.Right => Right,
            HostileAttackFacing.Up => Up,
            HostileAttackFacing.Left => Left,
            _ => Down,
        };
    }
}

internal static class HostileShadowMovementPresentationBindings
{
    internal static bool Supports(string assetBindingId)
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
}

/// <summary>
/// Small per-entity presentation clock for the shared physical Monster. It consumes the common
/// four-way resolver and animation metadata, but owns no target scan, pathfinding, authority state,
/// or protocol revision. Callers serialize only changed facing/frame scalars through inherited
/// Character.modData.
/// </summary>
internal sealed class HostileShadowMovementPresentationState
{
    private readonly int frameCount;
    private readonly int frameDurationMilliseconds;
    private double frameElapsedMilliseconds;

    internal HostileShadowMovementPresentationState(
        int frameCount,
        int frameDurationMilliseconds
    )
    {
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        this.frameCount = frameCount;
        this.frameDurationMilliseconds = frameDurationMilliseconds;
    }

    internal string FacingId { get; private set; } = HostileShadowFacingIds.Down;
    internal int FrameIndex { get; private set; }

    internal bool TryAdvance(
        bool isChasing,
        bool positionChanged,
        double standingX,
        double standingY,
        double targetStandingX,
        double targetStandingY,
        double elapsedMilliseconds,
        out bool presentationChanged
    )
    {
        presentationChanged = false;
        if (
            !double.IsFinite(elapsedMilliseconds)
            || elapsedMilliseconds < 0d
            || (
                isChasing
                && (
                    !double.IsFinite(standingX)
                    || !double.IsFinite(standingY)
                    || !double.IsFinite(targetStandingX)
                    || !double.IsFinite(targetStandingY)
                )
            )
        )
        {
            return false;
        }

        if (!isChasing)
        {
            presentationChanged = FrameIndex != 0;
            Reset();
            return true;
        }

        var facing = HostileShadowFacingIds.FromFacing(
            HostileAttackCollisionResolver.ResolveFacing(
                standingX,
                standingY,
                targetStandingX,
                targetStandingY
            )
        );
        if (!string.Equals(FacingId, facing, StringComparison.Ordinal))
        {
            FacingId = facing;
            presentationChanged = true;
        }

        if (!positionChanged || elapsedMilliseconds <= 0d)
            return true;

        var total = frameElapsedMilliseconds + elapsedMilliseconds;
        var framesElapsed = (long)Math.Floor(total / frameDurationMilliseconds);
        if (framesElapsed <= 0)
        {
            frameElapsedMilliseconds = total;
            return true;
        }

        frameElapsedMilliseconds = total
            - framesElapsed * frameDurationMilliseconds;
        var nextFrame = (int)((FrameIndex + framesElapsed) % frameCount);
        if (nextFrame != FrameIndex)
        {
            FrameIndex = nextFrame;
            presentationChanged = true;
        }
        return true;
    }

    internal void Reset()
    {
        FrameIndex = 0;
        frameElapsedMilliseconds = 0d;
    }
}
