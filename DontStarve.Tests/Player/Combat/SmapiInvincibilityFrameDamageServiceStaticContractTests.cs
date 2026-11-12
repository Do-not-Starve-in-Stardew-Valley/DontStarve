using Xunit;

namespace DontStarve.Tests.Player.Combat;

public sealed class SmapiInvincibilityFrameDamageServiceStaticContractTests
{
    [Fact]
    public void Adapter_uses_vanilla_take_damage_and_owns_only_invincibility_fields()
    {
        var source = Contract();

        Assert.Contains("target.takeDamage", source, StringComparison.Ordinal);
        Assert.Contains("target.temporarilyInvincible", source, StringComparison.Ordinal);
        Assert.Contains("target.temporaryInvincibilityTimer", source, StringComparison.Ordinal);
        Assert.Contains(
            "target.currentTemporaryInvincibilityDuration",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("target.health =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NonLethalDamage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("playSound", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_preserves_normal_policy_and_clears_zero_duration()
    {
        var source = Contract();

        Assert.Contains(
            "InvincibilityFramePolicyMode.Normal",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "durationMilliseconds <= 0",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "target.temporarilyInvincible = false",
            source,
            StringComparison.Ordinal
        );
    }

    private static string Contract()
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "PlayerCombat",
                "SmapiInvincibilityFrameDamageService.cs"
            )
        );
    }
}
