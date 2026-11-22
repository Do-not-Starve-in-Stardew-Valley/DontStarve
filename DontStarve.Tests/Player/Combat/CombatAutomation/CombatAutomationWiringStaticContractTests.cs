using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 战斗自动化装配（设计稿 5.6）静态契约：Coordinator 读全 9 个 key、按需订阅 UpdateTicked
/// （全关零开销）、Dispose 全量退订；弹弓驱动走原版 DoFunction 发射链；ModEntry 只做一次
/// 构造注册 + ApplySavedConfiguration 一个分支 + Dispose 一个条目，不堆内联逻辑。
/// </summary>
public sealed class CombatAutomationWiringStaticContractTests
{
    [Fact]
    public void Coordinator_reads_all_eight_combat_config_keys()
    {
        var source = Coordinator();

        // 8 个 key 全部由 Coordinator 读取（typed reader 封装 resolver 细节，key 名逐一出现）。
        Assert.Contains("ConfigKeys.EnableCombatAutoSwing", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.AutoSwingSword", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.AutoSwingClub", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.AutoSwingDagger", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.SlingshotAutoRelease", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.SlingshotPower", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.AutoSpecialMode", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.EnableMouseFacing", source, StringComparison.Ordinal);
        Assert.Contains("resolver.GetBoolean(", source, StringComparison.Ordinal);
        // 弹弓：开关走布尔通道，力度滑轮走 Number/GetInt 通道（经 ReadInt 封装）。
        Assert.Contains(
            "ReadInt(ConfigKeys.SlingshotPower, fallback: 100)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("resolver.GetInt(key)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resolver.GetEnum(ConfigKeys.SlingshotAutoRelease)",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Tick_subscription_follows_the_zero_cost_wire_unwire_pattern()
    {
        var source = Coordinator();

        Assert.Contains("helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;", source, StringComparison.Ordinal);
        Assert.Contains("helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.RequiresTickDriver", source, StringComparison.Ordinal);
        Assert.Contains("SmapiHoldToSwingPatch.Uninstall();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_fail_closed_keys_fall_back_without_throwing()
    {
        var source = Coordinator();

        Assert.Contains("LogKeyFallback(key, resolution.Reason", source, StringComparison.Ordinal);
        Assert.Contains("using fail-closed fallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Slingshot_driver_fires_through_the_vanilla_do_function_chain()
    {
        var source = Driver();

        Assert.Contains("player.usingSlingshot", source, StringComparison.Ordinal);
        Assert.Contains("slingshot.GetSlingshotChargeTime()", source, StringComparison.Ordinal);
        Assert.Contains("slingshot.GetBackArmDistance(player)", source, StringComparison.Ordinal);
        Assert.Contains("SlingshotAutoReleaseRules.ShouldRelease(", source, StringComparison.Ordinal);
        Assert.Contains("slingshot.canPlaySound = false;", source, StringComparison.Ordinal);
        Assert.Contains("slingshot.DoFunction(", source, StringComparison.Ordinal);
        // 开关关闭必须在任何世界状态读取之前短路。
        Assert.Contains(
            "if (!snapshot.SlingshotAutoReleaseEnabled)",
            source,
            StringComparison.Ordinal
        );
        // 按住中与输入守卫和长按挥舞同源。
        Assert.Contains("Game1.mouseClickPolling <= 0", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.IsGameInteractionSuspended()", source, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.HasWorldLevelBlocker()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModEntry_keeps_the_wiring_to_one_registration_one_branch_and_one_dispose()
    {
        var source = ModEntry();

        // config fail-closed 时不构建（resolver 不可用 → 功能整体静默关闭）。
        Assert.Contains(
            "_combatAutomation = _canWriteConfig",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("new CombatAutomationCoordinator(", source, StringComparison.Ordinal);
        Assert.Contains("_combatAutomation?.ApplyConfiguration();", source, StringComparison.Ordinal);
        Assert.Contains("_combatAutomation?.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_wires_auto_special_and_mouse_facing_drivers()
    {
        var source = Coordinator();

        // 阶段 4：自动特技由 UpdateTicked 分发；鼠标转身改由双触发源驱动
        // （2026-11-17 起手 setFarmerAnimating prefix + 完成 canMoveNow postfix），
        // 不再出现在 tick 分发里。
        Assert.Contains("new SmapiAutoSpecialDriver(", source, StringComparison.Ordinal);
        Assert.Contains("new SmapiMouseFacingDriver(", source, StringComparison.Ordinal);
        Assert.Contains("SmapiMouseFacingStartTurnPatch.Install(harmony, monitor, mouseFacingDriver)", source, StringComparison.Ordinal);
        Assert.Contains("SmapiMouseFacingTurnPatch.Install(harmony, monitor, mouseFacingDriver)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mouseFacingDriver.Tick", source, StringComparison.Ordinal);
        // 2026-11-14 实机修复：每驱动独立 try-catch（SafeTick）——单驱动异常不再中断
        // handler 杀死后续全部驱动，且留 Error 日志可定位。
        Assert.Contains("SafeTick(slingshotDriver.Tick, \"slingshot-release\")", source, StringComparison.Ordinal);
        Assert.Contains("SafeTick(autoSpecialDriver.Tick, \"auto-special\")", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", source, StringComparison.Ordinal);
        // 模式解析 fail-closed：未知值回落 Off 并留痕。
        Assert.Contains("ParseAutoSpecialMode(", source, StringComparison.Ordinal);
        Assert.Contains("using fail-closed fallback 'off'", source, StringComparison.Ordinal);
    }

    private static string Coordinator()
    {
        return ReadContract("CombatAutomationCoordinator.cs");
    }

    private static string Driver()
    {
        return ReadContract("SmapiSlingshotAutoReleaseDriver.cs");
    }

    private static string ModEntry()
    {
        // ModEntry 已有既有 Contracts 快照（NaturalDarkness 链接），复用同一份当前源码。
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "NaturalDarkness", "ModEntry.cs")
        );
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "CombatAutomation", fileName)
        );
    }
}
