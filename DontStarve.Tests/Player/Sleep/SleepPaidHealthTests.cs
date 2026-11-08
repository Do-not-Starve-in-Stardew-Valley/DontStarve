using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepPaidHealthTests
{
    [Fact]
    public void FullSanityStillPaysForMissingHealthDuringRealtimeWaiting()
    {
        var result = SleepRules.SettleRealtime(Snapshot(10, 200, 20), 600, 601);

        Assert.Equal(9.648, result.Hunger, 10);
        Assert.Equal(200, result.Sanity, 10);
        Assert.Equal(20.6, result.Health, 10);
    }

    [Fact]
    public void HealthAndSanityShareOnePaymentAndStopWhenBothAreFull()
    {
        var result = SleepRules.SettleRealtime(Snapshot(10, 198, 98), 600, 610);

        Assert.Equal(7.48, result.Hunger, 10);
        Assert.Equal(200, result.Sanity, 10);
        Assert.Equal(100d, result.Health, 10);
    }

    [Fact]
    public void PartialPaymentRestoresTwiceTheHealthWithoutChargingTwice()
    {
        var result = SleepRules.SettleRealtime(Snapshot(0.2, 100, 20), 600, 601);

        Assert.Equal(0, result.Hunger, 10);
        Assert.Equal(100.148, result.Sanity, 10);
        Assert.Equal(20.296, result.Health, 10);
        Assert.Equal(0, result.HungerDamage);
    }

    [Fact]
    public void FullHealthDoesNotStopPaidSanityRecovery()
    {
        var result = SleepRules.SettleRealtime(Snapshot(10, 100, 100), 600, 601);

        Assert.Equal(9.648, result.Hunger, 10);
        Assert.Equal(100.3, result.Sanity, 10);
        Assert.Equal(100d, result.Health, 10);
    }

    [Fact]
    public void BothFullPayOnlyNaturalHunger()
    {
        var result = SleepRules.SettleRealtime(Snapshot(10, 200, 100), 600, 601);

        Assert.Equal(9.948, result.Hunger, 10);
        Assert.Equal(100d, result.Health, 10);
    }

    [Fact]
    public void DisabledBalanceDoesNotGiveInstantFullHealthWhileOthersAreAwake()
    {
        var input = Snapshot(10, 100, 20) with
        {
            RecoveryBalance = SleepRecoveryBalance.Disabled,
        };
        var result = SleepRules.SettleRealtime(input, 600, 610);

        Assert.Equal(20d, result.Health, 10);
        Assert.Equal(103, result.Sanity, 10);
        Assert.Equal(9.48, result.Hunger, 10);
    }

    private static SleepSnapshot Snapshot(double hunger, double sanity, int health) =>
        new("42", "session", "night", SleepMode.Voluntary,
            600, 600, 610, "bed", hunger, 150, sanity, 200, health, 100,
            SleepRecoveryBalance.Default);
}
