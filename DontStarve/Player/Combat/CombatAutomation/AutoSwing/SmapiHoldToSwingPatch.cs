#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.AutoSwing;

/// <summary>
/// 长按挥舞放行 patch（设计稿 4.3/5.2）：postfix <c>Game1.didPlayerJustLeftClick(bool)</c>。
/// 原版近战武器按住只挥一次：重复链被 didPlayerJustLeftClick 的「新按下沿」门挡在
/// Game1.cs:11714（BeginUsingTool 门）与 :13002（FireTool 门）。本 postfix 在
/// 「总开关开 × 武器类别子开关开 × 使用键按住中 × 输入守卫通过 × 原方法返回 false」时
/// 改写为 true，让原版工具连用链路（mouseClickPolling&gt;250 → 合成 useToolButtonPressed →
/// FireTool/BeginUsingTool，首延迟 ~250ms、重触发周期 ~150ms，与决议 4 一致）对近战武器
/// 同样生效；镰刀的 leftClick 连点缓冲被 isScythe 排除，但其重挥走 :11714 BeginUsingTool
/// 路径，放行后同样连挥（链路验证见更新记录）。
/// 原方法已 true 或任一条件不满足时透传原值；弹弓/工具/空手分类不匹配，自然透传。
/// 波及面结论（grep 全部调用点）：Game1.cs:11658/11684 与 Utility.cs:6974 均在
/// ActiveObject/stowed 前置下才触达，持武器不进；HoeDirt/CrabPot 的
/// didPlayerJustClickAtAll 分支与原版工具连用行为同形（工具侧本来就会重复触达）。
/// </summary>
internal static class SmapiHoldToSwingPatch
{
    private static readonly List<MethodInfo> PatchedMethods = new();
    private static Harmony? harmony;
    private static IMonitor? monitor;
    private static CombatAutomationConfigSnapshot? snapshot;

    // 武器分类缓存：CurrentTool 引用未变时直接复用上次分类结果，避免每帧重复读取
    // type.Value/isScythe；引用变化才重分类（切换武器/空手各一次）。
    private static Tool? classifiedTool;
    private static CombatWeaponKind cachedWeaponKind = CombatWeaponKind.Other;

    internal static bool IsInstalled => harmony is not null;

    /// <summary>安装 postfix；重复调用与缺参安全（幂等）。patch 实例由 Coordinator 持有。</summary>
    internal static void Install(
        Harmony patchOwner,
        IMonitor modMonitor,
        CombatAutomationConfigSnapshot configSnapshot
    )
    {
        if (harmony is not null || patchOwner is null || configSnapshot is null)
            return;

        harmony = patchOwner;
        monitor = modMonitor;
        snapshot = configSnapshot;
        var target = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.didPlayerJustLeftClick),
            new[] { typeof(bool) }
        );
        if (target is null)
        {
            // fail-closed：目标缺失只禁用本功能并留痕，不抛异常拖垮游戏启动。
            modMonitor.Log(
                "Combat auto-swing target unavailable: Game1.didPlayerJustLeftClick(bool).",
                LogLevel.Warn
            );
            snapshot = null;
            return;
        }

        harmony.Patch(
            target,
            postfix: new HarmonyMethod(
                typeof(SmapiHoldToSwingPatch),
                nameof(DidPlayerJustLeftClickPostfix)
            )
        );
        PatchedMethods.Add(target);
    }

    internal static void Uninstall()
    {
        if (harmony is null)
            return;

        foreach (var method in PatchedMethods)
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        PatchedMethods.Clear();
        harmony = null;
        monitor = null;
        snapshot = null;
        classifiedTool = null;
        cachedWeaponKind = CombatWeaponKind.Other;
    }

    private static void DidPlayerJustLeftClickPostfix(ref bool __result)
    {
        // 兜底防抛穿（2026-11-15 实机教训：本 patch 挂在 Game1.UpdateControlInput 主
        // 输入路径上，任何异常都会抛穿游戏 Update——每帧爆红 + 输入中断）。
        try
        {
            // 原方法已 true（真·新按下沿）→ 透传；条件不满足 → 透传原值。
            if (__result || snapshot is null)
                return;

            if (!IsHoldRepeatPermitted())
                return;

            __result = true;
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] hold-to-swing postfix failed: {ex}",
                LogLevel.Error
            );
        }
    }

    private static bool IsHoldRepeatPermitted()
    {
        // 使用键按住中：原版按住期间 mouseClickPolling 逐帧累加、松开归零
        // （Game1.cs:12842-12848），与原版工具连用的合成重复源同源（含键盘 use-tool 按键，
        // 行为与工具连用对齐；游戏手柄按住不含在该信号内，见更新记录已知边界）。
        if (Game1.mouseClickPolling <= 0)
            return false;

        var player = Game1.player;
        if (player is null || !player.IsLocalPlayer)
            return false;

        // 与惊恐循环同源的输入守卫：菜单/暂停/失焦挂起 + 事件/小游戏/传送/过渡/死亡屏。
        if (
            CombatInputGuards.IsGameInteractionSuspended()
            || CombatInputGuards.HasWorldLevelBlocker()
        )
        {
            return false;
        }

        var tool = player.CurrentTool;
        if (tool is null)
            return false;

        if (!ReferenceEquals(tool, classifiedTool))
            Reclassify(tool);

        // snapshot 在 postfix 入口已判空；此处非空（安装期与使用期同生命周期）。
        var config = snapshot!;
        return AutoSwingRules.IsAutoSwingPermitted(
            config.AutoSwingMasterEnabled,
            config.AutoSwingSwordEnabled,
            config.AutoSwingClubEnabled,
            config.AutoSwingDaggerEnabled,
            cachedWeaponKind
        );
    }

    private static void Reclassify(Tool tool)
    {
        classifiedTool = tool;
        if (tool is MeleeWeapon weapon)
        {
            cachedWeaponKind = CombatWeaponClassifier.Classify(
                CombatHeldItemKind.MeleeWeapon,
                weapon.type.Value,
                weapon.isScythe()
            );
            return;
        }

        cachedWeaponKind = tool is Slingshot
            ? CombatWeaponClassifier.Classify(CombatHeldItemKind.Slingshot, 0, false)
            : CombatWeaponClassifier.Classify(CombatHeldItemKind.Tool, 0, false);
    }
}
