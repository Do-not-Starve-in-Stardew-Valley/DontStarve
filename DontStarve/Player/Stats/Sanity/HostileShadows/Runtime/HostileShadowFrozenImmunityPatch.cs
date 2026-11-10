#nullable enable

using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Projectiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// 方案①命中兜底：原版冰冻投射物（DebuffingProjectile，debuff="frozen"，冰球饰品发射）在
/// behaviorOnCollisionWithMonster 里没有任何免疫询问口——直接写 stunTime、播 "frozen" 音效、
/// 生成挂在怪物精灵图左上角并跟随的冰块贴纸。对带冻结免疫 modData 标记的影怪跳过整个原方法，
/// 音效/冰块/stunTime 全部不发生；投射物穿透次数未消耗，继续按原轨迹穿行。HostileShadowMonster
/// 现有的 stunTime 清零逻辑保持不变，继续作为非视觉层的效果兜底。
/// </summary>
internal static class HostileShadowFrozenImmunityPatch
{
    private static IMonitor? monitor;
    private static bool firstSuppressionLogged;

    internal static bool TryInstall(string harmonyId, IMonitor patchMonitor)
    {
        if (patchMonitor is null)
            throw new ArgumentNullException(nameof(patchMonitor));

        try
        {
            var target = AccessTools.Method(
                typeof(DebuffingProjectile),
                nameof(DebuffingProjectile.behaviorOnCollisionWithMonster),
                new[] { typeof(NPC), typeof(GameLocation) }
            );
            var prefix = AccessTools.Method(
                typeof(HostileShadowFrozenImmunityPatch),
                nameof(Prefix)
            );
            if (target is null || prefix is null)
            {
                patchMonitor.Log(
                    "Hostile shadow frozen immunity patch target unavailable; frozen projectiles keep producing ice visuals on shadows.",
                    LogLevel.Warn
                );
                return false;
            }

            new Harmony(harmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix)
            );
            monitor = patchMonitor;
            firstSuppressionLogged = false;
            patchMonitor.Log(
                "Hostile shadow frozen immunity patch installed for DebuffingProjectile.behaviorOnCollisionWithMonster.",
                LogLevel.Trace
            );
            return true;
        }
        catch (Exception exception)
        {
            monitor = null;
            patchMonitor.Log(
                $"Hostile shadow frozen immunity patch failed ({exception.GetType().Name}: {exception.Message}); vanilla frozen projectiles remain active.",
                LogLevel.Warn
            );
            return false;
        }
    }

    // 返回 false 跳过原方法：命中瞬间对冻结免疫影怪不做任何事。判断只读 modData 标记，
    // 不写死物种类型，未来资源表新增带 Frozen 免疫的影怪自动生效；普通怪物原样走原版路径。
    internal static bool Prefix(NPC n)
    {
        if (
            n is Monster monster
            && HostileShadowAutoTargetRules.SuppressesFrozenControlEffect(
                HasEnabledModData(
                    monster,
                    HostileShadowVanillaCombatContracts.FrozenImmunityModDataKey
                )
            )
        )
        {
            LogFirstSuppression();
            return false;
        }

        return true;
    }

    private static bool HasEnabledModData(Monster monster, string key)
    {
        return monster.modData.TryGetValue(key, out var value)
            && HostileShadowAutoTargetRules.IsEnabledModDataValue(value);
    }

    private static void LogFirstSuppression()
    {
        if (firstSuppressionLogged || monitor is null)
            return;

        firstSuppressionLogged = true;
        monitor.Log(
            "Hostile shadow frozen immunity suppressed its first frozen projectile hit; further suppressions are silent.",
            LogLevel.Trace
        );
    }
}
