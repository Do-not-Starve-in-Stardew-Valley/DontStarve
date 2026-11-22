#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.Illusions.Lighting;

public sealed class DontStarveLightAmbiencePolicyTests
{
    private const double DayRetainedDefaultLuminance =
        0.2126d * 1d + 0.7152d * (230d / 255d) + 0.0722d * (158d / 255d);

    [Fact]
    public void RetainedValuesConvertToSqrtDarknessChannels()
    {
        var dusk = DontStarveAmbienceTargets.FromRetained(
            150d / 255d,
            150d / 255d,
            150d / 255d
        );
        var expected = Math.Sqrt(1d - 150d / 255d);
        Assert.Equal(expected, dusk.R, precision: 10);
        Assert.Equal(expected, dusk.G, precision: 10);
        Assert.Equal(expected, dusk.B, precision: 10);
        Assert.False(dusk.IsWhite);
    }

    [Fact]
    public void JunimoProtectionSkipsTheAtmosphere()
    {
        Assert.Equal(
            NaturalDarknessScenePlan.None,
            DontStarveLightAmbiencePolicy.Resolve(
                Input(junimoProtected: true, timeOfDay: 1900),
                darknessEnabled: true
            )
        );
    }

    [Fact]
    public void SurfaceDayUsesDstWarmAmbientAndSurfaceDuskFadesInThreeSeconds()
    {
        var day = DontStarveLightAmbiencePolicy.Resolve(
            Input(timeOfDay: 1700),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.AmbientDay, day.Phase);
        Assert.Equal(1d, day.ProgressCeiling);
        Assert.Equal(0d, day.TransitionDurationSeconds);
        AssertDayColorMatchesDstDefault(day.TargetColor);

        var dusk = DontStarveLightAmbiencePolicy.Resolve(
            Input(timeOfDay: 1800),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.AmbientDusk, dusk.Phase);
        Assert.Equal(1d, dusk.ProgressCeiling);
        Assert.Equal(3d, dusk.TransitionDurationSeconds);
        var expectedGray = Math.Sqrt(1d - 150d / 255d);
        Assert.Equal(expectedGray, dusk.TargetColor.R, precision: 10);
        Assert.Equal(expectedGray, dusk.TargetColor.G, precision: 10);
        Assert.Equal(expectedGray, dusk.TargetColor.B, precision: 10);
    }

    [Fact]
    public void SurfaceNightFollowsTheDarknessSwitch()
    {
        var withDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(timeOfDay: 2000),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.NightThreeSeconds, withDarkness.Phase);
        Assert.True(withDarkness.TargetColor.IsWhite);
        Assert.Equal(1d, withDarkness.ProgressCeiling);
        Assert.Equal(3d, withDarkness.TransitionDurationSeconds);

        var withoutDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(timeOfDay: 2000),
            darknessEnabled: false
        );
        // 黑暗关闭：整夜保持黄昏值（与黄昏计划完全一致，不再触发过渡）。
        Assert.Equal(
            DontStarveLightAmbiencePolicy.Resolve(
                Input(timeOfDay: 1800),
                darknessEnabled: false
            ),
            withoutDarkness
        );
    }

    [Fact]
    public void MineUsesSkylightEquivalentsAndNightFollowsDarknessSwitch()
    {
        var day = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, timeOfDay: 1200),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.MineDay, day.Phase);
        Assert.Equal(0d, day.TransitionDurationSeconds);
        var expectedDayR = Math.Sqrt(1d - 0.85d * (180d / 255d));
        Assert.Equal(expectedDayR, day.TargetColor.R, precision: 10);

        var dusk = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, timeOfDay: 1800),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.MineDusk, dusk.Phase);
        Assert.Equal(
            DontStarveLightAmbiencePolicy.MineDuskTransitionSeconds,
            dusk.TransitionDurationSeconds
        );
        var expectedDuskB = Math.Sqrt(1d - 0.6d * 1d);
        Assert.Equal(expectedDuskB, dusk.TargetColor.B, precision: 10);

        var nightWithDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, timeOfDay: 2000),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.MineNight, nightWithDarkness.Phase);
        Assert.True(nightWithDarkness.TargetColor.IsWhite);
        Assert.Equal(1d, nightWithDarkness.TransitionDurationSeconds);

        var nightWithoutDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, timeOfDay: 2000),
            darknessEnabled: false
        );
        Assert.Equal(NaturalDarknessPhase.MineDusk, nightWithoutDarkness.Phase);
        Assert.Equal(dusk.TargetColor, nightWithoutDarkness.TargetColor);
    }

    [Fact]
    public void FullDarkMineLayersFollowTheDarknessSwitch()
    {
        var withDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, mineLevel: 35, timeOfDay: 1200),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.FullDark, withDarkness.Phase);

        var withoutDarkness = DontStarveLightAmbiencePolicy.Resolve(
            Input(mineShaft: true, mineLevel: 35, timeOfDay: 1200),
            darknessEnabled: false
        );
        Assert.Equal(NaturalDarknessPhase.MineDusk, withoutDarkness.Phase);
        Assert.Equal(
            DontStarveLightAmbiencePolicy.Resolve(
                Input(mineShaft: true, timeOfDay: 1800),
                darknessEnabled: false
            ).TargetColor,
            withoutDarkness.TargetColor
        );
    }

    [Fact]
    public void VolcanoDungeonUsesMineSkylightValues()
    {
        var plan = DontStarveLightAmbiencePolicy.Resolve(
            Input(volcanoDungeon: true, timeOfDay: 1200),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.MineDay, plan.Phase);
    }

    [Fact]
    public void ClinicInteriorNeverDarkensEvenWithDarknessOn()
    {
        var clinic = DontStarveLightAmbiencePolicy.Resolve(
            Input(
                outdoors: false,
                profile: NaturalDarknessProfile.Unchanged,
                timeOfDay: 2000
            ),
            darknessEnabled: true
        );
        Assert.Equal(NaturalDarknessPhase.AmbientDusk, clinic.Phase);
        Assert.False(clinic.TargetColor.IsWhite);
    }

    [Fact]
    public void MarkedInteriorsStillDarkenWithDarknessOnAndHoldDuskWithout()
    {
        var marked = Input(
            outdoors: false,
            profile: NaturalDarknessProfile.NightThreeSeconds,
            timeOfDay: 2000
        );
        var withDarkness = DontStarveLightAmbiencePolicy.Resolve(marked, darknessEnabled: true);
        Assert.Equal(NaturalDarknessPhase.NightThreeSeconds, withDarkness.Phase);
        Assert.True(withDarkness.TargetColor.IsWhite);

        var withoutDarkness = DontStarveLightAmbiencePolicy.Resolve(marked, darknessEnabled: false);
        Assert.Equal(NaturalDarknessPhase.AmbientDusk, withoutDarkness.Phase);
        Assert.False(withoutDarkness.TargetColor.IsWhite);

        var fullDark = Input(
            outdoors: false,
            profile: NaturalDarknessProfile.FullDark,
            timeOfDay: 1200
        );
        Assert.Equal(
            NaturalDarknessPhase.FullDark,
            DontStarveLightAmbiencePolicy.Resolve(fullDark, darknessEnabled: true).Phase
        );
        Assert.Equal(
            NaturalDarknessPhase.AmbientDusk,
            DontStarveLightAmbiencePolicy.Resolve(fullDark, darknessEnabled: false).Phase
        );
    }

    private static void AssertDayColorMatchesDstDefault(NaturalDarknessTargetColor color)
    {
        // 保留值 = DST ambient 颜色通道本身；换算后逐通道 sqrt(1−保留)。
        Assert.Equal(
            Math.Sqrt(1d - 1d),
            color.R,
            precision: 10
        );
        Assert.Equal(
            Math.Sqrt(1d - 230d / 255d),
            color.G,
            precision: 10
        );
        Assert.Equal(
            Math.Sqrt(1d - 158d / 255d),
            color.B,
            precision: 10
        );
    }

    private static NaturalDarknessLocationInput Input(
        bool matchedRule = true,
        NaturalDarknessProfile profile = NaturalDarknessProfile.NightThreeSeconds,
        bool outdoors = true,
        bool junimoProtected = false,
        bool mineShaft = false,
        bool volcanoDungeon = false,
        int mineLevel = 1,
        bool skullCavern = false,
        bool dangerousSkullCavern = false,
        int timeOfDay = 1200
    )
    {
        return new NaturalDarknessLocationInput(
            matchedRule,
            profile,
            outdoors,
            junimoProtected,
            mineShaft,
            volcanoDungeon,
            IsSpringSeason: false,
            mineLevel,
            skullCavern,
            dangerousSkullCavern,
            timeOfDay,
            StartingToGetDarkTime: 1800,
            TrulyDarkTime: 2000
        );
    }
}
