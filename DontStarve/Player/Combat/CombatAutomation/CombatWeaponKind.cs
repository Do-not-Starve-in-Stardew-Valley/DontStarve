#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation;

/// <summary>
/// 玩家当前手持物的粗分类。SMAPI 适配层负责把 <c>Farmer.CurrentTool</c> 归到其中一类；
/// 纯规则层只消费该枚举，不接触 Stardew 类型（可被测试工程源码链接编译）。
/// </summary>
internal enum CombatHeldItemKind
{
    MeleeWeapon,
    Slingshot,
    Tool,
    Other,
}

/// <summary>
/// 战斗自动化用的武器类别。Scythe 单独标注（三种镰刀 (W)47/53/66），但长按挥舞裁决上
/// 由剑子开关（AutoSwingSword）涵盖；SlingShot 不受武器子开关管辖，走独立档位驱动。
/// </summary>
internal enum CombatWeaponKind
{
    Sword,
    Club,
    Dagger,
    Scythe,
    SlingShot,
    Tool,
    Other,
}

/// <summary>
/// 武器分类纯规则。输入 = 手持物粗分类 + 近战武器的 <c>MeleeWeapon.type.Value</c>（int）
/// 与 <c>isScythe()</c>（bool）。
/// 1.6 加载后 Data/Weapons 的 Type=0 会在 <c>MeleeWeapon.ReloadData</c> 被重映射为 3
/// （1.6.15 MeleeWeapon.cs:159-162），因此剑类 = type 0 或 3；镰刀标注优先于 type 判定
/// （设计稿 4.2：镰刀不参与剑/锤/匕首 type 语义）。
/// </summary>
internal static class CombatWeaponClassifier
{
    internal const int MeleeTypeStabbingSword = 0;
    internal const int MeleeTypeDagger = 1;
    internal const int MeleeTypeClub = 2;
    internal const int MeleeTypeDefenseSword = 3;

    internal static CombatWeaponKind Classify(
        CombatHeldItemKind held,
        int meleeWeaponTypeValue,
        bool isScythe
    )
    {
        switch (held)
        {
            case CombatHeldItemKind.MeleeWeapon:
                if (isScythe)
                    return CombatWeaponKind.Scythe;
                return meleeWeaponTypeValue switch
                {
                    MeleeTypeStabbingSword or MeleeTypeDefenseSword => CombatWeaponKind.Sword,
                    MeleeTypeDagger => CombatWeaponKind.Dagger,
                    MeleeTypeClub => CombatWeaponKind.Club,
                    _ => CombatWeaponKind.Other,
                };
            case CombatHeldItemKind.Slingshot:
                return CombatWeaponKind.SlingShot;
            case CombatHeldItemKind.Tool:
                return CombatWeaponKind.Tool;
            default:
                return CombatWeaponKind.Other;
        }
    }
}
