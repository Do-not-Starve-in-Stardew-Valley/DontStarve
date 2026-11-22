using System.Collections.Generic;
using DontStarve.Player.Stats.Food.Category;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food.Category;

/// <summary>
/// 锁定食物类型常量与原版的一致性：DST constants.lua:2026-2109 的 19 类型 + 5 组。
/// 未来 AI 误改常量或漏项时在这里报警。
/// </summary>
public sealed class FoodTypesTests
{
    [Fact]
    public void AllTypesMatchesVanillaDstSet()
    {
        var expected = new[]
        {
            "GENERIC", "MEAT", "VEGGIE", "ELEMENTAL", "GEARS", "HORRIBLE", "INSECT",
            "SEEDS", "BERRY", "RAW", "BURNT", "NITRE", "ROUGHAGE", "WOOD", "GOODIES",
            "MONSTER", "LUNAR_SHARDS", "CORPSE", "MIASMA",
        };

        Assert.Equal(expected, FoodTypes.AllTypes);
    }

    [Fact]
    public void AllTypesHaveNoDuplicates()
    {
        var seen = new HashSet<string>();
        foreach (var type in FoodTypes.AllTypes)
            Assert.True(seen.Add(type), $"重复的食物类型 {type}");
    }

    [Fact]
    public void OmniGroupMatchesVanilla()
    {
        Assert.Equal(
            new[] { "MEAT", "VEGGIE", "INSECT", "SEEDS", "GENERIC", "GOODIES" },
            FoodGroups.Omni
        );
    }

    [Fact]
    public void FoodGroupsMatchVanillaMemberships()
    {
        Assert.Equal(new[] { "SEEDS", "BERRY" }, FoodGroups.BerriesAndSeeds);
        Assert.Equal(new[] { "MEAT", "VEGGIE", "BERRY", "GENERIC" }, FoodGroups.Bearger);
        Assert.Equal(new[] { "MEAT", "VEGGIE", "SEEDS" }, FoodGroups.Moose);
        Assert.Equal(new[] { "VEGGIE", "SEEDS", "GENERIC", "GOODIES" }, FoodGroups.Vegetarian);
    }

    [Fact]
    public void EveryGroupMemberIsARegisteredFoodType()
    {
        var allTypes = new HashSet<string>(FoodTypes.AllTypes);
        foreach (var group in new[]
                 {
                     FoodGroups.Omni,
                     FoodGroups.BerriesAndSeeds,
                     FoodGroups.Bearger,
                     FoodGroups.Moose,
                     FoodGroups.Vegetarian,
                 })
        {
            foreach (var member in group)
                Assert.True(allTypes.Contains(member), $"组合包出现未注册类型 {member}");
        }
    }

    [Fact]
    public void KnownDegreesCoverAllFourVersions()
    {
        // 四版本源码并集 21 个（设计稿 §3.2）：本体 12 + RoG frozen/seed + 派生标记 dried/precook
        // + SW jellyfish + HM bug/antihistamine/bone/filter。数量或成员变化时必须先核对原版。
        Assert.Equal(21, KnownIngredientDegrees.Names.Count);
        Assert.Contains("meat", KnownIngredientDegrees.Names);
        Assert.Contains("filter", KnownIngredientDegrees.Names);
        Assert.Contains("precook", KnownIngredientDegrees.Names);
        Assert.DoesNotContain("meats", KnownIngredientDegrees.Names);
    }
}
