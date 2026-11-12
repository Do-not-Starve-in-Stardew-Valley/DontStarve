using System;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

public sealed class HealthMultiplayerProtocolTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Food_settlement_requires_host_and_online_farmer(
        bool isMainPlayer,
        bool whoIsOnlineFarmer,
        bool expected
    )
    {
        // 决议 6：进食结算只发生在主机，且 who 必须是当前在线玩家对象（在线校验镜像
        // HungerFoodCompletionPatch 形态）。客机结构性不结算。
        Assert.Equal(
            expected,
            HealthMultiplayerProtocol.ShouldHostSettleFood(isMainPlayer, whoIsOnlineFarmer)
        );
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Minute_settlement_is_host_only(bool isMainPlayer, bool expected)
    {
        // 决议 6：分钟装备结算仅主机（镜像 Sanity.OnMinute 角色门形态）。
        Assert.Equal(expected, HealthMultiplayerProtocol.ShouldHostSettleMinute(isMainPlayer));
    }

    [Fact]
    public void Ledger_sync_is_applied_only_by_the_target_client()
    {
        // 决议 6 协议防御矩阵：只有非主机、消息来自主机、目标是本机玩家三条同时成立才应用。
        Assert.True(
            HealthMultiplayerProtocol.ShouldClientApplyLedgerSync(
                isMainPlayer: false,
                senderIsHost: true,
                targetIsLocalPlayer: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldClientApplyLedgerSync(
                isMainPlayer: true,
                senderIsHost: true,
                targetIsLocalPlayer: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldClientApplyLedgerSync(
                isMainPlayer: false,
                senderIsHost: false,
                targetIsLocalPlayer: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldClientApplyLedgerSync(
                isMainPlayer: false,
                senderIsHost: true,
                targetIsLocalPlayer: false
            )
        );
    }

    [Fact]
    public void External_report_is_accepted_only_by_the_host_from_the_online_reporter()
    {
        // 决议 6 协议防御矩阵：只归主机；报告者=发送者本人（防代报）、非主机自己、且在线。
        Assert.True(
            HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                isMainPlayer: true,
                reporterIsSender: true,
                reporterIsHostSelf: false,
                reporterIsOnline: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                isMainPlayer: false,
                reporterIsSender: true,
                reporterIsHostSelf: false,
                reporterIsOnline: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                isMainPlayer: true,
                reporterIsSender: false,
                reporterIsHostSelf: false,
                reporterIsOnline: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                isMainPlayer: true,
                reporterIsSender: true,
                reporterIsHostSelf: true,
                reporterIsOnline: true
            )
        );
        Assert.False(
            HealthMultiplayerProtocol.ShouldHostAcceptExternalReport(
                isMainPlayer: true,
                reporterIsSender: true,
                reporterIsHostSelf: false,
                reporterIsOnline: false
            )
        );
    }

    [Theory]
    [InlineData("123", true, 123L)]
    [InlineData("-456", true, -456L)]
    [InlineData("0", true, 0L)]
    [InlineData("0123", false, 0L)]
    [InlineData("abc", false, 0L)]
    [InlineData("12.5", false, 0L)]
    [InlineData("", false, 0L)]
    [InlineData(null, false, 0L)]
    public void Player_id_parsing_requires_canonical_invariant_long(
        string? raw,
        bool expected,
        long expectedId
    )
    {
        // key 与 wire 串都必须可无损还原（long InvariantCulture 规范十进制往返）。
        Assert.Equal(expected, HealthMultiplayerProtocol.TryParsePlayerId(raw, out var playerId));
        Assert.Equal(expectedId, playerId);
    }

    [Fact]
    public void From_player_id_round_trips_through_parse()
    {
        const long id = -9223372036854775807;
        Assert.True(
            HealthMultiplayerProtocol.TryParsePlayerId(
                HealthMultiplayerProtocol.FromPlayerId(id),
                out var parsed
            )
        );
        Assert.Equal(id, parsed);
    }
}
