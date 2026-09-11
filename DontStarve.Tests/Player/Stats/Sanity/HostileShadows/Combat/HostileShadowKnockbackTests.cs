using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileShadowKnockbackTests
{
    [Fact]
    public void Incoming_trajectory_matches_vanilla_third_scale_and_component_max()
    {
        var velocityX = 5f;
        var velocityY = -1f;

        HostileShadowKnockbackPolicy.ApplyDamageTrajectory(
            ref velocityX,
            ref velocityY,
            9,
            -12
        );

        Assert.Equal(5f, velocityX);
        Assert.Equal(-4f, velocityY);
    }

    [Fact]
    public void Damage_trajectory_uses_vanilla_integer_division_before_component_max()
    {
        var velocityX = 0f;
        var velocityY = 0f;

        HostileShadowKnockbackPolicy.ApplyDamageTrajectory(
            ref velocityX,
            ref velocityY,
            10,
            -10
        );

        Assert.Equal(3f, velocityX);
        Assert.Equal(-3f, velocityY);
    }

    [Fact]
    public void Direct_trajectory_is_not_divided_again()
    {
        var velocityX = 0f;
        var velocityY = 0f;

        HostileShadowKnockbackPolicy.ApplyIncomingTrajectory(
            ref velocityX,
            ref velocityY,
            10f,
            -10f
        );

        Assert.Equal(10f, velocityX);
        Assert.Equal(-10f, velocityY);
    }

    [Fact]
    public void Unblocked_default_monster_slice_moves_then_halves_velocity()
    {
        var step = HostileShadowKnockbackPolicy.Plan(
            6f,
            -9f,
            slipperiness: 2,
            blocked: false
        );

        Assert.True(step.Valid);
        Assert.True(step.Active);
        Assert.False(step.Blocked);
        Assert.Equal(6d, step.OffsetX);
        Assert.Equal(9d, step.OffsetY);
        Assert.Equal(3f, step.NextVelocityX);
        Assert.Equal(-4.5f, step.NextVelocityY);
    }

    [Fact]
    public void Blocked_high_slipperiness_slice_does_not_move_and_uses_quarter_decay()
    {
        var step = HostileShadowKnockbackPolicy.Plan(
            8f,
            -12f,
            slipperiness: 8,
            blocked: true
        );

        Assert.True(step.Valid);
        Assert.True(step.Active);
        Assert.True(step.Blocked);
        Assert.Equal(0d, step.OffsetX);
        Assert.Equal(0d, step.OffsetY);
        Assert.Equal(7.75f, step.NextVelocityX);
        Assert.Equal(-11.625f, step.NextVelocityY);
    }

    [Fact]
    public void Knockback_immunity_does_not_create_a_motion_slice()
    {
        var step = HostileShadowKnockbackPolicy.Plan(
            6f,
            -9f,
            slipperiness: -1,
            blocked: false
        );

        Assert.True(step.Valid);
        Assert.False(step.Active);
    }

    [Fact]
    public void Stun_does_not_consume_or_decay_the_pending_motion_slice()
    {
        var step = HostileShadowKnockbackPolicy.Plan(
            6f,
            -9f,
            slipperiness: 2,
            blocked: false,
            stunned: true
        );

        Assert.True(step.Valid);
        Assert.False(step.Active);
    }
}
