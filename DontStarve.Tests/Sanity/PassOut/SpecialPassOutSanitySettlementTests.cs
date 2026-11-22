using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SpecialPassOutSanitySettlementTests
{
    private const string Session = "11111111111141118111111111111111";

    [Fact]
    public void RegisteredNonLethalOutcomeOverridesSanityAndDisablesSleepRecovery()
    {
        var settlement = new SpecialPassOutSanitySettlement();

        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));
        Assert.True(
            settlement.TryResolve(
                Session,
                "1",
                42,
                137.5d,
                out var result
            )
        );

        Assert.Equal(0d, result.Sanity);
        Assert.Equal(SanityChangeSource.SanityDarknessSpecialDeath, result.Source);
        Assert.True(result.DisableSleepRecovery);
    }

    [Fact]
    public void SettlementDoesNotCrossSessionOrDayAndIsIdempotent()
    {
        var settlement = new SpecialPassOutSanitySettlement();

        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));
        Assert.False(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));
        Assert.False(
            settlement.TryResolve(
                "22222222222242228222222222222222",
                "1",
                42,
                10d,
                out _
            )
        );
        Assert.False(settlement.TryResolve(Session, "1", 43, 10d, out _));
        Assert.True(settlement.TryResolve(Session, "1", 42, 10d, out _));

        settlement.ClearSession(Session);
        Assert.False(settlement.TryResolve(Session, "1", 42, 10d, out _));
    }

    [Fact]
    public void WakeHealthCapResolvesAfterCalendarRollWithinSameSession()
    {
        // 登记在换日前（day 42），消费在换日后的 dayupdate postfix / OnSaving（day 43）：
        // TryResolveHealth 有意不做按天匹配（消费窗口由 ClearSession 闭环约束）。
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));

        Assert.True(settlement.TryResolveHealth(Session, "1", out var cap));
        Assert.Equal(20, cap);
    }

    [Fact]
    public void WakeHealthCapDoesNotCrossSessionOrPlayerAndClearsAtDayStarted()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));

        Assert.False(settlement.TryResolveHealth(
            "22222222222242228222222222222222", "1", out _));
        Assert.False(settlement.TryResolveHealth(Session, "2", out _));

        settlement.ClearSession(Session);
        Assert.False(settlement.TryResolveHealth(Session, "1", out _));
    }

    [Fact]
    public void DegenerateZeroCapIsNeverExposedSoNoZeroHealthWriteBack()
    {
        // 血 0 双死守卫边界：cap<=0 不暴露，调用方保持原版晨间值，不写回 0 血。
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 0, out _));
        Assert.False(settlement.TryResolveHealth(Session, "1", out _));

        Assert.False(settlement.RegisterNonLethal(Session, "2", 42, -1, out _));
        Assert.False(settlement.TryResolveHealth(Session, "2", out _));
    }

    [Fact]
    public void SanityResolveStillRequiresMatchingDayWhileHealthResolveDoesNot()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));

        // Sanity 记账消费点全部在换日前（DayEnding / 快照构建），保持按天匹配。
        Assert.False(settlement.TryResolve(Session, "1", 43, 10d, out _));
        // 血量记账消费点全部在换日后（dayupdate postfix / OnSaving），不做按天匹配。
        Assert.True(settlement.TryResolveHealth(Session, "1", out _));
    }

    [Fact]
    public void PendingDarknessReviveHealthResolvesThroughTheSamePreSaveSeam()
    {
        // 问题3（2026-11-14，用户决议）：黑暗死亡复活血量（原版 revive=10）与非致命 cap 共用
        // 同一个"dayupdate 之后、存档序列化之前"写回缝；登记不做按天匹配（消费在换日后）。
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, 10, out _));

        Assert.True(settlement.TryResolveHealth(Session, "1", out var reviveHealth));
        Assert.Equal(10, reviveHealth);
    }

    [Fact]
    public void PendingDarknessRegistrationDoesNotForceZeroSanityOverride()
    {
        // 黑暗死亡不清 Sanity：清晨恢复由 passout.reason 的 checkForEvents postfix
        // （SetToMaximumFraction）负责；PendingDarknessWakeHealth 登记绝不能触发 Sanity=0。
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, 10, out _));

        Assert.False(settlement.TryResolve(Session, "1", 42, 73d, out _));
    }

    [Fact]
    public void PendingDarknessRegistrationFollowsTheSameSessionAndDuplicateRules()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, 10, out _));
        // 同会话同日重复登记 → duplicate；跨会话/跨玩家不互通；ClearSession 闭环。
        Assert.False(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, 10, out _));
        Assert.False(settlement.TryResolveHealth(
            "22222222222242228222222222222222", "1", out _));
        Assert.False(settlement.TryResolveHealth(Session, "2", out _));

        settlement.ClearSession(Session);
        Assert.False(settlement.TryResolveHealth(Session, "1", out _));
    }

    [Fact]
    public void NewRegistrationSupersedesTheOtherKindForTheSamePlayer()
    {
        // 同一玩家新的特殊昏迷登记替换旧条目（夜 N 黑暗死亡 → 夜 N+1 非致命昏迷），
        // 血量缝只暴露最新登记，旧 revive 值不得复活。
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, 10, out _));
        Assert.True(settlement.RegisterNonLethal(Session, "1", 43, 20, out _));

        Assert.True(settlement.TryResolveHealth(Session, "1", out var cap));
        Assert.Equal(20, cap);
        Assert.True(settlement.TryResolve(Session, "1", 43, 10d, out _));

        Assert.True(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 44, 10, out _));
        Assert.False(settlement.TryResolve(Session, "1", 44, 10d, out _));
        Assert.True(settlement.TryResolveHealth(Session, "1", out var reviveHealth));
        Assert.Equal(10, reviveHealth);
    }

    [Fact]
    public void NegativeReviveHealthIsRejectedLikeOtherDegenerateInputs()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.False(settlement.RegisterPendingDarknessWakeHealth(Session, "1", 42, -1, out _));
        Assert.False(settlement.TryResolveHealth(Session, "1", out _));
    }
}
