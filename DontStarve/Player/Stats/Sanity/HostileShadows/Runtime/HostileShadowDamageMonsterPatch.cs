#nullable enable

using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// DIAG-20260812: 守卫影怪（恐吓/脱战隐藏）受击跳字拦截。1.6.15 原版
/// GameLocation.damageMonster 在 takeDamage 返回 0 时仍会添加伤害数字（显示 0），
/// 受击守卫只挡了扣血、挡不住跳字。prefix 检测攻击范围命中的目标：若全部是
/// 守卫状态的影怪，直接跳过整个受击处理（不扣血、不跳字、无命中音效/反馈）。
/// 命中未守卫影怪或其他目标时交还原版完整处理。
/// </summary>
internal static class HostileShadowDamageMonsterPatch
{
    internal static bool Prefix(
        Rectangle areaOfEffect,
        GameLocation __instance,
        ref bool __result
    )
    {
        var hitGuardedShadow = false;
        foreach (var character in __instance.characters)
        {
            if (character is not HostileShadowMonster monster)
                continue;
            if (!monster.GetBoundingBox().Intersects(areaOfEffect))
                continue;
            if (!IsGuarded(monster))
                return true; // 命中未守卫影怪：交还原版完整处理。
            hitGuardedShadow = true;
        }
        if (hitGuardedShadow)
        {
            __result = false;
            return false;
        }
        return true;
    }

    private static bool IsGuarded(HostileShadowMonster monster)
    {
        return IsFlag(monster, HostileShadowMonster.BindingHiddenModDataKey)
            || IsFlag(monster, HostileShadowMonster.RetreatingModDataKey);
    }

    private static bool IsFlag(HostileShadowMonster monster, string key)
    {
        return monster.modData.TryGetValue(key, out var value)
            && string.Equals(value, "1", StringComparison.Ordinal);
    }
}
