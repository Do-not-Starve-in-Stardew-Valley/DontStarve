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

        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, out _));
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

        Assert.True(settlement.RegisterNonLethal(Session, "1", 42, out _));
        Assert.False(settlement.RegisterNonLethal(Session, "1", 42, out _));
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
}
