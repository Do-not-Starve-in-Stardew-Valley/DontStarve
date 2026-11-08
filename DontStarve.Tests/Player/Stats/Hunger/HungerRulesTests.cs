#nullable enable

using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerRulesTests
{
    [Fact]
    public void OneValidMinuteConsumesPointZeroFiveTwo()
    {
        Assert.Equal(0.052d, HungerRules.CalculateNaturalLoss(1), 12);
    }

    [Fact]
    public void OneDayConsumesPointSeventyFourPointEightEight()
    {
        Assert.Equal(74.88d, HungerRules.CalculateNaturalLoss(1_440), 12);
    }

    [Fact]
    public void CrossingZeroDoesNotDamageUntilTheFollowingMinute()
    {
        var crossing = HungerRules.ApplyNaturalMinute(0.02d);

        Assert.True(crossing.RawCurrent < 0d);
        Assert.Equal(0d, crossing.BoundedCurrent);
        Assert.False(crossing.DamageDue);

        var following = HungerRules.ApplyNaturalMinute(crossing.RawCurrent);

        Assert.True(following.DamageDue);
    }

    [Fact]
    public void FixedEquipmentRecoveryIsAppliedAfterNaturalLossAndClamped()
    {
        var result = HungerRules.ApplyNaturalMinute(
            current: 10d,
            maximum: 10d,
            consumptionMultiplier: 1d,
            fixedRecovery: 1d
        );

        Assert.Equal(10d - 0.052d + 1d, result.RawCurrent, 12);
        Assert.Equal(10d, result.BoundedCurrent, 12);
        Assert.False(result.DamageDue);
    }

    [Fact]
    public void RuntimeOvershootIsPreservedButBoundaryValueIsClampedToZero()
    {
        var result = HungerRules.ApplyNaturalMinute(0d);

        Assert.Equal(-0.052d, result.RawCurrent, 12);
        Assert.Equal(0d, result.BoundedCurrent);
        Assert.Equal(0d, HungerRules.ClampForBoundary(-4d));
    }

    [Fact]
    public void ReviveRestoresTwoThirdsOfCurrentMaximum()
    {
        Assert.Equal(100d, HungerRules.CalculateReviveValue(150d), 12);
    }

    [Fact]
    public void BoundaryValueCannotExceedCurrentMaximum()
    {
        Assert.Equal(150d, HungerRules.ClampForBoundary(200d, 150d));
    }

    [Theory]
    [InlineData(0, 20, 20, 1, 19, 4)]
    [InlineData(1, 21, 100, 1, 20, 20)]
    [InlineData(1, 20, 100, 0, 20, 20)]
    [InlineData(2, 20, 100, 0, 20, 20)]
    public void DamageModesProduceIndependentPureResults(
        int modeValue,
        int currentHealth,
        int maximumHealth,
        int expectedDamage,
        int expectedRemainingHealth,
        int expectedFloor
    )
    {
        var result = HungerRules.CalculateDamage(
            (HungerDamageMode)modeValue,
            currentHealth,
            maximumHealth
        );

        Assert.Equal(expectedDamage, result.Damage);
        Assert.Equal(expectedRemainingHealth, result.RemainingHealth);
        Assert.Equal(expectedFloor, result.NonLethalFloor);
    }

    [Fact]
    public void NonLethalFloorUsesCeilingOfTwentyPercent()
    {
        var result = HungerRules.CalculateDamage(HungerDamageMode.NonLethal, 22, 101);

        Assert.Equal(21, result.NonLethalFloor);
        Assert.Equal(21, result.RemainingHealth);
        Assert.Equal(1, result.Damage);
    }

    [Fact]
    public void SleepCostIsAnIndependentOneToOneInput()
    {
        var defaultCost = HungerSleepCostRule.Default;

        Assert.Equal(3d, defaultCost.CalculateHungerCost(3d), 12);
        Assert.Equal(6d, new HungerSleepCostRule(2d).CalculateHungerCost(3d), 12);
        Assert.Equal(74.88d, HungerRules.CalculateNaturalLoss(1_440), 12);
    }
}
