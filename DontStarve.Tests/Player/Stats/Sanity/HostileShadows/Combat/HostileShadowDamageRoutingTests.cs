using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileShadowDamageRoutingTests
{
    [Fact]
    public void Guarded_shadow_does_not_cancel_vanilla_damage_for_other_targets()
    {
        var modEntry = Contract("ShadowProjection", "ModEntry.cs");
        var monster = Contract("HostileShadowAuthority", "HostileShadowMonster.cs");

        Assert.DoesNotContain(
            "HostileShadowDamageMonsterPatch",
            modEntry,
            StringComparison.Ordinal
        );

        var start = monster.IndexOf("public override bool isInvincible()", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var end = monster.IndexOf("public override void setTrajectory", start, StringComparison.Ordinal);
        Assert.True(end > start);

        var invincibility = monster[start..end];
        Assert.Contains("BindingHiddenModDataKey", invincibility, StringComparison.Ordinal);
        Assert.Contains("RetreatingModDataKey", invincibility, StringComparison.Ordinal);
    }

    private static string Contract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
