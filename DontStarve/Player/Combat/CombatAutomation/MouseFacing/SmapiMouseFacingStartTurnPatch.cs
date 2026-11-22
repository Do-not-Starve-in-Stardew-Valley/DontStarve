#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.MouseFacing;

/// <summary>
/// 攻击/用工具起手钩子（2026-11-17 用户拍板：旧口径「真正快速点击不转」作废，第一下即朝
/// 鼠标；工具加入自动转向）：双挂点 prefix，在朝向被读取之前把玩家转向鼠标。
/// <para>挂点 A = <c>MeleeWeapon.setFarmerAnimating(Farmer)</c>（近战挥舞总收口）：正常起手
/// （Tool.cs:722）、挥舞中点击重挥（MeleeWeapon.cs:494，绕过 BeginUsingTool）、镰刀劈地
/// （HoeDirt.cs:508）三条路径全汇入；近战独占本挂点（含重挥旁路）。</para>
/// <para>挂点 B = <c>Farmer.BeginUsingTool()</c>（非虚方法，全部工具起手必经
/// Game1.cs:11720）：工具不重写 beginUsing 也能覆盖（Axe/FishingRod 等重写了 beginUsing，
/// 挂 Tool.beginUsing 会漏）；工具内门「非近战」（近战交给挂点 A，避免同一下两 patch 都
/// 转身）；弹弓由执行器规则门排除（拉弓瞄准转身由原版负责，v2 起延续口径）。</para>
/// 原版起手朝向的消费时机：Game1.pressUseToolButton（Game1.cs:11717-11718）先算目标点再
/// 转身，但鼠标超出玩家 1 格时该点退化为「当前朝向面前格」（Character.GetToolLocation,
/// Character.cs:1218-1238）→ 第一刀永远用旧朝向；工具的作用格在释放时按当时朝向取面前格
/// （Game1.cs:11083 FireTool → GetToolLocation）——起手转身后作用格自动朝向鼠标，无错位。
/// 工具精灵朝向在 beginUsing 内读 FacingDirection（Axe.beginUsing → Update），本 prefix
/// 先转，全部生效。本 prefix 时机晚于 11718 的原版转身计算、早于一切朝向读取——覆盖它。
/// 鼠标不活跃（手柄操作/光标隐藏，wasMouseVisibleThisFrame=false）时不转：避免手柄动作
/// 被陈旧鼠标位置带偏（与原版 Game1.cs:11633 的输入判据同源）。完成转身不加此门，
/// 保持已部署行为不变（见 SmapiMouseFacingDriver）。
/// 起手瞬间无法区分单击与长按第一下（互斥）——2026-11-17 用户拍板取「第一下即朝鼠标」。
/// prefix 主体 try-catch 兜底：本钩子挂在攻击/使用主路径上，任何异常都不得抛穿游戏
/// Update（2026-11-15 实机爆红教训）。fail-closed：目标缺失只禁用并留痕。
/// </summary>
internal static class SmapiMouseFacingStartTurnPatch
{
    private static Harmony? harmony;
    private static readonly List<MethodInfo> PatchedMethods = new();
    private static SmapiMouseFacingDriver? driver;
    private static IMonitor? monitor;

    internal static bool IsInstalled => harmony is not null;

    internal static void Install(
        Harmony patchOwner,
        IMonitor modMonitor,
        SmapiMouseFacingDriver mouseFacingDriver
    )
    {
        if (harmony is not null || patchOwner is null || mouseFacingDriver is null)
            return;

        // 挂点 A：近战挥舞总收口（setFarmerAnimating 参数名 who，prefix 按名注入）。
        var meleeTarget = AccessTools.DeclaredMethod(
            typeof(MeleeWeapon),
            nameof(MeleeWeapon.setFarmerAnimating),
            new[] { typeof(Farmer) }
        );
        // 挂点 B：全部工具起手（BeginUsingTool 无参，prefix 用 __instance）。
        var toolTarget = AccessTools.DeclaredMethod(typeof(Farmer), nameof(Farmer.BeginUsingTool));
        if (meleeTarget is null || toolTarget is null)
        {
            modMonitor.Log(
                "Mouse facing start-turn target unavailable: "
                + $"MeleeWeapon.setFarmerAnimating(Farmer)={meleeTarget is not null}, "
                + $"Farmer.BeginUsingTool()={toolTarget is not null}.",
                LogLevel.Warn
            );
            return;
        }

        harmony = patchOwner;
        monitor = modMonitor;
        driver = mouseFacingDriver;
        harmony.Patch(
            meleeTarget,
            prefix: new HarmonyMethod(
                typeof(SmapiMouseFacingStartTurnPatch),
                nameof(MeleeSwingPrefix)
            )
        );
        harmony.Patch(
            toolTarget,
            prefix: new HarmonyMethod(
                typeof(SmapiMouseFacingStartTurnPatch),
                nameof(BeginUsingToolPrefix)
            )
        );
        PatchedMethods.Add(meleeTarget);
        PatchedMethods.Add(toolTarget);
    }

    internal static void Uninstall()
    {
        if (harmony is null)
            return;

        foreach (var method in PatchedMethods)
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        PatchedMethods.Clear();
        harmony = null;
        driver = null;
        monitor = null;
    }

    private static void MeleeSwingPrefix(Farmer who)
    {
        try
        {
            driver?.TryTurnOnAttackStarting(who);
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] mouse-facing start-turn prefix failed: {ex}",
                LogLevel.Error
            );
        }
    }

    private static void BeginUsingToolPrefix(Farmer __instance)
    {
        try
        {
            // 近战起手归挂点 A（setFarmerAnimating 独占，含重挥旁路），避免双 patch 重复转身。
            if (__instance.CurrentTool is MeleeWeapon)
                return;

            driver?.TryTurnOnAttackStarting(__instance);
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] mouse-facing start-turn prefix failed: {ex}",
                LogLevel.Error
            );
        }
    }
}
