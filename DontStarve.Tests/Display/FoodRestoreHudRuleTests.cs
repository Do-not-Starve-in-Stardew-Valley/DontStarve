using System;
using DontStarve.Display;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Display;

/// <summary>
/// 进食恢复弹窗纯规则单测：对齐原版 Farmer.doneEating 的 HUD 提示语义
/// （只有结算后 &gt; 结算前才弹、弹实际增量、double 按 (int) 截断、毒食物不弹）。
/// </summary>
public sealed class FoodRestoreHudRuleTests
{
    [Fact]
    public void Positive_delta_shows_and_reports_the_truncated_int_like_vanilla()
    {
        var decision = FoodRestoreHudRule.Decide(
            FoodRestoreHudGate.LocalFood,
            previousValue: 100d,
            currentValue: 140d
        );

        Assert.True(decision.Show);
        Assert.Equal(40, decision.DisplayedAmount);
    }

    [Fact]
    public void Zero_delta_does_not_show()
    {
        Assert.False(
            FoodRestoreHudRule
                .Decide(FoodRestoreHudGate.LocalFood, 120d, 120d)
                .Show
        );
    }

    [Fact]
    public void Negative_delta_poison_food_does_not_show()
    {
        Assert.False(
            FoodRestoreHudRule
                .Decide(FoodRestoreHudGate.LocalFood, 120d, 100d)
                .Show
        );
    }

    [Fact]
    public void Clamped_settlement_shows_the_actual_gain_not_the_food_value()
    {
        // 原版 doneEating 语义：先夹上限再比较，弹实际增量。
        // 130/150 吃 +40 → 结算后 150 → 弹 +20 而不是 +40。
        var decision = FoodRestoreHudRule.Decide(
            FoodRestoreHudGate.LocalFood,
            previousValue: 130d,
            currentValue: 150d
        );

        Assert.True(decision.Show);
        Assert.Equal(20, decision.DisplayedAmount);
    }

    [Fact]
    public void Fractional_delta_is_truncated_by_vanilla_int_cast()
    {
        var decision = FoodRestoreHudRule.Decide(
            FoodRestoreHudGate.LocalFood,
            previousValue: 10.5d,
            currentValue: 12.9d
        );

        Assert.True(decision.Show);
        Assert.Equal(2, decision.DisplayedAmount);
    }

    [Fact]
    public void Tiny_delta_truncated_to_zero_still_shows_like_vanilla_plus_zero()
    {
        // 显式原版对齐边界：微小增量按 (int) 截断为 0 时原版同样弹一条 "+0"，
        // 这里照弹，不做额外过滤；修改本断言等于偏离原版行为。
        var decision = FoodRestoreHudRule.Decide(
            FoodRestoreHudGate.LocalFood,
            previousValue: 10d,
            currentValue: 10.4d
        );

        Assert.True(decision.Show);
        Assert.Equal(0, decision.DisplayedAmount);
    }

    [Fact]
    public void Non_local_player_settlement_never_shows()
    {
        // 主机替远程农场工结算不弹：农场工的变化只弹在农场工自己的屏幕上。
        Assert.False(
            FoodRestoreHudRule
                .Decide(new FoodRestoreHudGate(false, true), 100d, 140d)
                .Show
        );
    }

    [Fact]
    public void Non_food_settlement_never_shows()
    {
        // 非 Food 来源/种类（Overtime 衰减、Debug 写入等）不弹。
        Assert.False(
            FoodRestoreHudRule
                .Decide(new FoodRestoreHudGate(true, false), 100d, 140d)
                .Show
        );
    }

    [Theory]
    [InlineData(double.NaN, 120d)]
    [InlineData(120d, double.NaN)]
    [InlineData(double.PositiveInfinity, 120d)]
    [InlineData(120d, double.PositiveInfinity)]
    public void Non_finite_settlement_values_fail_closed_without_showing(
        double previousValue,
        double currentValue
    )
    {
        // 缺基线（NaN）或快照异常时静默不弹：弹窗只是反馈，不能拿坏数据弹误导值。
        Assert.False(
            FoodRestoreHudRule
                .Decide(FoodRestoreHudGate.LocalFood, previousValue, currentValue)
                .Show
        );
    }

    [Fact]
    public void Snapshot_delta_fields_default_to_unknown_for_old_hosts_fail_safe()
    {
        // wire 契约：旧主机消息缺附加字段时反序列化为默认值（Unknown=0），
        // 接收端必须按「不可弹」处理（对应规则门控 IsFoodSettlement=false）。
        Assert.Equal(HungerChangeKind.Unknown, new HungerSnapshotMessage().DeltaKind);
        Assert.Equal(SanityChangeSource.Unknown, new SanitySnapshotMessage().DeltaSource);
    }
}
