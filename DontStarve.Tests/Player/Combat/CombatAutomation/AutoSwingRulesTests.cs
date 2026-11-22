using DontStarve.Player.Combat.CombatAutomation;
using DontStarve.Player.Combat.CombatAutomation.AutoSwing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

public sealed class AutoSwingRulesTests
{
    private const bool MasterOn = true;
    private const bool MasterOff = false;

    [Fact]
    public void Master_switch_off_denies_every_kind_even_with_all_sub_switches_on()
    {
        foreach (var kind in Enum.GetValues<CombatWeaponKind>())
        {
            Assert.False(
                AutoSwingRules.IsAutoSwingPermitted(
                    MasterOff,
                    swordSubSwitchEnabled: true,
                    clubSubSwitchEnabled: true,
                    daggerSubSwitchEnabled: true,
                    weaponKind: kind
                )
            );
        }
    }

    [Theory]
    [InlineData(true, (int)CombatWeaponKind.Sword, true)]
    [InlineData(false, (int)CombatWeaponKind.Sword, false)]
    [InlineData(true, (int)CombatWeaponKind.Club, false)]
    [InlineData(true, (int)CombatWeaponKind.Dagger, false)]
    public void Sword_kind_is_gated_only_by_the_sword_sub_switch(
        bool swordEnabled,
        int kindValue,
        bool expected
    )
    {
        // 内部枚举不能进公共测试方法签名（CS0051），以 int 传递并在调用处还原。
        Assert.Equal(
            expected,
            AutoSwingRules.IsAutoSwingPermitted(
                MasterOn,
                swordSubSwitchEnabled: swordEnabled,
                clubSubSwitchEnabled: false,
                daggerSubSwitchEnabled: false,
                weaponKind: (CombatWeaponKind)kindValue
            )
        );
    }

    [Fact]
    public void Scythe_follows_the_sword_sub_switch()
    {
        Assert.True(
            AutoSwingRules.IsAutoSwingPermitted(
                MasterOn,
                swordSubSwitchEnabled: true,
                clubSubSwitchEnabled: false,
                daggerSubSwitchEnabled: false,
                weaponKind: CombatWeaponKind.Scythe
            )
        );
        Assert.False(
            AutoSwingRules.IsAutoSwingPermitted(
                MasterOn,
                swordSubSwitchEnabled: false,
                clubSubSwitchEnabled: true,
                daggerSubSwitchEnabled: true,
                weaponKind: CombatWeaponKind.Scythe
            )
        );
    }

    [Theory]
    [InlineData((int)CombatWeaponKind.Club, true)]
    [InlineData((int)CombatWeaponKind.Club, false)]
    [InlineData((int)CombatWeaponKind.Dagger, true)]
    [InlineData((int)CombatWeaponKind.Dagger, false)]
    public void Club_and_dagger_kinds_are_gated_by_their_own_sub_switches(
        int kindValue,
        bool subSwitchEnabled
    )
    {
        var kind = (CombatWeaponKind)kindValue;
        var permitted = AutoSwingRules.IsAutoSwingPermitted(
            MasterOn,
            swordSubSwitchEnabled: subSwitchEnabled,
            clubSubSwitchEnabled: kind == CombatWeaponKind.Club && subSwitchEnabled,
            daggerSubSwitchEnabled: kind == CombatWeaponKind.Dagger && subSwitchEnabled,
            weaponKind: kind
        );
        Assert.Equal(subSwitchEnabled, permitted);
    }

    [Fact]
    public void Slingshot_tool_and_other_are_never_permitted_by_the_melee_gate()
    {
        foreach (var kind in new[]
                 {
                     CombatWeaponKind.SlingShot,
                     CombatWeaponKind.Tool,
                     CombatWeaponKind.Other,
                 })
        {
            Assert.False(
                AutoSwingRules.IsAutoSwingPermitted(
                    MasterOn,
                    swordSubSwitchEnabled: true,
                    clubSubSwitchEnabled: true,
                    daggerSubSwitchEnabled: true,
                    weaponKind: kind
                )
            );
        }
    }
}
