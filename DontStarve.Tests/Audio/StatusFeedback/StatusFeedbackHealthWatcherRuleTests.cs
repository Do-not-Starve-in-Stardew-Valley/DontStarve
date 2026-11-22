using DontStarve.Audio.StatusFeedback;
using Xunit;

namespace DontStarve.Tests.Audio.StatusFeedback;

/// <summary>
/// 健康差分 watcher 纯规则测试：方向判定、基线、抑制接口的发放与消费。
/// </summary>
public sealed class StatusFeedbackHealthWatcherRuleTests
{
    private static StatusFeedbackHealthWatchState NewState() => new();

    [Fact]
    public void FirstObservationOnlyBuildsBaseline()
    {
        var state = NewState();
        var decision = state.Observe(80);
        Assert.Equal(StatusFeedbackHealthWatchAction.None, decision.Action);
        Assert.Null(decision.Direction);
    }

    [Fact]
    public void RisingHealthPlaysUp()
    {
        var state = NewState();
        state.Observe(50);
        var decision = state.Observe(80);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, decision.Action);
        Assert.Equal(StatusFeedbackDirection.Up, decision.Direction);
    }

    [Fact]
    public void FallingHealthPlaysDown()
    {
        var state = NewState();
        state.Observe(80);
        var decision = state.Observe(30);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, decision.Action);
        Assert.Equal(StatusFeedbackDirection.Down, decision.Direction);
    }

    [Fact]
    public void UnchangedHealthNeverPlays()
    {
        var state = NewState();
        state.Observe(80);
        Assert.Equal(StatusFeedbackHealthWatchAction.None, state.Observe(80).Action);
        Assert.Equal(StatusFeedbackHealthWatchAction.None, state.Observe(80).Action);
    }

    [Fact]
    public void SuppressedContinuousLossIsSilentButDirectional()
    {
        // 睡眠/空腹扣血（overtime）：写值前抑制 → 该次差分不播音（DST：overtime 扣血无声）。
        var state = NewState();
        state.Observe(100);
        state.SuppressNextIfHealthChanges(100, 90);
        var decision = state.Observe(90);
        Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, decision.Action);
        Assert.Equal(StatusFeedbackDirection.Down, decision.Direction);
    }

    [Fact]
    public void SuppressionIsConsumedByOneObservationOnly()
    {
        var state = NewState();
        state.Observe(100);
        state.SuppressNextIfHealthChanges(100, 90);
        Assert.Equal(
            StatusFeedbackHealthWatchAction.Suppressed,
            state.Observe(90).Action
        );
        // 令牌已消费：后续离散变化恢复正常播音。
        var next = state.Observe(40);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, next.Action);
        Assert.Equal(StatusFeedbackDirection.Down, next.Direction);
    }

    [Fact]
    public void SuppressionTokenIsNotIssuedWhenHealthDoesNotActuallyChange()
    {
        // 写入值与当前值相同（如取整后无净变化）时不发放令牌，
        // 避免滞留令牌误吞下一次真实的离散音效。
        var state = NewState();
        state.Observe(80);
        state.SuppressNextIfHealthChanges(80, 80);
        Assert.Equal(0, state.PendingSuppressions);

        state.SuppressNextIfHealthChanges(80, double.NaN);
        Assert.Equal(0, state.PendingSuppressions);

        var decision = state.Observe(50);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, decision.Action);
    }

    [Fact]
    public void SuppressionCoversRecoveryDirectionToo()
    {
        // overtime 回血（DST：只闪不播音）同样静音。
        var state = NewState();
        state.Observe(50);
        state.SuppressNextIfHealthChanges(50, 60);
        var decision = state.Observe(60);
        Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, decision.Action);
        Assert.Equal(StatusFeedbackDirection.Up, decision.Direction);
    }

    [Fact]
    public void PendingSuppressionsAreCapped()
    {
        var state = NewState();
        for (var index = 0; index < 20; index++)
            state.SuppressNextIfHealthChanges(100, 90);
        Assert.Equal(
            StatusFeedbackHealthWatchState.MaxPendingSuppressions,
            state.PendingSuppressions
        );
    }

    [Fact]
    public void LethalDamageIntoZeroStillPlaysBeforeDeathState()
    {
        // 黑暗袭击/怪物攻击把玩家打到 0（死亡开始）是攻击伤害：照常播 down。
        var state = NewState();
        state.Observe(40);
        var deathHit = state.Observe(0);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, deathHit.Action);
        Assert.Equal(StatusFeedbackDirection.Down, deathHit.Direction);
    }

    [Fact]
    public void SustainedDeathStateRecoveryIsSilentRevivalReset()
    {
        // 复活重置序列（对齐原版 killScreen）：40→0（播 down）→ 死亡屏持续多刻 0
        // → 复活把 health 直接设为正值（原版设 10 / 上限 50%）→ 静音，不消耗令牌。
        var state = NewState();
        state.Observe(40);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, state.Observe(0).Action);
        // 死亡屏：0→0 连续多刻，形成持续死亡态。
        state.Observe(0);
        state.Observe(0);
        Assert.True(state.ConsecutiveLowTicks >= StatusFeedbackHealthWatchState.DeathStateMinimumTicks);

        var revival = state.Observe(10);
        Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, revival.Action);
        Assert.Equal(StatusFeedbackDirection.Up, revival.Direction);
        Assert.Equal(0, state.PendingSuppressions);

        // 复活后的正常治疗恢复正常播音（0→10 的静音不影响 10→60）。
        var heal = state.Observe(60);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, heal.Action);
    }

    [Fact]
    public void TransientZeroFollowedByHealStillPlays()
    {
        // 瞬时触碰 0（只被观测到一刻，未形成持续死亡态）之后的普通治疗仍播——
        // 复活抑制只针对持续死亡态，不扩大排除范围。
        var state = NewState();
        state.Observe(40);
        state.Observe(0);
        Assert.Equal(1, state.ConsecutiveLowTicks);

        var heal = state.Observe(60);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, heal.Action);
        Assert.Equal(StatusFeedbackDirection.Up, heal.Direction);
    }

    [Fact]
    public void RevivalSuppressionIsRuleBasedAndRepeatable()
    {
        // 连续两次死亡-复活循环，每次复活重置都静音（规则内建，不依赖令牌存量）。
        var state = NewState();
        for (var round = 0; round < 2; round++)
        {
            state.Observe(40);
            state.Observe(0);
            state.Observe(0);
            var revival = state.Observe(10);
            Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, revival.Action);
        }
    }

    [Fact]
    public void ZeroToPositiveRecoveryWithoutSustainedDeathConsumesTokensNormally()
    {
        // 复活规则未命中时（前刻未处于持续死亡态），抑制令牌语义保持原样。
        var state = NewState();
        state.Observe(0);
        Assert.Equal(1, state.ConsecutiveLowTicks);
        state.SuppressNextIfHealthChanges(0, 30);
        var decision = state.Observe(30);
        Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, decision.Action);
        Assert.Equal(0, state.PendingSuppressions);
    }

    [Fact]
    public void WakeReplayHealthWritebackIsSilentWithoutDeathStateAndRealDamageStillPlays()
    {
        // 黑暗死亡"医院直醒"重演（2026-11-14 修复回归锁）：mod 直置 killScreen 的清晨重演
        // 路径里玩家从未处于 ≤0 死亡态（血量基线保持正值，读档重演旧档时可能是 100），
        // 原版 checkForEvents killScreen 块平写 health=10 前，重演序列按既有 overtime 口径
        // 发放抑制令牌——这次系统重置差分静音，且只吞一次，其后的真实伤害照常播 down。
        var state = NewState();
        state.Observe(100);
        state.SuppressNextIfHealthChanges(100, 10);

        var revive = state.Observe(10);
        Assert.Equal(StatusFeedbackHealthWatchAction.Suppressed, revive.Action);
        Assert.Equal(StatusFeedbackDirection.Down, revive.Direction);
        Assert.Equal(0, state.PendingSuppressions);

        Assert.Equal(StatusFeedbackHealthWatchAction.None, state.Observe(10).Action);
        var realHit = state.Observe(4);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, realHit.Action);
        Assert.Equal(StatusFeedbackDirection.Down, realHit.Direction);
    }

    [Fact]
    public void IdempotentWakeReplayIssuesNoTokenAndKeepsRealChangeAudible()
    {
        // 幂等重演（新档血量已是 10）：不发令牌、无差分、无声音；随后的真实变化不受
        // 残留令牌影响照常播音。
        var state = NewState();
        state.Observe(10);
        state.SuppressNextIfHealthChanges(10, 10);
        Assert.Equal(0, state.PendingSuppressions);

        Assert.Equal(StatusFeedbackHealthWatchAction.None, state.Observe(10).Action);
        var heal = state.Observe(60);
        Assert.Equal(StatusFeedbackHealthWatchAction.Play, heal.Action);
        Assert.Equal(StatusFeedbackDirection.Up, heal.Direction);
    }

    [Fact]
    public void NonFiniteObservationDoesNotBecomeBaseline()
    {
        var state = NewState();
        state.Observe(80);
        state.Observe(double.NaN);
        var decision = state.Observe(80);
        Assert.Equal(StatusFeedbackHealthWatchAction.None, decision.Action);
    }

    [Fact]
    public void ResetClearsBaselineAndTokens()
    {
        var state = NewState();
        state.Observe(80);
        state.SuppressNextIfHealthChanges(80, 40);
        state.Reset();

        Assert.Equal(0, state.PendingSuppressions);
        Assert.Equal(0, state.ConsecutiveLowTicks);
        // 重置后的首刻只建立基线（跨世界切换不残留旧值差分）。
        Assert.Equal(StatusFeedbackHealthWatchAction.None, state.Observe(80).Action);
    }

    [Fact]
    public void StoreTracksPlayersIndependently()
    {
        var store = new StatusFeedbackHealthWatchStore();
        var playerA = store.GetOrCreate(1);
        var playerB = store.GetOrCreate(2);

        Assert.NotSame(playerA, playerB);
        Assert.Same(playerA, store.GetOrCreate(1));

        playerA.Observe(80);
        playerA.Observe(60);
        store.Remove(1);
        Assert.False(store.TryGetState(1, out _));
        Assert.True(store.TryGetState(2, out _));
    }

    [Fact]
    public void ResolverMapsValueDeltaToDirection()
    {
        Assert.Null(StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Health, 10, 10, "same"));
        Assert.Null(StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Health, double.NaN, 10, "nan"));
        Assert.Null(StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Health, double.PositiveInfinity, 10, "inf"));

        var up = StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Hunger, 10, 20, "eat");
        Assert.NotNull(up);
        Assert.Equal(StatusFeedbackSystem.Hunger, up!.Value.System);
        Assert.Equal(StatusFeedbackDirection.Up, up.Value.Direction);

        var down = StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Sanity, 20, 5, "night-attack");
        Assert.NotNull(down);
        Assert.Equal(StatusFeedbackDirection.Down, down!.Value.Direction);
    }
}
