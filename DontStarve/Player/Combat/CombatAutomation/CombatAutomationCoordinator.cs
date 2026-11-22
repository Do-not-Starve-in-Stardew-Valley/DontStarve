#nullable enable

using System;
using DontStarve.Config;
using DontStarve.Player.Combat.CombatAutomation.AutoSpecial;
using DontStarve.Player.Combat.CombatAutomation.AutoSwing;
using DontStarve.Player.Combat.CombatAutomation.MouseFacing;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace DontStarve.Player.Combat.CombatAutomation;

/// <summary>
/// 战斗自动化总装配（设计稿 5.0/5.6）：从 schema 驱动的 TypedConfigResolver 读 7 个战斗
/// key 缓存进 <see cref="CombatAutomationConfigSnapshot"/>；安装长按挥舞与自动特技
/// postfix，以及鼠标转身双触发源（起手 setFarmerAnimating prefix + 完成 canMoveNow
/// postfix，2026-11-17）；按快照决定是否订阅 UpdateTicked 驱动（弹弓档位/自动特技扫描；
/// 全部关闭时零订阅零开销，参照 StatusFeedbackCoordinator 的订阅/退订模式）。GMCM 保存后由
/// ModEntry.ApplySavedConfiguration 调 <see cref="ApplyConfiguration"/> 即时生效。
/// 边界：config 整体 fail-closed 时 ModEntry 不构建本类（快照保持全关）；单个 key 取值
/// 失败时写 fail-closed 回落值并留 Warn 日志。智能索敌/自动战斗已于 2026-11-15 按用户
/// 拍板整体移除（代码归档：文档/归档历史计划/20261115-索敌自动战斗代码归档/）。
/// </summary>
internal sealed class CombatAutomationCoordinator : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly TypedConfigResolver resolver;
    private readonly CombatAutomationConfigSnapshot snapshot = new();
    private readonly SmapiSlingshotAutoReleaseDriver slingshotDriver;
    private readonly SmapiAutoSpecialDriver autoSpecialDriver;
    private readonly SmapiMouseFacingDriver mouseFacingDriver;
    private readonly Harmony harmony;
    private bool tickWired;
    private bool disposed;

    internal CombatAutomationCoordinator(
        IModHelper helper,
        IMonitor monitor,
        TypedConfigResolver resolver,
        string patchOwnerId
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (string.IsNullOrWhiteSpace(patchOwnerId))
            throw new ArgumentException("Patch owner id is required.", nameof(patchOwnerId));

        slingshotDriver = new SmapiSlingshotAutoReleaseDriver(snapshot);
        autoSpecialDriver = new SmapiAutoSpecialDriver(snapshot, monitor: monitor);
        mouseFacingDriver = new SmapiMouseFacingDriver(snapshot);
        harmony = new Harmony(patchOwnerId + ".CombatAutomation");
        SmapiHoldToSwingPatch.Install(harmony, monitor, snapshot);
        SmapiAutoSpecialSwingPatch.Install(harmony, monitor, snapshot);
        SmapiMouseFacingStartTurnPatch.Install(harmony, monitor, mouseFacingDriver);
        SmapiMouseFacingTurnPatch.Install(harmony, monitor, mouseFacingDriver);
        ApplyConfiguration();
    }

    /// <summary>当前配置快照（与 patch/驱动共享同一实例；ApplyConfiguration 就地刷新）。</summary>
    internal CombatAutomationConfigSnapshot Snapshot => snapshot;

    internal bool IsTickWired => tickWired;

    /// <summary>重新读取 8 个战斗 key 并刷新快照；GMCM 保存后调用即全量即时生效。</summary>
    internal void ApplyConfiguration()
    {
        if (disposed)
            return;

        snapshot.AutoSwingMasterEnabled = ReadBoolean(
            ConfigKeys.EnableCombatAutoSwing,
            fallback: false
        );
        snapshot.AutoSwingSwordEnabled = ReadBoolean(ConfigKeys.AutoSwingSword, fallback: false);
        snapshot.AutoSwingClubEnabled = ReadBoolean(ConfigKeys.AutoSwingClub, fallback: false);
        snapshot.AutoSwingDaggerEnabled = ReadBoolean(ConfigKeys.AutoSwingDagger, fallback: false);
        snapshot.SlingshotAutoReleaseEnabled = ReadBoolean(
            ConfigKeys.SlingshotAutoRelease,
            fallback: false
        );
        // 力度读值失败回落 100（= schema 默认「最高」，也是原版满蓄力行为，最安全）；
        // 开关独立读、失败回落 false（战斗自动化 fail-closed 关闭的既有口径），互不牵连。
        snapshot.SlingshotReleasePowerPercent = ReadInt(ConfigKeys.SlingshotPower, fallback: 100);
        snapshot.AutoSpecialMode = ReadEnumText(ConfigKeys.AutoSpecialMode, fallback: "off");
        snapshot.ParsedAutoSpecialMode = ParseAutoSpecialMode(snapshot.AutoSpecialMode);
        snapshot.MouseFacingEnabled = ReadBoolean(ConfigKeys.EnableMouseFacing, fallback: false);
        WireTicksIfNeeded();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        UnwireTicks();
        SmapiHoldToSwingPatch.Uninstall();
        SmapiAutoSpecialSwingPatch.Uninstall();
        SmapiMouseFacingStartTurnPatch.Uninstall();
        SmapiMouseFacingTurnPatch.Uninstall();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        // 鼠标转身不再轮询：改由 SmapiMouseFacingTurnPatch 挂在 Farmer.canMoveNow
        // （一次完整攻击动作完成回调）驱动，见该类注释。
        // 每驱动独立 try-catch（2026-11-14 实机教训：单个驱动异常会中断 handler 杀死
        // 后续全部驱动；惊恐循环异常自禁用先例）：异常只影响当前驱动本 tick，并留
        // Error 日志可定位，不拖垮整链与游戏。
        SafeTick(slingshotDriver.Tick, "slingshot-release");
        SafeTick(autoSpecialDriver.Tick, "auto-special");
    }

    private void SafeTick(Action tick, string driverName)
    {
        try
        {
            tick();
        }
        catch (Exception ex)
        {
            monitor.Log(
                $"[combat-automation] driver '{driverName}' tick failed: {ex}",
                LogLevel.Error
            );
        }
    }

    private void WireTicksIfNeeded()
    {
        if (snapshot.RequiresTickDriver)
        {
            if (tickWired || disposed)
                return;

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            tickWired = true;
        }
        else
        {
            UnwireTicks();
        }
    }

    private void UnwireTicks()
    {
        if (!tickWired)
            return;

        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        tickWired = false;
    }

    private bool ReadBoolean(string key, bool fallback)
    {
        var resolution = resolver.GetBoolean(key);
        if (resolution.HasValue)
            return resolution.Value;

        LogKeyFallback(key, resolution.Reason, fallback ? "true" : "false");
        return fallback;
    }

    private int ReadInt(string key, int fallback)
    {
        var resolution = resolver.GetInt(key);
        if (resolution.HasValue)
            return resolution.Value;

        LogKeyFallback(
            key,
            resolution.Reason,
            fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        return fallback;
    }

    /// <summary>
    /// AutoSpecialMode 字符串 → 枚举（阶段 4/5 热路径零解析）。resolver 已按 schema
    /// allowedValues 校验，正常不会走到回落分支；未知值 fail-closed 回落 Off 并留痕
    /// （调用频率 = GMCM 保存 / 启动，非热路径）。
    /// </summary>
    private AutoSpecialMode ParseAutoSpecialMode(string modeText)
    {
        if (AutoSpecialRules.TryParseMode(modeText, out var mode))
            return mode;

        monitor.Log(
            $"[combat-automation] {ConfigKeys.AutoSpecialMode} value '{modeText}' unknown; using fail-closed fallback 'off'.",
            LogLevel.Warn
        );
        return AutoSpecialMode.Off;
    }

    private string ReadEnumText(string key, string fallback)
    {
        var resolution = resolver.GetEnum(key);
        if (resolution.HasValue)
            return resolution.Value;

        LogKeyFallback(key, resolution.Reason, fallback);
        return fallback;
    }

    private void LogKeyFallback(string key, string reason, string fallback)
    {
        monitor.Log(
            $"[combat-automation] {key} unavailable ({reason}); using fail-closed fallback '{fallback}'.",
            LogLevel.Warn
        );
    }
}
