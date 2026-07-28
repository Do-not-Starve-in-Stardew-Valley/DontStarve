#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal enum HostileAttackFacing
{
    Down,
    Right,
    Up,
    Left,
}

internal readonly record struct HostileAttackPoint(double X, double Y);

internal readonly record struct HostileAttackRectangle(
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
        && Height > 0d;

    internal bool Intersects(HostileAttackRectangle other)
    {
        return IsValid
            && other.IsValid
            && X < other.X + other.Width
            && X + Width > other.X
            && Y < other.Y + other.Height
            && Y + Height > other.Y;
    }
}

internal static class HostileAttackCollisionResolver
{
    internal static bool TryCreateWorldHurtBox(
        HostileAttackRuntimeDefinition? definition,
        double pivotWorldX,
        double pivotWorldY,
        out HostileAttackRectangle worldBox
    )
    {
        worldBox = default;
        if (
            definition is null
            || !double.IsFinite(pivotWorldX)
            || !double.IsFinite(pivotWorldY)
            || !double.IsFinite(definition.HurtDrawScale)
            || definition.HurtDrawScale <= 0d
            || !definition.HurtBoxSourcePx.IsValid
        )
        {
            return false;
        }

        var actorWorldX = pivotWorldX
            + (
                definition.ActorOriginSourcePx.X
                - definition.HurtPivotSourcePx.X
            ) * definition.HurtDrawScale;
        var actorWorldY = pivotWorldY
            + (
                definition.ActorOriginSourcePx.Y
                - definition.HurtPivotSourcePx.Y
            ) * definition.HurtDrawScale;
        var box = definition.HurtBoxSourcePx;
        worldBox = new HostileAttackRectangle(
            actorWorldX + box.X * definition.HurtDrawScale,
            actorWorldY + box.Y * definition.HurtDrawScale,
            box.Width * definition.HurtDrawScale,
            box.Height * definition.HurtDrawScale
        );
        return worldBox.IsValid;
    }

    internal static HostileAttackFacing ResolveFacing(
        double originX,
        double originY,
        double targetX,
        double targetY
    )
    {
        var x = targetX - originX;
        var y = targetY - originY;
        if (Math.Abs(x) > Math.Abs(y))
            return x >= 0d ? HostileAttackFacing.Right : HostileAttackFacing.Left;
        return y < 0d ? HostileAttackFacing.Up : HostileAttackFacing.Down;
    }

    internal static bool TryCreateWorldAttackBox(
        HostileAttackRuntimeDefinition? definition,
        double pivotWorldX,
        double pivotWorldY,
        HostileAttackFacing facing,
        out HostileAttackRectangle worldBox
    )
    {
        worldBox = default;
        if (
            definition is null
            || !double.IsFinite(pivotWorldX)
            || !double.IsFinite(pivotWorldY)
            || !double.IsFinite(definition.AttackDrawScale)
            || definition.AttackDrawScale <= 0d
            || !definition.AttackBoxSourcePx.IsValid
        )
        {
            return false;
        }

        var box = definition.AttackBoxSourcePx;
        var topLeft = Rotate(box.X, box.Y, facing);
        var topRight = Rotate(box.X + box.Width, box.Y, facing);
        var bottomLeft = Rotate(box.X, box.Y + box.Height, facing);
        var bottomRight = Rotate(
            box.X + box.Width,
            box.Y + box.Height,
            facing
        );
        var actorWorldX = pivotWorldX
            + (
                definition.ActorOriginSourcePx.X
                - definition.AttackPivotSourcePx.X
            ) * definition.AttackDrawScale;
        var actorWorldY = pivotWorldY
            + (
                definition.ActorOriginSourcePx.Y
                - definition.AttackPivotSourcePx.Y
            ) * definition.AttackDrawScale;
        var minX = Math.Min(
                Math.Min(topLeft.X, topRight.X),
                Math.Min(bottomLeft.X, bottomRight.X)
            ) * definition.AttackDrawScale;
        var minY = Math.Min(
                Math.Min(topLeft.Y, topRight.Y),
                Math.Min(bottomLeft.Y, bottomRight.Y)
            ) * definition.AttackDrawScale;
        var maxX = Math.Max(
                Math.Max(topLeft.X, topRight.X),
                Math.Max(bottomLeft.X, bottomRight.X)
            ) * definition.AttackDrawScale;
        var maxY = Math.Max(
                Math.Max(topLeft.Y, topRight.Y),
                Math.Max(bottomLeft.Y, bottomRight.Y)
            ) * definition.AttackDrawScale;
        worldBox = new HostileAttackRectangle(
            actorWorldX + minX,
            actorWorldY + minY,
            maxX - minX,
            maxY - minY
        );
        return worldBox.IsValid;
    }

    internal static bool IsWithinRange(
        HostileAttackPoint left,
        HostileAttackPoint right,
        double maximumRangePixels
    )
    {
        if (!double.IsFinite(maximumRangePixels) || maximumRangePixels <= 0d)
            return false;
        var x = right.X - left.X;
        var y = right.Y - left.Y;
        return (x * x) + (y * y) <= maximumRangePixels * maximumRangePixels;
    }

    private static HostileAttackPoint Rotate(
        double x,
        double y,
        HostileAttackFacing facing
    )
    {
        return facing switch
        {
            HostileAttackFacing.Right => new HostileAttackPoint(y, -x),
            HostileAttackFacing.Up => new HostileAttackPoint(-x, -y),
            HostileAttackFacing.Left => new HostileAttackPoint(-y, x),
            _ => new HostileAttackPoint(x, y),
        };
    }
}
