using System;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmDarknessWaitPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(16, true)]
    public void Hold_follows_the_active_flow_count(int activeFlowCount, bool expected)
    {
        Assert.Equal(
            expected,
            SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(activeFlowCount)
        );
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Local_freeze_applies_only_while_waiting_and_not_presenting(
        bool flowWaitActive,
        bool localPresentationActive,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            SanityTwoAmDarknessWaitPolicy.ShouldApplyLocalFreeze(
                flowWaitActive,
                localPresentationActive
            )
        );
    }

    [Fact]
    public void Multi_flow_wait_releases_only_after_the_last_flow_ends()
    {
        // 用户口径：多人同时触发时各自看各自的事件，其余人黑屏等待，等全部流结束才统一放行。
        Assert.True(SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(2));
        Assert.True(SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(1));
        Assert.False(SanityTwoAmDarknessWaitPolicy.ShouldHoldForActiveFlows(0));
    }
}

public sealed class SanityTwoAmDarknessWaitRuntimeContractTests
{
    [Fact]
    public void Wait_event_tick_update_must_never_end_like_a_vanilla_farm_event()
    {
        // 红线：原版 farmEvent 结束路径（tickUpdate=true）会硬编码 timeOfDay=600、床上 warp、
        // 移除明日邮件等换日收尾；等待画面只能由服务显式移除 Game1.farmEvent 结束。
        var source = ReadContract("SanityTwoAmDarknessWaitEvent.cs");

        var tickStart = source.IndexOf(
            "public override bool tickUpdate(GameTime time)",
            StringComparison.Ordinal
        );
        Assert.True(tickStart >= 0);
        var nextMember = source.IndexOf("public override void draw", tickStart, StringComparison.Ordinal);
        Assert.True(nextMember > tickStart);

        var method = source[tickStart..nextMember];
        Assert.Contains("return false;", method, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;", method, StringComparison.Ordinal);
        // 等待事件不得触碰任何换日收尾语义。
        Assert.DoesNotContain("Game1.timeOfDay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PassOutNewDay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NewDay(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_service_retract_never_clears_a_foreign_farm_event()
    {
        var source = ReadContract("SmapiSanityTwoAmDarknessWaitService.cs");

        // 占用原版 farmEvent 槽位前必须判空；收回必须 ReferenceEquals 守卫，绝不误清
        // 换日流程挑选的原版夜间事件。
        Assert.Contains("Game1.farmEvent is null", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(Game1.farmEvent, waitEvent)", source, StringComparison.Ordinal);
        // 冻结必须推迟本机 2600 原版强制昏迷（CanMove=false）并在放行时还原。
        Assert.Contains("farmer.CanMove = false;", source, StringComparison.Ordinal);
        Assert.Contains("farmer.CanMove = true;", source, StringComparison.Ordinal);
        // 单人模式不生效（事件本身即全屏黑）。
        Assert.Contains("!Game1.IsMultiplayer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Special_death_service_syncs_wait_state_on_every_flow_mutation()
    {
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        // 主机权威同步点：ApplyActions 末尾按状态机统计广播等待状态。
        Assert.Contains(
            "darknessWait.SetHostActiveFlowCount(stateMachine.Count);",
            source,
            StringComparison.Ordinal
        );
        // 会话清理必须无条件解除等待，任何一端不得被卡死在黑屏冻结里。
        Assert.Contains("darknessWait.ClearSession();", source, StringComparison.Ordinal);
        // 断线玩家的流必须取消，否则等待永不释放。
        Assert.Contains(
            "\"passout.two-am.peer-disconnected\"",
            source,
            StringComparison.Ordinal
        );
        // 本机演出活跃标志必须暴露给等待服务用于豁免。
        Assert.Contains(
            "internal bool IsLocalPresentationActive => localPresentationEvent is not null;",
            source,
            StringComparison.Ordinal
        );
    }

    private static string ReadContract(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Contracts", "PassOut", fileName)
    );
}
