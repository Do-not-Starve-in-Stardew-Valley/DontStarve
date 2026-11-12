using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

public sealed class HealthEquipmentRulesTests
{
    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> Table(params (string Id, double Value)[] entries)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, value) in entries)
            values[id] = value;

        return values;
    }

    [Fact]
    public void Sums_all_six_slots_from_their_own_tables()
    {
        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout("10", "353", "7", "2", "516", null, null),
            Table(("10", 0.5)),
            Table(("353", 1.5)),
            Table(("7", -0.25)),
            Table(("2", 2)),
            Table(("516", 0.75)),
            Empty
        );

        Assert.Equal(4.5d, total, precision: 12);
    }

    [Fact]
    public void Both_rings_read_the_same_ring_table()
    {
        var ring = Table(("516", 2d));

        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout(null, null, null, null, "516", "516", null),
            Empty,
            Empty,
            Empty,
            Empty,
            ring,
            Empty
        );

        Assert.Equal(4d, total, precision: 12);
    }

    [Fact]
    public void Missing_or_blank_ids_and_missing_records_count_zero()
    {
        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout("10", "", " ", "10", "999", null, "888"),
            Table(("10", 1d)),
            Empty,
            Empty,
            Table(("10", 1d)),
            Empty,
            Empty
        );

        // 空串/空白 id 归 0；"10" 只在 hat/boots 表存在；999/888 无记录 → 只有 1 + 1。
        Assert.Equal(2d, total, precision: 12);
    }

    [Fact]
    public void Non_finite_table_values_are_treated_as_zero()
    {
        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout("10", "20", null, null, null, null, null),
            Table(("10", double.NaN)),
            Table(("20", double.PositiveInfinity)),
            Empty,
            Empty,
            Empty,
            Empty
        );

        Assert.Equal(0d, total, precision: 12);
    }

    [Fact]
    public void Non_finite_total_fails_neutral_to_zero()
    {
        // 两个合法大数相加溢出为 Infinity → 归 0，不让脏数值写进血量结算。
        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout("10", "20", null, null, null, null, null),
            Table(("10", 1e308)),
            Table(("20", 1e308)),
            Empty,
            Empty,
            Empty,
            Empty
        );

        Assert.Equal(0d, total, precision: 12);
    }

    [Fact]
    public void Empty_tables_yield_zero()
    {
        // 初始交付空表：空表 = 装备层面完全不介入。
        var total = HealthEquipmentRules.CalculateEquipmentDelta(
            new HealthEquipmentLoadout("10", "353", "7", "2", "516", "516", "1"),
            Empty,
            Empty,
            Empty,
            Empty,
            Empty,
            Empty
        );

        Assert.Equal(0d, total, precision: 12);
    }
}
