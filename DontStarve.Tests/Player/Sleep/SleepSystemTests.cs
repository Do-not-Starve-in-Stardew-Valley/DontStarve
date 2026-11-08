using System;
using DontStarve.Player.Sleep;
using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepSystemTests
{
    private const string PlayerKey = "123";
    private const string SessionId = "session-a";

    [Fact]
    public void Realtime_and_blackout_batch_use_the_same_minute_rules()
    {
        var snapshot = Snapshot(
            mode: SleepMode.Voluntary,
            hunger: 10d,
            sanity: 100d,
            health: 10
        );

        var realtime = SleepRules.SettleRealtime(snapshot, 600, 610);
        var batch = SleepRules.SettleBlackoutBatch(snapshot, 600, 610);

        Assert.Equal(realtime, batch);
        Assert.Equal(10, realtime.ProcessedMinutes);
        Assert.Equal(3d, realtime.SanityRecovered, 12);
        Assert.Equal(0.52d, realtime.NaturalHungerConsumed, 12);
        Assert.Equal(3d, realtime.HungerPaidForRecovery, 12);
        Assert.Equal(6.48d, realtime.Hunger, 12);
    }

    [Fact]
    public void Blackout_batch_starts_from_last_sleep_point_and_does_not_replay_realtime_minutes()
    {
        var snapshot = Snapshot(
            mode: SleepMode.Voluntary,
            hunger: 10d,
            sanity: 100d,
            health: 10
        );

        var realtime = SleepRules.SettleRealtime(snapshot, 600, 605);
        var blackout = SleepRules.SettleBlackoutBatch(
            snapshot with
            {
                Hunger = realtime.Hunger,
                Sanity = snapshot.Sanity + realtime.SanityRecovered,
                Health = realtime.Health,
            },
            605,
            610
        );

        Assert.Equal(5, blackout.ProcessedMinutes);
        Assert.Equal(0.26d, blackout.NaturalHungerConsumed, 12);
        Assert.Equal(1.5d, blackout.SanityRecovered, 12);
        Assert.Equal(6.48d, blackout.Hunger, 12);
    }

    [Theory]
    [InlineData((int)SleepMode.Voluntary, true)]
    [InlineData((int)SleepMode.ExhaustionPassOut, false)]
    [InlineData((int)SleepMode.TimeLimitPassOut, false)]
    public void Only_voluntary_sleep_applies_sleep_recovery(
        int modeValue,
        bool recoveryExpected
    )
    {
        var mode = (SleepMode)modeValue;
        var result = SleepRules.SettleRealtime(
            Snapshot(mode, hunger: 10d, sanity: 100d, health: 10),
            600,
            601
        );

        Assert.Equal(recoveryExpected ? 0.3d : 0d, result.SanityRecovered, 12);
        Assert.Equal(recoveryExpected ? 0.3d : 0d, result.HungerPaidForRecovery, 12);
    }

    [Fact]
    public void Hunger_shortage_stops_paid_sanity_recovery_after_natural_loss()
    {
        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: 0.1d,
                sanity: 100d,
                health: 10
            ),
            600,
            601
        );

        Assert.Equal(0d, result.Hunger, 12);
        Assert.Equal(0.048d, result.SanityRecovered, 12);
        Assert.Equal(0.048d, result.HungerPaidForRecovery, 12);
        Assert.Equal(0, result.HungerDamage);
    }

    [Fact]
    public void Full_sanity_does_not_charge_hunger()
    {
        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: 10d,
                sanity: 200d,
                health: 10
            ),
            600,
            601
        );

        Assert.Equal(0d, result.SanityRecovered, 12);
        Assert.Equal(0d, result.HungerPaidForRecovery, 12);
        Assert.Equal(9.948d, result.Hunger, 12);
    }

    [Theory]
    [InlineData(false, 0d, false)]
    [InlineData(false, 10d, false)]
    [InlineData(true, 0d, true)]
    [InlineData(true, 10d, false)]
    public void Sleep_hunger_warning_requires_hunger_system_and_predicted_zero(
        bool hungerEnabled,
        double predictedHunger,
        bool expectedWarning
    )
    {
        var balance = SleepRecoveryBalance.Default with { HungerEnabled = hungerEnabled };
        var prediction = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: predictedHunger,
                sanity: 200d,
                health: 10
            ) with { RecoveryBalance = balance },
            600,
            600
        );

        Assert.Equal(
            expectedWarning,
            SleepRules.ShouldConfirmHungerWarning(balance, prediction)
        );
    }

    [Fact]
    public void Invalid_bed_values_fail_safe_to_neutral_recovery_inputs()
    {
        var balance = new SleepRecoveryBalance(
            Enabled: true,
            NaturalHungerPerMinute: 0.052d,
            BaseSanityRecoveryPerMinute: 0.3d,
            BedSanityMultiplier: double.NaN,
            HungerCostPerSanity: 0d
        );

        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: 10d,
                sanity: 100d,
                health: 10
            ) with { RecoveryBalance = balance },
            600,
            601
        );

        Assert.Equal(0.3d, result.SanityRecovered, 12);
        Assert.Equal(0.3d, result.HungerPaidForRecovery, 12);
    }

    [Fact]
    public void Bed_multiplier_and_hunger_cost_ratio_apply_only_to_sleep_recovery()
    {
        var balance = new SleepRecoveryBalance(
            Enabled: true,
            NaturalHungerPerMinute: 0.052d,
            BaseSanityRecoveryPerMinute: 0.3d,
            BedSanityMultiplier: 2d,
            HungerCostPerSanity: 2d
        );

        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: 10d,
                sanity: 100d,
                health: 10
            ) with { RecoveryBalance = balance },
            600,
            601
        );

        Assert.Equal(0.6d, result.SanityRecovered, 12);
        Assert.Equal(1.2d, result.HungerPaidForRecovery, 12);
        Assert.Equal(8.748d, result.Hunger, 12);
        Assert.Equal(0.052d, result.NaturalHungerConsumed, 12);
    }

    [Fact]
    public void Disabled_balance_keeps_recovery_but_does_not_charge_hunger()
    {
        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.Voluntary,
                hunger: 10d,
                sanity: 100d,
                health: 10
            ) with { RecoveryBalance = SleepRecoveryBalance.Disabled },
            600,
            601
        );

        Assert.Equal(0.052d, result.NaturalHungerConsumed, 12);
        Assert.Equal(9.948d, result.Hunger, 12);
        Assert.Equal(0.3d, result.SanityRecovered, 12);
        Assert.Equal(0d, result.HungerPaidForRecovery, 12);
    }

    [Theory]
    [InlineData((int)HungerDamageMode.Default, 1)]
    [InlineData((int)HungerDamageMode.NonLethal, 2)]
    [InlineData((int)HungerDamageMode.Disabled, 10)]
    public void Hunger_damage_modes_are_applied_only_on_the_minute_after_zero(
        int damageModeValue,
        int expectedHealth
    )
    {
        var damageMode = (HungerDamageMode)damageModeValue;
        var snapshot = Snapshot(
            mode: SleepMode.Voluntary,
            hunger: 0.01d,
            sanity: 100d,
            health: 10
        ) with { MaximumHealth = 10 };

        var result = SleepRules.SettleRealtime(snapshot, 600, 610, damageMode);

        Assert.Equal(expectedHealth, result.Health);
        Assert.Equal(10 - expectedHealth, result.HungerDamage);
    }

    [Fact]
    public void Pass_out_modes_keep_natural_hunger_but_do_not_create_sleep_receipt_recovery()
    {
        var result = SleepRules.SettleRealtime(
            Snapshot(
                mode: SleepMode.ExhaustionPassOut,
                hunger: 10d,
                sanity: 100d,
                health: 10
            ),
            600,
            603
        );

        Assert.Equal(3, result.ProcessedMinutes);
        Assert.Equal(0.156d, result.NaturalHungerConsumed, 12);
        Assert.Equal(0d, result.SanityRecovered, 12);
        Assert.Equal(0d, result.HungerPaidForRecovery, 12);
    }

    private static SleepSnapshot Snapshot(
        SleepMode mode,
        double hunger,
        double sanity,
        int health
    )
    {
        return new SleepSnapshot(
            PlayerKey,
            SessionId,
            "settlement-a",
            mode,
            600,
            600,
            610,
            "bed-a",
            hunger,
            150d,
            sanity,
            200d,
            health,
            10,
            SleepRecoveryBalance.Default
        );
    }

}
