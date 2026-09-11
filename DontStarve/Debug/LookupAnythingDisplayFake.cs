#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Debug;

/// <summary>
/// DIAG-20260807: LookupAnything 显示拦截（getter 层，不修改怪物字段 → 零联机风险）。
/// Lookup 的怪物攻击栏读 Monster.DamageToFarmer 属性 getter；本 mod 为禁原版接触伤害
/// 把该字段恒置 0。方案：Harmony patch getter——仅当 isLookupEnumerating（CharacterSubject
/// 构造 Postfix 打开；HostileShadowMonster.update 每 tick 关闭）时对影怪返回配置攻击力
/// （modData DisplayDamageModDataKey），其余时间返回真实值 0。
/// 安全性：getter 拦截是纯本地行为，不写 NetInt 字段 → 网络同步值恒为 0，其他玩家/其他
/// 机器读到的永远是 0；本机 Lookup 菜单打开时游戏暂停（activeClickableMenu → paused），
/// 本机无人能移动碰撞，窗口无实际暴露。
/// 掉落：改为 TryMaterialize 静态写 2 个虚空精华（纯展示；实际掉落走自有结算）。
/// 重要：不要 patch Lookup 内部的迭代器方法（&lt;GetData&gt;d__XX.MoveNext）——Harmony/
/// MonoMod 对编译器生成的迭代器状态机方法做 Detour 会抛 InvalidProgramException（实测
/// Mod crashed on entry）。窗口开关只能用非迭代器的切入点（构造函数）。
/// </summary>
internal static class LookupAnythingDisplayFake
{
    private const string LookupAssemblyName = "LookupAnything";
    private const string SubjectTypeName =
        "Pathoschild.Stardew.LookupAnything.Framework.Lookups.Characters.CharacterSubject";
    private const string DropListFieldTypeName =
        "Pathoschild.Stardew.LookupAnything.Framework.Fields.ItemDropListField";
    private const string ItemDropDataTypeName =
        "Pathoschild.Stardew.LookupAnything.Framework.Data.ItemDropData";

    private static bool patched;
    private static bool isLookupEnumerating;
    // DIAG-20260807: 当前 Lookup 目标是否为影怪（CharacterSubject 构造 Postfix 设置）。
    // ItemDropListField 构造 Postfix 消费：把第二个虚空精华掉落改成 50% 概率显示。
    private static bool isShadowLookupTarget;

    internal static void TryPatch(IMonitor monitor)
    {
        if (patched)
            return;
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(
                a.GetName().Name,
                LookupAssemblyName,
                StringComparison.OrdinalIgnoreCase
            ));
        var subjectType = assembly?.GetType(SubjectTypeName);
        if (subjectType is null)
        {
            monitor.Log(
                "LookupAnything display fake: LookupAnything not loaded, skip.",
                LogLevel.Debug
            );
            return;
        }
        var harmony = new Harmony("Yurin.DontStarve.LookupAnythingDisplayFake");

        // 1) patch Monster.DamageToFarmer getter——Lookup 窗口期间对影怪返回配置攻击力。
        // 1.6.15 中该属性定义于 Monster（NPC 基类可能也有同名，回退尝试）。
        var damageGetter = AccessTools.PropertyGetter(typeof(Monster), "DamageToFarmer");
        if (damageGetter is null)
            damageGetter = AccessTools.PropertyGetter(typeof(NPC), "DamageToFarmer");
        if (damageGetter is null)
        {
            monitor.Log(
                "LookupAnything display fake: DamageToFarmer getter not found, skip.",
                LogLevel.Warn
            );
            return;
        }
        harmony.Patch(
            damageGetter,
            prefix: new HarmonyMethod(
                typeof(LookupAnythingDisplayFake).GetMethod(
                    nameof(DamageGetterPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic
                )
            )
        );

        // 2) CharacterSubject 构造 Postfix——打开 Lookup 显示窗口。Lookup 的 GetData 在
        // 构造后的 LookupMenu 构造中同步枚举（ToArray），窗口保持到 update 关闭即可。
        // 不要尝试 patch GetData/任何迭代器——会 InvalidProgramException。
        var ctor = subjectType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 11);
        if (ctor is null)
        {
            monitor.Log(
                "LookupAnything display fake: CharacterSubject constructor not found, skip.",
                LogLevel.Warn
            );
            return;
        }
        harmony.Patch(
            ctor,
            postfix: new HarmonyMethod(
                typeof(LookupAnythingDisplayFake).GetMethod(
                    nameof(SubjectCtorPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic
                )
            )
        );

        // 3) ItemDropListField 构造 Postfix——影怪的第二个掉落（虚空精华）显示 50% 概率。
        // 构造函数非迭代器，patch 安全；改 Drops 数组元素（readonly 字段引用不变）。
        var dropFieldType = assembly?.GetType(DropListFieldTypeName);
        var dropDataType = assembly?.GetType(ItemDropDataTypeName);
        if (dropFieldType is not null && dropDataType is not null)
        {
            var dropCtor = dropFieldType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 9);
            if (dropCtor is not null)
            {
                harmony.Patch(
                    dropCtor,
                    postfix: new HarmonyMethod(
                        typeof(LookupAnythingDisplayFake).GetMethod(
                            nameof(DropListCtorPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic
                        )
                    )
                );
            }
        }
        patched = true;
        monitor.Log(
            "LookupAnything display fake patched (shadow monster attack shown via getter interception).",
            LogLevel.Debug
        );
    }

    /// <summary>由 HostileShadowMonster.update 每 tick 调用，关闭 Lookup 显示窗口。</summary>
    internal static void EndLookupWindow()
    {
        isLookupEnumerating = false;
    }

    private static bool DamageGetterPrefix(
        Monster __instance,
        ref int __result
    )
    {
        if (
            isLookupEnumerating
            && __instance is HostileShadowMonster monster
            && monster.modData.TryGetValue(
                HostileShadowMonster.DisplayDamageModDataKey,
                out var displayText
            )
            && int.TryParse(
                displayText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var displayDamage
            )
        )
        {
            __result = displayDamage;
            return false;
        }
        return true;
    }

    private static void SubjectCtorPostfix(NPC npc)
    {
        isLookupEnumerating = true;
        // 每次构造重置标记：普通怪物 false，影怪 true（ItemDropListField Postfix 消费）。
        isShadowLookupTarget = npc is HostileShadowMonster;
    }

    /// <summary>
    /// DIAG-20260807: 影怪掉落概率显示。Lookup 的 ItemDropListField 构造完成后，
    /// 若目标是影怪，把第二个虚空精华掉落（原 Probability=1 保底）改为 0.5f——
    /// Lookup 会显示为 "50% 几率 虚空精华"（Probability &gt; 0.99 才显示纯名称）。
    /// 纯显示层修改（反射改数组元素），不触碰怪物字段、无网络同步，零联机风险。
    /// </summary>
    private static void DropListCtorPostfix(object __instance)
    {
        if (!isShadowLookupTarget)
            return;
        isShadowLookupTarget = false;
        try
        {
            var type = __instance.GetType();
            var dropsField = type.GetField(
                "Drops",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            if (dropsField?.GetValue(__instance) is not Array drops || drops.Length < 2)
                return;
            var tuple = drops.GetValue(1);
            if (tuple is null)
                return;
            var tupleType = tuple.GetType();
            var dataField = tupleType.GetField("Item1");
            var itemField = tupleType.GetField("Item2");
            var spriteField = tupleType.GetField("Item3");
            if (
                dataField?.GetValue(tuple) is not { } data
                || itemField?.GetValue(tuple) is not { } item
            )
            {
                return;
            }
            var dataType = data.GetType();
            var itemId = (string?)dataType.GetProperty("ItemId")?.GetValue(data);
            var probability = (float?)dataType.GetProperty("Probability")?.GetValue(data);
            if (
                probability is > 0.99f
                && (string.Equals(itemId, "769", StringComparison.Ordinal)
                    || string.Equals(itemId, "(O)769", StringComparison.Ordinal))
            )
            {
                // ItemDropData 主构造：(string ItemId, int MinDrop, int MaxDrop, float Probability, string? Conditions)
                var newData = Activator.CreateInstance(
                    dataType,
                    itemId,
                    1,
                    1,
                    0.5f,
                    null
                );
                var newTuple = Activator.CreateInstance(
                    tupleType,
                    newData,
                    item,
                    spriteField?.GetValue(tuple)
                );
                drops.SetValue(newTuple, 1);
            }
        }
        catch
        {
            // 显示层兜底：任何反射失败都保持原样，绝不影响游戏逻辑。
        }
    }
}
