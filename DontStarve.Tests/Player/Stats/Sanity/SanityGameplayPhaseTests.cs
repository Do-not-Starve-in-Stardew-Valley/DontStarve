using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityGameplayPhaseTests
{
    [Theory]
    [InlineData(0.500001d, (int)SanityGameplayPhase.Above50)]
    [InlineData(0.5d, (int)SanityGameplayPhase.ThresholdBoundary)]
    [InlineData(0.150001d, (int)SanityGameplayPhase.Harmless50)]
    [InlineData(0.15d, (int)SanityGameplayPhase.ThresholdBoundary)]
    [InlineData(0.100001d, (int)SanityGameplayPhase.Hostile15)]
    [InlineData(0.1d, (int)SanityGameplayPhase.ThresholdBoundary)]
    [InlineData(0d, (int)SanityGameplayPhase.Hostile10)]
    public void Ratio_contract_uses_mutually_exclusive_strict_boundaries(
        double ratio,
        int expected
    )
    {
        Assert.True(
            SanityGameplayPhaseCatalog.TryResolve(ratio, out var actual)
        );
        Assert.Equal((SanityGameplayPhase)expected, actual);
    }

    [Theory]
    [InlineData(-0.001d)]
    [InlineData(1.001d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Ratio_contract_rejects_values_outside_a_valid_sanity_ratio(
        double ratio
    )
    {
        Assert.False(
            SanityGameplayPhaseCatalog.TryResolve(ratio, out _)
        );
    }

    [Theory]
    [InlineData((int)SanityGameplayPhase.Above50, (int)SanityShadowPoolTier.Inactive)]
    [InlineData((int)SanityGameplayPhase.Harmless50, (int)SanityShadowPoolTier.Harmless50)]
    [InlineData((int)SanityGameplayPhase.Hostile15, (int)SanityShadowPoolTier.Hostile15)]
    [InlineData((int)SanityGameplayPhase.Hostile10, (int)SanityShadowPoolTier.Hostile10)]
    [InlineData((int)SanityGameplayPhase.ThresholdBoundary, (int)SanityShadowPoolTier.Inactive)]
    public void Ratio_contract_maps_to_the_matching_shadow_pool(
        int phase,
        int expected
    )
    {
        Assert.Equal(
            (SanityShadowPoolTier)expected,
            SanityGameplayPhaseCatalog.ToShadowPoolTier(
                (SanityGameplayPhase)phase
            )
        );
    }
}
