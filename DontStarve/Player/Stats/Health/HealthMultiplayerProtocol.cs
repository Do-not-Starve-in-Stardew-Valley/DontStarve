#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 决议 6 双向消息（H→C 下达余数/兑现，C→H 回报外部血量变化）的 wire DTO。
/// 原版不同步 Farmer.health（普通 int 字段，血量底层状态长在各客机本地），健康因此
/// 比饥饿多出这一段双向同步；复用 SMAPI ModMessage 传输，消息类型串以 DontStarve.Health/
/// 前缀命名。字段只增不改，旧字段缺失反序列化为缺省值。
/// </summary>
internal sealed class HealthLedgerSyncMessage
{
    /// <summary>目标玩家 UniqueMultiplayerID 的规范十进制串（主机下达给哪个农场工）。</summary>
    public string TargetPlayerId { get; set; } = string.Empty;

    /// <summary>主机权威余数（覆盖客机本地影子）。</summary>
    public double Remainder { get; set; }

    /// <summary>兑现的整数移动（NewHealth − 结算基准），客机按 clamp(本地血 + delta) 落条。</summary>
    public int HealthDelta { get; set; }
}

internal sealed class HealthExternalReportMessage
{
    /// <summary>报告者 UniqueMultiplayerID 的规范十进制串（必须等于消息发送者本人）。</summary>
    public string ReporterPlayerId { get; set; } = string.Empty;

    /// <summary>客机本地原版血量绝对值（外部变化零折叠，只用于主机对账视图）。</summary>
    public int Health { get; set; }
}

/// <summary>
/// 健康多人协议纯规则：玩家 id 规范化 + 主机/客机门控矩阵（决议 6）。
/// 不引用 SMAPI/Stardew 类型，便于纯规则单测；SMAPI 侧在 SmapiHealthMultiplayerAdapter。
/// 能力位说明：饥饿的能力位用于"客机提交请求 vs 主机结算"的双路径防双计；健康的客机
/// 从不结算（结构性无双计），v1 不引入线上能力位——主机进食补丁安装失败仅降级为
/// 主机自身效果缺失 + Warn（Health.Init 已有该日志）。
/// </summary>
internal static class HealthMultiplayerProtocol
{
    internal const string LedgerSyncMessageType = "DontStarve.Health/LedgerSync";
    internal const string ExternalReportMessageType = "DontStarve.Health/ExternalReport";

    /// <summary>Canonical InvariantCulture long round-trip：key 与 wire 串都必须可无损还原。
    /// 解析失败或非规范（如前导 0）时 playerId 一律置 0，避免调用方误用半解析值。</summary>
    internal static bool TryParsePlayerId(string? raw, out long playerId)
    {
        playerId = 0;
        if (
            !long.TryParse(
                raw,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed
            )
        )
        {
            return false;
        }

        if (
            !string.Equals(
                raw,
                parsed.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        playerId = parsed;
        return true;
    }

    internal static string FromPlayerId(long playerId) =>
        playerId.ToString(CultureInfo.InvariantCulture);

    // 决议 6 门控矩阵（纯策略，游戏边界层负责取事实后调用）：
    // | 动作                 | 主机（IsMainPlayer）           | 客机                     |
    // | 进食结算（doneEating）| 结算（自己走本地路径，农场工走下达）| 结构性不结算（无双计）|
    // | 分钟装备结算          | 结算所有在线玩家               | 不结算                   |
    // | LedgerSync（H→C）    | 忽略（不消费自己下达的指令）    | 仅目标客机应用           |
    // | ExternalReport（C→H）| 接受（报告者=发送者本人）      | 忽略                     |

    /// <summary>进食结算主机门：Context.IsMainPlayer 且 who 是当前在线玩家对象（镜像
    /// HungerFoodCompletionPatch 的 GetPlayer(onlyOnline) 在线校验形态）。</summary>
    internal static bool ShouldHostSettleFood(bool isMainPlayer, bool whoIsOnlineFarmer)
    {
        return isMainPlayer && whoIsOnlineFarmer;
    }

    /// <summary>分钟装备结算主机门（镜像 Sanity.OnMinute 的角色门形态；Health 无 changeService，
    /// SanityAuthorityRole.Host 本就由 Context.IsMainPlayer 派生，故直接以它为事实源）。</summary>
    internal static bool ShouldHostSettleMinute(bool isMainPlayer)
    {
        return isMainPlayer;
    }

    /// <summary>LedgerSync 应用门：只有非主机、消息来自主机、且目标就是本机玩家时应用。
    /// 主机收到 LedgerSync / 非目标玩家收到 → 忽略（协议防御）。已知边界：split-screen
    /// 的第二屏幕同为 IsMainPlayer，其下达同步会被此门忽略（行为等同 v1 本地口径，
    /// 多人时序整体属 PendingRealMachine）。</summary>
    internal static bool ShouldClientApplyLedgerSync(
        bool isMainPlayer,
        bool senderIsHost,
        bool targetIsLocalPlayer
    )
    {
        return !isMainPlayer && senderIsHost && targetIsLocalPlayer;
    }

    /// <summary>ExternalReport 接受门：只归主机；报告者必须是发送者本人（防代报）、
    /// 不是主机自己、且当前在线。数值合法性（health ≥ 0）由宿主边界层把关。</summary>
    internal static bool ShouldHostAcceptExternalReport(
        bool isMainPlayer,
        bool reporterIsSender,
        bool reporterIsHostSelf,
        bool reporterIsOnline
    )
    {
        return isMainPlayer && reporterIsSender && !reporterIsHostSelf && reporterIsOnline;
    }
}
