using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowNpcGreetingStaticTests
{
    [Fact]
    public void Greeting_filter_targets_only_this_mods_physical_shadow_entity()
    {
        var patch = ReadContract("HostileShadowNpcGreetingPatch.cs");

        Assert.Contains("typeof(NPC)", patch, StringComparison.Ordinal);
        Assert.Contains("nameof(NPC.sayHiTo)", patch, StringComparison.Ordinal);
        Assert.Contains("new[] { typeof(Character) }", patch, StringComparison.Ordinal);
        Assert.Contains(
            "return character is not HostileShadowMonster;",
            patch,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("character is not Monster", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("character.IsMonster", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_installs_the_narrow_greeting_filter()
    {
        var entry = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ShadowProjection", "ModEntry.cs")
        );

        Assert.Contains("HostileShadowNpcGreetingPatch.TryInstall", entry, StringComparison.Ordinal);
        Assert.Contains(".npc-greeting", entry, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName)
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
