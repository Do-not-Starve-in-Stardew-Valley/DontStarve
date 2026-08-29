using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileAttackMotionAndStateTests
{
    [Theory]
    [InlineData(32d)]
    [InlineData(64d)]
    [InlineData(96d)]
    public void Versioned_motion_uses_half_half_hold_hold_then_exact_reset(
        double tileSize
    )
    {
        var policy = HostileAttackTestFactory.Definition().Motion;

        AssertAdvance(policy, 1, tileSize, tileSize * 0.5d);
        AssertAdvance(policy, 2, tileSize, tileSize);
        AssertAdvance(policy, 3, tileSize, tileSize);
        AssertAdvance(policy, 4, tileSize, tileSize);
        AssertAdvance(policy, 5, tileSize, 0d);
        Assert.Equal(tileSize, policy.TotalAdvanceTiles * tileSize);
    }

    [Fact]
    public void Frame_2_3_4_5_boundaries_gate_box_and_reset_origin()
    {
        var (machine, definition, first) = HostileAttackTestFactory.StartAttack();
        var frameDuration = definition.AttackFrameDurationMilliseconds;

        Assert.Equal(1, first.AttackFrameNumber);
        Assert.False(definition.IsActiveFrame(2));
        var second = machine.Advance(HostileAttackTestFactory.Input(), frameDuration);
        Assert.Equal(2, second.AttackFrameNumber);
        Assert.Equal(164d, second.PositionX, precision: 8);

        var third = machine.Advance(HostileAttackTestFactory.Input(), frameDuration);
        Assert.Equal(3, third.AttackFrameNumber);
        Assert.True(definition.IsActiveFrame(third.AttackFrameNumber));
        Assert.Equal(second.PositionX, third.PositionX, precision: 8);

        var fourth = machine.Advance(HostileAttackTestFactory.Input(), frameDuration);
        Assert.Equal(4, fourth.AttackFrameNumber);
        Assert.True(definition.IsActiveFrame(fourth.AttackFrameNumber));
        Assert.Equal(third.PositionX, fourth.PositionX, precision: 8);

        var reset = machine.Advance(HostileAttackTestFactory.Input(), frameDuration);
        Assert.Equal(HostileShadowStateIds.Chase, reset.StateId);
        Assert.Equal(0, reset.AttackFrameNumber);
        Assert.Equal(100d, reset.PositionX, precision: 8);
        Assert.Equal(200d, reset.PositionY, precision: 8);
        Assert.Null(machine.CurrentInstance);
    }

    [Fact]
    public void Repeated_attack_cycles_recompute_from_origin_without_drift()
    {
        var (machine, definition, _) = HostileAttackTestFactory.StartAttack();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            for (var frame = 1; frame <= definition.AttackFrameCount; frame++)
            {
                var decision = machine.Advance(
                    HostileAttackTestFactory.Input(),
                    definition.AttackFrameDurationMilliseconds
                );
                if (frame == definition.AttackFrameCount)
                {
                    Assert.Equal(100d, decision.PositionX, precision: 8);
                    Assert.Equal(200d, decision.PositionY, precision: 8);
                }
            }
            if (cycle < 99)
            {
                var restart = machine.Advance(HostileAttackTestFactory.Input(), 0d);
                Assert.Equal(HostileShadowStateIds.Attack, restart.StateId);
                Assert.Equal(132d, restart.PositionX, precision: 8);
            }
        }
    }

    [Fact]
    public void Injectable_taunt_and_interval_policy_controls_public_transitions()
    {
        var definition = HostileAttackTestFactory.Definition();
        var policy = new TestTransitionPolicy(shouldTaunt: true, delaySeconds: 2d);
        var machine = new HostileAttackStateMachine(definition, policy);

        var taunt = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            definition.SpawnDurationMilliseconds
        );
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);

        var chase = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            definition.TauntDurationMilliseconds
        );
        Assert.Equal(HostileShadowStateIds.Chase, chase.StateId);

        var attack = machine.Advance(HostileAttackTestFactory.Input(), 0d);
        Assert.Equal(HostileShadowStateIds.Attack, attack.StateId);
        for (var frame = 0; frame < definition.AttackFrameCount; frame++)
        {
            machine.Advance(
                HostileAttackTestFactory.Input(),
                definition.AttackFrameDurationMilliseconds
            );
        }
        Assert.Equal(HostileShadowStateIds.Chase, machine.StateId);

        var waiting = machine.Advance(HostileAttackTestFactory.Input(), 1999d);
        Assert.Equal(HostileShadowStateIds.Chase, waiting.StateId);
        var next = machine.Advance(HostileAttackTestFactory.Input(), 1d);
        Assert.Equal(HostileShadowStateIds.Attack, next.StateId);
        Assert.True(policy.TauntCalls > 0);
        Assert.True(policy.IntervalCalls > 0);
    }

    [Fact]
    public void Projection_conversion_starts_with_one_taunt_and_does_not_retaunt_on_first_target()
    {
        var definition = HostileAttackTestFactory.Definition(intervalSeconds: 1d);
        var policy = new TestTransitionPolicy(shouldTaunt: true, delaySeconds: 0d);
        var machine = new HostileAttackStateMachine(
            definition,
            policy,
            HostileShadowStateIds.Taunt
        );

        Assert.Equal(HostileShadowStateIds.Taunt, machine.StateId);
        var stillTaunting = machine.Advance(
            HostileAttackTestFactory.Input(hasTarget: false, inRange: false),
            definition.TauntDurationMilliseconds - 1d
        );
        var idle = machine.Advance(
            HostileAttackTestFactory.Input(hasTarget: false, inRange: false),
            1d
        );
        var chase = machine.Advance(
            HostileAttackTestFactory.Input(inRange: false),
            0d
        );

        Assert.Equal(HostileShadowStateIds.Taunt, stillTaunting.StateId);
        Assert.Equal(HostileShadowStateIds.Idle, idle.StateId);
        Assert.Equal(HostileShadowStateIds.Chase, chase.StateId);
        Assert.Equal(0, policy.TauntCalls);
    }

    [Theory]
    [InlineData(ShadowMonsterAssetBindingIds.CreeperFear)]
    [InlineData(ShadowMonsterAssetBindingIds.Terrorbeak)]
    public void Hurt_box_center_round_trip_preserves_the_shared_projection_center(
        string bindingId
    )
    {
        var definition = HostileAttackTestFactory.Definition(bindingId);
        const double centerX = 1234.5d;
        const double centerY = 678.25d;

        Assert.True(
            HostileAttackCollisionResolver.TryResolvePivotForHurtBoxCenter(
                definition,
                centerX,
                centerY,
                out var pivotX,
                out var pivotY
            )
        );
        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldHurtBox(
                definition,
                pivotX,
                pivotY,
                out var hurtBox
            )
        );

        Assert.Equal(centerX, hurtBox.X + (hurtBox.Width / 2d), precision: 8);
        Assert.Equal(centerY, hurtBox.Y + (hurtBox.Height / 2d), precision: 8);
    }

    [Fact]
    public void Collision_box_rotation_does_not_change_gameplay_motion_distance()
    {
        var definition = HostileAttackTestFactory.Definition();
        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                100d,
                200d,
                HostileAttackFacing.Down,
                out var down
            )
        );
        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                100d,
                200d,
                HostileAttackFacing.Right,
                out var right
            )
        );
        Assert.True(down.IsValid);
        Assert.True(right.IsValid);
        Assert.Equal(down.Width, right.Height, precision: 8);
        Assert.Equal(down.Height, right.Width, precision: 8);

        AssertAdvance(definition.Motion, 2, 96d, 96d);
    }

    private static void AssertAdvance(
        HostileAttackMotionPolicy policy,
        int frame,
        double tileSize,
        double expected
    )
    {
        Assert.True(
            policy.TryGetCumulativeWorldAdvance(frame, tileSize, out var actual)
        );
        Assert.Equal(expected, actual, precision: 8);
    }

    private sealed class TestTransitionPolicy : IHostileAttackTransitionPolicy
    {
        private readonly bool shouldTaunt;
        private readonly double delaySeconds;

        internal TestTransitionPolicy(bool shouldTaunt, double delaySeconds)
        {
            this.shouldTaunt = shouldTaunt;
            this.delaySeconds = delaySeconds;
        }

        internal int TauntCalls { get; private set; }
        internal int IntervalCalls { get; private set; }

        public bool ShouldTauntBeforeFirstChase(
            HostileAttackTransitionContext context
        )
        {
            TauntCalls++;
            return shouldTaunt;
        }

        public bool TryResolvePostAttackTransition(
            HostileAttackTransitionContext context,
            double configuredProfileIntervalSeconds,
            out HostileAttackPostAttackTransition transition
        )
        {
            IntervalCalls++;
            transition = new HostileAttackPostAttackTransition(
                false,
                delaySeconds
            );
            return true;
        }
    }
}
