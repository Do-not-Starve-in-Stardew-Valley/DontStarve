#nullable enable

using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerWarningRulesTests
{
    [Fact]
    public void ExactThresholdDoesNotEnterLowRegion()
    {
        var result = HungerWarningRules.Evaluate(50d, 150d, false);

        Assert.False(result.IsLow);
        Assert.False(result.ShouldWarn);
    }

    [Fact]
    public void FallingBelowOneThirdWarnsOnlyOnTheEntryEdge()
    {
        var entry = HungerWarningRules.Evaluate(49.999d, 150d, false);
        var continued = HungerWarningRules.Evaluate(40d, 150d, entry.IsLow);

        Assert.True(entry.IsLow);
        Assert.True(entry.ShouldWarn);
        Assert.True(continued.IsLow);
        Assert.False(continued.ShouldWarn);
    }

    [Fact]
    public void RecoveryToThresholdArmsTheNextEntryWarning()
    {
        var recovered = HungerWarningRules.Evaluate(50d, 150d, true);
        var reentered = HungerWarningRules.Evaluate(49d, 150d, recovered.IsLow);

        Assert.False(recovered.IsLow);
        Assert.False(recovered.ShouldWarn);
        Assert.True(reentered.IsLow);
        Assert.True(reentered.ShouldWarn);
    }
}
