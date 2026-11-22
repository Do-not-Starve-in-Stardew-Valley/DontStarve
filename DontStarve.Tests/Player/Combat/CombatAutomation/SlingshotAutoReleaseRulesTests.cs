using DontStarve.Player.Combat.CombatAutomation.AutoSwing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

public sealed class SlingshotAutoReleaseRulesTests
{
    [Fact]
    public void Vanilla_back_arm_gate_stays_closed_below_the_vanilla_fire_threshold()
    {
        // 原版发射门 GetBackArmDistance > 4；非 legacy 下 (int)(20f × charge) 截断后
        // 需要 charge ≥ 0.25 才能满足（charge=0.24 → 4 → 不发射）。任何力度都不得绕过该门。
        Assert.False(SlingshotAutoReleaseRules.ShouldRelease(25, 0.24f, 4));
        Assert.False(SlingshotAutoReleaseRules.ShouldRelease(50, 0.1f, 2));
        Assert.False(SlingshotAutoReleaseRules.ShouldRelease(100, 1f, 4));
    }

    [Fact]
    public void Min_power_25_tracks_the_vanilla_fire_gate_exactly()
    {
        // 力度 25% ≈ 旧 Min 档：发射门一开（charge ≥ 0.25）即射。
        Assert.True(
            SlingshotAutoReleaseRules.ShouldRelease(25, 0.25f, 5)
        );
        Assert.True(
            SlingshotAutoReleaseRules.ShouldRelease(25, 1f, 20)
        );
    }

    [Theory]
    [InlineData(0.49f, false)]
    [InlineData(0.5f, true)]
    [InlineData(0.75f, true)]
    public void Power_50_releases_at_half_charge(float chargeTime, bool expected)
    {
        Assert.Equal(
            expected,
            SlingshotAutoReleaseRules.ShouldRelease(
                50,
                chargeTime,
                backArmDistance: 20
            )
        );
    }

    [Theory]
    [InlineData(0.99f, false)]
    [InlineData(1f, true)]
    public void Power_100_releases_only_at_full_charge(float chargeTime, bool expected)
    {
        Assert.Equal(
            expected,
            SlingshotAutoReleaseRules.ShouldRelease(
                100,
                chargeTime,
                backArmDistance: 20
            )
        );
    }

    [Theory]
    [InlineData(30, 0.29f, false)]
    [InlineData(30, 0.3f, true)]
    [InlineData(85, 0.84f, false)]
    [InlineData(85, 0.85f, true)]
    public void Intermediate_powers_release_at_their_own_charge_threshold(
        int powerPercent,
        float chargeTime,
        bool expected
    )
    {
        // 滑轮化后的连续力度：任意 25..100 值按各自阈值判定（旧档位版只有三档）。
        Assert.Equal(
            expected,
            SlingshotAutoReleaseRules.ShouldRelease(
                powerPercent,
                chargeTime,
                backArmDistance: 20
            )
        );
    }

    [Fact]
    public void Boundary_powers_match_the_schema_slider_bounds()
    {
        // 规则的边界常量与 schema 滑轮边界（config-options.json Min/Max）一致：
        // 低于 25% 是原版死区不开放，高于 100% 无意义。
        Assert.Equal(25, SlingshotAutoReleaseRules.MinPowerPercent);
        Assert.Equal(100, SlingshotAutoReleaseRules.MaxPowerPercent);
        Assert.False(
            SlingshotAutoReleaseRules.ShouldRelease(
                SlingshotAutoReleaseRules.MaxPowerPercent,
                0.99f,
                20
            )
        );
        Assert.True(
            SlingshotAutoReleaseRules.ShouldRelease(
                SlingshotAutoReleaseRules.MaxPowerPercent,
                1f,
                20
            )
        );
    }
}
