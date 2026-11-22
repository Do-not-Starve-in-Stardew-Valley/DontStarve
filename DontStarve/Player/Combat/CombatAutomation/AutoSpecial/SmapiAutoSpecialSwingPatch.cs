#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.AutoSpecial;

/// <summary>
/// 锤/匕首自动特技触发 patch（2026-11-15 二次修复）：双挂点 postfix——
/// ① <c>MeleeWeapon.leftClick(Farmer)</c>（public override，MeleeWeapon.cs:484）= 重挥/
/// 连击缓冲入口（合成 press 150ms 周期 → FireTool → leftClick）；
/// ② <c>Farmer.BeginUsingTool()</c>（public，Farmer.cs:6816）= 首击入口，也是智能索敌/
/// 自动战斗程序化攻击的入口（决议 5：索敌/自动战斗攻击期间同样自动特技）。
/// 触发条件（全满足才调 animateSpecialMove，其内部 specialCooldown/fadeToBlack/镰刀门
/// 兜底）：攻击驱动期（长按中 mouseClickPolling&gt;0 || 索敌/自动战斗外部驱动槽）×
/// 模式含锤匕 × 冷却好（原版 static 字段 ≤0）× 输入守卫。**不检测怪物/距离**（用户
/// 2026-11-14 口径：锤砸地/匕首连刺是无条件动作，不依赖范围内有怪；剑格挡仍走
/// <see cref="SmapiAutoSpecialDriver"/> 的贴脸扫描）。
/// **2026-11-15 修复（实机爆红卡死根因）**：
/// 1. 两个挂点必须各自独立 postfix——上一版共用 `AfterMeleeSwing(Farmer __instance)`，
///    而 leftClick 的实例是 MeleeWeapon，Harmony 把武器引用按 Farmer 签名传入，
///    getter 读 Farmer 字段偏移直接 NRE（实机日志：base update loop 每帧
///    NullReferenceException at Farmer.get_IsLocalPlayer ← AfterMeleeSwing）。
/// 2. **postfix 主体 try-catch 兜底**：本 patch 挂在 Game1.UpdateControlInput 主输入
///    路径上，任何异常都会抛穿游戏 Update（每帧爆红 + 输入处理中断 = 剑/锤点按无反应、
///    卡死）。兜底后异常只降级为 Error 日志，绝不拖垮主循环。
/// 3. 本地玩家判定改 `Game1.player is null || !ReferenceEquals(player, Game1.player)`，
///    不经 Farmer.IsLocalPlayer getter（消除其内部对 Game1.player 的解引用风险）。
/// 触发沿 Debug 级日志（复测口径：冷却行为以日志数据定夺）。fail-closed 安装。
/// </summary>
internal static class SmapiAutoSpecialSwingPatch
{
    private static readonly List<MethodInfo> PatchedMethods = new();
    private static Harmony? harmony;
    private static IMonitor? monitor;
    private static CombatAutomationConfigSnapshot? snapshot;
    private static SmapiSpecialCooldownReader? cooldownReader;

    // 武器分类缓存：CurrentTool 引用未变时复用（与其它适配层同模式）。
    private static Tool? classifiedTool;
    private static CombatWeaponKind cachedWeaponKind = CombatWeaponKind.Other;

    internal static bool IsInstalled => harmony is not null;

    /// <summary>安装双挂点 postfix；重复调用与缺参安全（幂等）。patch 实例由 Coordinator 持有。</summary>
    internal static void Install(
        Harmony patchOwner,
        IMonitor modMonitor,
        CombatAutomationConfigSnapshot configSnapshot,
        SmapiSpecialCooldownReader? cooldownReaderOverride = null
    )
    {
        if (harmony is not null || patchOwner is null || configSnapshot is null)
            return;

        var leftClickTarget = AccessTools.DeclaredMethod(
            typeof(MeleeWeapon),
            nameof(MeleeWeapon.leftClick)
        );
        var beginUsingToolTarget = AccessTools.DeclaredMethod(
            typeof(Farmer),
            nameof(Farmer.BeginUsingTool)
        );
        if (leftClickTarget is null || beginUsingToolTarget is null)
        {
            modMonitor.Log(
                "Combat auto-special targets unavailable: MeleeWeapon.leftClick(Farmer) / Farmer.BeginUsingTool().",
                LogLevel.Warn
            );
            return;
        }

        harmony = patchOwner;
        monitor = modMonitor;
        snapshot = configSnapshot;
        cooldownReader = cooldownReaderOverride ?? new SmapiSpecialCooldownReader();
        // 两个挂点各自独立 postfix：leftClick 的实例是 MeleeWeapon、BeginUsingTool 的
        // 实例是 Farmer——绝不能共用签名（2026-11-15 实机 NRE 根因）。
        harmony.Patch(
            leftClickTarget,
            postfix: new HarmonyMethod(
                typeof(SmapiAutoSpecialSwingPatch),
                nameof(LeftClickPostfix)
            )
        );
        harmony.Patch(
            beginUsingToolTarget,
            postfix: new HarmonyMethod(
                typeof(SmapiAutoSpecialSwingPatch),
                nameof(BeginUsingToolPostfix)
            )
        );
        PatchedMethods.Add(leftClickTarget);
        PatchedMethods.Add(beginUsingToolTarget);
    }

    internal static void Uninstall()
    {
        if (harmony is null)
            return;

        foreach (var method in PatchedMethods)
        {
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }

        PatchedMethods.Clear();
        harmony = null;
        monitor = null;
        snapshot = null;
        cooldownReader = null;
        classifiedTool = null;
        cachedWeaponKind = CombatWeaponKind.Other;
    }

    /// <summary>leftClick 挂点 postfix：__instance=MeleeWeapon（当前武器），who=挥舞者。</summary>
    private static void LeftClickPostfix(MeleeWeapon __instance, Farmer who)
    {
        // leftClick 本身已含 health/菜单/farmEvent/eventUp/swimming/bathing/onBridge 门
        // （MeleeWeapon.cs:486）。兜底防抛穿：见 TryTriggerForSwing 的 try-catch。
        try
        {
            TryTriggerForSwing(who, "left-click");
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] auto-special left-click postfix failed: {ex}",
                LogLevel.Error
            );
        }
    }

    /// <summary>BeginUsingTool 挂点 postfix：__instance=Farmer（首击/索敌攻击入口）。</summary>
    private static void BeginUsingToolPostfix(Farmer __instance)
    {
        try
        {
            TryTriggerForSwing(__instance, "begin-tool");
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"[combat-automation] auto-special begin-tool postfix failed: {ex}",
                LogLevel.Error
            );
        }
    }

    /// <summary>
    /// 共用触发核心（锤/匕首）。任何调用点异常已被上层 postfix 兜底捕获。
    /// </summary>
    private static void TryTriggerForSwing(Farmer? player, string triggerSource)
    {
        if (snapshot is null || cooldownReader is null)
            return;

        if (snapshot.ParsedAutoSpecialMode == AutoSpecialMode.Off)
            return;

        if (player is null)
            return;

        // 本地玩家判定：不经 Farmer.IsLocalPlayer getter（其内部解引用 Game1.player，
        // 引用等值判定即可，且天然排除多人远程玩家的工具调用）。
        if (Game1.player is null || !ReferenceEquals(player, Game1.player))
            return;

        // 攻击驱动期 = 长按已达到最小按住时长（250ms，对齐原版长按定义）：快速点击
        // （每次按下短于 250ms）不触发自动特技；长按超过阈值后的每次挥击才触发。
        // 按住时长由 SmapiAutoSpecialDriver 每 tick 累计（mouseClickPolling 会被原版
        // 合成重触发重置为 100，不能直接当累计时长，Game1.cs:12857）。
        if (!SmapiAutoSpecialDriver.IsHoldLongEnough)
            return;

        // 输入守卫：挂起（菜单/暂停/失焦）与世界级阻断期间不触发。
        if (
            CombatInputGuards.IsGameInteractionSuspended()
            || CombatInputGuards.HasWorldLevelBlocker()
        )
        {
            return;
        }

        var tool = player.CurrentTool;
        if (tool is null || !ReferenceEquals(tool, classifiedTool))
            Reclassify(tool);

        int cooldownMs;
        var kind = cachedWeaponKind;
        if (kind == CombatWeaponKind.Club)
        {
            if (!AutoSpecialRules.ModeIncludesClubOrDagger(snapshot.ParsedAutoSpecialMode))
                return;

            cooldownMs = cooldownReader.ClubCooldownMs;
        }
        else if (kind == CombatWeaponKind.Dagger)
        {
            if (!AutoSpecialRules.ModeIncludesClubOrDagger(snapshot.ParsedAutoSpecialMode))
                return;

            cooldownMs = cooldownReader.DaggerCooldownMs;
        }
        else
        {
            // 剑/镰刀/其它：剑格挡走 SmapiAutoSpecialDriver 贴脸扫描；镰刀原版排除特技。
            return;
        }

        if (cooldownMs > 0)
            return;

        monitor?.Log(
            $"[combat-automation] auto-special triggered (source={triggerSource}, kind={kind}, cooldown-was={cooldownMs}, mode={snapshot.ParsedAutoSpecialMode}).",
            LogLevel.Debug
        );
        if (player.CurrentTool is MeleeWeapon weapon)
        {
            weapon.animateSpecialMove(player);
        }
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
