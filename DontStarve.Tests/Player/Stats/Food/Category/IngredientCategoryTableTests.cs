using System.Collections.Generic;
using System.Linq;
using DontStarve.Player.Stats.Food.Category;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food.Category;

/// <summary>
/// 食材属性表建表/派生/累加的契约测试。规则对应原版 AddIngredientValues
/// （DST cooking.lua:45-72）与 GetIngredientValues（cooking.lua:217-231）。
/// </summary>
public sealed class IngredientCategoryTableTests
{
    [Fact]
    public void DegreesAreKeptAndFoodTypeDefaultsToGeneric()
    {
        var table = Build(new Dictionary<string, IngredientEntryDto>
        {
            ["186"] = Dto(degrees: new Dictionary<string, double> { ["meat"] = 0.5 }),
        });

        Assert.True(table.TryGetEntry("186", out var entry));
        Assert.Equal(0.5, entry.Degrees["meat"]);
        Assert.Equal(FoodTypes.Generic, entry.FoodType);
        Assert.Null(entry.SecondaryFoodtype);
    }

    [Fact]
    public void CookVariantCopiesDegreesAndAddsPrecookMarker()
    {
        var table = Build(new Dictionary<string, IngredientEntryDto>
        {
            ["186"] = Dto(
                degrees: new Dictionary<string, double> { ["meat"] = 1 },
                cookVariants: new List<string> { "187" }
            ),
        });

        Assert.True(table.TryGetEntry("187", out var derived));
        Assert.Equal(1, derived.Degrees["meat"]);
        Assert.Equal(1, derived.Degrees["precook"]);
        // 派生变体继承源条目食物类型（原版熟肉与生肉 foodtype 一致）。
        Assert.Equal(FoodTypes.Generic, derived.FoodType);
        // 源条目本身不携带派生标记。
        Assert.False(table.TryGetEntry("186", out var source) && source.Degrees.ContainsKey("precook"));
    }

    [Fact]
    public void DryVariantAddsDriedMarker()
    {
        var table = Build(new Dictionary<string, IngredientEntryDto>
        {
            ["186"] = Dto(
                degrees: new Dictionary<string, double> { ["meat"] = 1, ["monster"] = 1 },
                dryVariants: new List<string> { "901" }
            ),
        });

        Assert.True(table.TryGetEntry("901", out var derived));
        Assert.Equal(1, derived.Degrees["dried"]);
        Assert.Equal(1, derived.Degrees["monster"]);
        Assert.False(derived.Degrees.ContainsKey("precook"));
    }

    [Fact]
    public void ExplicitEntryWinsOverDerivationAndWarns()
    {
        var warnings = new List<string>();
        var table = IngredientCategoryTable.Build(
            new Dictionary<string, IngredientEntryDto>
            {
                ["186"] = Dto(
                    degrees: new Dictionary<string, double> { ["meat"] = 1 },
                    cookVariants: new List<string> { "187" }
                ),
                ["187"] = Dto(degrees: new Dictionary<string, double> { ["meat"] = 0.5 }),
            },
            warnings.Add
        );

        Assert.True(table.TryGetEntry("187", out var entry));
        Assert.Equal(0.5, entry.Degrees["meat"]);
        Assert.False(entry.Degrees.ContainsKey("precook"));
        Assert.Contains(warnings, w => w.Contains("显式优先"));
    }

    [Fact]
    public void DuplicateVariantDeclarationWarnsAndFirstSourceWins()
    {
        var warnings = new List<string>();
        var table = IngredientCategoryTable.Build(
            new Dictionary<string, IngredientEntryDto>
            {
                ["186"] = Dto(
                    degrees: new Dictionary<string, double> { ["meat"] = 1 },
                    cookVariants: new List<string> { "187" }
                ),
                ["188"] = Dto(
                    degrees: new Dictionary<string, double> { ["meat"] = 2 },
                    cookVariants: new List<string> { "187" }
                ),
            },
            warnings.Add
        );

        Assert.True(table.TryGetEntry("187", out var entry));
        Assert.Equal(1, entry.Degrees["meat"]);
        Assert.Equal(1, entry.Degrees["precook"]);
        Assert.Contains(warnings, w => w.Contains("重复声明"));
    }

    [Fact]
    public void SelfVariantAndBlankVariantAreRejected()
    {
        var warnings = new List<string>();
        IngredientCategoryTable.Build(
            new Dictionary<string, IngredientEntryDto>
            {
                ["186"] = Dto(
                    degrees: new Dictionary<string, double> { ["meat"] = 1 },
                    cookVariants: new List<string> { "186", "" }
                ),
            },
            warnings.Add
        );

        Assert.Contains(warnings, w => w.Contains("把自己声明为变体"));
        Assert.Contains(warnings, w => w.Contains("空 id"));
    }

    [Fact]
    public void UnknownDegreeNameWarnsButIsKept()
    {
        var warnings = new List<string>();
        var table = IngredientCategoryTable.Build(
            new Dictionary<string, IngredientEntryDto>
            {
                ["186"] = Dto(degrees: new Dictionary<string, double> { ["meats"] = 1 }),
            },
            warnings.Add
        );

        // 原版对未知 tag 照常入表（只是没有菜谱引用），这里同样保留，仅告警防拼写手滑。
        Assert.True(table.TryGetEntry("186", out var entry));
        Assert.Equal(1, entry.Degrees["meats"]);
        Assert.Contains(warnings, w => w.Contains("meats"));
    }

    [Fact]
    public void SamePrimaryAndSecondaryFoodtypeDropsSecondary()
    {
        var warnings = new List<string>();
        var table = IngredientCategoryTable.Build(
            new Dictionary<string, IngredientEntryDto>
            {
                ["186"] = Dto(
                    degrees: new Dictionary<string, double> { ["meat"] = 1 },
                    foodType: FoodTypes.Meat,
                    secondaryFoodtype: FoodTypes.Meat
                ),
            },
            warnings.Add
        );

        Assert.True(table.TryGetEntry("186", out var entry));
        Assert.Null(entry.SecondaryFoodtype);
        Assert.Contains(warnings, w => w.Contains("副类型已丢弃"));
    }

    [Fact]
    public void GetIngredientValuesCountsNamesAndAccumulatesTags()
    {
        var table = Build(new Dictionary<string, IngredientEntryDto>
        {
            ["186"] = Dto(degrees: new Dictionary<string, double> { ["meat"] = 1, ["monster"] = 1 }),
            ["187"] = Dto(degrees: new Dictionary<string, double> { ["meat"] = 0.5, ["precook"] = 1 }),
        });

        var values = table.GetIngredientValues(new[] { "186", "187", "186", "999" });

        // 原版语义：所有出现过的 id 都计数（含未注册 999），度值只从已注册条目累加。
        Assert.Equal(2, values.Names["186"]);
        Assert.Equal(1, values.Names["187"]);
        Assert.Equal(1, values.Names["999"]);
        Assert.Equal(2.5, values.Tags["meat"]);
        // 186 出现两次，monster 度逐次累加 1+1。
        Assert.Equal(2, values.Tags["monster"]);
        Assert.Equal(1, values.Tags["precook"]);
        Assert.False(values.Tags.ContainsKey("nothing"));
    }

    [Fact]
    public void DerivedEntriesDoNotParticipateInFurtherDerivation()
    {
        // 派生变体不允许链式展开：变体声明只从显式条目读取（设计稿 §4.1）。
        var table = Build(new Dictionary<string, IngredientEntryDto>
        {
            ["186"] = Dto(
                degrees: new Dictionary<string, double> { ["meat"] = 1 },
                cookVariants: new List<string> { "187" }
            ),
        });

        Assert.False(table.TryGetEntry("187_cooked", out _));
    }

    private static IngredientCategoryTable Build(Dictionary<string, IngredientEntryDto> raw)
    {
        return IngredientCategoryTable.Build(raw, _ => { });
    }

    private static IngredientEntryDto Dto(
        Dictionary<string, double> degrees,
        string? foodType = null,
        string? secondaryFoodtype = null,
        List<string>? cookVariants = null,
        List<string>? dryVariants = null
    )
    {
        return new IngredientEntryDto
        {
            FoodType = foodType,
            SecondaryFoodtype = secondaryFoodtype,
            CookVariants = cookVariants,
            DryVariants = dryVariants,
            Degrees = new Dictionary<string, double>(degrees),
        };
    }
}
