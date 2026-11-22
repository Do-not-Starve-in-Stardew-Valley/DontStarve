using System;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

public sealed class HealthLedgerRulesTests
{
    [Theory]
    [InlineData(42.75d, 100, true, 42, 0.75d, false)]
    [InlineData(125.5d, 100, true, 100, 0d, false)]
    [InlineData(42.75d, 100, false, 42, 0d, false)]
    [InlineData(0d, 100, true, 0, 0d, true)]
    public void Debug_value_sets_health_and_remainder_without_bypassing_the_ledger(
        double requestedValue,
        int maximum,
        bool decimalLedgerEnabled,
        int expectedHealth,
        double expectedRemainder,
        bool expectedLethal
    )
    {
        var result = HealthLedgerRules.SetDebugValue(
            requestedValue,
            maximum,
            decimalLedgerEnabled
        );

        Assert.Equal(expectedHealth, result.NewHealth);
        Assert.Equal(expectedRemainder, result.NewRemainder, precision: 10);
        Assert.Equal(expectedLethal, result.IsLethal);
    }

    [Fact]
    public void Fractional_damage_lands_integer_part_and_keeps_remainder()
    {
        // 用户确认算例：100 血吃 22.5 伤害 → 78 血、余 -0.5（真值 77.5）。
        var result = HealthLedgerRules.ApplyEvent(100, 0d, -22.5d, 100);

        Assert.Equal((78, -0.5d, false), result);
    }

    [Fact]
    public void Consecutive_fractional_damage_carries_the_second_unit()
    {
        // 用户确认算例：连吃 22.5 + 10.6 → 第二次余数累计跨过 -1，
        // 进位补扣 1 → 67 血、余 -0.1（真值 66.9 = 100 - 22.5 - 10.6）。
        var first = HealthLedgerRules.ApplyEvent(100, 0d, -22.5d, 100);
        var second = HealthLedgerRules.ApplyEvent(
            first.NewHealth,
            first.NewRemainder,
            -10.6d,
            100
        );

        Assert.Equal(67, second.NewHealth);
        Assert.Equal(-0.1d, second.NewRemainder, precision: 12);
        Assert.False(second.IsLethal);
    }

    [Fact]
    public void Remainder_carry_grants_one_health_point()
    {
        // 0.7 余数 + 0.5 效果 → 余数跨过 +1，立即兑现 1 点（50 → 51，余 0.2）。
        var result = HealthLedgerRules.ApplyEvent(50, 0.7d, 0.5d, 100);

        Assert.Equal(51, result.NewHealth);
        Assert.Equal(0.2d, result.NewRemainder, precision: 12);
        Assert.False(result.IsLethal);
    }

    [Fact]
    public void Carry_beyond_maximum_is_absorbed_by_the_health_cap()
    {
        // 满血时进位被上限钳掉，余数随真值域钳制归 0，不出现假溢出。
        var result = HealthLedgerRules.ApplyEvent(100, 0.7d, 0.5d, 100);

        Assert.Equal((100, 0d, false), result);
    }

    [Fact]
    public void Healing_at_full_health_clamps_health_and_zeroes_remainder()
    {
        var result = HealthLedgerRules.ApplyEvent(100, 0.5d, 3.7d, 100);

        Assert.Equal((100, 0d, false), result);
    }

    [Fact]
    public void Exact_zero_true_value_is_lethal()
    {
        // 决议 4：真值 ≤ 0 = 死亡，恰好 0 也算死亡（血 0、余 0）。
        var result = HealthLedgerRules.ApplyEvent(10, 0d, -10d, 100);

        Assert.Equal((0, 0d, true), result);
    }

    [Fact]
    public void Negative_carry_into_zero_health_is_lethal()
    {
        var result = HealthLedgerRules.ApplyEvent(78, -0.5d, -77.6d, 100);

        Assert.Equal((0, 0d, true), result);
    }

    [Fact]
    public void Large_overshoot_is_lethal_and_never_resurrects()
    {
        var result = HealthLedgerRules.ApplyEvent(5, 0d, -250.5d, 100);

        Assert.Equal((0, 0d, true), result);
    }

    [Fact]
    public void Survives_with_positive_remainder_at_zero_blood()
    {
        // 真值 = 血 + 余数 > 0 即存活：血 0 + 余 0.5，不判死（显示层口径本轮不动 HUD）。
        var result = HealthLedgerRules.ApplyEvent(3, 0.5d, -3d, 100);

        Assert.Equal(0, result.NewHealth);
        Assert.Equal(0.5d, result.NewRemainder, precision: 12);
        Assert.False(result.IsLethal);
    }

    [Fact]
    public void Fractional_heal_at_zero_health_keeps_negative_remainder_in_range()
    {
        var result = HealthLedgerRules.ApplyEvent(0, -0.9d, 2d, 100);

        Assert.Equal(2, result.NewHealth);
        Assert.Equal(-0.9d, result.NewRemainder, precision: 12);
        Assert.False(result.IsLethal);
    }

    [Fact]
    public void Fractional_damage_above_maximum_is_absorbed_by_the_cap()
    {
        var result = HealthLedgerRules.ApplyEvent(100, 0d, 3.7d, 100);

        Assert.Equal((100, 0d, false), result);
    }

    [Fact]
    public void Non_finite_delta_is_a_neutral_event()
    {
        // 脏输入防御：NaN/Infinity 视为 0 效果，不得污染或清空既有余数。
        var result = HealthLedgerRules.ApplyEvent(50, 0.25d, double.NaN, 100);

        Assert.Equal((50, 0.25d, false), result);
        var infinity = HealthLedgerRules.ApplyEvent(50, 0.25d, double.PositiveInfinity, 100);

        Assert.Equal((50, 0.25d, false), infinity);
    }

    [Fact]
    public void Non_finite_remainder_input_is_treated_as_zero()
    {
        var result = HealthLedgerRules.ApplyEvent(50, double.NaN, -2.5d, 100);

        Assert.Equal((48, -0.5d, false), result);
    }

    [Fact]
    public void Out_of_range_start_health_is_clamped_at_write_time()
    {
        // 外部 mod 把血推到 150（上限 100）：结算时写血钳制吸收，不拉回外部改动前的值。
        var result = HealthLedgerRules.ApplyEvent(150, 0d, -45d, 100);

        Assert.Equal((100, 0d, false), result);
    }

    [Fact]
    public void Integer_mode_truncates_toward_zero_and_caps()
    {
        Assert.Equal((78, false), HealthLedgerRules.ApplyIntegerEvent(100, -22.5d, 100));
        Assert.Equal((68, false), HealthLedgerRules.ApplyIntegerEvent(78, -10.6d, 100));
        Assert.Equal((1, false), HealthLedgerRules.ApplyIntegerEvent(1, -0.9d, 100));
        Assert.Equal((100, false), HealthLedgerRules.ApplyIntegerEvent(100, 500.4d, 100));
        Assert.Equal((0, true), HealthLedgerRules.ApplyIntegerEvent(5, -22.5d, 100));
        Assert.Equal((0, true), HealthLedgerRules.ApplyIntegerEvent(0, -1d, 100));
    }

    [Fact]
    public void Integer_mode_ignores_non_finite_delta()
    {
        Assert.Equal((100, false), HealthLedgerRules.ApplyIntegerEvent(100, double.NaN, 100));
    }

    [Theory]
    [InlineData(0.9, 100, 100, 0)]
    [InlineData(-0.9, 0, 100, 0)]
    [InlineData(-0.5, 50, 100, -0.5)]
    [InlineData(0.3, 99, 100, 0.3)]
    [InlineData(-5, 3, 100, -3)]
    [InlineData(2.5, 99, 100, 1)]
    public void Clamp_remainder_keeps_true_value_inside_zero_to_maximum(
        double remainder,
        int health,
        int maxHealth,
        double expected
    )
    {
        Assert.Equal(expected, HealthLedgerRules.ClampRemainder(remainder, health, maxHealth), precision: 12);
    }

    [Fact]
    public void Clamp_remainder_treats_non_finite_input_as_zero()
    {
        Assert.Equal(0d, HealthLedgerRules.ClampRemainder(double.NaN, 50, 100), precision: 12);
        Assert.Equal(
            0d,
            HealthLedgerRules.ClampRemainder(double.NegativeInfinity, 50, 100),
            precision: 12
        );
    }

    [Fact]
    public void Format_and_parse_round_trip_in_invariant_culture()
    {
        const double value = -0.09999999999999964;

        Assert.True(
            HealthLedgerRules.TryParseRemainder(HealthLedgerRules.FormatRemainder(value), out var parsed)
        );
        Assert.Equal(value, parsed, precision: 15);
    }

    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("-0.5", -0.5)]
    [InlineData("1E-05", 0.00001)]
    public void Parse_accepts_invariant_culture_numbers(string raw, double expected)
    {
        Assert.True(HealthLedgerRules.TryParseRemainder(raw, out var value));
        Assert.Equal(expected, value, precision: 12);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0,5")]
    [InlineData(null)]
    public void Parse_rejects_invalid_input_without_throwing(string? raw)
    {
        Assert.False(HealthLedgerRules.TryParseRemainder(raw, out var value));
        Assert.Equal(0d, value, precision: 12);
    }
}
