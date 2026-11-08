using System.Text.Json;
using DontStarve.Player.Sleep;
using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepOvernightSettlementTests
{
    [Fact]
    public void BalancedNightUsesPaidBedtimeHealthInsteadOfFreeVanillaRecovery()
    {
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(Input(10, 200, 20), out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var result));

        Assert.Equal(26d, result.Health, 10);
        Assert.Equal(6.48, result.Hunger, 10);
    }

    [Fact]
    public void DisabledBalanceAppliesStarvationToVanillaHealthNotBedtimeHealth()
    {
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(Input(0, 200, 2) with
            { RecoveryBalance = SleepRecoveryBalance.Disabled }, out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var result));

        Assert.Equal(90d, result.Health, 10);
        Assert.Equal(10, result.HungerDamage);
    }

    [Fact]
    public void RepeatedSaveDoesNotChargeTheNightTwice()
    {
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(Input(10, 100, 20), out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var first));
        Assert.True(night.TryFinalize("42", 26, HungerDamageMode.Default, out var second));

        Assert.Equal(first, second);
        Assert.Equal(6.48, second.Hunger, 10);
        Assert.Equal(26d, second.Health, 10);
    }

    [Fact]
    public void ReloadRestoresSavedFinalValuesWithoutReplayingNightOrDependingOnSession()
    {
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(Input(10, 100, 20), out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var final));
        var saved = JsonSerializer.Serialize(new SavedPlayer(final.Hunger, final.Sanity, final.Health));
        var reloaded = JsonSerializer.Deserialize<SavedPlayer>(saved)!;
        var newRuntime = new SleepOvernightSettlement();

        Assert.False(newRuntime.TryFinalize("42", 100, HungerDamageMode.Default, out _));
        Assert.Equal(new SavedPlayer(6.48, 103, 26),
            new SavedPlayer(Math.Round(reloaded.Hunger, 8), Math.Round(reloaded.Sanity, 8), Math.Round(reloaded.Health, 8)));
    }

    [Fact]
    public void RealtimeWaitingIsKeptAndOnlyRemainingBlackoutMinutesAreSettled()
    {
        var input = Input(10, 100, 20);
        var realtime = SleepRules.SettleRealtime(input, 600, 604);
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(input with
        {
            Hunger = realtime.Hunger, Sanity = realtime.Sanity, Health = realtime.Health,
            BlackoutStartMinute = 604,
        }, out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var result));

        Assert.Equal(6, result.ProcessedMinutes);
        Assert.Equal(6.48, result.Hunger, 10);
        Assert.Equal(103, result.Sanity, 10);
        Assert.Equal(26d, result.Health, 10);
    }

    [Fact]
    public void EachPlayerHasTheirOwnBedtimeStateAndFinalization()
    {
        var night = new SleepOvernightSettlement();
        Assert.True(night.TryPrepare(Input(10, 200, 20), out _));
        Assert.True(night.TryPrepare(Input(10, 200, 90) with { PlayerKey = "7" }, out _));
        Assert.True(night.TryFinalize("42", 100, HungerDamageMode.Default, out var first));
        Assert.True(night.TryFinalize("7", 100, HungerDamageMode.Default, out var second));

        Assert.Equal(26d, first.Health, 10);
        Assert.Equal(96d, second.Health, 10);
    }

    private sealed record SavedPlayer(double Hunger, double Sanity, double Health);
    private static SleepSnapshot Input(double hunger, double sanity, double health) =>
        new("42", "session", "night", SleepMode.Voluntary,
            600, 600, 610, "bed", hunger, 150, sanity, 200, health, 100,
            SleepRecoveryBalance.Default);
}
