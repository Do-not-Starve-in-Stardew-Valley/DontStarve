using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowPassThroughStaticTests
{
    [Fact]
    public void Shared_physical_shadow_entity_is_passable_without_a_global_collision_patch()
    {
        var monster = Contract("HostileShadowMonster.cs");
        var world = Contract("SmapiHostileShadowWorldRuntime.cs");
        var entry = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "ShadowProjection",
                "ModEntry.cs"
            )
        );

        Assert.Contains("farmerPassesThrough = true;", monster, StringComparison.Ordinal);
        Assert.Contains(
            "monster = new HostileShadowMonster",
            world,
            StringComparison.Ordinal
        );
        Assert.Contains("\"Creeper Fear\"", world, StringComparison.Ordinal);
        Assert.Contains("\"Terrorbeak\"", world, StringComparison.Ordinal);
        Assert.DoesNotContain("HostileShadowCollisionPatch", entry, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "nameof(GameLocation.isCollidingPosition)",
            entry,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ModManifest.UniqueID + \".collision\"",
            entry,
            StringComparison.Ordinal
        );
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                fileName
            )
        );
    }
}
