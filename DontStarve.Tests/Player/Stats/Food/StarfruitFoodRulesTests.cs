using DontStarve.Player.Stats.Food;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class StarfruitFoodRulesTests
{
    [Theory]
    [InlineData("434", true)]
    [InlineData("434 ", false)]
    [InlineData("348", false)]
    [InlineData(null, false)]
    public void OnlyTheVanillaStarfruitItemIdIsSpecial(string? itemId, bool expected)
    {
        Assert.Equal(expected, StarfruitFoodRules.IsStarfruit(itemId));
    }

    [Theory]
    [InlineData(50, 200, 150)]
    [InlineData(200, 200, 0)]
    [InlineData(250, 200, 0)]
    [InlineData(-1, 200, 201)]
    public void FillDeltaUsesCurrentMaximum(double current, double maximum, double expected)
    {
        Assert.Equal(expected, StarfruitFoodRules.GetFillDelta(current, maximum));
    }

    [Fact]
    public void InvalidMaximumFailsClosed()
    {
        Assert.Equal(0, StarfruitFoodRules.GetFillDelta(20, 0));
        Assert.Equal(0, StarfruitFoodRules.GetFillDelta(double.NaN, 200));
        Assert.Equal(0, StarfruitFoodRules.GetFillDelta(20, double.PositiveInfinity));
    }
}
