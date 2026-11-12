using System;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

public sealed class HealthRulesTests
{
    [Theory]
    [InlineData(-50, 22.5)]
    [InlineData(-22, 9.9)]
    [InlineData(0, 0)]
    [InlineData(50, 22.5)]
    public void Negative_food_damage_is_abs_of_stamina_mirror_times_045(
        int staminaMirror,
        double expected
    )
    {
        // 决议 1：负食扣血 = |体力变化| × 0.45（含品质加成的体力镜像值）。
        Assert.Equal(
            expected,
            HealthRules.CalculateNegativeFoodHealthDamage(staminaMirror),
            precision: 12
        );
    }

    [Theory]
    [InlineData(10, 3, 7)]
    [InlineData(-5, 0, -5)]
    [InlineData(12.6, 12, 0.6)]
    [InlineData(0, 0, 0)]
    public void Food_correction_is_json_value_minus_applied_vanilla_delta(
        double jsonValue,
        int appliedVanillaDelta,
        double expected
    )
    {
        // 决议 2 覆盖抵扣：快照差值天然覆盖 生命药水 773 / 星果 434 / 滋补水 349 / 虫肉块 874
        // 特例与原版钳制。
        Assert.Equal(
            expected,
            HealthRules.CalculateFoodCorrection(jsonValue, appliedVanillaDelta),
            precision: 12
        );
    }

    [Theory]
    [InlineData(230, 103.5)]
    [InlineData(50, 22.5)]
    [InlineData(0, 0)]
    public void Knife_before_true_value_is_stamina_mirror_times_045(
        int staminaMirror,
        double expected
    )
    {
        // 决议 2 三轮全线口径：无表正食的刀前真值 = 体力变化 × 0.45，未截断。
        Assert.Equal(
            expected,
            HealthRules.CalculateKnifeBeforeTrueValue(staminaMirror),
            precision: 12
        );
    }

    [Fact]
    public void Knife_before_correction_recovers_the_truncation_remainder()
    {
        // 用户确认算例：体力镜像 230 → 真值 103.5，原版刀 (int)((float)230 × 0.45f) = 103
        //（float 纯截断零随机），修正量 = +0.5，走事件落账进余数。
        var knife = (int)((float)230 * 0.45f);
        Assert.Equal(103, knife);

        var correction = HealthRules.CalculateKnifeBeforeTrueValue(230) - knife;
        Assert.Equal(0.5d, correction, precision: 12);

        // 修正量落账：原版刀已把 50 血抬到 153（max 200），余数 0.5 挂账，真值 153.5。
        var landed = HealthLedgerRules.ApplyEvent(153, 0d, correction, 200);
        Assert.Equal((153, 0.5d, false), landed);
    }

    [Fact]
    public void Food_effects_follow_the_balance_switch_only()
    {
        // 决议 8 门控矩阵：负食扣血 + JSON 食物应用都只听「生命值效果平衡」。
        Assert.True(HealthRules.ShouldApplyFoodEffects(true));
        Assert.False(HealthRules.ShouldApplyFoodEffects(false));
    }

    [Fact]
    public void Equipment_effects_follow_the_dont_starve_health_switch_only()
    {
        // 决议 8 门控矩阵：装备六槽回血/掉血只听「饥荒风格生命值」。
        Assert.True(HealthRules.ShouldApplyEquipmentEffects(true));
        Assert.False(HealthRules.ShouldApplyEquipmentEffects(false));
    }

    [Fact]
    public void Decimal_ledger_follows_the_dont_starve_health_switch_only()
    {
        // 决议 8 门控矩阵：「饥荒风格生命值」关 = 整数模式（无余数账本）。
        Assert.True(HealthRules.UseDecimalLedger(true));
        Assert.False(HealthRules.UseDecimalLedger(false));
    }
}
