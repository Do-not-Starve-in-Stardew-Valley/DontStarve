using System.Globalization;
using System.Reflection;
using DontStarve.Display.UIElements;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

/// <summary>
/// 决议 10 要求 A 的纯规则测试：效果值解析优先级（特殊 &gt; JSON &gt; 刀前/负食）、
/// 负食负值、-300 豁免、负食补行计划、特例物品识别，以及格式化与饥饿行先例
/// （实图 +62.5 饥饿值，LocalizedValueFormatter.TryFormatSigned）逐字符对齐。
/// </summary>
public sealed class HealthEffectResolutionTests
{
    private static HealthEffectFacts Facts(
        int effectiveEdibility,
        int staminaMirror,
        bool hasSpecialFormula = false,
        double specialFormulaValue = 0,
        bool hasJsonEntry = false,
        double jsonEntryValue = 0
    )
    {
        return new HealthEffectFacts(
            "(O)1000",
            0,
            effectiveEdibility,
            staminaMirror,
            hasSpecialFormula,
            specialFormulaValue,
            hasJsonEntry,
            jsonEntryValue
        );
    }

    [Theory]
    [InlineData(true, false, 12.6)]
    [InlineData(true, true, 12.6)]
    public void Special_formula_wins_over_everything(
        bool hasJsonEntry,
        bool negativeEdibility,
        double specialValue
    )
    {
        var facts = negativeEdibility
            ? Facts(
                -40,
                -100,
                hasSpecialFormula: true,
                specialFormulaValue: specialValue,
                hasJsonEntry: hasJsonEntry,
                jsonEntryValue: 5
            )
            : Facts(
                40,
                100,
                hasSpecialFormula: true,
                specialFormulaValue: specialValue,
                hasJsonEntry: hasJsonEntry,
                jsonEntryValue: 5
            );

        var resolution = HealthEffectResolver.Resolve(facts);
        Assert.Equal(HealthEffectSource.SpecialFormula, resolution.Source);
        Assert.Equal(specialValue, resolution.EffectValue, precision: 12);
    }

    [Theory]
    [InlineData(40, 100, 5)]
    [InlineData(-40, -100, 5)]
    public void Json_entry_wins_over_default_formulas(
        int effectiveEdibility,
        int staminaMirror,
        double jsonValue
    )
    {
        var resolution = HealthEffectResolver.Resolve(
            Facts(effectiveEdibility, staminaMirror, hasJsonEntry: true, jsonEntryValue: jsonValue)
        );
        Assert.Equal(HealthEffectSource.JsonEntry, resolution.Source);
        Assert.Equal(jsonValue, resolution.EffectValue, precision: 12);
    }

    [Theory]
    [InlineData(230, 103.5)]
    [InlineData(50, 22.5)]
    [InlineData(0, 0)]
    public void Default_positive_food_uses_the_untruncated_knife_before_value(
        int staminaMirror,
        double expected
    )
    {
        // 决议 2 三轮全线口径：无表正食 = 体力变化 × 0.45 未截断（230 → 103.5，
        // 原版刀 (int)((float)230 × 0.45f) = 103 只进账本修正量，不进 tooltip 真值）。
        var resolution = HealthEffectResolver.Resolve(Facts(40, staminaMirror));
        Assert.Equal(HealthEffectSource.KnifeBefore, resolution.Source);
        Assert.Equal(expected, resolution.EffectValue, precision: 12);
    }

    [Theory]
    [InlineData(-100, -45)]
    [InlineData(-50, -22.5)]
    [InlineData(-22, -9.9)]
    public void Default_negative_food_uses_the_negative_formula_value(int staminaMirror, double expected)
    {
        // 决议 1：负食效果真值 = −|体力变化| × 0.45，tooltip 注入负值（如 -22.5）。
        var resolution = HealthEffectResolver.Resolve(Facts(-40, staminaMirror));
        Assert.Equal(HealthEffectSource.NegativeFood, resolution.Source);
        Assert.Equal(expected, resolution.EffectValue, precision: 12);
    }

    [Fact]
    public void Native_inedible_items_resolve_to_none_and_are_exempt()
    {
        // 决议 10：-300 不能吃，无行，不干预。
        var resolution = HealthEffectResolver.Resolve(
            Facts(SeedEdibilityNativeInedibleEdibility, -750, hasJsonEntry: true, jsonEntryValue: 9)
        );
        Assert.Equal(HealthEffectSource.None, resolution.Source);
        Assert.Equal(0, resolution.EffectValue, precision: 12);
    }

    [Theory]
    [InlineData(12.6, 3, 9.6)]
    [InlineData(10, 3, 7)]
    [InlineData(12.6, 12, 0.6)]
    [InlineData(-45, 0, -45)]
    public void Unified_correction_is_effect_value_minus_applied_vanilla_delta(
        double effectValue,
        int appliedVanillaDelta,
        double expectedCorrection
    )
    {
        // 决议 2 覆盖抵扣统一契约：correction = effectValue − appliedVanillaDelta。
        // 负食分支下原版结算量恒为 0（Object.cs:3894-3896），与旧写法数值一致。
        Assert.Equal(
            expectedCorrection,
            HealthRules.CalculateFoodCorrection(effectValue, appliedVanillaDelta),
            precision: 12
        );
    }

    [Theory]
    [InlineData(-40, -100, true)]
    [InlineData(-1, -2, true)]
    [InlineData(40, 100, false)]
    [InlineData(0, 0, false)]
    public void Negative_row_plan_follows_effective_edibility_sign(
        int effectiveEdibility,
        int staminaMirror,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            HealthTooltipRules.ShouldAppendNegativeFoodHealthRow(effectiveEdibility, staminaMirror)
        );
    }

    [Fact]
    public void Negative_row_plan_mirrors_the_vanilla_branch_skip_conditions()
    {
        // stamina == -300：原版 else-if 整体不画（连负能量行都不画），本系统也不能补行；
        // stamina == -1：healAmountToDisplay 门不进，行区整体不画。
        Assert.False(HealthTooltipRules.ShouldAppendNegativeFoodHealthRow(-120, -300));
        Assert.False(HealthTooltipRules.ShouldAppendNegativeFoodHealthRow(-40, -1));
        // -300 物品（edibility 直接等于 -300）在更高资格门被排除，这里再挡一次是防御。
        Assert.False(HealthTooltipRules.ShouldAppendNegativeFoodHealthRow(-300, -750));
    }

    [Theory]
    [InlineData(230, 103, true)]
    [InlineData(50, 22, true)]
    [InlineData(0, 0, true)]
    public void Plain_vanilla_formula_values_are_recognized_for_decimal_injection(
        int staminaMirror,
        int vanillaHealthValue,
        bool expected
    )
    {
        // 普通正食：原版数字 = (int)((float)体力 × 0.45f) 纯截断 → 可注入刀前真值小数。
        Assert.Equal(
            expected,
            HealthTooltipRules.IsVanillaPlainFormulaValue(vanillaHealthValue, staminaMirror)
        );
    }

    [Theory]
    [InlineData(100, 68)]   // 虫肉块 (O)874 型：原版 ×0.68 特例
    [InlineData(230, 999)]  // 生命药水 (O)773 型：原版固定 999 特例
    [InlineData(230, 0)]    // 滋补水 (O)349 / 星果 (O)434 型：原版归零特例
    public void Vanilla_special_case_values_keep_the_vanilla_tooltip(int staminaMirror, int vanillaHealthValue)
    {
        // 决议 10：特例物品维持原版 tooltip，不顺手修——识别结果是「不注入」。
        Assert.False(
            HealthTooltipRules.IsVanillaPlainFormulaValue(vanillaHealthValue, staminaMirror)
        );
    }

    [Theory]
    [InlineData(103.5, "+103.5")]
    [InlineData(62.5, "+62.5")]
    [InlineData(-22.5, "-22.5")]
    [InlineData(103.454, "+103.45")]
    [InlineData(22.5, "+22.5")]
    [InlineData(0, "0")]
    public void Effect_value_formatting_matches_the_hunger_row_precedent(
        double value,
        string expected
    )
    {
        // 饥饿行小数先例：+0.##;-0.##;0 —— 两位内小数、去尾零、正数带 +、负数无 +、零为 "0"。
        Assert.True(
            LocalizedValueFormatter.TryFormatSigned(
                value,
                CultureInfo.InvariantCulture,
                out var formatted
            )
        );
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void Pure_rule_shapes_stay_single_sourced_for_tooltip_and_settlement()
    {
        // 反射锁形状：解析入口只有 Resolve 一个，产物只含 EffectValue + Source；
        // HealthRules 三个公式保持原签名，结算与 tooltip 共用，不许出现第二套口径。
        // internal 成员需显式 NonPublic 绑定（GetMethods() 默认只返回 public）。
        const BindingFlags allDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        var resolveMethods = typeof(HealthEffectResolver).GetMethods(allDeclared);
        Assert.Single(resolveMethods.Where(method => method.Name == "Resolve"));

        var resolutionProperties = typeof(HealthEffectResolution)
            .GetProperties(allDeclared)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "EffectValue", "Source" }, resolutionProperties);

        var rulesMethods = typeof(HealthRules).GetMethods(allDeclared);
        Assert.Single(rulesMethods.Where(method => method.Name == "CalculateKnifeBeforeTrueValue"));
        Assert.Single(rulesMethods.Where(method => method.Name == "CalculateNegativeFoodHealthDamage"));
        Assert.Single(rulesMethods.Where(method => method.Name == "CalculateFoodCorrection"));
    }

    private static int SeedEdibilityNativeInedibleEdibility =>
        DontStarve.Player.Stats.Food.SeedEdibilityRules.NativeInedibleEdibility;
}
