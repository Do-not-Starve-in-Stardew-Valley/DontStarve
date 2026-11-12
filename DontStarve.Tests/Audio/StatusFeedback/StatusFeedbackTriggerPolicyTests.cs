using DontStarve.Audio.StatusFeedback;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Audio.StatusFeedback;

/// <summary>
/// 触发策略分类表契约：对齐 DST 语义——只有 not-overtime 的离散增减才播音。
/// Theory 参数用 int 承载内部枚举，避免测试方法签名泄露 internal 类型。
/// </summary>
public sealed class StatusFeedbackTriggerPolicyTests
{
    private static SanityChangeSource Sanity(int value) =>
        (SanityChangeSource)value;

    private static HungerChangeKind Hunger(int value) =>
        (HungerChangeKind)value;

    [Theory]
    [InlineData((int)SanityChangeSource.Food)]
    [InlineData((int)SanityChangeSource.Garbage)]
    [InlineData((int)SanityChangeSource.FlowerCollection)]
    [InlineData((int)SanityChangeSource.DarknessAttack)]
    [InlineData((int)SanityChangeSource.HostileShadowKill)]
    [InlineData((int)SanityChangeSource.TimeLimitPassOut)]
    [InlineData((int)SanityChangeSource.ExhaustionPassOut)]
    [InlineData((int)SanityChangeSource.SanityDarknessSpecialDeath)]
    public void DiscreteSanitySourcesPlay(int source)
    {
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(Sanity(source))
        );
    }

    [Theory]
    [InlineData((int)SanityChangeSource.Equipment)]
    [InlineData((int)SanityChangeSource.Npc)]
    [InlineData((int)SanityChangeSource.Junimo)]
    [InlineData((int)SanityChangeSource.Monster)]
    [InlineData((int)SanityChangeSource.Night)]
    [InlineData((int)SanityChangeSource.Mine)]
    [InlineData((int)SanityChangeSource.Sleep)]
    [InlineData((int)SanityChangeSource.Buff)]
    [InlineData((int)SanityChangeSource.VoluntarySleep)]
    [InlineData((int)SanityChangeSource.BathHouse)]
    public void ContinuousSanitySourcesStaySilent(int source)
    {
        Assert.Equal(
            StatusFeedbackTriggerKind.Continuous,
            StatusFeedbackTriggerPolicy.Classify(Sanity(source))
        );
    }

    [Fact]
    public void UnknownSanitySourceDefaultsToDiscrete()
    {
        // 用户的硬要求：未知/新增来源默认 discrete（自动播音），
        // 以后加新的一次性增减效果不需要在本策略接线。
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(SanityChangeSource.Unknown)
        );
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(Sanity(9999))
        );
    }

    [Fact]
    public void NonGameplayAndResetSanitySourcesStaySilent()
    {
        // 已知非玩法来源：Migration=存档迁移批量写入；Administration=调试命令；
        // HealthDeath=真死亡复活重置（理智 Set 为上限 50%，2026-11-11 用户实机
        // 反馈复活重置不该响，与 Migration/Administration 同类静音）。
        Assert.Equal(
            StatusFeedbackTriggerKind.Continuous,
            StatusFeedbackTriggerPolicy.Classify(SanityChangeSource.Migration)
        );
        Assert.Equal(
            StatusFeedbackTriggerKind.Continuous,
            StatusFeedbackTriggerPolicy.Classify(SanityChangeSource.Administration)
        );
        Assert.Equal(
            StatusFeedbackTriggerKind.Continuous,
            StatusFeedbackTriggerPolicy.Classify(SanityChangeSource.HealthDeath)
        );
    }

    [Theory]
    [InlineData((int)HungerChangeKind.Overtime)]
    [InlineData((int)HungerChangeKind.Debug)]
    [InlineData((int)HungerChangeKind.Revive)]
    public void ContinuousHungerKindsStaySilent(int kind)
    {
        Assert.Equal(
            StatusFeedbackTriggerKind.Continuous,
            StatusFeedbackTriggerPolicy.Classify(Hunger(kind))
        );
    }

    [Theory]
    [InlineData((int)HungerChangeKind.Food)]
    public void DiscreteHungerKindsPlay(int kind)
    {
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(Hunger(kind))
        );
    }

    [Fact]
    public void UnknownHungerKindDefaultsToDiscrete()
    {
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(HungerChangeKind.Unknown)
        );
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(Hunger(9999))
        );
    }

    [Fact]
    public void EveryDefinedSanitySourceIsCoveredByAnExplicitOrOpenRule()
    {
        // 分类表必须对全部枚举值可判定且不给 Unknown（未知即 open 默认，不是静音）。
        foreach (var source in Enum.GetValues<SanityChangeSource>())
        {
            var kind = StatusFeedbackTriggerPolicy.Classify(source);
            Assert.NotEqual(StatusFeedbackTriggerKind.Unknown, kind);
        }
    }

    [Fact]
    public void EveryDefinedHungerKindIsCoveredByAnExplicitOrOpenRule()
    {
        foreach (var kind in Enum.GetValues<HungerChangeKind>())
        {
            var trigger = StatusFeedbackTriggerPolicy.Classify(kind);
            Assert.NotEqual(StatusFeedbackTriggerKind.Unknown, trigger);
        }
    }
}
