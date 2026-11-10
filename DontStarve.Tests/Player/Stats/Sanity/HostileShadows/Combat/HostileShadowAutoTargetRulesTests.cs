using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

/// <summary>
/// 冻结原版自动索敌与冰冻投射物两条接缝的纯判定契约。modData key 字符串必须与
/// HostileShadowMonster 别名引用的真源一致——已物化的存档实体与 Harmony 补丁都依赖这些字面量。
/// </summary>
public sealed class HostileShadowAutoTargetRulesTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    public void Auto_target_exclusion_follows_the_decided_scope(
        bool callerIgnoresAllHostileShadows,
        bool isHostileShadow,
        bool bindingHidden,
        bool expected
    )
    {
        var actual = HostileShadowAutoTargetRules.ExcludesFromAutoTargeting(
            callerIgnoresAllHostileShadows,
            isHostileShadow,
            bindingHidden
        );

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Marked_callers_exclude_even_visible_shadow_states()
    {
        // 冰球/青蛙口径：不索敌任何影怪（可见游荡、进战、脱战恐吓全部排除）。
        Assert.True(
            HostileShadowAutoTargetRules.ExcludesFromAutoTargeting(
                callerIgnoresAllHostileShadows: true,
                isHostileShadow: true,
                bindingHidden: false
            )
        );
    }

    [Fact]
    public void Unmarked_callers_keep_targeting_visible_dangerous_shadows()
    {
        // 魔法箭筒口径：危险态影怪照常可索敌可打，仅绑定隐藏态被普适规则剔除。
        Assert.False(
            HostileShadowAutoTargetRules.ExcludesFromAutoTargeting(
                callerIgnoresAllHostileShadows: false,
                isHostileShadow: true,
                bindingHidden: false
            )
        );
        Assert.True(
            HostileShadowAutoTargetRules.ExcludesFromAutoTargeting(
                callerIgnoresAllHostileShadows: false,
                isHostileShadow: true,
                bindingHidden: true
            )
        );
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Frozen_control_is_suppressed_only_for_frozen_immune_shadows(
        bool frozenImmunityEnabled,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            HostileShadowAutoTargetRules.SuppressesFrozenControlEffect(frozenImmunityEnabled)
        );
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void Mod_data_enabled_flag_accepts_only_the_frozen_literal(
        string? value,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            HostileShadowAutoTargetRules.IsEnabledModDataValue(value)
        );
    }

    [Fact]
    public void Contract_keys_are_frozen_to_the_values_already_in_save_entities()
    {
        Assert.Equal(
            "Yurin.DontStarve/HostileShadow/Immunity/Frozen",
            HostileShadowVanillaCombatContracts.FrozenImmunityModDataKey
        );
        Assert.Equal(
            "Yurin.DontStarve/HostileShadow/BindingHidden",
            HostileShadowVanillaCombatContracts.BindingHiddenModDataKey
        );
        Assert.Equal("1", HostileShadowVanillaCombatContracts.EnabledModDataValue);
    }
}
