using System;
using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityIdlePanicLoopRulesTests
{
    [Fact]
    public void Start_begins_in_faint_phase_and_first_partial_tick_is_silent()
    {
        var rules = new SanityIdlePanicLoopRules();

        Assert.False(rules.IsActive);
        Assert.Equal(SanityIdlePanicPhase.Inactive, rules.Phase);

        rules.Start();

        Assert.True(rules.IsActive);
        Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
        Assert.Equal(
            SanityIdlePanicLoopCommand.None,
            rules.Tick(TimeSpan.FromMilliseconds(SanityIdlePanicLoopRules.FaintPhaseDuration.TotalMilliseconds - 1))
        );
        Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
    }

    [Fact]
    public void Loop_alternates_faint_then_startled_and_repeats_indefinitely()
    {
        var rules = new SanityIdlePanicLoopRules();
        rules.Start();

        var faint = SanityIdlePanicLoopRules.FaintPhaseDuration;
        var startled = SanityIdlePanicLoopRules.StartledPhaseDuration;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(faint - TimeSpan.FromMilliseconds(1)));
            Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
            Assert.Equal(SanityIdlePanicLoopCommand.BeginStartled, rules.Tick(TimeSpan.FromMilliseconds(1)));
            Assert.Equal(SanityIdlePanicPhase.Startled, rules.Phase);

            Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(startled - TimeSpan.FromMilliseconds(1)));
            Assert.Equal(SanityIdlePanicPhase.Startled, rules.Phase);
            Assert.Equal(SanityIdlePanicLoopCommand.BeginFaint, rules.Tick(TimeSpan.FromMilliseconds(1)));
            Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
        }
    }

    [Fact]
    public void Cancel_returns_to_inactive_and_ignores_ticks()
    {
        var rules = new SanityIdlePanicLoopRules();
        rules.Start();
        rules.Tick(SanityIdlePanicLoopRules.FaintPhaseDuration);
        Assert.Equal(SanityIdlePanicPhase.Startled, rules.Phase);

        rules.Cancel();

        Assert.False(rules.IsActive);
        Assert.Equal(SanityIdlePanicPhase.Inactive, rules.Phase);
        Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(TimeSpan.FromSeconds(10)));
        Assert.Equal(SanityIdlePanicPhase.Inactive, rules.Phase);
    }

    [Fact]
    public void Tick_ignores_inactive_instance_and_negative_elapsed()
    {
        var rules = new SanityIdlePanicLoopRules();

        Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(TimeSpan.FromSeconds(1)));
        Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(TimeSpan.FromMilliseconds(-1)));

        rules.Start();
        Assert.Equal(SanityIdlePanicLoopCommand.None, rules.Tick(TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
    }

    [Fact]
    public void Sanity_eligibility_is_at_or_below_half_with_positive_maximum()
    {
        Assert.True(SanityIdlePanicLoopRules.IsSanityEligible(49.9d, 100d));
        Assert.True(SanityIdlePanicLoopRules.IsSanityEligible(50d, 100d));
        Assert.False(SanityIdlePanicLoopRules.IsSanityEligible(50.1d, 100d));
        Assert.False(SanityIdlePanicLoopRules.IsSanityEligible(0d, 0d));
        Assert.False(SanityIdlePanicLoopRules.IsSanityEligible(10d, -5d));
    }

    [Fact]
    public void Faint_frame_contract_mirrors_vanilla_case_293_without_day_end_actions()
    {
        // 原版 FarmerSprite case 293（passOutTired）帧序：16/0/16/4/5，原版逐帧 1000/500/1000/200/2000ms。
        // 末帧延长为 5250ms（用户实测反馈：仅拉长倒地末帧，使单轮循环约 1.5 倍）。
        // 原版第 5 帧链式挂接 doSleepEmote 与 passOutFromTired（上床结算并结束当天）；本循环的
        // 帧契约只有纯帧数据，由适配层重建 AnimationFrame 数组，天然不携带任何帧动作。
        Assert.Equal(new[] { 16, 0, 16, 4, 5 }, SanityIdlePanicLoopRules.FaintFrameIds);
        Assert.Equal(new[] { 1000, 500, 1000, 200, 5250 }, SanityIdlePanicLoopRules.FaintFrameMilliseconds);

        var expectedTotal = 0;
        foreach (var milliseconds in SanityIdlePanicLoopRules.FaintFrameMilliseconds)
            expectedTotal += milliseconds;
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedTotal),
            SanityIdlePanicLoopRules.FaintPhaseDuration
        );
        Assert.Equal(TimeSpan.FromMilliseconds(1800), SanityIdlePanicLoopRules.StartledPhaseDuration);
    }

    [Fact]
    public void Idle_accumulator_reaches_threshold_after_one_uninterrupted_second()
    {
        var rules = new SanityIdlePanicLoopRules();
        Assert.False(rules.IdleThresholdReached);

        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            rules.ObserveIdle(TimeSpan.FromMilliseconds(500), interrupted: false)
        );
        Assert.False(rules.IdleThresholdReached);

        Assert.Equal(
            SanityIdlePanicLoopRules.IdleThreshold,
            rules.ObserveIdle(TimeSpan.FromMilliseconds(500), interrupted: false)
        );
        Assert.True(rules.IdleThresholdReached);

        // 达到阈值后保持饱和，继续累计不再增长。
        Assert.Equal(
            SanityIdlePanicLoopRules.IdleThreshold,
            rules.ObserveIdle(TimeSpan.FromMilliseconds(500), interrupted: false)
        );
        Assert.True(rules.IdleThresholdReached);
    }

    [Fact]
    public void Idle_accumulator_resets_on_interruption_and_negative_elapsed()
    {
        var rules = new SanityIdlePanicLoopRules();
        rules.ObserveIdle(TimeSpan.FromMilliseconds(900), interrupted: false);

        Assert.Equal(
            TimeSpan.Zero,
            rules.ObserveIdle(TimeSpan.FromMilliseconds(50), interrupted: true)
        );
        Assert.False(rules.IdleThresholdReached);

        // 负时长等价于中断：清零且不累计。
        rules.ObserveIdle(TimeSpan.FromMilliseconds(900), interrupted: false);
        Assert.Equal(
            TimeSpan.Zero,
            rules.ObserveIdle(TimeSpan.FromMilliseconds(-1), interrupted: false)
        );
        Assert.False(rules.IdleThresholdReached);

        // ResetIdle 手动清零。
        rules.ObserveIdle(TimeSpan.FromSeconds(2), interrupted: false);
        Assert.True(rules.IdleThresholdReached);
        rules.ResetIdle();
        Assert.False(rules.IdleThresholdReached);
    }

    [Fact]
    public void Idle_threshold_is_one_second_and_independent_of_phase_machine()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SanityIdlePanicLoopRules.IdleThreshold);

        // 相位机与待机累计互不干扰：Start/Cancel 不清待机计时，ResetIdle 不动相位。
        var rules = new SanityIdlePanicLoopRules();
        rules.ObserveIdle(TimeSpan.FromSeconds(2), interrupted: false);
        rules.Start();
        Assert.True(rules.IdleThresholdReached);
        Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
        rules.ResetIdle();
        Assert.False(rules.IdleThresholdReached);
        Assert.Equal(SanityIdlePanicPhase.Faint, rules.Phase);
    }

    [Theory]
    [InlineData("using-tool")]
    [InlineData("using-slingshot")]
    [InlineData("eating")]
    public void Action_owned_interruptions_preserve_the_vanilla_animation(string reason)
    {
        Assert.False(
            SanityIdlePanicInterruptionPolicy.ShouldReleasePlayerAnimation(reason)
        );
    }

    [Theory]
    [InlineData("moving")]
    [InlineData("panic.interrupted.sanity-recovered")]
    [InlineData("panic.interrupted.day-ending")]
    public void Non_action_interruptions_release_the_panic_animation(string reason)
    {
        Assert.True(SanityIdlePanicInterruptionPolicy.ShouldReleasePlayerAnimation(reason));
    }
}
