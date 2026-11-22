#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// 修复4（2026-11-13）运行时宿主：任一玩家的 2 点黑暗特殊流活跃期间，非演出端进入
/// "黑屏等待"。主机权威：主机在本机状态机上统计活跃流数量（SanityTwoAmSpecialDeathStateMachine），
/// 数量变化即广播 SanityTwoAmDarknessWaitMessage；客户端只接受主机消息驱动本地等待。
/// 本地承载：把 SanityTwoAmDarknessWaitEvent 赋给 Game1.farmEvent（原版夜间事件语义：
/// 纯黑绘制、HUD 隐藏、本地时钟冻结、工具/暂停菜单被原版门拦截）+ freezeControls + CanMove=false。
/// 冻结会推迟本机玩家在 2600 的原版强制昏迷（Game1.UpdateOther 的 startToPassOut 要求
/// player.canMove），因此等待玩家在放行后才结算自己的昏迷/换日——这就是"等全部流结束才
/// 统一放行/结算"的实现方式。单人模式不需要等待画面（事件本身就是全屏黑），整个机制在
/// !Game1.IsMultiplayer 时不生效。
/// 结束路径：流全部结束（主机统计归零）或会话清理（回标题/读档/系统停用/主机断线）时，
/// 主机广播 ActiveFlowCount=0 并本地解除；各端逐端 retract，绝不遗留冻结状态。
/// 已知边界：分屏（同一进程多本地玩家）下 farmEvent 是进程级静态，等待画面无法按屏区分，
/// 本轮多人口径为主机+农具手联机，不做分屏声明。
/// </summary>
internal sealed class SmapiSanityTwoAmDarknessWaitService : IDisposable
{
    private const int MaximumLoggedReasons = 32;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly Func<bool> isLocalPresentationActive;
    private SanityTwoAmDarknessWaitEvent? waitEvent;
    private bool frozeCanMove;
    private bool holdActive;
    private int lastBroadcastFlowCount = -1;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool disposed;

    internal SmapiSanityTwoAmDarknessWaitService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        Func<bool> isLocalPresentationActive
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.isLocalPresentationActive = isLocalPresentationActive
            ?? throw new ArgumentNullException(nameof(isLocalPresentationActive));

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
    }

    /// <summary>
    /// 主机侧：本机状态机活跃流数量变化时调用。数量大于 0 即持有等待状态并广播；
    /// 归零即放行并广播。重复值幂等（不重发广播）。仅主机主实例有效。
    /// </summary>
    internal void SetHostActiveFlowCount(int activeFlowCount)
    {
        if (disposed || !IsHostAuthority)
            return;
        holdActive = SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(activeFlowCount);
        if (activeFlowCount == lastBroadcastFlowCount)
            return;
        lastBroadcastFlowCount = activeFlowCount;
        BroadcastWaitState(activeFlowCount);
    }

    /// <summary>主机侧：向单个（刚连接的）对端补发当前等待状态。</summary>
    internal void SendWaitStateTo(long playerId)
    {
        if (disposed || !IsHostAuthority)
            return;
        try
        {
            helper.Multiplayer.SendMessage(
                CreateWaitMessage(lastBroadcastFlowCount),
                SanityTwoAmSpecialDeathContract.DarknessWaitMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { playerId }
            );
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.wait-send-failed", $"The darkness wait state could not be sent ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    /// <summary>会话清理（读档/回标题/系统停用/主机断线）：无条件解除本地等待并（主机）广播放行。</summary>
    internal void ClearSession()
    {
        if (disposed)
            return;
        holdActive = false;
        lastBroadcastFlowCount = -1;
        if (IsHostAuthority)
            BroadcastWaitState(0);
    }

    private bool IsHostAuthority =>
        lifecycle.AuthorityRole == SanityAuthorityRole.Host && Context.IsMainPlayer;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        RetractFreeze();
        holdActive = false;
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (disposed || !string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;
        if (e.Type != SanityTwoAmSpecialDeathContract.DarknessWaitMessageType)
            return;
        // 客户端只接受主机消息；主机本机统计权威，不消费远端等待消息。
        if (lifecycle.AuthorityRole != SanityAuthorityRole.Client)
            return;
        if (e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID)
            return;
        var message = e.ReadAs<SanityTwoAmDarknessWaitMessage>();
        if (
            message is null
            || message.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !string.Equals(message.SessionId, lifecycle.SessionId, StringComparison.Ordinal)
        )
            return;

        holdActive = SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(
            message.ActiveFlowCount
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;
        if (!Context.IsWorldReady || !Game1.IsMultiplayer)
        {
            RetractFreeze();
            return;
        }

        var shouldFreeze = SanityTwoAmDarknessWaitPolicy.ShouldApplyLocalFreeze(
            holdActive,
            isLocalPresentationActive()
        );
        if (shouldFreeze)
            ApplyFreeze();
        else
            RetractFreeze();
    }

    private void ApplyFreeze()
    {
        // 只在原版 farmEvent 空闲时占用：换日流程（_newDayAfterFade）会先置 null 再挑选
        // 原版夜间事件；若该槽位被原版事件占用，则保持原版画面（其自身也是黑屏+冻结语义），
        // 收回时用 ReferenceEquals 守卫绝不误清原版事件。
        if (!ReferenceEquals(Game1.farmEvent, waitEvent) && Game1.farmEvent is null)
        {
            waitEvent = new SanityTwoAmDarknessWaitEvent();
            Game1.farmEvent = waitEvent;
        }
        if (ReferenceEquals(Game1.farmEvent, waitEvent))
            Game1.freezeControls = true;
        if (!frozeCanMove && Game1.player is Farmer farmer)
        {
            // CanMove=false 同时冻结移动并推迟本机玩家在 2600 的原版强制昏迷，直到放行。
            farmer.CanMove = false;
            frozeCanMove = true;
        }
    }

    private void RetractFreeze()
    {
        if (ReferenceEquals(Game1.farmEvent, waitEvent))
        {
            Game1.farmEvent = null;
            // freezeControls 是原版全局门，夜间事件/暂停提示也使用；仅在收回自己占用时复位，
            // 避免在未占用时误清其它系统的冻结。
            Game1.freezeControls = false;
        }
        waitEvent = null;
        if (frozeCanMove)
        {
            if (Game1.player is Farmer farmer)
                farmer.CanMove = true;
            frozeCanMove = false;
        }
    }

    private SanityTwoAmDarknessWaitMessage CreateWaitMessage(int activeFlowCount) => new()
    {
        SessionId = lifecycle.SessionId,
        ActiveFlowCount = activeFlowCount,
    };

    private void BroadcastWaitState(int activeFlowCount)
    {
        try
        {
            helper.Multiplayer.SendMessage(
                CreateWaitMessage(activeFlowCount),
                SanityTwoAmSpecialDeathContract.DarknessWaitMessageType,
                modIDs: new[] { modId }
            );
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.wait-broadcast-failed", $"The darkness wait broadcast failed ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    private void LogOnce(string reason, string message)
    {
        // 与特殊死亡服务一致的有限去重：同一 reason 只记一次，避免广播失败路径刷日志。
        if (loggedReasons.Count >= MaximumLoggedReasons || !loggedReasons.Add(reason))
            return;
        monitor.Log(message, LogLevel.Warn);
    }
}
