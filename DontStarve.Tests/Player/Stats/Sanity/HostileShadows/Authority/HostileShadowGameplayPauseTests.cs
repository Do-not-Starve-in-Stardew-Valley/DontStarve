using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowGameplayPauseTests
{
    [Theory]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, false, false, true)]
    public void Pause_matrix_separates_single_player_menu_from_multiplayer_and_time_pause(
        bool menuOpen,
        bool isMultiplayer,
        bool gamePaused,
        bool gameActive,
        bool expectedFrozen
    )
    {
        Assert.Equal(
            expectedFrozen,
            HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                menuOpen,
                isMultiplayer,
                gamePaused,
                gameActive
            )
        );
    }

    [Fact]
    public void Every_live_shadow_clock_and_behavior_entry_uses_the_shared_pause_gate()
    {
        var host = Contract("HostileShadowAuthority", "SmapiHostileShadowHost.cs");
        var world = Contract("HostileShadowAuthority", "SmapiHostileShadowWorldRuntime.cs");
        var monster = Contract("HostileShadowAuthority", "HostileShadowMonster.cs");
        var projection = Contract("ShadowProjection", "SmapiHarmlessProjectionHost.cs");

        Assert.Equal(2, Count(host, "HostileShadowGameplayPausePolicy.IsBehaviorFrozen("));
        Assert.Contains("Game1.activeClickableMenu is not null", host, StringComparison.Ordinal);
        Assert.Contains("Game1.IsMultiplayer", host, StringComparison.Ordinal);
        Assert.True(
            Slice(host, "private void OnUpdateTicked", "private bool CanRetreatForCurrentTarget")
                .IndexOf("HostileShadowGameplayPausePolicy.IsBehaviorFrozen(", StringComparison.Ordinal)
                < Slice(host, "private void OnUpdateTicked", "private bool CanRetreatForCurrentTarget")
                    .IndexOf("TrackWarpAndQueueFastSpawns();", StringComparison.Ordinal)
        );

        Assert.Equal(1, Count(world, "HostileShadowGameplayPausePolicy.IsBehaviorFrozen("));
        Assert.Contains("Game1.activeClickableMenu is not null", world, StringComparison.Ordinal);
        Assert.Contains("Game1.IsMultiplayer", world, StringComparison.Ordinal);

        Assert.Equal(2, Count(monster, "HostileShadowGameplayPausePolicy.IsBehaviorFrozen("));
        Assert.Contains("Game1.activeClickableMenu is not null", monster, StringComparison.Ordinal);
        Assert.Contains("Game1.IsMultiplayer", monster, StringComparison.Ordinal);

        Assert.Equal(1, Count(projection, "HostileShadowGameplayPausePolicy.IsBehaviorFrozen("));
        Assert.Contains("Game1.activeClickableMenu is not null", projection, StringComparison.Ordinal);
        Assert.Contains("Game1.IsMultiplayer", projection, StringComparison.Ordinal);
        var projectionTick = Slice(
            projection,
            "private void OnUpdateTicked",
            "private void UpdateSpeciesBehaviors"
        );
        Assert.True(
            projectionTick.IndexOf(
                "HostileShadowGameplayPausePolicy.IsBehaviorFrozen(",
                StringComparison.Ordinal
            )
                < projectionTick.IndexOf("PruneDeadBindingProjections();", StringComparison.Ordinal)
        );

        Assert.DoesNotContain("Game1.paused || !Game1.game1.IsActive", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.paused || !Game1.game1.IsActive", world, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.paused || !Game1.game1.IsActive", monster, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.paused || !Game1.game1.IsActive", projection, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Contract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
