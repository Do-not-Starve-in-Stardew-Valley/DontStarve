#nullable enable

using System.Collections.Generic;

namespace DontStarve.Player.Stats.Food.Category;

/// <summary>
/// 原版食物类型常量。值逐字对应 DST <c>constants.lua:2026-2047</c> 的 FOODTYPE（19 项）。
/// 注意契约：foodtype 在原版是「自由字符串」（单机版整族没有常量表，edible.foodtype 默认
/// "GENERIC"），因此这里只锁 DST 官方集合供代码引用，运行期不做枚举校验；单机专属值
/// （TASTY/BONE/GOLDDUST 等）是合法字符串，由 JSON 直接书写（设计稿 §3.1，待确认问题5）。
/// </summary>
internal static class FoodTypes
{
    internal const string Generic = "GENERIC";
    internal const string Meat = "MEAT";
    internal const string Veggie = "VEGGIE";
    internal const string Elemental = "ELEMENTAL";
    internal const string Gears = "GEARS";
    internal const string Horrible = "HORRIBLE";
    internal const string Insect = "INSECT";
    internal const string Seeds = "SEEDS";
    internal const string Berry = "BERRY";
    internal const string Raw = "RAW";
    internal const string Burnt = "BURNT";
    internal const string Nitre = "NITRE";
    internal const string Roughage = "ROUGHAGE";
    internal const string Wood = "WOOD";
    internal const string Goodies = "GOODIES";
    internal const string Monster = "MONSTER";
    internal const string LunarShards = "LUNAR_SHARDS";
    internal const string Corpse = "CORPSE";
    internal const string Miasma = "MIASMA";

    /// <summary>全部官方类型，顺序与 constants.lua 一致；仅供遍历/测试，不做校验白名单用。</summary>
    internal static readonly IReadOnlyList<string> AllTypes = new[]
    {
        Generic,
        Meat,
        Veggie,
        Elemental,
        Gears,
        Horrible,
        Insect,
        Seeds,
        Berry,
        Raw,
        Burnt,
        Nitre,
        Roughage,
        Wood,
        Goodies,
        Monster,
        LunarShards,
        Corpse,
        Miasma,
    };
}

/// <summary>
/// 原版食物类型组合包。值与成员逐字对应 DST <c>constants.lua:2049-2109</c> 的 FOODGROUP（5 组）。
/// 消费契约对齐 eater 组件：diet 列表元素可以是单类型或整组（DST eater.lua:22-24）。
/// </summary>
internal static class FoodGroups
{
    internal static readonly IReadOnlyList<string> Omni = new[]
    {
        FoodTypes.Meat,
        FoodTypes.Veggie,
        FoodTypes.Insect,
        FoodTypes.Seeds,
        FoodTypes.Generic,
        FoodTypes.Goodies,
    };

    internal static readonly IReadOnlyList<string> BerriesAndSeeds = new[]
    {
        FoodTypes.Seeds,
        FoodTypes.Berry,
    };

    internal static readonly IReadOnlyList<string> Bearger = new[]
    {
        FoodTypes.Meat,
        FoodTypes.Veggie,
        FoodTypes.Berry,
        FoodTypes.Generic,
    };

    internal static readonly IReadOnlyList<string> Moose = new[]
    {
        FoodTypes.Meat,
        FoodTypes.Veggie,
        FoodTypes.Seeds,
    };

    internal static readonly IReadOnlyList<string> Vegetarian = new[]
    {
        FoodTypes.Veggie,
        FoodTypes.Seeds,
        FoodTypes.Generic,
        FoodTypes.Goodies,
    };
}

/// <summary>
/// 四版本源码实际出现过的「度」名并集（设计稿 §3.2，21 个；wiki「茶滤度」源码键为 filter）。
/// 仅用于加载期打 Debug 告警防手滑键（如 meats→meat），不封死扩展：度在原版就是自由键+数值，
/// 玩家自创新度属合法输入，告警措辞按「不在原版已知列表」提示而非报错。
/// </summary>
internal static class KnownIngredientDegrees
{
    // HashSet：加载期逐键查存在性，避免 LINQ Contains 分配。
    internal static readonly HashSet<string> Names = new()
    {
        "meat",
        "monster",
        "fish",
        "fruit",
        "veggie",
        "egg",
        "sweetener",
        "dairy",
        "magic",
        "fat",
        "decoration",
        "inedible",
        "frozen",
        "seed",
        "dried",
        "precook",
        "jellyfish",
        "bug",
        "antihistamine",
        "bone",
        "filter",
    };
}
