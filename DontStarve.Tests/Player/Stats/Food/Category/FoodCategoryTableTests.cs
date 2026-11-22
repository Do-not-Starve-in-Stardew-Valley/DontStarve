using System.Collections.Generic;
using DontStarve.Player.Stats.Food.Category;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food.Category;

/// <summary>
/// 成品食物表建表契约：默认值、主副类型冲突降级、perishDays 归一。
/// 对应原版 preparedfoods prefab 生成器（prefabs/preparedfoods.lua:113-147）与
/// 腐坏挂载条件（`data.perishtime ~= nil and data.perishtime > 0`）。
/// </summary>
public sealed class FoodCategoryTableTests
{
    [Fact]
    public void MissingEntryIsNotFound()
    {
        var table = Build(new Dictionary<string, FoodEntryDto>());

        Assert.False(table.TryGetEntry("220", out _));
    }

    [Fact]
    public void EmptyEntryFallsBackToVanillaDefaults()
    {
        var table = Build(new Dictionary<string, FoodEntryDto>
        {
            ["220"] = new FoodEntryDto(),
        });

        Assert.True(table.TryGetEntry("220", out var entry));
        // foodtype 缺省 GENERIC（原版 data.foodtype or FOODTYPE.GENERIC）。
        Assert.Equal(FoodTypes.Generic, entry.FoodType);
        Assert.Null(entry.SecondaryFoodtype);
        // perishDays 缺省 = 不腐坏。
        Assert.Null(entry.PerishDays);
    }

    [Fact]
    public void PrimaryAndSecondaryConflictDropsSecondaryAndWarns()
    {
        var warnings = new List<string>();
        var table = FoodCategoryTable.Build(
            new Dictionary<string, FoodEntryDto>
            {
                ["220"] = new FoodEntryDto
                {
                    FoodType = FoodTypes.Meat,
                    SecondaryFoodtype = FoodTypes.Meat,
                },
            },
            warnings.Add
        );

        Assert.True(table.TryGetEntry("220", out var entry));
        Assert.Equal(FoodTypes.Meat, entry.FoodType);
        Assert.Null(entry.SecondaryFoodtype);
        Assert.Contains(warnings, w => w.Contains("副类型已丢弃"));
    }

    [Fact]
    public void DistinctSecondaryIsKept()
    {
        // 原版仅 3 处主副类型用法，monsterlasagna 即 MEAT + MONSTER。
        var table = Build(new Dictionary<string, FoodEntryDto>
        {
            ["220"] = new FoodEntryDto
            {
                FoodType = FoodTypes.Meat,
                SecondaryFoodtype = FoodTypes.Monster,
            },
        });

        Assert.True(table.TryGetEntry("220", out var entry));
        Assert.Equal(FoodTypes.Monster, entry.SecondaryFoodtype);
    }

    [Fact]
    public void PerishDaysZeroMeansNeverPerish()
    {
        // 原版 perishtime=0 或 nil 都不挂腐坏组件（prefabs/preparedfoods.lua:142-147）。
        var table = Build(new Dictionary<string, FoodEntryDto>
        {
            ["220"] = new FoodEntryDto { PerishDays = 0 },
            ["222"] = new FoodEntryDto { PerishDays = 10 },
        });

        Assert.True(table.TryGetEntry("220", out var zero));
        Assert.Null(zero.PerishDays);

        Assert.True(table.TryGetEntry("222", out var ten));
        Assert.Equal(10, ten.PerishDays);
    }

    [Fact]
    public void NegativePerishDaysWarnsAndNormalizesToNeverPerish()
    {
        var warnings = new List<string>();
        var table = FoodCategoryTable.Build(
            new Dictionary<string, FoodEntryDto>
            {
                ["220"] = new FoodEntryDto { PerishDays = -3 },
            },
            warnings.Add
        );

        Assert.True(table.TryGetEntry("220", out var entry));
        Assert.Null(entry.PerishDays);
        Assert.Contains(warnings, w => w.Contains("perishDays 为负数"));
    }

    [Fact]
    public void NullEntryWarnsAndIsSkipped()
    {
        var warnings = new List<string>();
        var table = FoodCategoryTable.Build(
            new Dictionary<string, FoodEntryDto>
            {
                ["220"] = null!,
            },
            warnings.Add
        );

        Assert.False(table.TryGetEntry("220", out _));
        Assert.Contains(warnings, w => w.Contains("条目 220 为空"));
    }

    private static FoodCategoryTable Build(Dictionary<string, FoodEntryDto> raw)
    {
        return FoodCategoryTable.Build(raw, _ => { });
    }
}
