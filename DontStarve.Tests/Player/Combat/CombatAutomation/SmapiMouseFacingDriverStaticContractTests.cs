using System;
using System.IO;
using DontStarve.Player.Combat.CombatAutomation;
using DontStarve.Player.Combat.CombatAutomation.AutoSwing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 跟随鼠标转身静态契约（2026-11-15 第三次收敛；2026-11-17 起手转身 + 工具加入：旧口径
/// 「真正快速点击不转」作废，第一下即朝鼠标；近战 + 工具全覆盖，弹弓排除）：双触发源——
/// 起手双挂点 prefix（MeleeWeapon.setFarmerAnimating + Farmer.BeginUsingTool）+ 完成
/// postfix Farmer.canMoveNow，均不轮询 UpdateTicked。
/// 测试工程不引用游戏程序集，触发源、裁决门与执行落点以源码契约表达；裁决矩阵覆盖在
/// MouseFacingTurnRulesTests，分区几何覆盖在 MouseFacingMapperTests（纯规则源码链接编译）。
/// </summary>
public sealed class SmapiMouseFacingDriverStaticContractTests
{
    [Fact]
    public void Turn_is_driven_by_the_vanilla_attack_completion_callback()
    {
        var patch = PatchSource();

        // 触发源 = Farmer.canMoveNow（武器/特技/工具动作动画最后一帧的 behaviorAtEndOfFrame
        // 回调，内部设 CanMove=true/UsingTool=false）——原版「完整动作完成」的唯一准确
        // 时刻；走路/站立不产生该回调。
        Assert.Contains("nameof(Farmer.canMoveNow)", patch, StringComparison.Ordinal);
        Assert.Contains("new[] { typeof(Farmer) }", patch, StringComparison.Ordinal);
        Assert.Contains("CanMoveNowPostfix(Farmer who)", patch, StringComparison.Ordinal);
        // postfix 调用执行器（不在 patch 里内联转身逻辑）。
        Assert.Contains("TryTurnOnAttackCompleted(who)", patch, StringComparison.Ordinal);
        // 挂在游戏 Update 路径上：异常必须兜底，不得抛穿主循环。
        Assert.Contains("catch (Exception ex)", patch, StringComparison.Ordinal);
        Assert.Contains("mouse-facing turn postfix failed", patch, StringComparison.Ordinal);
        // fail-closed 安装与卸载。
        Assert.Contains("Mouse facing target unavailable", patch, StringComparison.Ordinal);
        Assert.Contains("internal static void Uninstall()", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_turn_covers_melee_swing_and_tool_use_entry_points()
    {
        var patch = StartTurnPatchSource();

        // 挂点 A = MeleeWeapon.setFarmerAnimating（近战挥舞总收口）：正常起手
        // （Tool.cs:722）、挥舞中重挥（MeleeWeapon.cs:494）、镰刀劈地（HoeDirt.cs:508）
        // 三条路径全汇入；BeginUsingTool 会漏掉重挥旁路。
        Assert.Contains("nameof(MeleeWeapon.setFarmerAnimating)", patch, StringComparison.Ordinal);
        Assert.Contains("new[] { typeof(Farmer) }", patch, StringComparison.Ordinal);
        Assert.Contains("MeleeSwingPrefix(Farmer who)", patch, StringComparison.Ordinal);
        // 挂点 B = Farmer.BeginUsingTool（非虚方法，全部工具起手必经）：Axe/FishingRod
        // 等重写 beginUsing，挂 Tool.beginUsing 会漏。
        Assert.Contains("nameof(Farmer.BeginUsingTool)", patch, StringComparison.Ordinal);
        Assert.Contains("BeginUsingToolPrefix(Farmer __instance)", patch, StringComparison.Ordinal);
        // 双挂点共享同一执行器（不在 patch 里内联转身逻辑）。
        Assert.Contains("TryTurnOnAttackStarting(who)", patch, StringComparison.Ordinal);
        Assert.Contains("TryTurnOnAttackStarting(__instance)", patch, StringComparison.Ordinal);
        // 工具挂点内门：近战归挂点 A 独占（含重挥旁路），避免双 patch 重复转身。
        Assert.Contains("if (__instance.CurrentTool is MeleeWeapon)", patch, StringComparison.Ordinal);
        // 挂在攻击/使用主路径上：异常必须兜底，不得抛穿游戏 Update。
        Assert.Equal(2, CountOccurrences(patch, "catch (Exception ex)"));
        Assert.Contains("mouse-facing start-turn prefix failed", patch, StringComparison.Ordinal);
        // fail-closed 安装与卸载（双目标任一缺失整体禁用）。
        Assert.Contains("Mouse facing start-turn target unavailable", patch, StringComparison.Ordinal);
        Assert.Contains("internal static void Uninstall()", patch, StringComparison.Ordinal);
        Assert.Contains("foreach (var method in PatchedMethods)", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Executor_uses_the_shared_rule_gate_and_tool_weapon_gate()
    {
        var source = Driver();

        // 两触发源共享同一转身体（裁决 + 坐标口径 + 分区映射单点维护）。
        Assert.Contains("TryTurnTowardsMouse(player);", source, StringComparison.Ordinal);
        // 裁决统一走纯规则（可单测），不在执行器里散落布尔链。
        Assert.Contains("MouseFacingTurnRules.ShouldTurn(", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.MouseFacingEnabled", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(player, Game1.player)", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.IsGameInteractionSuspended()", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.HasWorldLevelBlocker()", source, StringComparison.Ordinal);
        // 手持门（2026-11-17 工具加入；同日弹弓不特判）：手持任意 Tool 均允许
        // （近战武器/工具/弹弓）；空手排除。
        Assert.Contains("player.CurrentTool is Tool", source, StringComparison.Ordinal);
        // 弹弓不写类型特判（2026-11-17 用户：不要特殊处理）。
        Assert.DoesNotContain("Slingshot", source, StringComparison.Ordinal);
        // 转身落点。
        Assert.Contains("player.faceDirection(direction);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_turn_gates_on_active_mouse_aiming()
    {
        var source = Driver();

        // 起手转身只在鼠标活跃（光标可见）时生效：手柄动作不被陈旧鼠标位置带偏
        // （与原版 Game1.cs:11633 的输入判据同源）。
        Assert.Contains("internal void TryTurnOnAttackStarting(Farmer? player)", source, StringComparison.Ordinal);
        Assert.Contains("Game1.wasMouseVisibleThisFrame", source, StringComparison.Ordinal);
        // 完成转身保持既有部署行为：不加鼠标活跃门。
        Assert.Contains("internal void TryTurnOnAttackCompleted(Farmer? player)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_installs_the_turn_patch_and_stops_polling_mouse_facing()
    {
        var source = Coordinator();

        // 双触发源：起手（setFarmerAnimating + BeginUsingTool 双挂点）+ 完成（canMoveNow postfix）。
        Assert.Contains("SmapiMouseFacingStartTurnPatch.Install(harmony, monitor, mouseFacingDriver)", source, StringComparison.Ordinal);
        Assert.Contains("SmapiMouseFacingTurnPatch.Install(harmony, monitor, mouseFacingDriver)", source, StringComparison.Ordinal);
        Assert.Contains("SmapiMouseFacingStartTurnPatch.Uninstall()", source, StringComparison.Ordinal);
        Assert.Contains("SmapiMouseFacingTurnPatch.Uninstall()", source, StringComparison.Ordinal);
        // 不再轮询 mouse-facing（旧缺陷：帧末轮询看不到同帧起手的间隙）。
        Assert.DoesNotContain("mouseFacingDriver.Tick", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_facing_alone_no_longer_requires_the_tick_driver()
    {
        var snapshot = new CombatAutomationConfigSnapshot();
        Assert.False(snapshot.RequiresTickDriver);

        // 事件式驱动：开启转身不订阅 UpdateTicked。
        snapshot.MouseFacingEnabled = true;
        Assert.False(snapshot.RequiresTickDriver);

        snapshot.SlingshotAutoReleaseEnabled = true;
        Assert.True(snapshot.RequiresTickDriver);
    }

    [Fact]
    public void Mapper_is_pure_and_direction_values_align_with_vanilla()
    {
        var source = Mapper();

        // 纯函数：只做数学映射，不读 Game1/玩家状态，无分配。
        Assert.DoesNotContain("Game1.", source, StringComparison.Ordinal);
        Assert.Contains("FaceUp = 0", source, StringComparison.Ordinal);
        Assert.Contains("FaceRight = 1", source, StringComparison.Ordinal);
        Assert.Contains("FaceDown = 2", source, StringComparison.Ordinal);
        Assert.Contains("FaceLeft = 3", source, StringComparison.Ordinal);
        // 边界口径：|dx| > |dy| 才左右，否则上下。
        Assert.Contains("Math.Abs(dx) > Math.Abs(dy)", source, StringComparison.Ordinal);
        // 纯规则不携带任何武器特判。
        Assert.DoesNotContain("Slingshot", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string Driver()
    {
        return ReadContract("SmapiMouseFacingDriver.cs");
    }

    private static string PatchSource()
    {
        return ReadContract("SmapiMouseFacingTurnPatch.cs");
    }

    private static string StartTurnPatchSource()
    {
        return ReadContract("SmapiMouseFacingStartTurnPatch.cs");
    }

    private static string Mapper()
    {
        return ReadContract("MouseFacingMapper.cs");
    }

    private static string Coordinator()
    {
        return ReadContract("CombatAutomationCoordinator.cs");
    }

    private static string ReadContract(string fileName)
    {
        // Mapper/Rules 是纯规则（测试工程源码链接编译），同时以 Contracts 快照供契约读取。
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "CombatAutomation", fileName)
        );
    }
}
