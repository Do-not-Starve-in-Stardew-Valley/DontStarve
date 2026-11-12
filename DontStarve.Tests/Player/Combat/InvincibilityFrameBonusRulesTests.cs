using DontStarve.Player.Combat.InvincibilityFrames;
using Xunit;

namespace DontStarve.Tests.Player.Combat;

public sealed class InvincibilityFrameBonusRulesTests
{
    [Fact]
    public void Contributions_are_summed_in_input_order()
    {
        var result = InvincibilityFrameBonusRules.TryAggregate(
            new[]
            {
                new InvincibilityFrameBonusContribution("protective-ring", 400),
                new InvincibilityFrameBonusContribution("future-ring", 125),
            },
            out var total,
            out var reason
        );

        Assert.True(result, reason);
        Assert.Equal(525, total);
    }

    [Fact]
    public void Duplicate_source_ids_are_rejected()
    {
        var result = InvincibilityFrameBonusRules.TryAggregate(
            new[]
            {
                new InvincibilityFrameBonusContribution("same", 400),
                new InvincibilityFrameBonusContribution("same", 400),
            },
            out _,
            out var reason
        );

        Assert.False(result);
        Assert.Equal("invincibility-frame.bonus-source-duplicate", reason);
    }

    [Fact]
    public void Negative_contribution_fails_closed()
    {
        var result = InvincibilityFrameBonusRules.TryAggregate(
            new[] { new InvincibilityFrameBonusContribution("bad", -1) },
            out _,
            out var reason
        );

        Assert.False(result);
        Assert.Equal("invincibility-frame.bonus-negative", reason);
    }

    [Fact]
    public void Yoba_is_not_a_duration_provider()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "PlayerCombat",
                "ProtectiveRingInvincibilityFrameBonusSource.cs"
            )
        );

        Assert.Contains("ProtectiveRingId", source, StringComparison.Ordinal);
        Assert.Contains("400", source, StringComparison.Ordinal);
        Assert.DoesNotContain("YobaRingId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Buff21", source, StringComparison.Ordinal);
    }
}
