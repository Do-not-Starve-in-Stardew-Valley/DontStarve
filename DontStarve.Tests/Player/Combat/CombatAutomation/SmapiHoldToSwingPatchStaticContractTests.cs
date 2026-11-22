using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 长按挥舞 postfix（设计稿 4.3/5.2）静态契约。测试工程不引用游戏程序集，因此目标
/// 方法签名存在性以源码契约表达：patch 必须用 <c>nameof(Game1.didPlayerJustLeftClick)</c>
/// + 显式 <c>typeof(bool)</c> 参数表绑定目标 —— 1.6.15 API 改名/改签名时主工程直接编译失败，
/// 等效于 AccessTools.Method 非空断言的编译期版本。
/// </summary>
public sealed class SmapiHoldToSwingPatchStaticContractTests
{
    [Fact]
    public void Patch_binds_the_vanilla_didPlayerJustLeftClick_signature()
    {
        var source = Patch();

        Assert.Contains("nameof(Game1.didPlayerJustLeftClick)", source, StringComparison.Ordinal);
        Assert.Contains("new[] { typeof(bool) }", source, StringComparison.Ordinal);
        Assert.Contains("postfix: new HarmonyMethod(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("prefix:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Postfix_only_lifts_a_false_result_and_passes_everything_else_through()
    {
        var source = Patch();

        Assert.Contains("private static void DidPlayerJustLeftClickPostfix(ref bool __result)", source, StringComparison.Ordinal);
        Assert.Contains("if (__result || snapshot is null)", source, StringComparison.Ordinal);
        Assert.Contains("__result = true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Hold_repeat_requires_the_vanilla_hold_signal_rules_gate_and_input_guards()
    {
        var source = Patch();

        // 按住中判定：原版按住累加、松开归零（Game1.cs:12842-12848）。
        Assert.Contains("Game1.mouseClickPolling <= 0", source, StringComparison.Ordinal);
        // 放行裁决走纯规则（总开关 × 子开关 × 武器类别）。
        Assert.Contains("AutoSwingRules.IsAutoSwingPermitted(", source, StringComparison.Ordinal);
        // 输入守卫与惊恐循环同源。
        Assert.Contains("CombatInputGuards.IsGameInteractionSuspended()", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.HasWorldLevelBlocker()", source, StringComparison.Ordinal);
        Assert.Contains("player.IsLocalPlayer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Weapon_classification_is_cached_per_current_tool_reference()
    {
        var source = Patch();

        Assert.Contains("ReferenceEquals(tool, classifiedTool)", source, StringComparison.Ordinal);
        Assert.Contains("weapon.type.Value", source, StringComparison.Ordinal);
        Assert.Contains("weapon.isScythe()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_target_fails_closed_and_install_stays_idempotent()
    {
        var source = Patch();

        Assert.Contains("if (harmony is not null || patchOwner is null || configSnapshot is null)", source, StringComparison.Ordinal);
        Assert.Contains("Combat auto-swing target unavailable", source, StringComparison.Ordinal);
        Assert.Contains("internal static void Uninstall()", source, StringComparison.Ordinal);
    }

    private static string Patch()
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "CombatAutomation",
                "SmapiHoldToSwingPatch.cs"
            )
        );
    }
}
