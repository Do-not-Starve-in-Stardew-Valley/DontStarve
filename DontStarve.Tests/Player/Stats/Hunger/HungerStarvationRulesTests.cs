#nullable enable

using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerStarvationRulesTests
{
    [Fact]
    public void CrossingIntoZeroDoesNotDamageOnTheCrossingMinute()
    {
        var step = HungerStarvationRules.Evaluate(
            wasStarving: false,
            boundedHunger: 0d
        );

        Assert.True(step.IsStarving);
        Assert.False(step.DamageDue);
    }

    [Fact]
    public void EveryFollowingZeroMinuteRequiresOneDamageEvaluation()
    {
        var first = HungerStarvationRules.Evaluate(true, 0d);
        var second = HungerStarvationRules.Evaluate(first.IsStarving, 0d);

        Assert.True(first.DamageDue);
        Assert.True(second.DamageDue);
    }

    [Fact]
    public void PositiveHungerLeavesStarvationAndStopsDamage()
    {
        var step = HungerStarvationRules.Evaluate(true, 1d);

        Assert.False(step.IsStarving);
        Assert.False(step.DamageDue);
    }

    [Fact]
    public void WakingAtZeroStartsDamageOnTheFirstFollowingHungerMinute()
    {
        var step = HungerStarvationRules.ResumeAfterSleep(0d);

        Assert.True(step.IsStarving);
        Assert.True(step.DamageDue);
    }

    [Fact]
    public void WakingWithPositiveHungerLeavesStarvation()
    {
        var step = HungerStarvationRules.ResumeAfterSleep(0.001d);

        Assert.False(step.IsStarving);
        Assert.False(step.DamageDue);
    }
}
