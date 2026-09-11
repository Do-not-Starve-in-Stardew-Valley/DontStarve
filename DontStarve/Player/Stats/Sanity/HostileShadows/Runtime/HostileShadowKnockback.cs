#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// One host-side knockback movement slice. Stardew's Monster.MovePosition applies one velocity
/// slice per update and then decays the velocity; the world runtime consumes that same slice as
/// part of its authoritative movement plan instead of letting the physical entity move itself.
/// </summary>
internal readonly record struct HostileShadowKnockbackStep(
    bool Valid,
    bool Active,
    bool Blocked,
    double OffsetX,
    double OffsetY,
    float NextVelocityX,
    float NextVelocityY
)
{
    internal static HostileShadowKnockbackStep None => new(
        true,
        false,
        false,
        0d,
        0d,
        0f,
        0f
    );
}

/// <summary>
/// Pure portion of the Stardew 1.6 Monster knockback contract. Map collision is deliberately
/// supplied by the host runtime, while trajectory scaling and velocity decay stay testable here.
/// </summary>
internal static class HostileShadowKnockbackPolicy
{
    internal const float StopThreshold = 0.05f;

    internal static void ApplyIncomingTrajectory(
        ref float currentVelocityX,
        ref float currentVelocityY,
        float trajectoryX,
        float trajectoryY
    )
    {
        // This is the direct setTrajectory/Monster.doSetTrajectory contract. The damage path
        // divides its integer inputs before calling setTrajectory; callers which already hold a
        // Vector2 trajectory must not divide it a second time.
        if (Math.Abs(trajectoryX) > Math.Abs(currentVelocityX))
            currentVelocityX = trajectoryX;
        if (Math.Abs(trajectoryY) > Math.Abs(currentVelocityY))
            currentVelocityY = trajectoryY;
    }

    internal static void ApplyDamageTrajectory(
        ref float currentVelocityX,
        ref float currentVelocityY,
        int xTrajectory,
        int yTrajectory
    )
    {
        // Stardew 1.6.15 uses int xTrajectory / 3 and int yTrajectory / 3 in
        // Monster.takeDamage. C# integer division truncates toward zero, so keep that detail
        // instead of using a floating-point 1/3 multiplier.
        ApplyIncomingTrajectory(
            ref currentVelocityX,
            ref currentVelocityY,
            xTrajectory / 3,
            yTrajectory / 3
        );
    }

    internal static HostileShadowKnockbackStep Plan(
        float velocityX,
        float velocityY,
        int slipperiness,
        bool blocked,
        bool stunned = false
    )
    {
        // Monster.MovePosition returns before collision testing, movement, or velocity decay
        // while stunTime is positive. The velocity is intentionally retained for the next tick.
        if (stunned)
            return HostileShadowKnockbackStep.None;
        if (velocityX == 0f && velocityY == 0f)
            return HostileShadowKnockbackStep.None;
        if (slipperiness == -1)
            return HostileShadowKnockbackStep.None;
        if (
            !float.IsFinite(velocityX)
            || !float.IsFinite(velocityY)
            || slipperiness <= 0
        )
        {
            return new HostileShadowKnockbackStep(
                false,
                false,
                blocked,
                0d,
                0d,
                0f,
                0f
            );
        }

        var nextVelocityX = velocityX;
        var nextVelocityY = velocityY;
        if (slipperiness < 1000)
        {
            // Monster.MovePosition uses the slower quarter-decay branch after a blocked
            // high-slipperiness move; ordinary shadows use the direct branch.
            var divisor = blocked && slipperiness >= 8
                ? slipperiness * 4f
                : slipperiness;
            nextVelocityX -= velocityX / divisor;
            nextVelocityY -= velocityY / divisor;
            if (Math.Abs(nextVelocityX) <= StopThreshold)
                nextVelocityX = 0f;
            if (Math.Abs(nextVelocityY) <= StopThreshold)
                nextVelocityY = 0f;
        }

        return new HostileShadowKnockbackStep(
            true,
            true,
            blocked,
            blocked ? 0d : velocityX,
            blocked ? 0d : -velocityY,
            nextVelocityX,
            nextVelocityY
        );
    }
}
