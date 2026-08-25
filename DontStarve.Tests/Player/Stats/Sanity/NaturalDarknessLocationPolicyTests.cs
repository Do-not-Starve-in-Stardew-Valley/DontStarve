using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class NaturalDarknessLocationPolicyTests
{
    [Fact]
    public void FullDarkLocationsAreImmediatelyBlackUnlessJunimosProtectThem()
    {
        var fullDark = NaturalDarknessLocationPolicy.Resolve(
            Input(profile: NaturalDarknessProfile.FullDark)
        );
        Assert.Equal(NaturalDarknessPhase.FullDark, fullDark.Phase);
        Assert.Equal(1d, fullDark.TargetWhiteBlend);
        Assert.Equal(0d, fullDark.TransitionDurationSeconds);

        var protectedPlan = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.FullDark,
                junimoProtected: true
            )
        );
        Assert.Equal(NaturalDarknessScenePlan.None, protectedPlan);
    }

    [Theory]
    [InlineData(1950, (int)NaturalDarknessPhase.None)]
    [InlineData(2000, (int)NaturalDarknessPhase.NightThreeSeconds)]
    public void NightProfileStartsOnlyAtTrueDarkness(int timeOfDay, int expectedPhase)
    {
        var plan = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.NightThreeSeconds,
                timeOfDay: timeOfDay
            )
        );

        var expected = (NaturalDarknessPhase)expectedPhase;
        Assert.Equal(expected, plan.Phase);
        if (expected == NaturalDarknessPhase.NightThreeSeconds)
        {
            Assert.Equal(1d, plan.TargetWhiteBlend);
            Assert.Equal(
                NaturalDarknessTransitionPolicy.NightTransitionDurationSeconds,
                plan.TransitionDurationSeconds
            );
        }
    }

    [Fact]
    public void UnmatchedOutdoorLocationsKeepTheExistingNightRuleButUnmatchedIndoorsDoNot()
    {
        var outdoor = NaturalDarknessLocationPolicy.Resolve(
            Input(
                matchedRule: false,
                profile: NaturalDarknessProfile.Unchanged,
                outdoors: true,
                timeOfDay: 2000
            )
        );
        var indoor = NaturalDarknessLocationPolicy.Resolve(
            Input(
                matchedRule: false,
                profile: NaturalDarknessProfile.Unchanged,
                outdoors: false,
                timeOfDay: 2000
            )
        );

        Assert.Equal(NaturalDarknessPhase.NightThreeSeconds, outdoor.Phase);
        Assert.Equal(NaturalDarknessScenePlan.None, indoor);
    }

    [Fact]
    public void MineDuskKeepsSeventyPercentOfTheOriginalLightThenNightCompletesInOneSecond()
    {
        var dusk = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.MineTwoStage,
                mineShaft: true,
                timeOfDay: 1800
            )
        );
        Assert.Equal(NaturalDarknessPhase.MineDusk, dusk.Phase);
        Assert.Equal(0.30d, dusk.TargetWhiteBlend, precision: 10);
        Assert.Equal(2d, dusk.TransitionDurationSeconds);

        var night = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.MineTwoStage,
                mineShaft: true,
                timeOfDay: 2000
            )
        );
        Assert.Equal(NaturalDarknessPhase.MineNight, night.Phase);
        Assert.Equal(1d, night.TargetWhiteBlend);
        Assert.Equal(1d, night.TransitionDurationSeconds);
    }

    [Theory]
    [InlineData(31, false, false)]
    [InlineData(39, false, false)]
    [InlineData(1000, true, false)]
    [InlineData(121, true, true)]
    [InlineData(77377, false, false)]
    public void RequiredMineDepthAndDangerOverridesAreAlwaysFullDark(
        int mineLevel,
        bool skullCavern,
        bool dangerousSkullCavern
    )
    {
        var plan = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.MineTwoStage,
                mineShaft: true,
                mineLevel: mineLevel,
                skullCavern: skullCavern,
                dangerousSkullCavern: dangerousSkullCavern,
                timeOfDay: 600
            )
        );

        Assert.Equal(NaturalDarknessPhase.FullDark, plan.Phase);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(40)]
    public void OrdinaryMineLevelsOutsideTheDarkBandStillUseTheirTimePlan(int mineLevel)
    {
        var plan = NaturalDarknessLocationPolicy.Resolve(
            Input(
                profile: NaturalDarknessProfile.MineTwoStage,
                mineShaft: true,
                mineLevel: mineLevel,
                timeOfDay: 1800
            )
        );

        Assert.Equal(NaturalDarknessPhase.MineDusk, plan.Phase);
    }

    [Fact]
    public void UnchangedProfileKeepsVolcanoLightingUntouched()
    {
        Assert.Equal(
            NaturalDarknessScenePlan.None,
            NaturalDarknessLocationPolicy.Resolve(
                Input(profile: NaturalDarknessProfile.Unchanged, timeOfDay: 2600)
            )
        );
    }

    private static NaturalDarknessLocationInput Input(
        bool matchedRule = true,
        NaturalDarknessProfile profile = NaturalDarknessProfile.NightThreeSeconds,
        bool outdoors = false,
        bool junimoProtected = false,
        bool mineShaft = false,
        int mineLevel = 1,
        bool skullCavern = false,
        bool dangerousSkullCavern = false,
        int timeOfDay = 1800
    )
    {
        return new NaturalDarknessLocationInput(
            matchedRule,
            profile,
            outdoors,
            junimoProtected,
            mineShaft,
            mineLevel,
            skullCavern,
            dangerousSkullCavern,
            timeOfDay,
            StartingToGetDarkTime: 1800,
            TrulyDarkTime: 2000
        );
    }
}
