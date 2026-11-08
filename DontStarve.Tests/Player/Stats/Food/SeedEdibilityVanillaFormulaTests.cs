using DontStarve.Player.Stats.Food;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

/// <summary>
/// Locks the pure mirrors of vanilla Object.staminaRecoveredOnConsumption /
/// healthRecoveredOnConsumption. These functions are the single source of truth that both the
/// Tooltip eligibility gate and the corrected numeric seams resolve through, so a regression here
/// is exactly what would let a "-750 Energy" row reappear.
/// </summary>
public sealed class SeedEdibilityVanillaFormulaTests
{
    private const string SeedId = "(O)472";

    [Theory]
    // The fan-visible failure mode: raw sentinel reached the numeric seam.
    [InlineData(SeedId, -300, 0, -750)]
    // The designed value: 1 * 2.5 -> Ceiling -> 3.
    [InlineData(SeedId, 1, 0, 3)]
    // Quality participates: Ceiling(2.5) + 1 * 1 = 4.
    [InlineData(SeedId, 1, 1, 4)]
    // Silver/Gold behave the same way for higher qualities.
    [InlineData(SeedId, 1, 2, 5)]
    public void StaminaMirrorsVanillaFormulaIncludingQuality(
        string itemId,
        int edibility,
        int quality,
        int expected
    )
    {
        Assert.Equal(
            expected,
            SeedEdibilityRules.GetVanillaStaminaRecovered(itemId, edibility, quality)
        );
    }

    [Theory]
    [InlineData("(O)773", 0)]
    [InlineData("(O)434", 999)]
    public void StaminaKeepsVanillaSpecialCases(string itemId, int expected)
    {
        Assert.Equal(
            expected,
            SeedEdibilityRules.GetVanillaStaminaRecovered(itemId, 5, 0)
        );
    }

    [Theory]
    // Vanilla's own guard: a negative Edibility never yields a Health row.
    [InlineData(SeedId, -300, 0, 0)]
    // Designed seed: (int)(3 * 0.45) = 1.
    [InlineData(SeedId, 1, 0, 1)]
    public void HealthMirrorsVanillaNegativeGuardAndDefaultRatio(
        string itemId,
        int edibility,
        int quality,
        int expected
    )
    {
        Assert.Equal(
            expected,
            SeedEdibilityRules.GetVanillaHealthRecovered(itemId, edibility, quality)
        );
    }

    [Theory]
    [InlineData("(O)874", 5, 0, 8)] // 68% of Ceiling(12.5) = 8
    [InlineData("(O)434", 5, 0, 0)] // Stardrop: no Health
    [InlineData("(O)349", 5, 0, 0)] // Vanilla hardcodes 0 Health
    [InlineData("(O)773", 5, 0, 999)] // Vanilla hardcodes 999 Health
    public void HealthKeepsVanillaSpecialCases(
        string itemId,
        int edibility,
        int quality,
        int expected
    )
    {
        Assert.Equal(
            expected,
            SeedEdibilityRules.GetVanillaHealthRecovered(itemId, edibility, quality)
        );
    }

    [Fact]
    public void QualifyObjectItemIdMatchesVanillaShape()
    {
        Assert.Equal("(O)472", SeedEdibilityRules.QualifyObjectItemId("472"));
        Assert.Equal(
            SeedEdibilityRules.QualifyObjectItemId(SeedEdibilityRules.StardropItemId),
            "(O)434"
        );
    }

    [Fact]
    public void NativeSentinelMatchesVanillaBackingFieldDefault()
    {
        // Object.edibility is initialised to -300 in vanilla; the sentinel must stay in sync or the
        // eligibility gate would start admitting raw inedible objects.
        Assert.Equal(-300, SeedEdibilityRules.NativeInedibleEdibility);
    }
}
