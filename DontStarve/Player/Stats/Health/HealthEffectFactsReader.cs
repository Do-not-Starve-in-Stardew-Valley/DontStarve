#nullable enable

using DontStarve.Player.Stats.Food;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 决议 10 实施要求 A 的边界层：把 SObject 的事实抽成纯 record（HealthEffectFacts），
/// 让结算（Health.OnFoodSettled）与 tooltip（SmapiHealthTooltipPatch）从同一个入口取数。
/// edibility 一律走 SeedEdibilityRuntime.ResolveEffectiveEdibility、体力镜像一律走
/// ResolveEffectiveStaminaRecovered（同源原则，防重演 -750 能量事故）；JSON 与特殊公式
/// 命中判定分别走 FoodRuleRuntime / HealthFoodOverrideRules，与结算完全同源。
/// 本文件含游戏边界类型，不参与纯规则单测（纯规则在 HealthEffectResolution.cs）。
/// </summary>
internal static class HealthEffectFactsReader
{
    internal static HealthEffectFacts Read(
        SObject food,
        int effectiveEdibility,
        int staminaMirror
    )
    {
        var hasSpecialFormula = HealthFoodOverrideRules.TryResolveSpecial(
            food,
            out var specialFormulaValue
        );
        var hasJsonEntry = FoodRuleRuntime.TryGetHealth(food, out var jsonEntryValue);

        return new HealthEffectFacts(
            food.QualifiedItemId,
            food.Quality,
            effectiveEdibility,
            staminaMirror,
            hasSpecialFormula,
            specialFormulaValue,
            hasJsonEntry,
            jsonEntryValue
        );
    }
}
