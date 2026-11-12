using DontStarve.Player.Combat.InvincibilityFrames;
using Xunit;

namespace DontStarve.Tests.Player.Combat;

public sealed class InvincibilityFrameRulesTests
{
    [Fact]
    public void Normal_policy_preserves_vanilla_duration()
    {
        var result = InvincibilityFrameRules.ResolveDuration(
            InvincibilityFramePolicy.Normal,
            bonusMilliseconds: 800
        );

        Assert.True(result.Success);
        Assert.False(result.OverridesVanilla);
        Assert.Equal(0, result.DurationMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void Strong_policy_uses_fixed_duration_and_ignores_bonuses(int duration)
    {
        var result = InvincibilityFrameRules.ResolveDuration(
            InvincibilityFramePolicy.Strong(duration),
            bonusMilliseconds: 800
        );

        Assert.True(result.Success);
        Assert.True(result.OverridesVanilla);
        Assert.Equal(duration, result.DurationMilliseconds);
    }

    [Fact]
    public void Weak_policy_adds_registered_bonus()
    {
        var result = InvincibilityFrameRules.ResolveDuration(
            InvincibilityFramePolicy.Weak(300),
            bonusMilliseconds: 800
        );

        Assert.True(result.Success);
        Assert.True(result.OverridesVanilla);
        Assert.Equal(1100, result.DurationMilliseconds);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(300, -1)]
    public void Negative_policy_or_bonus_fails_closed(int duration, int bonus)
    {
        var policy = duration < 0
            ? InvincibilityFramePolicy.Strong(duration)
            : InvincibilityFramePolicy.Weak(duration);

        var result = InvincibilityFrameRules.ResolveDuration(policy, bonus);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void Existing_invincibility_blocks_the_hit_before_policy_resolution()
    {
        var outcome = InvincibilityFrameRules.Classify(
            new InvincibilityFrameObservation(
                CanBeDamagedBefore: false,
                TemporarilyInvincibleBefore: true,
                HealthBefore: 100,
                HealthAfter: 100,
                YobaBuffBefore: false,
                YobaBuffAfter: false,
                VanillaInvincibilityGranted: false
            )
        );

        Assert.Equal(
            InvincibilityFrameDamageOutcome.BlockedByExistingInvincibility,
            outcome
        );
    }

    [Fact]
    public void Yoba_prevention_is_not_treated_as_a_duration_override()
    {
        var outcome = InvincibilityFrameRules.Classify(
            new InvincibilityFrameObservation(
                CanBeDamagedBefore: true,
                TemporarilyInvincibleBefore: false,
                HealthBefore: 100,
                HealthAfter: 100,
                YobaBuffBefore: false,
                YobaBuffAfter: true,
                VanillaInvincibilityGranted: false
            )
        );

        Assert.Equal(InvincibilityFrameDamageOutcome.YobaPreventedDamage, outcome);
    }

    [Fact]
    public void Accepted_vanilla_hit_is_eligible_for_strong_or_weak_override()
    {
        var outcome = InvincibilityFrameRules.Classify(
            new InvincibilityFrameObservation(
                CanBeDamagedBefore: true,
                TemporarilyInvincibleBefore: false,
                HealthBefore: 100,
                HealthAfter: 90,
                YobaBuffBefore: false,
                YobaBuffAfter: false,
                VanillaInvincibilityGranted: true
            )
        );

        Assert.Equal(InvincibilityFrameDamageOutcome.Applied, outcome);
    }
}
