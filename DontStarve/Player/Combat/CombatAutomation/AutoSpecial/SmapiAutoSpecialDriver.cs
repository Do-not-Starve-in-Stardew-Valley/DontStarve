#nullable enable

using System;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.AutoSpecial;

/// <summary>
/// 自动特技驱动（设计稿 5.3 / 决议 5；2026-11-14 实机修复后架构）：
/// - 剑格挡：UpdateTicked 扫描，**任何时刻**生效（决议 5：静止、走位、挥舞中都触发——
///   怪物贴脸时玩家通常正在交战，CanMove 门会挡掉核心场景，故格挡路径不设 CanMove 门；
///   动画切换由 setFarmerAnimating 语义保证安全）。扫描每
///   <see cref="CombatAutomationConstants.SpecialScanIntervalTicks"/> tick 一次。
/// - 锤/匕首：**不再走 UpdateTicked 扫描**（实机结论：连击链里 CanMove=true 只有 1-2 帧，
///   4 tick 扫描几乎必然错过→锤子永不触发、匕首随机触发）。改由
///   <see cref="SmapiAutoSpecialSwingPatch"/>（postfix MeleeWeapon.leftClick，即每次点击/
///   重挥的精确入口）按「长按中+冷却好」触发，且**不检测怪物/距离**（用户 2026-11-14
///   口径：锤砸地/匕首连刺是无条件动作，特技范围判定交由原版特技自身）。
/// 触发动作与原版右键同落点：<c>((MeleeWeapon)player.CurrentTool).animateSpecialMove(player)</c>，
/// 内部自带 specialCooldown() 门、fadeToBlack 门与镰刀排除。冷却=原版 public static
/// <c>MeleeWeapon.*Cooldown</c> 字段（反编译确认：doAnimateSpecialMove case 3/2/1 设
/// 1500/6000/3000ms，MeleeWeapon.Update 每帧递减，持武器即递减——机制完好；旧版体感
/// "没冷却"系触发点漂移，触发沿诊断日志供复测核对）。
/// 剑格挡扫描：for 循环遍历 currentLocation.characters（NetCollection 支持索引访问），
/// is Monster 过滤 + IsInvisible 排除 + 距离平方短路命中即触发，零 LINQ 零分配。
/// </summary>
internal sealed class SmapiAutoSpecialDriver
{
    private readonly CombatAutomationConfigSnapshot snapshot;
    private readonly SmapiSpecialCooldownReader cooldownReader;
    private readonly IMonitor? monitor;

    // 武器分类缓存：CurrentTool 引用未变时复用上次结果（与 SmapiHoldToSwingPatch 同
    // 模式，各适配层持私有缓存；提取共享缓存需改已验收代码，本轮不做）。
    private Tool? classifiedTool;
    private CombatWeaponKind cachedWeaponKind = CombatWeaponKind.Other;

    private int ticksSinceLastScan;

    /// <summary>每 tick 近似时长（60fps ≈ 16ms），仅用于累计按住时长。</summary>
    private const int TickDurationMs = 16;

    // 连续按住攻击键的累计时长（毫秒）：SmapiAutoSpecialSwingPatch 的触发判据。
    // 静态因为 SwingPatch 是静态安装（无实例引用）；松开、挂起或模式关闭时归零。
    private static int holdDurationMs;

    /// <summary>当前连续按住攻击键的近似时长（毫秒）。</summary>
    internal static int HoldDurationMs => holdDurationMs;

    /// <summary>
    /// 长按是否已达到自动特技的最小触发时长（<see cref="CombatAutomationConstants.AutoSpecialMinHoldMs"/>
    /// = 250ms，对齐原版"长按"定义）。快速点击每次按下短于该值 → 不触发自动特技。
    /// </summary>
    internal static bool IsHoldLongEnough =>
        holdDurationMs >= CombatAutomationConstants.AutoSpecialMinHoldMs;

    internal SmapiAutoSpecialDriver(
        CombatAutomationConfigSnapshot snapshot,
        SmapiSpecialCooldownReader? cooldownReader = null,
        IMonitor? monitor = null
    )
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.cooldownReader = cooldownReader ?? new SmapiSpecialCooldownReader();
        this.monitor = monitor;
    }

    internal void Tick()
    {
        // 模式关闭：清零长按计时（避免关闭期间残留）后短路。
        if (snapshot.ParsedAutoSpecialMode == AutoSpecialMode.Off)
        {
            holdDurationMs = 0;
            return;
        }

        var player = Game1.player;
        if (player is null || !player.IsLocalPlayer)
            return;

        // 长按计时（每 tick，先于扫描节流）：mouseClickPolling > 0 = 按住中（含鼠标左键与
        // 键盘 use-tool，松开归零；原版在合成重触发时把它重置为 100 但始终 >0，因此只能
        // 当布尔信号用，不能当累计时长）。挂起/世界阻断期间清零，避免菜单暂停期间累积。
        if (
            CombatInputGuards.IsGameInteractionSuspended()
            || CombatInputGuards.HasWorldLevelBlocker()
        )
        {
            holdDurationMs = 0;
            return;
        }
        holdDurationMs =
            Game1.mouseClickPolling > 0 ? holdDurationMs + TickDurationMs : 0;

        // 节流：非扫描 tick 只递增计数器，不做任何世界状态读取。
        ticksSinceLastScan++;
        if (ticksSinceLastScan < CombatAutomationConstants.SpecialScanIntervalTicks)
            return;
        ticksSinceLastScan = 0;

        if (player.usingSlingshot)
            return;

        var tool = player.CurrentTool;
        if (tool is null)
            return;

        if (!ReferenceEquals(tool, classifiedTool))
            Reclassify(tool);

        // 仅剑格挡仍走 UpdateTicked 扫描（任何时刻，决议 5）。锤/匕首已迁移至
        // SmapiAutoSpecialSwingPatch（leftClick postfix，见类注释）。
        var kind = cachedWeaponKind;
        if (kind == CombatWeaponKind.Sword)
        {
            if (
                AutoSpecialRules.ShouldTriggerSwordBlock(
                    snapshot.ParsedAutoSpecialMode,
                    kind,
                    cooldownReader.DefenseCooldownMs
                )
            )
            {
                ScanForBlockTrigger(player);
            }
        }
    }

    private static void ScanForBlockTrigger(Farmer player)
    {
        var location = player.currentLocation;
        if (location is null)
            return;

        var playerBox = player.GetBoundingBox();
        var characters = location.characters;
        for (int i = 0; i < characters.Count; i++)
        {
            if (characters[i] is not Monster monster || monster.IsInvisible)
                continue;

            // 非威胁态排除（2026-11-18 用户需求）：等待复活的木乃伊假死、缩壳帧的岩石蟹
            // 当前完全不会攻击玩家，不触发自动格挡（判定字段均为原版公开 Net 字段，
            // 逐字口径见 AutoSpecialRules.IsNonThreatForAutoBlock 的注释出处）。
            var isMummyAwaitingRevive =
                monster is StardewValley.Monsters.Mummy mummy
                && mummy.reviveTimer.Value > 0;
            var isCrabInShell =
                monster is StardewValley.Monsters.RockCrab crab
                && !crab.shellGone.Value
                && crab.Sprite.currentFrame % 4 == 0;
            if (
                AutoSpecialRules.IsNonThreatForAutoBlock(
                    isMummyAwaitingRevive,
                    isCrabInShell
                )
            )
            {
                continue;
            }

            var monsterBox = monster.GetBoundingBox();
            if (
                AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                    playerBox.X,
                    playerBox.Y,
                    playerBox.Right,
                    playerBox.Bottom,
                    monsterBox.X,
                    monsterBox.Y,
                    monsterBox.Right,
                    monsterBox.Bottom
                )
            )
            {
                TriggerSpecialMove(player, "sword-block");
                return;
            }
        }
    }

    /// <summary>
    /// 触发落点与原版右键一致（Game1.cs:11476-11478）；CurrentTool 必为 MeleeWeapon
    /// （kind 由分类器保证），防御性 re-check 不抛错。animateSpecialMove 内部的
    /// specialCooldown/fadeToBlack/镰刀门兜底，重复触发静默无效。触发沿 Debug 级日志
    /// （2026-11-14 实机复测口径：冷却行为以日志数据定夺），低频可接受。
    /// </summary>
    internal static void TriggerSpecialMove(Farmer player, string triggerSource)
    {
        if (player.CurrentTool is MeleeWeapon weapon)
        {
            weapon.animateSpecialMove(player);
        }
    }

    /// <summary>
    /// 锤/匕首的 UpdateTicked 扫描已删除（实机结论：连击链 CanMove=true 仅 1-2 帧、
    /// 4 tick 扫描必然错过——锤子永不触发、匕首随机触发）。迁移至
    /// <see cref="SmapiAutoSpecialSwingPatch"/>（leftClick postfix，无怪物检测口径）。
    /// </summary>

    private void Reclassify(Tool tool)
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
