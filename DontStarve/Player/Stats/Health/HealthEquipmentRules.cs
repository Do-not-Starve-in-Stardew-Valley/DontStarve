#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 六槽装备的当前穿戴快照（裸 ItemId，可空）。镜像理智侧 EquipmentSanityLoadout 形态；
/// 槽位到表的映射（hat/shirt/pants/boots/ring/trinket）由边界层组装时固定。
/// </summary>
internal readonly record struct HealthEquipmentLoadout(
    string? HatId,
    string? ShirtId,
    string? PantsId,
    string? BootsId,
    string? LeftRingId,
    string? RightRingId,
    string? TrinketId
);

/// <summary>
/// 装备血量效果求和纯规则（决议 3，模板照抄理智装备机器）：逐槽查表累加、
/// 左右戒指共用 ring 表、非有限表值与非有限总和一律归 0（fail-neutral）。
/// 表 key 与 JSON 表的裸 ItemId 字符串匹配。
/// </summary>
internal static class HealthEquipmentRules
{
    internal static double CalculateEquipmentDelta(
        HealthEquipmentLoadout loadout,
        IReadOnlyDictionary<string, double> hatValues,
        IReadOnlyDictionary<string, double> shirtValues,
        IReadOnlyDictionary<string, double> pantsValues,
        IReadOnlyDictionary<string, double> bootsValues,
        IReadOnlyDictionary<string, double> ringValues,
        IReadOnlyDictionary<string, double> trinketValues
    )
    {
        var total = 0d;
        total += GetConfiguredValue(hatValues, loadout.HatId);
        total += GetConfiguredValue(shirtValues, loadout.ShirtId);
        total += GetConfiguredValue(pantsValues, loadout.PantsId);
        total += GetConfiguredValue(bootsValues, loadout.BootsId);
        total += GetConfiguredValue(ringValues, loadout.LeftRingId);
        total += GetConfiguredValue(ringValues, loadout.RightRingId);
        total += GetConfiguredValue(trinketValues, loadout.TrinketId);
        return double.IsFinite(total) ? total : 0;
    }

    private static double GetConfiguredValue(
        IReadOnlyDictionary<string, double> values,
        string? itemId
    )
    {
        if (
            string.IsNullOrWhiteSpace(itemId)
            || !values.TryGetValue(itemId, out var value)
            || !double.IsFinite(value)
        )
        {
            return 0;
        }

        return value;
    }
}
