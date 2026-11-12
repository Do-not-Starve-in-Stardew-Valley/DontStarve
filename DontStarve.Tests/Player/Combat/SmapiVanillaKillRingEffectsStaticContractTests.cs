using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Combat;

public sealed class SmapiVanillaKillRingEffectsStaticContractTests
{
    private const string AdapterRelativePath =
        "DontStarve/Player/Combat/SmapiVanillaKillRingEffects.cs";

    [Fact]
    public void Adapter_is_farmer_explicit_and_never_falls_back_to_global_player()
    {
        var source = ReadAdapterSource();

        Assert.Contains("CaptureSnapshot(Farmer", source, StringComparison.Ordinal);
        Assert.Contains("ApplyPlan(", source, StringComparison.Ordinal);
        Assert.Contains("GameLocation location", source, StringComparison.Ordinal);
        Assert.Contains("Vector2 explosionTile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.player", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_keeps_vanilla_napalm_arguments_and_terrain_exception()
    {
        var source = ReadAdapterSource();

        Assert.Contains("location.explode(", source, StringComparison.Ordinal);
        Assert.Contains("damageFarmers: false", source, StringComparison.Ordinal);
        Assert.Contains("damage_amount: -1", source, StringComparison.Ordinal);
        Assert.Contains(
            "destroyObjects: location is not Farm && location is not SlimeHutch",
            source,
            StringComparison.Ordinal
        );
    }

    private static string ReadAdapterSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, AdapterRelativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the production adapter source: {AdapterRelativePath}"
        );
    }
}
