using System;
using System.Collections.Generic;
using System.Linq;
using DontStarve.Audio.StatusFeedback;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Audio.StatusFeedback;

/// <summary>
/// 触发策略分类表契约：对齐 DST 语义——只有 not-overtime 的离散增减才播音。
/// Theory 参数用 int 承载内部枚举，避免测试方法签名泄露 internal 类型。
/// </summary>
/// <remarks>
/// 2026-11-18 加严教训：AmbientLight 新来源漏接静音清单时，旧的遍历断言只查
/// 「不给 Unknown」，open 默认（未知→Discrete）天然满足，等于没设防。现在
/// ExpectedContinuous*/ExpectedDiscrete* 两份期望表是权威全集：新增枚举值不显式
/// 归入其一，并集断言与逐值断言双双爆红。
/// </remarks>
public sealed class StatusFeedbackTriggerPolicyTests
{
    private static SanityChangeSource Sanity(int value) =>
        (SanityChangeSource)value;

    private static HungerChangeKind Hunger(int value) =>
        (HungerChangeKind)value;

    // —— 权威期望表（全集）：新增枚举值必须显式归入其一 ——

    /// <summary>Sanity 静音侧：持续来源 + 系统重置（语义见策略文件 XML 注释逐条对应）。</summary>
    private static readonly IReadOnlySet<SanityChangeSource> ExpectedContinuousSanity =
        new HashSet<SanityChangeSource>
        {
            SanityChangeSource.Equipment,
            SanityChangeSource.Npc,
            SanityChangeSource.Junimo,
            SanityChangeSource.Monster,
            SanityChangeSource.Night,
            SanityChangeSource.Mine,
            SanityChangeSource.Sleep,
            SanityChangeSource.Buff,
            SanityChangeSource.VoluntarySleep,
            SanityChangeSource.BathHouse,

            // 按光照自然流失=每游戏分钟持续来源，静音（2026-11-18 补接回归）。
            SanityChangeSource.AmbientLight,

            SanityChangeSource.Migration,
            SanityChangeSource.Administration,
            SanityChangeSource.HealthDeath,
        };

    /// <summary>
    /// Sanity 播音侧：一次性事件 + Unknown（Unknown 走 open 默认 Discrete，
    /// 是枚举定义成员，必须显式入表才能被并集断言覆盖）。
    /// </summary>
    private static readonly IReadOnlySet<SanityChangeSource> ExpectedDiscreteSanity =
        new HashSet<SanityChangeSource>
        {
            SanityChangeSource.Unknown,
            SanityChangeSource.Food,
            SanityChangeSource.Garbage,
            SanityChangeSource.FlowerCollection,
            SanityChangeSource.DarknessAttack,
            SanityChangeSource.HostileShadowKill,
            SanityChangeSource.TimeLimitPassOut,
            SanityChangeSource.ExhaustionPassOut,
            SanityChangeSource.SanityDarknessSpecialDeath,
        };

    /// <summary>Hunger 静音侧：overtime 语义 + 调试写入 + 死亡复活重置。</summary>
    private static readonly IReadOnlySet<HungerChangeKind> ExpectedContinuousHunger =
        new HashSet<HungerChangeKind>
        {
            HungerChangeKind.Overtime,
            HungerChangeKind.Debug,
            HungerChangeKind.Revive,
        };

    /// <summary>Hunger 播音侧：进食一次性事件 + Unknown（open 默认）。</summary>
    private static readonly IReadOnlySet<HungerChangeKind> ExpectedDiscreteHunger =
        new HashSet<HungerChangeKind>
        {
            HungerChangeKind.Unknown,
            HungerChangeKind.Food,
        };

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
    [InlineData((int)SanityChangeSource.AmbientLight)]
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
        // Unknown=0 是枚举定义成员，已显式归入 ExpectedDiscreteSanity。
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
        // Unknown=0 是枚举定义成员，已显式归入 ExpectedDiscreteHunger。
        Assert.Equal(
            StatusFeedbackTriggerKind.Discrete,
            StatusFeedbackTriggerPolicy.Classify(Hunger(9999))
        );
    }

    [Fact]
    public void EveryDefinedSanitySourceHasAnExplicitExpectedClassification()
    {
        // 强断言（2026-11-18 加严，替代只查「不给 Unknown」的弱遍历）：
        // 1) 期望表并集 == 枚举全集 → 新增来源不显式入表即爆红；
        // 2) 两集合不相交 → 同一来源不得同时归入两侧；
        // 3) 逐值分类与集合归属一致 → 策略 switch 与期望表永不漂移。
        var all = Enum.GetValues<SanityChangeSource>().ToHashSet();

        var union = ExpectedContinuousSanity
            .Union(ExpectedDiscreteSanity)
            .ToHashSet();
        Assert.True(
            union.SetEquals(all),
            "SanityChangeSource 出现未归类的枚举值：" +
            string.Join(", ", all.Except(union).Select(v => $"{v}={(int)v}"))
        );
        Assert.Empty(ExpectedContinuousSanity.Intersect(ExpectedDiscreteSanity));

        foreach (var source in all)
        {
            var expectedKind = ExpectedContinuousSanity.Contains(source)
                ? StatusFeedbackTriggerKind.Continuous
                : StatusFeedbackTriggerKind.Discrete;
            Assert.Equal(
                expectedKind,
                StatusFeedbackTriggerPolicy.Classify(source)
            );
        }
    }

    [Fact]
    public void EveryDefinedHungerKindHasAnExplicitExpectedClassification()
    {
        // 同上：Hunger 侧全集强断言（Unknown 走 open 默认，显式归入 Discrete 表）。
        var all = Enum.GetValues<HungerChangeKind>().ToHashSet();

        var union = ExpectedContinuousHunger
            .Union(ExpectedDiscreteHunger)
            .ToHashSet();
        Assert.True(
            union.SetEquals(all),
            "HungerChangeKind 出现未归类的枚举值：" +
            string.Join(", ", all.Except(union).Select(v => $"{v}={(int)v}"))
        );
        Assert.Empty(ExpectedContinuousHunger.Intersect(ExpectedDiscreteHunger));

        foreach (var kind in all)
        {
            var expectedKind = ExpectedContinuousHunger.Contains(kind)
                ? StatusFeedbackTriggerKind.Continuous
                : StatusFeedbackTriggerKind.Discrete;
            Assert.Equal(
                expectedKind,
                StatusFeedbackTriggerPolicy.Classify(kind)
            );
        }
    }
}
