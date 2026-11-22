using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Debug;

public sealed class DebugCommandsStaticContractTests
{
    [Fact]
    public void Collision_boxes_start_hidden_and_help_reports_the_new_default()
    {
        var source = ReadSource("Debug", "DebugCommands.cs");

        Assert.Contains("private static bool boxesVisible = false;", source, StringComparison.Ordinal);
        Assert.Contains("默认 off", source, StringComparison.Ordinal);
        Assert.DoesNotContain("默认 on", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_and_energy_commands_are_registered_with_their_own_state_sources()
    {
        var path = FindSourcePath("Debug", "PlayerResourceDebugCommands.cs");
        Assert.NotNull(path);
        var source = File.ReadAllText(path!);

        Assert.Contains("\"ds_health\"", source, StringComparison.Ordinal);
        Assert.Contains("\"ds_energy\"", source, StringComparison.Ordinal);
        Assert.Contains("health.TrySetDebugValue", source, StringComparison.Ordinal);
        Assert.Contains("player.Stamina", source, StringComparison.Ordinal);
        Assert.Contains("player.MaxStamina", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Player_resource_commands_are_registered_during_mod_startup()
    {
        var source = ReadSource("ModEntry.cs");

        Assert.Contains("new PlayerResourceDebugCommands(", source, StringComparison.Ordinal);
        Assert.Contains("statManagerResult.Health", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativePath)
    {
        var path = FindSourcePath(relativePath);
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    private static string? FindSourcePath(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName, "DontStarve" }.Concat(relativePath).ToArray()
            );
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }
}
