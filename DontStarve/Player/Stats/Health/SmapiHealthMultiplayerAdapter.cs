#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 决议 6 双向消息通道（传输层，镜像 SmapiHungerMultiplayerCoordinator 形态）：
/// - H→C "DontStarve.Health/LedgerSync"：主机把农场工的权威余数与兑现整数下达给对应客机。
/// - C→H "DontStarve.Health/ExternalReport"：客机把本地原版血量的绝对值回报主机对账。
/// 本类只做传输与协议防御（门控矩阵见 HealthMultiplayerProtocol），不做任何结算；
/// 账本落点全部在 Health 宿主回调里。播种时机由宿主驱动（Load 后 / PeerConnected），
/// 因为权威字典归宿主所有。
/// 消息异常不崩游戏：try/catch + 去重 Warn（不静默），先例 hunger/sanity 协调器。
/// </summary>
internal sealed class SmapiHealthMultiplayerAdapter
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly Action<long, double, int> onLedgerSyncReceived;
    private readonly Action<long, int> onExternalReportReceived;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);

    internal SmapiHealthMultiplayerAdapter(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        Action<long, double, int> onLedgerSyncReceived,
        Action<long, int> onExternalReportReceived
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = modId ?? throw new ArgumentNullException(nameof(modId));
        this.onLedgerSyncReceived = onLedgerSyncReceived
            ?? throw new ArgumentNullException(nameof(onLedgerSyncReceived));
        this.onExternalReportReceived = onExternalReportReceived
            ?? throw new ArgumentNullException(nameof(onExternalReportReceived));
    }

    internal void Initialize()
    {
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
    }

    internal void ResetSessionDiagnostics()
    {
        loggedReasons.Clear();
    }

    /// <summary>主机会话专属：向指定农场工下达余数/兑现。客机侧调用被忽略（结构上不会发生）。</summary>
    internal void SendLedgerSync(long targetPlayerId, double remainder, int healthDelta)
    {
        if (!Context.IsMainPlayer)
            return;
        if (!double.IsFinite(remainder))
        {
            LogOnce("ledger-sync-remainder-not-finite", LogLevel.Warn);
            return;
        }

        try
        {
            helper.Multiplayer.SendMessage(
                new HealthLedgerSyncMessage
                {
                    TargetPlayerId = HealthMultiplayerProtocol.FromPlayerId(targetPlayerId),
                    Remainder = remainder,
                    HealthDelta = healthDelta,
                },
                HealthMultiplayerProtocol.LedgerSyncMessageType,
                new[] { modId },
                new[] { targetPlayerId }
            );
        }
        catch (Exception ex)
        {
            LogOnce($"ledger-sync-send-failed:{ex.GetType().Name}", LogLevel.Warn);
        }
    }

    /// <summary>客机会话专属：把本地血量绝对值回报主机。主机侧调用被忽略。</summary>
    internal void SendExternalReport(long reporterPlayerId, int health)
    {
        if (Context.IsMainPlayer)
            return;

        var host = Game1.MasterPlayer;
        if (host is null)
        {
            LogOnce("external-report-host-is-unavailable", LogLevel.Trace);
            return;
        }

        try
        {
            helper.Multiplayer.SendMessage(
                new HealthExternalReportMessage
                {
                    ReporterPlayerId = HealthMultiplayerProtocol.FromPlayerId(reporterPlayerId),
                    Health = health,
                },
                HealthMultiplayerProtocol.ExternalReportMessageType,
                new[] { modId },
                new[] { host.UniqueMultiplayerID }
            );
        }
        catch (Exception ex)
        {
            LogOnce($"external-report-send-failed:{ex.GetType().Name}", LogLevel.Warn);
        }
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        try
        {
            switch (e.Type)
            {
                case HealthMultiplayerProtocol.LedgerSyncMessageType:
                    HandleLedgerSync(e);
                    break;
                case HealthMultiplayerProtocol.ExternalReportMessageType:
                    HandleExternalReport(e);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogOnce($"health-message-decode-failed:{ex.GetType().Name}", LogLevel.Warn);
        }
    }

    private void HandleLedgerSync(ModMessageReceivedEventArgs e)
    {
        var message = e.ReadAs<HealthLedgerSyncMessage>();
        if (
            message is null
            || !HealthMultiplayerProtocol.TryParsePlayerId(message.TargetPlayerId, out var targetPlayerId)
        )
        {
            LogOnce("ledger-sync-message-is-invalid", LogLevel.Warn);
            return;
        }

        // 数值非有限 → 丢弃 + Warn（协议防御：余数是账本源头，脏值宁可丢也不污染影子）。
        if (!double.IsFinite(message.Remainder))
        {
            LogOnce("ledger-sync-remainder-not-finite", LogLevel.Warn);
            return;
        }

        var host = Game1.MasterPlayer;
        var local = Game1.player;
        var senderIsHost = host is not null && e.FromPlayerID == host.UniqueMultiplayerID;
        var targetIsLocalPlayer = local is not null
            && targetPlayerId == local.UniqueMultiplayerID;
        if (
            !HealthMultiplayerProtocol.ShouldClientApplyLedgerSync(
                Context.IsMainPlayer,
                senderIsHost,
                targetIsLocalPlayer
            )
        )
        {
            // 协议防御：主机收到 LedgerSync / 非目标玩家收到 → 忽略（Trace 去重，不刷日志）。
            LogOnce("ledger-sync-ignored-by-role-or-target", LogLevel.Trace);
            return;
        }

        onLedgerSyncReceived(targetPlayerId, message.Remainder, message.HealthDelta);
    }

    private void HandleExternalReport(ModMessageReceivedEventArgs e)
    {
        var message = e.ReadAs<HealthExternalReportMessage>();
        if (
            message is null
            || !HealthMultiplayerProtocol.TryParsePlayerId(message.ReporterPlayerId, out var reporterPlayerId)
        )
        {
            LogOnce("external-report-message-is-invalid", LogLevel.Warn);
            return;
        }

        var host = Game1.MasterPlayer;
        var reporterIsSender = reporterPlayerId == e.FromPlayerID;
        var reporterIsHostSelf = host is not null
            && reporterPlayerId == host.UniqueMultiplayerID;
        var reporterIsOnline = Game1.GetPlayer(reporterPlayerId, onlyOnline: true) is not null;
        if (
            !HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                Context.IsMainPlayer,
                reporterIsSender,
                reporterIsHostSelf,
                reporterIsOnline
            )
        )
        {
            // 协议防御：非主机收到 / 主机收到自身 / 代报 / 离线玩家 → 忽略（Trace 去重）。
            LogOnce("external-report-ignored-by-role-or-owner", LogLevel.Trace);
            return;
        }

        onExternalReportReceived(reporterPlayerId, message.Health);
    }

    private void LogOnce(string reason, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedReasons.Add(reason))
            return;

        // Prefix matches the Hunger/Sanity multiplayer lanes so all lanes stay attributable
        // in the SMAPI log; dedup keeps one line per reason per session.
        monitor.Log($"Health multiplayer: {reason}", level);
    }
}
