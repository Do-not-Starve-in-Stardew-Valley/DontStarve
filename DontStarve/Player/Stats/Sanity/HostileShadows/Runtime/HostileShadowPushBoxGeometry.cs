#nullable enable

using System;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// World-space rectangle used only by the shadow-creature PushBox path. It is intentionally a
/// separate type from HostileAttackRectangle so future crowd collision cannot accidentally reuse
/// attack or hurt-box semantics.
/// </summary>
internal readonly record struct HostileShadowPushBoxWorldRectangle(
    double X,
    double Y,
    double Width,
    double Height
)
{
    internal bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0d
        && Height > 0d
        && double.IsFinite(X + Width)
        && double.IsFinite(Y + Height);

    internal bool Intersects(HostileShadowPushBoxWorldRectangle other)
    {
        return TryGetIntersectionDepth(other, out _, out _);
    }

    internal bool TryGetIntersectionDepth(
        HostileShadowPushBoxWorldRectangle other,
        out double overlapX,
        out double overlapY
    )
    {
        overlapX = 0d;
        overlapY = 0d;
        if (!IsValid || !other.IsValid)
            return false;

        overlapX = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        overlapY = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        if (!double.IsFinite(overlapX) || !double.IsFinite(overlapY))
        {
            overlapX = 0d;
            overlapY = 0d;
            return false;
        }

        return overlapX > 0d && overlapY > 0d;
    }
}

/// <summary>
/// Converts the independently-defined PushBox source rectangle into world pixels using the
/// existing actor-origin/pivot/draw-scale convention. This class performs no entity, player, or
/// map collision work and has no movement side effects.
/// </summary>
internal static class HostileShadowPushBoxGeometry
{
    internal static bool TryCreateWorldBox(
        HostileAttackRuntimeDefinition? definition,
        double pivotWorldX,
        double pivotWorldY,
        out HostileShadowPushBoxWorldRectangle worldBox
    )
    {
        worldBox = default;
        if (definition is null)
            return false;

        return TryCreateWorldBox(
            definition.ActorOriginSourcePx,
            definition.PushBoxPivotSourcePx,
            definition.PushBoxSourcePx,
            definition.PushBoxDrawScale,
            pivotWorldX,
            pivotWorldY,
            out worldBox
        );
    }

    internal static bool TryCreateWorldBox(
        HostileAttackPoint actorOriginSourcePx,
        HostileAttackPoint pushBoxPivotSourcePx,
        SanityResourceRectangle sourcePx,
        double drawScale,
        double pivotWorldX,
        double pivotWorldY,
        out HostileShadowPushBoxWorldRectangle worldBox
    )
    {
        worldBox = default;
        if (
            !double.IsFinite(actorOriginSourcePx.X)
            || !double.IsFinite(actorOriginSourcePx.Y)
            || !double.IsFinite(pushBoxPivotSourcePx.X)
            || !double.IsFinite(pushBoxPivotSourcePx.Y)
            || !SanityHostilePushBoxDefinition.IsValidSourceRectangle(sourcePx)
            || !double.IsFinite(drawScale)
            || drawScale <= 0d
            || !double.IsFinite(pivotWorldX)
            || !double.IsFinite(pivotWorldY)
        )
        {
            return false;
        }

        var actorWorldX = pivotWorldX
            + (actorOriginSourcePx.X - pushBoxPivotSourcePx.X) * drawScale;
        var actorWorldY = pivotWorldY
            + (actorOriginSourcePx.Y - pushBoxPivotSourcePx.Y) * drawScale;
        worldBox = new HostileShadowPushBoxWorldRectangle(
            actorWorldX + sourcePx.X * drawScale,
            actorWorldY + sourcePx.Y * drawScale,
            sourcePx.Width * drawScale,
            sourcePx.Height * drawScale
        );
        return worldBox.IsValid;
    }
}
