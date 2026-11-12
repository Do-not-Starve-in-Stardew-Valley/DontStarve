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
        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, out _));

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
