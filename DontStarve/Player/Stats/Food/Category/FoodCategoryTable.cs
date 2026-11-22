#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DontStarve.Player.Stats.Food.Category;

/// <summary>food.json 的单条原始数据。System.Text.Json 绑定面；类 internal，公共属性只为序列化绑定。</summary>
internal sealed class FoodEntryDto
{
    [JsonPropertyName("foodtype")]
    public string? FoodType { get; set; }

    [JsonPropertyName("secondaryFoodtype")]
    public string? SecondaryFoodtype { get; set; }

    [JsonPropertyName("perishDays")]
    public double? PerishDays { get; set; }
}

/// <summary>
/// 展开后的单条成品食物分类。对应原版 preparedfoods 条目的分类字段（非数值——数值继续走
/// Hunger/Sanity/Health 三张老表 + FoodRuleResolver，两层互不覆盖，设计稿 §4.4）。
/// PerishDays 单位是「天」，对应原版 perishtime/480秒；null = 不腐坏（原版 perishtime=nil，
/// 例：DST jellybean）。
/// </summary>
internal sealed record FoodCategoryEntry(
    string FoodType,
    string? SecondaryFoodtype,
    double? PerishDays
);

/// <summary>成品食物表（Asset/Food/food.json）。只读静态内容：加载一次，查询 O(1)。</summary>
internal sealed class FoodCategoryTable
{
    private readonly Dictionary<string, FoodCategoryEntry> entries;

    private FoodCategoryTable(Dictionary<string, FoodCategoryEntry> entries)
    {
        this.entries = entries;
    }

    internal static FoodCategoryTable Empty()
    {
        return Build(
            new Dictionary<string, FoodEntryDto>(StringComparer.Ordinal),
            _ => { }
        );
    }

    /// <summary>
    /// 建表规则：foodtype 缺省 GENERIC（原版 preparedfoods prefab 生成器
    /// `data.foodtype or FOODTYPE.GENERIC`）；主副类型同值告警并丢弃副类型（原版 assert，
    /// 降级不崩）；perishDays ≤ 0 归一为不腐坏（原版 `data.perishtime ~= nil and perishtime > 0`
    /// 才挂腐坏组件，prefabs/preparedfoods.lua:142-147），负数额外告警防笔误。
    /// </summary>
    internal static FoodCategoryTable Build(
        IReadOnlyDictionary<string, FoodEntryDto> raw,
        Action<string> warn
    )
    {
        var entries = new Dictionary<string, FoodCategoryEntry>(StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            var dto = pair.Value;
            if (dto is null)
            {
                warn($"[Food] food.json 条目 {pair.Key} 为空，已跳过");
                continue;
            }

            var foodType = dto.FoodType ?? FoodTypes.Generic;
            string? secondary = null;
            if (!string.IsNullOrWhiteSpace(dto.SecondaryFoodtype))
            {
                if (string.Equals(foodType, dto.SecondaryFoodtype, StringComparison.Ordinal))
                    warn($"[Food] food.json 条目 {pair.Key} 的 foodtype 与 secondaryFoodtype 同值，副类型已丢弃");
                else
                    secondary = dto.SecondaryFoodtype;
            }

            double? perishDays = null;
            if (dto.PerishDays.HasValue)
            {
                if (dto.PerishDays.Value < 0)
                    warn($"[Food] food.json 条目 {pair.Key} 的 perishDays 为负数，按不腐坏处理（请核对数值）");
                else if (dto.PerishDays.Value > 0)
                    perishDays = dto.PerishDays.Value;
            }

            entries[pair.Key] = new FoodCategoryEntry(foodType, secondary, perishDays);
        }

        return new FoodCategoryTable(entries);
    }

    internal bool TryGetEntry(string itemId, out FoodCategoryEntry entry)
    {
        return entries.TryGetValue(itemId, out entry!);
    }
}
