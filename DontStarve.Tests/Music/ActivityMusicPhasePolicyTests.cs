using System.Reflection;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicPhasePolicyTests
{
    [Fact]
    public void Dusk_policy_exposes_the_frozen_boundary_and_cooldown_contract()
    {
        var policy = typeof(ActivityMusicPhasePolicyTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicPhasePolicy"
        );
        Assert.NotNull(policy);

        var cooldown = policy!.GetField(
            "DuskSuppressionSeconds",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(cooldown);
        Assert.Equal(15, cooldown!.GetValue(null));

        var dawnCooldown = policy.GetField(
            "DawnSuppressionSeconds",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(dawnCooldown);
        Assert.Equal(15, dawnCooldown!.GetValue(null));
    }

    [Theory]
    [InlineData(550, 600, true)]
    [InlineData(600, 600, false)]
    [InlineData(610, 600, false)]
    [InlineData(2350, 600, false)]
    public void Dawn_crossing_policy_handles_boundary_and_time_rollback(
        int oldTime,
        int newTime,
        bool expected
    )
    {
        var policy = typeof(ActivityMusicPhasePolicyTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicPhasePolicy"
        );
        Assert.NotNull(policy);
        var method = policy!.GetMethod(
            "IsDawnCrossing",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object[] { oldTime, newTime, 600 });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1750, 1800, 1800, true)]
    [InlineData(1800, 1800, 1800, false)]
    [InlineData(1830, 1820, 1800, false)]
    [InlineData(2350, 100, 1800, false)]
    public void Dusk_crossing_policy_handles_boundary_and_time_rollback(
        int oldTime,
        int newTime,
        int boundary,
        bool expected
    )
    {
        var policy = typeof(ActivityMusicPhasePolicyTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicPhasePolicy"
        );
        Assert.NotNull(policy);
        var method = policy!.GetMethod(
            "IsDuskCrossing",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object[] { oldTime, newTime, boundary });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, 1750, 1800, false)]
    [InlineData(false, 1800, 1800, true)]
    [InlineData(true, 2200, 1800, false)]
    public void Work_policy_only_blocks_non_cave_locations_after_dusk(
        bool isCaveLike,
        int timeOfDay,
        int startDarkTime,
        bool expected
    )
    {
        var policy = typeof(ActivityMusicPhasePolicyTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicPhasePolicy"
        );
        Assert.NotNull(policy);
        var method = policy!.GetMethod(
            "IsWorkBlocked",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var actual = method!.Invoke(
            null,
            new object[] { isCaveLike, timeOfDay, startDarkTime }
        );

        Assert.Equal(expected, actual);
    }
}
