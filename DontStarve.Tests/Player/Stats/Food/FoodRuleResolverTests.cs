using System.Collections.Generic;
using DontStarve.Player.Stats.Food;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class FoodRuleResolverTests
{
    [Fact]
    public void CompositeDriedFruitEntryWinsOverFormula()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["DriedFruit"] = 78.125,
                ["282"] = 9.375,
                ["DriedFruit/282"] = 68,
            },
            sanity: new Dictionary<string, double>
            {
                ["DriedFruit"] = 50,
                ["282"] = 1,
                ["DriedFruit/282"] = 68,
            }
        );

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts("DriedFruit", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Sanity,
                out var value
            )
        );
        Assert.Equal(68, value);
    }

    [Fact]
    public void CompositeHoneyEntryWinsOverCategoryDefault()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["340"] = 9.375,
                ["340/591"] = 4.5,
            },
            sanity: new Dictionary<string, double>
            {
                ["340"] = 0,
                ["340/591"] = 3,
            }
        );

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts("340", FoodRuleSourceKind.Honey, "591"),
                FoodValueDimension.Sanity,
                out var value
            )
        );
        Assert.Equal(3, value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(2.75)]
    public void ExplicitZeroNegativeAndFractionalValuesArePresentValues(double expected)
    {
        var resolver = Create(
            hunger: new Dictionary<string, double> { ["item"] = expected },
            sanity: new Dictionary<string, double>()
        );

        Assert.True(
            resolver.TryResolve(
                new FoodItemFacts("item", FoodRuleSourceKind.None, null),
                FoodValueDimension.Hunger,
                out var value
            )
        );
        Assert.Equal(expected, value);
    }

    [Fact]
    public void JuiceUsesTwoAndHalfTimesSourceHungerAndCategorySanityDefault()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["350"] = 23.4,
                ["24"] = 12.5,
            },
            sanity: new Dictionary<string, double> { ["350"] = 5, ["24"] = 3 }
        );
        var facts = new FoodItemFacts("350", FoodRuleSourceKind.Juice, "24");

        Assert.Equal(31.25, Resolve(resolver, facts, FoodValueDimension.Hunger));
        Assert.Equal(5, Resolve(resolver, facts, FoodValueDimension.Sanity));
    }

    [Fact]
    public void PickleAndJellyInheritSourceHunger()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["342"] = 12.5,
                ["344"] = 9.375,
                ["24"] = 12.5,
                ["282"] = 9.375,
            },
            sanity: new Dictionary<string, double> { ["342"] = 10, ["344"] = 10 }
        );

        Assert.Equal(
            12.5,
            Resolve(
                resolver,
                new FoodItemFacts("342", FoodRuleSourceKind.Pickle, "24"),
                FoodValueDimension.Hunger
            )
        );
        Assert.Equal(
            9.375,
            Resolve(
                resolver,
                new FoodItemFacts("344", FoodRuleSourceKind.Jelly, "282"),
                FoodValueDimension.Hunger
            )
        );
    }

    [Fact]
    public void DriedFruitAndSmokedFishUseTheirDimensionSpecificFormulas()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["DriedFruit"] = 78.125,
                ["SmokedFish"] = 22.5,
                ["282"] = 9.375,
                ["698"] = 37.5,
            },
            sanity: new Dictionary<string, double>
            {
                ["DriedFruit"] = 50,
                ["SmokedFish"] = 20,
                ["282"] = 1,
                ["698"] = 10,
            }
        );

        Assert.Equal(
            28.125,
            Resolve(
                resolver,
                new FoodItemFacts("DriedFruit", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Hunger
            )
        );
        Assert.Equal(
            53,
            Resolve(
                resolver,
                new FoodItemFacts("DriedFruit", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Sanity
            )
        );
        Assert.Equal(
            37.5,
            Resolve(
                resolver,
                new FoodItemFacts("SmokedFish", FoodRuleSourceKind.SmokedFish, "698"),
                FoodValueDimension.Hunger
            )
        );
        Assert.Equal(
            30,
            Resolve(
                resolver,
                new FoodItemFacts("SmokedFish", FoodRuleSourceKind.SmokedFish, "698"),
                FoodValueDimension.Sanity
            )
        );
    }

    [Fact]
    public void AgedRoeAndRoeRemainSeparateFamilies()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["447"] = 9.375,
                ["812"] = 4.6875,
                ["812/698"] = 4.6875,
            },
            sanity: new Dictionary<string, double>
            {
                ["447"] = 5,
                ["812"] = 0,
                ["812/698"] = 0,
            }
        );

        var agedRoe = new FoodItemFacts("447", FoodRuleSourceKind.AgedRoe, "698");
        var roe = new FoodItemFacts("812", FoodRuleSourceKind.Roe, "698");

        Assert.Equal(9.375, Resolve(resolver, agedRoe, FoodValueDimension.Hunger));
        Assert.Equal(5, Resolve(resolver, agedRoe, FoodValueDimension.Sanity));
        Assert.Equal(4.6875, Resolve(resolver, roe, FoodValueDimension.Hunger));
        Assert.Equal(0, Resolve(resolver, roe, FoodValueDimension.Sanity));
    }

    [Fact]
    public void MissingSourceFallsBackToKnownCategoryDefault()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["350"] = 23.4,
                ["342"] = 12.5,
            },
            sanity: new Dictionary<string, double>
            {
                ["350"] = 5,
                ["342"] = 10,
            }
        );

        Assert.Equal(
            23.4,
            Resolve(
                resolver,
                new FoodItemFacts("350", FoodRuleSourceKind.Juice, "missing"),
                FoodValueDimension.Hunger
            )
        );
        Assert.Equal(
            10,
            Resolve(
                resolver,
                new FoodItemFacts("342", FoodRuleSourceKind.Pickle, null),
                FoodValueDimension.Sanity
            )
        );
    }

    [Fact]
    public void UnknownCategoryDoesNotBorrowAnotherCategoryRule()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["350"] = 23.4,
                ["24"] = 12.5,
            },
            sanity: new Dictionary<string, double>()
        );

        Assert.False(
            resolver.TryResolve(
                new FoodItemFacts("unknown", FoodRuleSourceKind.None, "24"),
                FoodValueDimension.Hunger,
                out _
            )
        );
    }

    [Fact]
    public void NonDefaultItemOverrideStillWinsBeforeItsFamilyFormula()
    {
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["Raisins"] = 37.5,
                ["282"] = 9.375,
            },
            sanity: new Dictionary<string, double>()
        );

        Assert.Equal(
            37.5,
            Resolve(
                resolver,
                new FoodItemFacts("Raisins", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Hunger
            )
        );
    }

    [Fact]
    public void HealthDimensionUsesExcelFamilyFormulas()
    {
        // 2026-11-14 用户规则（Excel 生命值列）：腌菜+10、果汁5倍绝对值、果干5倍+50、
        // 蘑菇干3倍、熏鱼+20；「原来数值」= 源食材在健康表里的生命值（与饥饿/理智机器同源）。
        var resolver = Create(
            hunger: new Dictionary<string, double>
            {
                ["24"] = 12.5,
                ["282"] = 9.375,
                ["698"] = 37.5,
            },
            sanity: new Dictionary<string, double>
            {
                ["24"] = 3,
                ["282"] = 1,
                ["698"] = 10,
            },
            health: new Dictionary<string, double>
            {
                ["24"] = -3,
                ["282"] = 3,
                ["698"] = 25,
            }
        );

        Assert.Equal(
            7,
            Resolve(
                resolver,
                new FoodItemFacts("342", FoodRuleSourceKind.Pickle, "24"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            15,
            Resolve(
                resolver,
                new FoodItemFacts("350", FoodRuleSourceKind.Juice, "24"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            65,
            Resolve(
                resolver,
                new FoodItemFacts("DriedFruit", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            75,
            Resolve(
                resolver,
                new FoodItemFacts("DriedMushrooms", FoodRuleSourceKind.DriedMushroom, "698"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            45,
            Resolve(
                resolver,
                new FoodItemFacts("SmokedFish", FoodRuleSourceKind.SmokedFish, "698"),
                FoodValueDimension.Health
            )
        );
    }

    [Fact]
    public void HealthDimensionHasNoFormulaForUnruledFamiliesAndKeepsExplicitKeys()
    {
        // 酒/果冻/鱼籽/陈年鱼籽/蜂蜜无健康公式（统一值走显式基键回退）；显式复合键压过公式。
        var resolver = Create(
            hunger: new Dictionary<string, double> { ["282"] = 9.375 },
            sanity: new Dictionary<string, double> { ["282"] = 1 },
            health: new Dictionary<string, double>
            {
                ["348"] = 10,
                ["344"] = 5,
                ["447"] = 5,
                ["812"] = 1,
                ["DriedFruit/282"] = 7,
            }
        );

        Assert.Equal(
            10,
            Resolve(
                resolver,
                new FoodItemFacts("348", FoodRuleSourceKind.Wine, "282"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            5,
            Resolve(
                resolver,
                new FoodItemFacts("344", FoodRuleSourceKind.Jelly, "282"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            5,
            Resolve(
                resolver,
                new FoodItemFacts("447", FoodRuleSourceKind.AgedRoe, "698"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            1,
            Resolve(
                resolver,
                new FoodItemFacts("812", FoodRuleSourceKind.Roe, "698"),
                FoodValueDimension.Health
            )
        );
        Assert.Equal(
            7,
            Resolve(
                resolver,
                new FoodItemFacts("DriedFruit", FoodRuleSourceKind.DriedFruit, "282"),
                FoodValueDimension.Health
            )
        );
    }

    private static FoodRuleResolver Create(
        IReadOnlyDictionary<string, double> hunger,
        IReadOnlyDictionary<string, double> sanity,
        IReadOnlyDictionary<string, double>? health = null
    )
    {
        return new FoodRuleResolver(hunger, sanity, health);
    }

    private static double Resolve(
        FoodRuleResolver resolver,
        FoodItemFacts facts,
        FoodValueDimension dimension
    )
    {
        Assert.True(resolver.TryResolve(facts, dimension, out var value));
        return value;
    }
}
