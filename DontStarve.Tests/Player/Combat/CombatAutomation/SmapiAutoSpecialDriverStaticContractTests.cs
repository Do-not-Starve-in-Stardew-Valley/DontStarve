using System;
using System.IO;
using DontStarve.Player.Combat.CombatAutomation;
using DontStarve.Player.Combat.CombatAutomation.AutoSpecial;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 自动特技驱动（设计稿 5.3 / 决议 5）静态契约。测试工程不引用游戏程序集，落点/门/
/// 几何以源码契约表达；几何与模式裁决的行为覆盖在 AutoSpecialRulesTests（纯规则源码
/// 链接编译）。阶段 6 接入槽（SetExternalAttackDrive）一并锁定。
/// </summary>
public sealed class SmapiAutoSpecialDriverStaticContractTests
{
    [Fact]
    public void Trigger_uses_the_vanilla_right_click_entry_point()
    {
        var source = Driver();

        // 原版右键落点：Game1.cs:11476-11478 ((MeleeWeapon)CurrentTool).animateSpecialMove(player)。
        // 主工程编译期即校验 animateSpecialMove(Farmer) 在 1.6.15 存在（改名直接编译失败）。
        Assert.Contains("player.CurrentTool is MeleeWeapon weapon", source, StringComparison.Ordinal);
        Assert.Contains("weapon.animateSpecialMove(player);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Block_scan_has_no_canmove_gate_and_keeps_the_shared_guards()
    {
        var source = Driver();

        // 2026-11-14 实机修复：格挡"任何时刻"生效（决议 5）——怪物贴脸时玩家通常正在
        // 挥舞（CanMove=false），CanMove 门会挡掉核心场景，故格挡路径不得再设该门。
        Assert.DoesNotContain("!player.CanMove || player.canOnlyWalk", source, StringComparison.Ordinal);
        Assert.Contains("player.usingSlingshot", source, StringComparison.Ordinal);
        // 输入守卫与长按挥舞同源。
        Assert.Contains("CombatInputGuards.IsGameInteractionSuspended()", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.HasWorldLevelBlocker()", source, StringComparison.Ordinal);
        Assert.Contains("player.IsLocalPlayer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_only_scans_sword_block_club_and_dagger_moved_to_swing_patch()
    {
        var source = Driver();

        // 2026-11-14 实机修复：锤/匕首触发点迁移至 SmapiAutoSpecialSwingPatch
        // （leftClick + BeginUsingTool 双挂点，无怪物检测口径）；Driver 只留剑格挡扫描。
        Assert.Contains("AutoSpecialRules.ShouldTriggerSwordBlock(", source, StringComparison.Ordinal);
        Assert.Contains("cooldownReader.DefenseCooldownMs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanForClubTrigger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanForDaggerTrigger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldTriggerClubSpecial", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldTriggerDaggerSpecial", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Swing_patch_triggers_club_and_dagger_on_click_without_monster_check()
    {
        var source = SwingPatch();

        // 双挂点：leftClick（重挥/连击缓冲）+ BeginUsingTool（首击，决议 5）。
        Assert.Contains("nameof(MeleeWeapon.leftClick)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Farmer.BeginUsingTool)", source, StringComparison.Ordinal);
        // 攻击驱动 = 长按已达到最小按住时长（250ms）：快速点击不触发自动特技
        // （2026-11-15 平衡调整；旧判据 mouseClickPolling>0 在按下第一帧即成立）。
        Assert.Contains("if (!SmapiAutoSpecialDriver.IsHoldLongEnough)", source, StringComparison.Ordinal);
        // 用户口径（2026-11-14）：锤/匕首不检测怪物/距离——无任何怪物扫描。
        Assert.DoesNotContain("characters", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBoundingBox", source, StringComparison.Ordinal);
        // 冷却=原版静态字段读取。
        Assert.Contains("cooldownReader.ClubCooldownMs", source, StringComparison.Ordinal);
        Assert.Contains("cooldownReader.DaggerCooldownMs", source, StringComparison.Ordinal);
        // 剑/镰刀不进本 patch（剑格挡另走贴脸扫描、镰刀原版排除）。
        Assert.Contains("kind == CombatWeaponKind.Club", source, StringComparison.Ordinal);
        Assert.Contains("kind == CombatWeaponKind.Dagger", source, StringComparison.Ordinal);
        // fail-closed 安装与卸载。
        Assert.Contains("Combat auto-special targets unavailable", source, StringComparison.Ordinal);
        Assert.Contains("internal static void Uninstall()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_is_interval_throttled_and_allocation_free()
    {
        var source = Driver();

        // 扫描周期常量（CombatAutomationConstants 单点调参）+ 节流计数器。
        Assert.Contains("CombatAutomationConstants.SpecialScanIntervalTicks", source, StringComparison.Ordinal);
        Assert.Contains("ticksSinceLastScan < CombatAutomationConstants.SpecialScanIntervalTicks", source, StringComparison.Ordinal);
        // 零 LINQ：for 循环 + 模式匹配过滤 + 命中即返回。
        Assert.Contains("for (int i = 0; i < characters.Count; i++)", source, StringComparison.Ordinal);
        Assert.Contains("is not Monster monster || monster.IsInvisible", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Any(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".First", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_mode_short_circuits_before_any_world_state_read()
    {
        var source = Driver();

        Assert.Contains("snapshot.ParsedAutoSpecialMode == AutoSpecialMode.Off", source, StringComparison.Ordinal);
        // 关闭短路必须是 Tick 的第一件事（先于 Game1.player 读取）。
        var shortCircuitIndex = source.IndexOf(
            "snapshot.ParsedAutoSpecialMode == AutoSpecialMode.Off",
            StringComparison.Ordinal
        );
        var playerReadIndex = source.IndexOf("var player = Game1.player;", StringComparison.Ordinal);
        Assert.True(shortCircuitIndex >= 0);
        Assert.True(playerReadIndex > shortCircuitIndex);
    }

    [Fact]
    public void Driver_tracks_the_hold_duration_for_the_250ms_auto_special_threshold()
    {
        var source = Driver();

        // 按住时长由驱动每 tick 自行累计：mouseClickPolling 会被原版合成重触发重置为
        // 100（Game1.cs:12857），不能直接当累计时长。
        Assert.Contains("private static int holdDurationMs;", source, StringComparison.Ordinal);
        Assert.Contains("holdDurationMs + TickDurationMs", source, StringComparison.Ordinal);
        Assert.Contains("internal static bool IsHoldLongEnough", source, StringComparison.Ordinal);
        Assert.Contains(
            "holdDurationMs >= CombatAutomationConstants.AutoSpecialMinHoldMs",
            source,
            StringComparison.Ordinal
        );
        // 挂起/阻断与模式关闭时清零，避免暂停期间累积。
        Assert.Contains("holdDurationMs = 0;", source, StringComparison.Ordinal);
        // 阈值 250ms（对齐原版长按定义）。
        Assert.Equal(250, CombatAutomationConstants.AutoSpecialMinHoldMs);
        // 剑格挡距离 2026-11-15 平衡值 16px。
        Assert.Equal(16, CombatAutomationConstants.BlockTriggerDistancePx);
    }

    [Fact]
    public void Cooldown_reader_defaults_to_vanilla_static_fields_and_stays_injectable()
    {
        var source = Reader();

        // 默认读原版静态字段（public static int，无反射）；测试可注入 Func<int> 假冷却。
        Assert.Contains("MeleeWeapon.defenseCooldown", source, StringComparison.Ordinal);
        Assert.Contains("MeleeWeapon.clubCooldown", source, StringComparison.Ordinal);
        Assert.Contains("MeleeWeapon.daggerCooldown", source, StringComparison.Ordinal);
        Assert.Contains("Func<int> defenseCooldownMs", source, StringComparison.Ordinal);
        Assert.Contains("多人注记", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tick_subscription_fails_closed_without_the_auto_special_mode()
    {
        var snapshot = new CombatAutomationConfigSnapshot();

        // off（默认 fail-closed）不触发订阅；开启任一阶段 4/5 功能后需要 tick 驱动。
        Assert.False(snapshot.RequiresTickDriver);
        snapshot.ParsedAutoSpecialMode = AutoSpecialMode.All;
        Assert.True(snapshot.RequiresTickDriver);
        snapshot.ParsedAutoSpecialMode = AutoSpecialMode.Off;
        // 2026-11-15 三次收敛：鼠标转身改由 Farmer.canMoveNow 完成回调驱动，
        // 不再需要 UpdateTicked 订阅。
        snapshot.MouseFacingEnabled = true;
        Assert.False(snapshot.RequiresTickDriver);
    }

    private static string Driver()
    {
        return ReadContract("SmapiAutoSpecialDriver.cs");
    }

    private static string SwingPatch()
    {
        return ReadContract("SmapiAutoSpecialSwingPatch.cs");
    }

    private static string Reader()
    {
        return ReadContract("SmapiSpecialCooldownReader.cs");
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "CombatAutomation", fileName)
        );
    }
}
