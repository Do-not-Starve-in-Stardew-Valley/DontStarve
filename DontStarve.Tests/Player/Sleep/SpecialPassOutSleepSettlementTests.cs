using DontStarve.Player.Sleep;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SpecialPassOutSleepSettlementTests
{
    private const string Session = "11111111111141118111111111111111";

    [Fact]
    public void SnapshotOverrideZerosSanityAndDisablesRecoveryWithAuditableSource()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, 20, out _));

        var snapshot = Input(137.5d);
        var overridden = SleepSanitySettlementPolicy.Apply(
            snapshot,
            settlement,
            Session,
            "1",
            42
        );

        Assert.Equal(0d, overridden.Sanity);
        Assert.False(overridden.RecoveryBalance.SanityRecoveryEnabled);
        Assert.True(overridden.RecoveryBalance.SanityEnabled);
        Assert.Equal(
            SanityChangeSource.SanityDarknessSpecialDeath,
            overridden.RecoveryBalance.SanitySettlementSource
        );
    }

    [Fact]
    public void OrdinaryVoluntarySleepStillUsesItsExistingSourceAndRecovery()
    {
        var snapshot = Input(137.5d);
        var unchanged = SleepSanitySettlementPolicy.Apply(
            snapshot,
            new SpecialPassOutSanitySettlement(),
            Session,
            "1",
            42
        );

        Assert.Equal(snapshot, unchanged);
        Assert.True(unchanged.RecoveryBalance.SanityEnabled);
        Assert.Equal(
            SanityChangeSource.VoluntarySleep,
            unchanged.RecoveryBalance.SanitySettlementSource
        );
    }

    [Fact]
    public void VoluntarySleeperWithoutNonLethalRegistrationHasNoWakeHealthEntry()
    {
        // 自愿睡眠回归锁定：没有非致命登记的玩家（自愿睡眠走 overnight 既有结算）在
        // dayupdate 后写回缝查询不到血量上限记账项，原版满血恢复与既有结算不受影响。
        var settlement = new SpecialPassOutSanitySettlement();

        Assert.False(settlement.TryResolveHealth(Session, "1", out _));

        settlement.RegisterNonLethal(Session, "2", 42, 20, out _);
        // 他人登记也不外溢到自愿睡眠玩家。
        Assert.False(settlement.TryResolveHealth(Session, "1", out _));
    }

    [Fact]
    public void SanitySnapshotOverrideIsUnaffectedByWakeHealthCapValue()
    {
        var settlement = new SpecialPassOutSanitySettlement();
        settlement.RegisterNonLethal(Session, "1", 42, 0, out _);

        var overridden = SleepSanitySettlementPolicy.Apply(
            Input(137.5d),
            settlement,
            Session,
            "1",
            42
        );

        // cap=0 只影响血量写回缝（TryResolveHealth 不暴露），Sanity=0 记账不受影响。
        Assert.Equal(0d, overridden.Sanity);
        Assert.False(overridden.RecoveryBalance.SanityRecoveryEnabled);
    }

    private static SleepSnapshot Input(double sanity) => new(
        "1",
        Session,
        "settlement-1",
        SleepMode.Voluntary,
        100,
        100,
        106,
        "bed",
        50d,
        100d,
        sanity,
        200d,
        10d,
        100,
        SleepRecoveryBalance.Default
    );
}
