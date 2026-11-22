using DontStarve.Player.Combat.CombatAutomation;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

public sealed class CombatWeaponClassifierTests
{
    [Theory]
    [InlineData(0, (int)CombatWeaponKind.Sword)]
    [InlineData(1, (int)CombatWeaponKind.Dagger)]
    [InlineData(2, (int)CombatWeaponKind.Club)]
    [InlineData(3, (int)CombatWeaponKind.Sword)]
    [InlineData(7, (int)CombatWeaponKind.Other)]
    public void Melee_type_maps_to_expected_kind(int typeValue, int expectedKind)
    {
        // 内部枚举不能进公共测试方法签名（CS0051），以 int 传递并在断言处还原。
        Assert.Equal(
            (CombatWeaponKind)expectedKind,
            CombatWeaponClassifier.Classify(CombatHeldItemKind.MeleeWeapon, typeValue, isScythe: false)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Scythe_annotation_wins_over_melee_type(int typeValue)
    {
        Assert.Equal(
            CombatWeaponKind.Scythe,
            CombatWeaponClassifier.Classify(CombatHeldItemKind.MeleeWeapon, typeValue, isScythe: true)
        );
    }

    [Fact]
    public void Held_kinds_map_without_melee_inputs()
    {
        Assert.Equal(
            CombatWeaponKind.SlingShot,
            CombatWeaponClassifier.Classify(CombatHeldItemKind.Slingshot, 0, false)
        );
        Assert.Equal(
            CombatWeaponKind.Tool,
            CombatWeaponClassifier.Classify(CombatHeldItemKind.Tool, 0, false)
        );
        Assert.Equal(
            CombatWeaponKind.Other,
            CombatWeaponClassifier.Classify(CombatHeldItemKind.Other, 0, false)
        );
    }
}
