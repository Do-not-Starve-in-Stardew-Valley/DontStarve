#nullable enable

using System;
using DontStarve.Player.Stats.Food;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 血量效果值的来源类别（决议 2 优先级链的标记）。tooltip 与结算共用同一解析结果，
/// 类别标记让 tooltip 能区分「该注入真值」与「维持原版」的边界（决议 10）。
/// </summary>
internal enum HealthEffectSource
{
    /// <summary>-300（inedible）天然豁免：不能吃、无行、无效果，tooltip 维持原版。</summary>
    None,

    /// <summary>物品专属特殊公式（v1 恒未命中，接缝已留在 HealthFoodOverrideRules）。</summary>
    SpecialFormula,

    /// <summary>JSON 条目命中（Asset/Health/food.json）：效果完全等于 JSON 值。</summary>
    JsonEntry,

    /// <summary>无条目正食默认：刀前真值 = 体力镜像 × 0.45 未截断（决议 2 三轮全线口径）。</summary>
    KnifeBefore,

    /// <summary>无条目负食默认：−|体力镜像| × 0.45（决议 1，原版负食不扣血由本系统补上）。</summary>
    NegativeFood,
}

/// <summary>
/// SObject 事实的纯投影（决议 10 实施要求 A）：边界层（HealthEffectFactsReader）把
/// effectiveEdibility、quality、qualifiedItemId、体力镜像值与 JSON/特殊公式命中情况抽成本
/// record，纯规则层只看本 record，不接触任何游戏类型。HasSpecialFormula/SpecialFormulaValue
/// 与 QualifiedItemId/Quality 在 v1 是未来特殊公式接缝的预留位（HealthFoodOverrideRules 恒
/// 未命中），但一旦命中就必须参与优先级解析——结算与 tooltip 都走这一处，保证
/// 「tooltip 与结算说同一句话」不会随优先级链扩展而分叉。
/// </summary>
internal readonly record struct HealthEffectFacts(
    string QualifiedItemId,
    int Quality,
    int EffectiveEdibility,
    int StaminaMirror,
    bool HasSpecialFormula,
    double SpecialFormulaValue,
    bool HasJsonEntry,
    double JsonEntryValue
);

/// <summary>解析产物：效果真值 + 来源类别。EffectValue 是「这一口会记进账本的真值」。</summary>
internal readonly record struct HealthEffectResolution(
    double EffectValue,
    HealthEffectSource Source
);

/// <summary>
/// 血量效果值解析的纯规则（决议 2 / 决议 10 共用，结算与 tooltip 的唯一取值口）。
/// 优先级冻结为：特殊公式 &gt; JSON 条目 &gt; 默认（无条目正食 = 刀前真值，无条目负食 = 负食公式）。
/// </summary>
internal static class HealthEffectResolver
{
    internal static HealthEffectResolution Resolve(HealthEffectFacts facts)
    {
        // -300 = inedible：不能吃、不触发进食动画，天然豁免（决议 10）。
        if (facts.EffectiveEdibility == SeedEdibilityRules.NativeInedibleEdibility)
            return new HealthEffectResolution(0, HealthEffectSource.None);

        // 优先级（决议 2）：特殊公式 > JSON 条目 > 默认；JSON 命中时不再走公式
        // （JSON 与公式同时存在听 JSON）。
        if (facts.HasSpecialFormula)
            return new HealthEffectResolution(
                facts.SpecialFormulaValue,
                HealthEffectSource.SpecialFormula
            );

        if (facts.HasJsonEntry)
            return new HealthEffectResolution(
                facts.JsonEntryValue,
                HealthEffectSource.JsonEntry
            );

        if (facts.EffectiveEdibility < 0)
        {
            // 决议 1：负食扣血 = |体力变化| × 0.45，效果真值为负数。
            // 体力照原版扣，血由本系统补上（原版负食 healthRecovered 返回 0）。
            return new HealthEffectResolution(
                -HealthRules.CalculateNegativeFoodHealthDamage(facts.StaminaMirror),
                HealthEffectSource.NegativeFood
            );
        }

        // 正食默认（决议 2 三轮全线口径）：刀前真值 = 体力变化 × 0.45，未截断。
        return new HealthEffectResolution(
            HealthRules.CalculateKnifeBeforeTrueValue(facts.StaminaMirror),
            HealthEffectSource.KnifeBefore
        );
    }
}

/// <summary>
/// 决议 10 tooltip 生命行的纯规则：负食分支是否补画生命行、原版生命值是否出自普通公式。
/// 高度计算（drawHoverText 的 num2 三元式）与行绘制（负食 else-if 分支）必须共同经由
/// 这里联动——高度三元式只给正数生命行预留高度，两处任何一处单改都会文字溢框或留白。
/// </summary>
internal static class HealthTooltipRules
{
    /// <summary>原版每行推进 34px（能量/生命行；见 SanityTooltipLayoutContract.CustomSurvivalRowHeight 同源）。</summary>
    internal const int VanillaSurvivalRowAdvance = 34;

    /// <summary>原版每行预留 40px（drawHoverText 高度三元式的 +40 / +40×行数）。</summary>
    internal const int VanillaSurvivalRowHeightReserve = 40;

    /// <summary>
    /// 原版负食分支（num9 &lt; 0 且 != -300）是否需要本系统补画生命行。必须逐条镜像原版
    /// drawHoverText 负食分支的两个跳过条件，否则高度多预留 40 而行没画（或反之）：
    /// - stamina == -300：原版 else-if 整体不画（连负能量行都不画）；
    /// - stamina == -1：healAmountToDisplay 门不进，整个行区不画。
    /// -300 物品（effectiveEdibility == -300）在更高的资格门就被排除，这里再挡一次是防御。
    /// </summary>
    internal static bool ShouldAppendNegativeFoodHealthRow(
        int effectiveEdibility,
        int staminaMirror
    )
    {
        if (effectiveEdibility == SeedEdibilityRules.NativeInedibleEdibility)
            return false;

        if (effectiveEdibility >= 0)
            return false;

        // 原版负食分支自身的两个跳过条件（镜像 IClickableMenu.cs:1311）。
        return staminaMirror != -300
            && staminaMirror != -1
            && staminaMirror < 0;
    }

    /// <summary>
    /// 原版生命行数字是否出自普通公式（Object.healthRecoveredOnConsumption default 分支，
    /// Object.cs:3908 的 (int)((float)体力 × 0.45f) 纯截断）。相符 → 该物品是普通正食，
    /// 刀前真值注入有意义；不符 → 原版走了特例分支（虫肉块 (O)874 ×0.68、生命药水 (O)773 →
    /// 999 等），决议 10：特例物品 tooltip 维持原版，不顺手修。注入值与原版显示值不一致时
    /// 一律退回原版，宁可少注入也不改写原版特例。
    /// </summary>
    internal static bool IsVanillaPlainFormulaValue(int vanillaHealthValue, int staminaMirror)
    {
        return vanillaHealthValue == (int)((float)staminaMirror * 0.45f);
    }
}
