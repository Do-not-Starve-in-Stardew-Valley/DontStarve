using System;
using System.IO;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class WakeDeathPresentationPolicyTests
{
    [Theory]
    [InlineData((int)WakeDeathPresentationMode.HiddenHospital, true, 0, false, true)]
    [InlineData((int)WakeDeathPresentationMode.Natural, true, 0, false, false)]
    [InlineData((int)WakeDeathPresentationMode.HiddenHospital, false, 0, false, false)]
    [InlineData((int)WakeDeathPresentationMode.HiddenHospital, true, 1, false, false)]
    [InlineData((int)WakeDeathPresentationMode.HiddenHospital, true, 0, true, false)]
    public void Hidden_mode_holds_native_bed_death_only_for_pending_zero_health(
        int mode,
        bool markerPresent,
        int health,
        bool killScreen,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathPresentationPolicy.ShouldHoldNativeDeath(
                (WakeDeathPresentationMode)mode,
                markerPresent,
                health,
                killScreen
            )
        );
    }

    [Theory]
    [InlineData(true, 0, false, true, false, false, false, false, true, true)]
    [InlineData(true, 0, false, false, false, false, false, false, false, false)]
    [InlineData(true, 0, false, true, true, false, false, false, true, false)]
    [InlineData(true, 0, false, true, false, true, false, false, true, false)]
    [InlineData(true, 0, false, true, false, false, true, false, true, false)]
    [InlineData(true, 0, false, true, false, false, false, true, true, false)]
    [InlineData(true, 0, false, true, false, false, false, false, false, false)]
    public void Hidden_hospital_release_waits_for_the_post_settlement_stable_boundary(
        bool markerPresent,
        int health,
        bool killScreen,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool newDay,
        bool messageAfterPauseEmpty,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathPresentationPolicy.ShouldReleaseHiddenHospital(
                markerPresent,
                health,
                killScreen,
                worldReady,
                isFading,
                eventUp,
                menuOpen,
                newDay,
                messageAfterPauseEmpty
            )
        );
    }

    [Fact]
    public void Hidden_hospital_wake_death_has_a_separate_policy_from_load_replay()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "WakeDeathPresentationPolicy.cs"
        );

        Assert.Contains("WakeDeathPresentationMode.HiddenHospital", source, StringComparison.Ordinal);
        Assert.Contains("ShouldHoldNativeDeath", source, StringComparison.Ordinal);
        Assert.Contains("ShouldReleaseHiddenHospital", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Darkness_outcome_does_not_start_the_native_bed_death_block()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Stats",
            "Sanity",
            "PassOut",
            "SmapiSanityTwoAmSpecialDeathService.cs"
        );

        var begin = source.IndexOf("private void BeginHiddenHospitalPassOut", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        var end = source.IndexOf("internal void BeginHiddenHospitalHandoff", begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        var method = source[begin..end];

        Assert.DoesNotContain("Game1.killScreen = true", method, StringComparison.Ordinal);
        Assert.DoesNotContain("farmer.freezePause = 8000", method, StringComparison.Ordinal);
        Assert.Contains("SanityTwoAmVanillaPassOutBridge.BeginNoFeePassOut", source, StringComparison.Ordinal);

        var sleepSource = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );
        Assert.Contains("RegisterHiddenHospitalDeath", sleepSource, StringComparison.Ordinal);
        Assert.Contains("SleepMode.TimeLimitPassOut", sleepSource, StringComparison.Ordinal);
        Assert.Contains("HiddenHospitalWakeReady", sleepSource, StringComparison.Ordinal);
        Assert.Contains("ApplyHiddenHospitalWakeHealth", sleepSource, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.FullName;
            foreach (var part in parts)
                candidate = Path.Combine(candidate, part);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
