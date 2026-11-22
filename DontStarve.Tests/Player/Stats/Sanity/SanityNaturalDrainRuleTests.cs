#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityNaturalDrainRuleTests
{
    [Theory]
    [InlineData("TimePlace", (int)SanityNaturalDrainRule.TimePlace)]
    [InlineData("ByLight", (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("Off", (int)SanityNaturalDrainRule.Off)]
    public void ConfigValuesParseExactly(string value, int expected)
    {
        Assert.True(SanityNaturalDrainRulePolicy.TryParse(value, out var rule));
        Assert.Equal((SanityNaturalDrainRule)expected, rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bylight")]
    [InlineData("Legacy")]
    public void UnknownValuesFailClosedToTimePlace(string? value)
    {
        Assert.False(SanityNaturalDrainRulePolicy.TryParse(value, out var rule));
        Assert.Equal(SanityNaturalDrainRule.TimePlace, rule);
    }

    [Theory]
    [InlineData(null, (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("", (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("bylight", (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("Legacy", (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("TimePlace", (int)SanityNaturalDrainRule.TimePlace)]
    [InlineData("ByLight", (int)SanityNaturalDrainRule.ByLight)]
    [InlineData("Off", (int)SanityNaturalDrainRule.Off)]
    public void MissingOrInvalidConfigFallsBackToShippedDefault(
        string? value,
        int expected
    )
    {
        // 用户 2026-11-17 追加拍板：配置缺失/损坏/值非法 = 默认按光照程度（同 GMCM 默认），
        // 只保留纯解析，不保留旧「失败退回时间地点规则」的特例。
        Assert.Equal(
            (SanityNaturalDrainRule)expected,
            SanityNaturalDrainRulePolicy.ResolveOrDefault(value)
        );
    }

    [Theory]
    [InlineData(360, 1800, true)]      // 第1天 6:00 春夏白天起点
    [InlineData(1079, 1800, true)]     // 春夏 17:59 仍是白天
    [InlineData(1080, 1800, false)]    // 春夏 18:00 起非白天（黄昏按光值）
    [InlineData(15120, 1800, true)]    // 第10天正午：修复点——累计分钟必须取模
    [InlineData(15480, 1800, false)]   // 第11天 18:00（正午+6小时，%1440=1080）
    [InlineData(900, 1500, false)]     // 冬季 15:00 即非白天（startDark=15:00）
    [InlineData(899, 1500, true)]      // 冬季 14:59 仍白天
    [InlineData(30, 1800, false)]      // 凌晨 0:30 非白天（按光值档）
    [InlineData(120, 1800, false)]     // 次日 2:00（跨午夜 26:00→120）非白天
    [InlineData(-1440, 1800, false)]   // 负累计取模：-1440%1440=0 → 0:00 非白天
    public void SurfaceDayBranchUsesModuloMinutesNotAccumulatedTime(
        long time,
        int startDarkTimeOfDay,
        bool expected
    )
    {
        // 2026-11-18 白天持续掉 san 回归根因：time 是跨天累计分钟，直接与
        // timeOfDay 格式比较导致累计超过 startDark 后永远判「非白天」。
        Assert.Equal(
            expected,
            SanityNaturalDrainRulePolicy.IsDayOnSurface(time, startDarkTimeOfDay)
        );
    }

    [Fact]
    public void InvalidStartDarkTimeOfDayFailsClosedToNotDay()
    {
        Assert.False(SanityNaturalDrainRulePolicy.IsDayOnSurface(720, -1));
        Assert.False(SanityNaturalDrainRulePolicy.IsDayOnSurface(720, 1560)); // 分钟位 60 非法
    }

    [Fact]
    public void MissingLightEvidenceDrainsNothing()
    {
        Assert.Equal(
            0d,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: false,
                isCaveLike: false,
                hasConfirmedScore: false,
                score: 0d
            )
        );
        // 非法分数视同证据缺失，绝不按快档结算。
        Assert.Equal(
            0d,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: false,
                isCaveLike: true,
                hasConfirmedScore: true,
                score: double.NaN
            )
        );
    }

    [Fact]
    public void SurfaceDayDrainsNothingRegardlessOfLight()
    {
        Assert.Equal(
            0d,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: true,
                isCaveLike: false,
                hasConfirmedScore: true,
                score: 0d
            )
        );
        Assert.Equal(
            0d,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: true,
                isCaveLike: false,
                hasConfirmedScore: true,
                score: 0.95d
            )
        );
    }

    [Fact]
    public void CaveLikeLocationsIgnoreTheDayBranch()
    {
        Assert.Equal(
            SanityNaturalDrainRulePolicy.SlowPerGameMinute,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: true,
                isCaveLike: true,
                hasConfirmedScore: true,
                score: 0.63d
            )
        );
    }

    [Theory]
    [InlineData(0.09d, 0.2778d)]
    [InlineData(0.1d, 0.02778d)]
    [InlineData(0.5d, 0.02778d)]
    [InlineData(0.6d, 0.02778d)]
    [InlineData(0.61d, 0.02778d)]
    public void FastTierAppliesStrictlyBelowDustLowLight(
        double score,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
                isDayOnSurface: false,
                isCaveLike: false,
                hasConfirmedScore: true,
                score
            ),
            precision: 10
        );
    }
}
