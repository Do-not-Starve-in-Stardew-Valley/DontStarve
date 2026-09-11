using System.Collections.Generic;
using DontStarve.Player.Stats.Food;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class SeedEdibilityRulesTests
{
    [Theory]
    [InlineData("472")]
    [InlineData("499")]
    [InlineData("770")]
    [InlineData("MixedFlowerSeeds")]
    [InlineData("495")]
    [InlineData("ModCrop.Seeds")]
    public void SeedsTypeIncludesVanillaMixedWildAncientAndModCropSeeds(string itemId)
    {
        Assert.Equal(
            SeedClassification.TargetCropSeed,
            SeedEdibilityRules.Classify(
                new SeedMetadataFacts(itemId, SeedEdibilityRules.SeedsObjectType, true)
            )
        );
    }

    [Theory]
    [InlineData("114", "Arch")]
    [InlineData("251", "Basic")]
    [InlineData("628", "Basic")]
    [InlineData("309", "Crafting")]
    public void NonCropSeedObjectsAreNotTargets(string itemId, string objectType)
    {
        Assert.Equal(
            SeedClassification.NotTarget,
            SeedEdibilityRules.Classify(new SeedMetadataFacts(itemId, objectType, true))
        );
    }

    [Fact]
    public void SproutingStoneFruitIsExcludedBeforeMetadataFallback()
    {
        Assert.Equal(
            SeedClassification.ExplicitlyExcluded,
            SeedEdibilityRules.Classify(
                new SeedMetadataFacts(
                    SeedEdibilityRules.ExcludedSproutingStoneFruitItemId,
                    null,
                    HasObjectData: false
                )
            )
        );
    }

    [Theory]
    [InlineData(null, "Seeds", true)]
    [InlineData("Unknown", null, true)]
    [InlineData("Unknown", "Seeds", false)]
    public void IncompleteMetadataFailsClosed(
        string? itemId,
        string? objectType,
        bool hasObjectData
    )
    {
        Assert.Equal(
            SeedClassification.Unknown,
            SeedEdibilityRules.Classify(
                new SeedMetadataFacts(itemId, objectType, hasObjectData)
            )
        );
    }

    [Theory]
    [InlineData(1, true, -300, 1)]
    [InlineData(1, false, -300, -300)]
    [InlineData(0, true, 7, 7)]
    [InlineData(2, true, -300, -300)]
    [InlineData(3, true, -300, -300)]
    public void EffectiveEdibilityChangesOnlyEnabledTargetSeeds(
        int classification,
        bool enabled,
        int nativeEdibility,
        int expected
    )
    {
        Assert.Equal(
            expected,
            SeedEdibilityRules.GetEffectiveEdibility(
                (SeedClassification)classification,
                enabled,
                nativeEdibility
            )
        );
    }

    [Fact]
    public void SeedFoodCategoryDefaultAndExactItemOverrideRemainDataDriven()
    {
        var resolver = new FoodRuleResolver(
            new Dictionary<string, double>
            {
                [SeedEdibilityRules.FoodRuleDefaultKey] = 4.6875,
                ["ModCrop.Seeds"] = 7.25,
            },
            new Dictionary<string, double>
            {
                [SeedEdibilityRules.FoodRuleDefaultKey] = 0,
            }
        );

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts(
                    "UnknownCrop.Seeds",
                    FoodRuleSourceKind.Seed,
                    null
                ),
                FoodValueDimension.Hunger,
                out var defaultHunger
            )
        );
        Assert.Equal(4.6875, defaultHunger);

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts("ModCrop.Seeds", FoodRuleSourceKind.Seed, null),
                FoodValueDimension.Hunger,
                out var exactHunger
            )
        );
        Assert.Equal(7.25, exactHunger);

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts(
                    "UnknownCrop.Seeds",
                    FoodRuleSourceKind.Seed,
                    null
                ),
                FoodValueDimension.Sanity,
                out var defaultSanity
            )
        );
        Assert.Equal(0, defaultSanity);
    }
}
