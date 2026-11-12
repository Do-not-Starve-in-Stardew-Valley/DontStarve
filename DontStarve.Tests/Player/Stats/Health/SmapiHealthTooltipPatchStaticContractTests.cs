using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

/// <summary>
/// 静态契约测试（参照 SmapiHealthFoodCompletionPatchStaticContractTests 的源码字符串风格；
/// 测试工程不引用游戏程序集，故用源码锁定替代反射）：锁定 tooltip 补丁的安装目标
/// （IClickableMenu.drawHoverText final overload）、两个接缝的锚定形状，以及
/// 「负食补行高度联动必须与绘制共用同一计划函数」的决议 10 契约。
/// 契约被破坏时这里先红，避免重演接缝漂移与文字溢框。
/// </summary>
public sealed class SmapiHealthTooltipPatchStaticContractTests
{
    private const string PatchRelativePath =
        "DontStarve/Player/Stats/Health/SmapiHealthTooltipPatch.cs";

    private const string PureRulesRelativePath =
        "DontStarve/Player/Stats/Health/HealthEffectResolution.cs";

    private const string HealthRelativePath =
        "DontStarve/Player/Stats/Health/Health.cs";

    [Fact]
    public void Patch_exists_and_targets_the_final_drawHoverText_overload()
    {
        var source = ReadSource(PatchRelativePath);

        // 安装目标锁死在 IClickableMenu.drawHoverText 的 StringBuilder final overload
        //（能量/生命行组装区 :1288-1317 与高度计算 :1114 都在该方法内）。
        Assert.Contains("typeof(IClickableMenu)", source, StringComparison.Ordinal);
        Assert.Contains(
            "nameof(IClickableMenu.drawHoverText)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("typeof(StringBuilder)", source, StringComparison.Ordinal);
        Assert.Contains("typeof(string[])", source, StringComparison.Ordinal);
        Assert.Contains("typeof(Item)", source, StringComparison.Ordinal);
        Assert.Contains("buffIconsToDisplay", source, StringComparison.Ordinal);
        Assert.Contains("hoveredItem", source, StringComparison.Ordinal);
        // 局部变量布局必须与 SmapiVanillaSanityTooltipService 同源验证过（1 = 宽度、
        // 2 = 高度、5 = x、6 = y），漂移时 fail closed。
        Assert.Contains("HasExpectedLocalLayout", source, StringComparison.Ordinal);
        Assert.Contains("WidthLocalIndex", source, StringComparison.Ordinal);
        Assert.Contains("HeightLocalIndex", source, StringComparison.Ordinal);
        Assert.Contains("XLocalIndex", source, StringComparison.Ordinal);
        Assert.Contains("YLocalIndex", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_owner_is_dedicated_and_install_fails_loud()
    {
        var source = ReadSource(PatchRelativePath);

        Assert.Contains(".Stats.Health.Tooltip", source, StringComparison.Ordinal);
        Assert.Contains("TryInstall(", source, StringComparison.Ordinal);
        // 失败 Warn 不静默（镜像进食补丁纪律）。
        Assert.Contains("patch-owner-readback-failed", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Transpiler", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_installs_the_patch_with_the_same_gate_as_settlement()
    {
        var source = ReadSource(HealthRelativePath);

        // 安装点镜像进食补丁（Health.Init 内）；门控与结算同一句：
        // ShouldApplyFoodEffects(EnableHealthEffectBalance) —— 开关关 = tooltip 完全原版。
        Assert.Contains("SmapiHealthTooltipPatch.TryInstall", source, StringComparison.Ordinal);
        Assert.Contains(
            "ShouldApplyFoodEffects(ReadEnableHealthEffectBalance())",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Positive_row_seam_only_swaps_the_injected_number_after_the_vanilla_template()
    {
        var source = ReadSource(PatchRelativePath);

        // 正食：原版生命行原位原样 —— 锚定原版模板与 "+" 拼接组，只换实参表达式；
        // 宽度采样点（无 "+" 拼接）天然不匹配，≥999 的 " 100%" 三元式更早分流不受影响。
        Assert.Contains("Strings\\\\UI:ItemHover_Health", source, StringComparison.Ordinal);
        Assert.Contains("FormatPositiveHealthRowText", source, StringComparison.Ordinal);
        Assert.Contains("IsVanillaPlainFormulaValue", source, StringComparison.Ordinal);
        // 注入失败/关闭时逐字节退回原版串 "+" + 整数。
        Assert.Contains("string.Concat(VanillaPlusSign, vanillaHealthValue.ToString())",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Height_and_negative_row_draw_share_one_plan_function()
    {
        var patchSource = ReadSource(PatchRelativePath);
        var rulesSource = ReadSource(PureRulesRelativePath);

        // 高度计算（:1114 三元式）与负食补行（:1311-1316 分支）必须一致修改：
        // 两处都经 TryPlanNegativeFoodRow → HealthTooltipRules 纯计划函数联动，
        // 单改任一处都会文字溢框或留白。
        Assert.Contains("AddNegativeFoodRowHeight", patchSource, StringComparison.Ordinal);
        Assert.Contains("DrawNegativeFoodHealthRow", patchSource, StringComparison.Ordinal);
        Assert.Contains(
            "TryPlanNegativeFoodRow(hoveredItem, out _)",
            patchSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "TryPlanNegativeFoodRow(hoveredItem, out var formattedValue)",
            patchSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ShouldAppendNegativeFoodHealthRow",
            patchSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ShouldAppendNegativeFoodHealthRow",
            rulesSource,
            StringComparison.Ordinal
        );
        // 高度补量 = 原版每行预留 40；负食分支跳过条件（stamina == -300 / -1）被纯规则镜像。
        Assert.Contains("VanillaSurvivalRowHeightReserve", patchSource, StringComparison.Ordinal);
        Assert.Contains("staminaMirror != -300", rulesSource, StringComparison.Ordinal);
        Assert.Contains("staminaMirror != -1", rulesSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_row_seam_anchors_on_the_vanilla_negative_energy_row()
    {
        var source = ReadSource(PatchRelativePath);
        var rulesSource = ReadSource(PureRulesRelativePath);

        // 负食分支锚点：负能量行模板 + num9.ToString()（无 "+" 拼接）+ 34px 行尾推进，
        // 借此与正能量行/宽度采样点区分；34px 常量单源于纯规则文件。
        Assert.Contains("Strings\\\\UI:ItemHover_Energy", source, StringComparison.Ordinal);
        Assert.Contains("VanillaSurvivalRowAdvance", source, StringComparison.Ordinal);
        Assert.Contains(
            "VanillaSurvivalRowAdvance = 34",
            rulesSource,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Tooltip_is_a_pure_projection_that_never_mutates_state()
    {
        var source = ReadSource(PatchRelativePath);

        // 纯投影契约：只读本地数据，不改游戏状态、不写血、不碰余数账本、不结算。
        Assert.DoesNotContain(".health =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("remainderByPlayer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SettleEvent(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.player", source, StringComparison.Ordinal);
        Assert.DoesNotContain("modData", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Edibility_stays_single_sourced_and_formatting_matches_the_hunger_precedent()
    {
        var source = ReadSource(PatchRelativePath);

        // 同源原则（防重演 -750 能量事故）：edibility 一律走 SeedEdibilityRuntime，
        // 不直接读原始字段；数值格式化走 LocalizedValueFormatter（+0.##;-0.##;0，与
        // 饥饿行小数先例 +62.5 逐字符对齐），不许出现第二套格式化。
        Assert.Contains(
            "SeedEdibilityRuntime.ResolveEffectiveEdibility",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SeedEdibilityRuntime.ResolveEffectiveStaminaRecovered",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("edibility.Value", source, StringComparison.Ordinal);
        Assert.Contains(
            "LocalizedValueFormatter.TryFormatSigned",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Settlement_reads_the_same_effect_value_through_the_shared_resolver()
    {
        var source = ReadSource(HealthRelativePath);

        // 决议 10 要求 A：结算侧改为共享解析（HealthEffectResolver.Resolve + 事实读取器），
        // 旧的四分支本地公式判断不允许回流。
        Assert.Contains("HealthEffectResolver.Resolve", source, StringComparison.Ordinal);
        Assert.Contains("HealthEffectFactsReader.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveSpecial(obj, out var specialValue)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FoodRuleRuntime.TryGetHealth(obj, out var jsonValue)", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the production source: {relativePath}"
        );
    }
}
