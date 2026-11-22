#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation.AutoSwing;

/// <summary>
/// 长按挥舞放行裁决（纯函数，设计稿 5.2）。总开关关闭 = 全部不放行；武器类别与对应
/// 子开关不匹配不放行；镰刀（Scythe）按剑子开关裁决（用户决议：三种镰刀由「剑」选项
/// 涵盖）；SlingShot/Tool/Other 一律不放行——弹弓按住行为由 SlingshotAutoRelease 开关
/// 加 SlingshotPower 力度滑轮的发射驱动单独承接，工具保持原版自带连用（决议 1）。
/// 「按住中」判定与输入守卫不在本规则内，由 SMAPI postfix 侧判定。
/// </summary>
internal static class AutoSwingRules
{
    internal static bool IsAutoSwingPermitted(
        bool masterEnabled,
        bool swordSubSwitchEnabled,
        bool clubSubSwitchEnabled,
        bool daggerSubSwitchEnabled,
        CombatWeaponKind weaponKind
    )
    {
        if (!masterEnabled)
            return false;

        return weaponKind switch
        {
            CombatWeaponKind.Sword or CombatWeaponKind.Scythe => swordSubSwitchEnabled,
            CombatWeaponKind.Club => clubSubSwitchEnabled,
            CombatWeaponKind.Dagger => daggerSubSwitchEnabled,
            _ => false,
        };
    }
}
