#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Combat.CombatAutomation.MouseFacing;

/// <summary>
/// 攻击动作完成钩子（2026-11-15 用户口径第三次收敛）：postfix <c>Farmer.canMoveNow(Farmer)</c>。
/// <c>canMoveNow</c> 是原版「一次完整攻击动作完成」的唯一准确回调——武器挥舞/特技动画的
/// 最后一帧以 <c>behaviorAtEndOfFrame</c> 调用它（FarmerSprite.cs:1265 等），内部设
/// CanMove=true、UsingTool=false（Farmer.cs:6759-6767）。
/// 为什么不轮询 UpdateTicked：动作完成（canMoveNow，UpdateLocations 阶段）与长按合成
/// press 的下一刀起手（UpdateControlInput 阶段）可能发生在同一帧——帧末轮询永远看不到
/// "UsingTool=false" 的间隙，导致长按连挥时转身时灵时不灵（2026-11-15 二修缺陷）。
/// 挂在完成回调上则不受同帧起手影响：每挥完一刀必然触发一次。
/// 覆盖：长按连挥、快速点击（单击）、手柄攻击、特技/格挡动作完成；走路/站立无此回调，
/// 天然不转。工具/弹弓由执行器的近战武器门排除。
/// postfix 主体 try-catch 兜底：本钩子挂在游戏 Update 路径上，任何异常都不得抛穿主循环
/// （2026-11-15 实机爆红教训）。fail-closed：目标缺失只禁用并留痕。
/// </summary>
internal static class SmapiMouseFacingTurnPatch
{
    private static Harmony? harmony;
    private static MethodInfo? patchedMethod;
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

        var target = AccessTools.DeclaredMethod(
            typeof(Farmer),
            nameof(Farmer.canMoveNow),
            new[] { typeof(Farmer) }
        );
        if (target is null)
        {
            modMonitor.Log(
                "Mouse facing target unavailable: Farmer.canMoveNow(Farmer).",
                LogLevel.Warn
            );
            return;
        }

        harmony = patchOwner;
        monitor = modMonitor;
        driver = mouseFacingDriver;
        harmony.Patch(
            target,
            postfix: new HarmonyMethod(
                typeof(SmapiMouseFacingTurnPatch),
                nameof(CanMoveNowPostfix)
            )
        );
        patchedMethod = target;
    }

    internal static void Uninstall()
    {
        if (harmony is null)
            return;

        if (patchedMethod is not null)
        {
            harmony.Unpatch(patchedMethod, HarmonyPatchType.All, harmony.Id);
        }

        harmony = null;
        patchedMethod = null;
        driver = null;
        monitor = null;
    }

    private static void CanMoveNowPostfix(Farmer who)
    {
        try
        {
            driver?.TryTurnOnAttackCompleted(who);
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] mouse-facing turn postfix failed: {ex}",
                LogLevel.Error
            );
        }
    }
}
