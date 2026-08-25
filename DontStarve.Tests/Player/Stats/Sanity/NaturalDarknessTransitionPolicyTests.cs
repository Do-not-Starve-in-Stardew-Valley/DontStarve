using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class NaturalDarknessTransitionPolicyTests
{
    [Theory]
    [InlineData(0d, 1d, 1.5d, 3d, 0.5d)]
    [InlineData(0d, 0.30d, 1d, 2d, 0.15d)]
    [InlineData(0d, 0.30d, 2d, 2d, 0.30d)]
    [InlineData(0.30d, 1d, 0.5d, 1d, 0.65d)]
    [InlineData(0.30d, 1d, 1d, 1d, 1d)]
    public void TransitionsReachTheirCurrentStageTargetInTheRequestedRealTime(
        double startingProgress,
        double targetProgress,
        double elapsedRealSeconds,
        double durationSeconds,
        double expectedProgress
    )
    {
        Assert.Equal(
            expectedProgress,
            NaturalDarknessTransitionPolicy.Advance(
                startingProgress,
                targetProgress,
                elapsedRealSeconds,
                durationSeconds
            ),
            precision: 10
        );
    }

    [Fact]
    public void ImmediateOrLighterPlanChangesNeverLeaveStaleDarkness()
    {
        Assert.Equal(
            1d,
            NaturalDarknessTransitionPolicy.Advance(0d, 1d, 0.01d, 0d)
        );
        Assert.Equal(
            0.30d,
            NaturalDarknessTransitionPolicy.Advance(0.80d, 0.30d, 0.01d, 3d)
        );
    }

    [Fact]
    public void InvalidElapsedTimeCannotCreateExtraDarkness()
    {
        Assert.Equal(
            0.4d,
            NaturalDarknessTransitionPolicy.Advance(0.4d, 1d, double.NaN, 3d),
            precision: 10
        );
        Assert.Equal(
            0.4d,
            NaturalDarknessTransitionPolicy.Advance(0.4d, 1d, -1d, 3d),
            precision: 10
        );
        Assert.Equal(
            0f,
            NaturalDarknessTransitionPolicy.GetLightmapWhiteBlend(double.NaN)
        );
        Assert.Equal(
            1f,
            NaturalDarknessTransitionPolicy.GetLightmapWhiteBlend(2d)
        );
    }

    [Fact]
    public void WarpArrivalUsesTheDestinationCurrentPlanRatherThanReplayingIt()
    {
        var dusk = new NaturalDarknessScenePlan(
            NaturalDarknessPhase.MineDusk,
            NaturalDarknessTransitionPolicy.MineDuskWhiteBlend,
            NaturalDarknessTransitionPolicy.MineDuskTransitionDurationSeconds
        );
        var night = new NaturalDarknessScenePlan(
            NaturalDarknessPhase.NightThreeSeconds,
            1d,
            NaturalDarknessTransitionPolicy.NightTransitionDurationSeconds
        );

        Assert.Equal(
            0.30d,
            NaturalDarknessTransitionPolicy.GetWarpArrivalProgress(dusk),
            precision: 10
        );
        Assert.Equal(1d, NaturalDarknessTransitionPolicy.GetWarpArrivalProgress(night));
        Assert.Equal(
            0d,
            NaturalDarknessTransitionPolicy.GetWarpArrivalProgress(
                NaturalDarknessScenePlan.None
            )
        );
    }
}
