#nullable enable

using System;
using System.IO;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class WakeDeathLoadGatePolicyTests
{
    [Theory]
    [InlineData(true, 0, false, true)]
    [InlineData(true, -1, false, true)]
    [InlineData(false, 0, false, false)]
    [InlineData(true, 1, false, false)]
    [InlineData(true, 0, true, false)]
    public void Wake_death_gate_arms_only_for_unresolved_zero_health_marker(
        bool markerPresent,
        int health,
        bool killScreen,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathLoadGatePolicy.ShouldArm(markerPresent, health, killScreen)
        );
    }

    [Theory]
    [InlineData(true, false, true, 0, false, 600, true)]
    [InlineData(true, false, true, -1, false, 600, true)]
    [InlineData(false, false, true, 0, false, 600, false)]
    [InlineData(true, true, true, 0, false, 600, false)]
    [InlineData(true, false, false, 0, false, 600, false)]
    [InlineData(true, false, true, 1, false, 600, false)]
    [InlineData(true, false, true, 0, true, 600, false)]
    [InlineData(true, false, true, 0, false, 2600, false)]
    public void Early_farmer_update_gate_blocks_load_death_before_save_loaded(
        bool hasLoadedGame,
        bool worldReady,
        bool localPlayer,
        int health,
        bool killScreen,
        int timeOfDay,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathLoadGatePolicy.ShouldEarlyArm(
                hasLoadedGame,
                worldReady,
                localPlayer,
                health,
                killScreen,
                timeOfDay
            )
        );
    }

    [Theory]
    [InlineData(true, 0, true, false, false, false, false, true)]
    [InlineData(true, 0, false, false, false, false, false, false)]
    [InlineData(true, 0, true, true, false, false, false, false)]
    [InlineData(true, 0, true, false, true, false, false, false)]
    [InlineData(true, 0, true, false, false, true, false, false)]
    [InlineData(true, 0, true, false, false, false, true, false)]
    public void Wake_death_gate_stays_closed_until_fade_and_world_are_stable(
        bool armed,
        int health,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool killScreen,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathLoadGatePolicy.ShouldRelease(
                armed,
                health,
                worldReady,
                isFading,
                eventUp,
                menuOpen,
                killScreen
            )
        );
    }

    [Theory]
    [InlineData(true, 1, false, true)]
    [InlineData(true, 0, true, true)]
    [InlineData(false, 0, false, false)]
    public void Wake_death_gate_is_cleared_after_revival_or_native_death(
        bool armed,
        int health,
        bool killScreen,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            WakeDeathLoadGatePolicy.ShouldClear(
                armed,
                health,
                killScreen
            )
        );
    }

    [Fact]
    public void Runtime_adapter_arms_at_save_load_and_releases_only_from_stable_tick()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        Assert.Contains("TryArmWakeDeathLoadGate();", source, StringComparison.Ordinal);
        Assert.Contains("farmer.CanMove = false;", source, StringComparison.Ordinal);
        Assert.Contains("WakeDeathLoadGatePolicy.ShouldRelease", source, StringComparison.Ordinal);
        Assert.Contains("Game1.IsFading()", source, StringComparison.Ordinal);
        Assert.Contains("Context.IsWorldReady", source, StringComparison.Ordinal);
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
