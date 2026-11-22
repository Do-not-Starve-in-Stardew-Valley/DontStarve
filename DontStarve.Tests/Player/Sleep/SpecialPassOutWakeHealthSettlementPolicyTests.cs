using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

/// <summary>
/// 非致命昏迷"醒来血量上限"的保存前最终值规则。场景对应原版时序：
/// 玩家在夜里被写入 min(捕获血量, 20%上限) 后，vanilla dayupdate 换日时把 health 满血回填
/// （1.6.15 Farmer.dayupdate），结算必须在 dayupdate 之后写回上限值；写回点先于存档序列化，
/// 存档自然携带，读档即上限血量。
/// </summary>
public sealed class SpecialPassOutWakeHealthSettlementPolicyTests
{
    [Fact]
    public void DayUpdateFullHealthRefillIsCappedBackToTwentyPercentFloor()
    {
        // 昏迷时血量 80/100（高于 20% 上限）：dayupdate 回填满血 100 后，结算压回 20。
        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 100,
            wakeHealthCap: 20,
            out var finalHealth));
        Assert.Equal(20, finalHealth);
    }

    [Fact]
    public void CapturedHealthBelowFloorKeepsItsOwnLowerValue()
    {
        // 昏迷时血量 13/100（本来就低于 20% 上限 20）：保持原值 13，不抬高。
        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 100,
            wakeHealthCap: 13,
            out var finalHealth));
        Assert.Equal(13, finalHealth);
    }

    [Fact]
    public void CapAboveMaximumHealthIsClampedToMaximum()
    {
        // 防御边界：登记值超出当前最大血量（升级前捕获/数据异常）时裁剪到最大血量。
        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 80,
            wakeHealthCap: 100,
            out var finalHealth));
        Assert.Equal(80, finalHealth);
    }

    [Fact]
    public void DegenerateZeroCapOrZeroMaximumNeverWritesBack()
    {
        // 血 0 双死守卫边界：cap<=0 或最大血量<=0 不写回，保持原版晨间值。
        Assert.False(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 100, wakeHealthCap: 0, out var zeroCapHealth));
        Assert.Equal(0, zeroCapHealth);

        Assert.False(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 100, wakeHealthCap: -5, out var negativeCapHealth));
        Assert.Equal(0, negativeCapHealth);

        Assert.False(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 0, wakeHealthCap: 20, out var zeroMaximumHealth));
        Assert.Equal(0, zeroMaximumHealth);
    }

    [Fact]
    public void WakeHealthSettlementIsIndependentOfSleepRecoveryBalance()
    {
        // DisableSleepRecovery 语义：血量就是上限值本身，饥饿不支付、无恢复单位；
        // 规则不接收 SleepRecoveryBalance 输入，平衡开关开/关结果一致。
        const int maximumHealth = 100;
        const int wakeHealthCap = 20;

        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth, wakeHealthCap, out var withBalance));
        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth, wakeHealthCap, out var withoutBalance));
        Assert.Equal(withBalance, withoutBalance);
        Assert.Equal(20, withBalance);
    }

    [Fact]
    public void VanillaReviveHealthPassesThroughTheSamePreSaveSeam()
    {
        // 问题3（2026-11-14，用户决议）：黑暗死亡复活血量（原版 checkForEvents 的 revive=10，
        // GameLocation.cs:15614）走同一条保存前最终值缝：dayupdate 满血回填后压回 10，
        // 存档携带、读档即 10；清晨 vanilla 的再次赋值幂等。血 0 双死守卫（health<=0 委托
        // 原版）因晨间血量恒为 10 > 0 正常不命中，守卫本体语义不变。
        Assert.True(SpecialPassOutWakeHealthSettlementPolicy.TryResolveFinalHealth(
            maximumHealth: 100,
            wakeHealthCap: PendingDarknessWakePolicy.VanillaReviveHealth,
            out var finalHealth));
        Assert.Equal(10, finalHealth);
    }
}
