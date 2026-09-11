using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowCheckScheduleTests
{
    [Fact]
    public void Each_shadow_starts_at_generation_plus_ten_and_then_repeats_every_ten()
    {
        var schedule = new HostileShadowCheckSchedule<long>(10);
        schedule.Register(1, 100);

        Assert.False(schedule.TryConsumeIfDue(1, 109, 100));
        Assert.True(schedule.TryConsumeIfDue(1, 110, 100));
        Assert.False(schedule.TryConsumeIfDue(1, 119, 100));
        Assert.True(schedule.TryConsumeIfDue(1, 120, 100));
    }

    [Fact]
    public void Shadows_generated_at_different_times_keep_independent_check_times()
    {
        var schedule = new HostileShadowCheckSchedule<long>(10);
        schedule.Register(1, 100);
        schedule.Register(2, 105);

        Assert.False(schedule.TryConsumeIfDue(1, 109, 100));
        Assert.True(schedule.TryConsumeIfDue(1, 110, 100));
        Assert.False(schedule.TryConsumeIfDue(2, 114, 105));
        Assert.True(schedule.TryConsumeIfDue(2, 115, 105));
    }

    [Fact]
    public void Same_minute_shadows_each_consume_their_own_probability_attempt()
    {
        var schedule = new HostileShadowCheckSchedule<long>(10);
        schedule.Register(1, 100);
        schedule.Register(2, 100);

        Assert.True(schedule.TryConsumeIfDue(1, 110, 100));
        Assert.True(schedule.TryConsumeIfDue(2, 110, 100));
        Assert.Equal(120, schedule.TryGetNextCheck(1, out var first) ? first : -1);
        Assert.Equal(120, schedule.TryGetNextCheck(2, out var second) ? second : -1);
    }

    [Fact]
    public void A_large_time_jump_consumes_once_and_never_replays_old_rounds()
    {
        var schedule = new HostileShadowCheckSchedule<long>(10);
        schedule.Register(1, 100);

        Assert.True(schedule.TryConsumeIfDue(1, 500, 100));
        Assert.False(schedule.TryConsumeIfDue(1, 500, 100));
        Assert.False(schedule.TryConsumeIfDue(1, 509, 100));
        Assert.True(schedule.TryConsumeIfDue(1, 510, 100));
    }

    [Fact]
    public void Time_rewind_rebases_existing_shadows_to_now_plus_ten()
    {
        var schedule = new HostileShadowCheckSchedule<long>(10);
        schedule.Register(1, 100);
        schedule.Register(2, 105);
        schedule.Rebase(new[] { 1L, 2L }, 60);

        Assert.False(schedule.TryConsumeIfDue(1, 69, 100));
        Assert.False(schedule.TryConsumeIfDue(2, 69, 105));
        Assert.True(schedule.TryConsumeIfDue(1, 70, 100));
        Assert.True(schedule.TryConsumeIfDue(2, 70, 105));
    }
}
