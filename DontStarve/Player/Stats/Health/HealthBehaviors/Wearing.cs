#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Health.HealthBehaviors;

/// <summary>
/// 装备血量效果边界层（决议 3，模板照抄理智 Wearing）：由 Health 宿主每游戏 1 分钟调用一次，
/// 读取六槽现穿戴 → 组装纯规则 loadout → HealthEquipmentRules 求和。
/// 本类只负责读数与求和，写血、余数落账与死亡判定归 Health 宿主结算。
/// 与理智 Wearing 的刻意差异：六张表加载 fail-closed（失败 Warn + 空表，先例
/// HungerEquipmentRuntime），不学理智的裸加载（加载失败会直接崩游戏）。
/// </summary>
internal sealed class Wearing
{
    private const string HatPath = "Asset/Health/hat.json";
    private const string ShirtPath = "Asset/Health/shirt.json";
    private const string PantsPath = "Asset/Health/pants.json";
    private const string BootsPath = "Asset/Health/boots.json";
    private const string RingPath = "Asset/Health/ring.json";
    private const string TrinketPath = "Asset/Health/trinket.json";

    private IReadOnlyDictionary<string, double> hatValues = EmptyTable();
    private IReadOnlyDictionary<string, double> shirtValues = EmptyTable();
    private IReadOnlyDictionary<string, double> pantsValues = EmptyTable();
    private IReadOnlyDictionary<string, double> bootsValues = EmptyTable();
    private IReadOnlyDictionary<string, double> ringValues = EmptyTable();
    private IReadOnlyDictionary<string, double> trinketValues = EmptyTable();

    internal void Load(IModHelper helper, IMonitor monitor)
    {
        // 表内仍按裸 ItemId 配置；新增装备优先扩 JSON，不要把单件装备写死到这里。
        hatValues = LoadTable(helper, monitor, HatPath);
        shirtValues = LoadTable(helper, monitor, ShirtPath);
        pantsValues = LoadTable(helper, monitor, PantsPath);
        bootsValues = LoadTable(helper, monitor, BootsPath);
        ringValues = LoadTable(helper, monitor, RingPath);
        trinketValues = LoadTable(helper, monitor, TrinketPath);
    }

    internal double Update(Farmer player)
    {
        // 每分钟重新读取当前穿戴状态，不缓存装备对象，避免换装后继续沿用旧效果。
        var hat = player.hat.Value;
        var shirt = player.shirtItem.Value;
        var pants = player.pantsItem.Value;
        var boots = player.boots.Value;
        var leftRing = player.leftRing.Value;
        var rightRing = player.rightRing.Value;

        // 当前 Stardew 1.6 inventory 把 MaximumTrinkets 固定为 1；只读取 slot 0，
        // 不把 NetList 的可扩展形状误写成已支持多个同时生效的 trinket 槽（先例 Sanity Wearing）。
        var trinket = player.trinketItems.Count > 0 ? player.trinketItems[0] : null;

        // 和可为负 = 持续掉血（可致死），由宿主按事件落账结算。
        return HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout(
                hat?.ItemId,
                shirt?.ItemId,
                pants?.ItemId,
                boots?.ItemId,
                leftRing?.ItemId,
                rightRing?.ItemId,
                trinket?.ItemId
            ),
            hatValues,
            shirtValues,
            pantsValues,
            bootsValues,
            ringValues,
            trinketValues
        );
    }

    private static IReadOnlyDictionary<string, double> LoadTable(
        IModHelper helper,
        IMonitor monitor,
        string path
    )
    {
        try
        {
            var raw = helper.ModContent.Load<Dictionary<string, double>>(path);
            if (raw is null)
            {
                monitor.Log(
                    $"Health equipment data unavailable ({path}); using a neutral empty table.",
                    LogLevel.Warn
                );
                return EmptyTable();
            }

            return new Dictionary<string, double>(raw, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Health equipment data failed closed ({path}; {exception.GetType().Name}); using a neutral empty table.",
                LogLevel.Warn
            );
            return EmptyTable();
        }
    }

    private static IReadOnlyDictionary<string, double> EmptyTable()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal);
    }
}
