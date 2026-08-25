using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MineWallSconcePolicyTests
{
    [Fact]
    public void OrdinaryMineDuskRepeatsTheThreeSecondSosThenThreeSecondCooldown()
    {
        var unit = MineWallSconcePolicy.SosUnitDurationSeconds;

        Assert.True(ShouldDraw(phase: NaturalDarknessPhase.MineDusk, elapsed: 0.01d));
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: unit * 1.5d
            )
        );
        Assert.True(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: unit * 2.5d
            )
        );
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: unit * 6d
            )
        );
        Assert.True(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: unit * 9d
            )
        );

        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: MineWallSconcePolicy.SosSignalDurationSeconds + 0.01d
            )
        );
        Assert.True(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineDusk,
                elapsed: MineWallSconcePolicy.SosSignalDurationSeconds
                    + MineWallSconcePolicy.SosCooldownDurationSeconds
                    + 0.01d
            )
        );
    }

    [Fact]
    public void MineNightImmediatelySuppressesOrdinaryWallSconces()
    {
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.MineNight,
                elapsed: 0d
            )
        );
    }

    [Theory]
    [InlineData(600, true)]
    [InlineData(609, true)]
    [InlineData(610, false)]
    [InlineData(700, true)]
    [InlineData(1759, false)]
    [InlineData(1800, false)]
    public void OrdinaryMineDarkBandUsesHourlyTenMinuteSconceWindows(
        int timeOfDay,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: timeOfDay,
                mineLevel: 31
            )
        );
    }

    [Theory]
    [InlineData(600, true)]
    [InlineData(629, true)]
    [InlineData(630, false)]
    [InlineData(700, false)]
    [InlineData(800, true)]
    [InlineData(830, false)]
    [InlineData(1759, false)]
    [InlineData(1800, false)]
    public void DeepSkullCavernUsesEvenHourlyThirtyMinuteSconceWindows(
        int timeOfDay,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: timeOfDay,
                mineLevel: 1000,
                skullCavern: true
            )
        );
    }

    [Fact]
    public void DuskImmediatelyCancelsAnOtherwiseActiveSpecialFloorWindow()
    {
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: 1800,
                mineLevel: 31
            )
        );
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: 1800,
                mineLevel: 1000,
                skullCavern: true
            )
        );
    }

    [Fact]
    public void DangerousSkullCavernAndUnscheduledFullDarkMinesNeverDrawSconces()
    {
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: 600,
                mineLevel: 121,
                skullCavern: true,
                dangerousSkullCavern: true
            )
        );
        Assert.False(
            ShouldDraw(
                phase: NaturalDarknessPhase.FullDark,
                timeOfDay: 600,
                mineLevel: 77377
            )
        );
    }

    [Fact]
    public void InactiveNaturalDarknessLeavesNativeWallSconcesUntouched()
    {
        Assert.True(
            ShouldDraw(
                phase: NaturalDarknessPhase.None,
                timeOfDay: 1800,
                mineLevel: 31
            )
        );
    }

    private static bool ShouldDraw(
        NaturalDarknessPhase phase,
        double elapsed = 0d,
        int timeOfDay = 600,
        int mineLevel = 1,
        bool skullCavern = false,
        bool dangerousSkullCavern = false
    )
    {
        return MineWallSconcePolicy.ShouldDraw(
            new MineWallSconceInput(
                phase,
                elapsed,
                timeOfDay,
                StartingToGetDarkTime: 1800,
                mineLevel,
                skullCavern,
                dangerousSkullCavern
            )
        );
    }
}
