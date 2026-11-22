#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Food.Category;

/// <summary>
/// 食物分类层加载器（20261113 设计稿阶段1）。职责只有三件：读两份 JSON、解析成纯 POCO、
/// 建表并落日志。所有校验/派生逻辑都在纯逻辑类里（可测试），本文件只做 SMAPI 适配。
/// 读盘走 mod 目录直读 + System.Text.Json：SMAPI IDataHelper 没有原文读取接口（无
/// ReadTextFile），ModContent.Load 又绑定 Newtonsoft（主工程不引用），这样零新依赖；
/// 文件随 mod 文件夹分发，路径读取与 ModContent.Load 等价。失败按 AGENTS.md 规则记
/// Error 日志并退回空表，不崩游戏、不覆盖任何玩家数据。
/// 表是只读静态内容：StatManager.Initialize 时加载一次，运行期零读取成本。
/// </summary>
internal static class CategoryTableLoader
{
    internal const string IngredientTablePath = "Asset/Food/ingredient.json";
    internal const string FoodTablePath = "Asset/Food/food.json";

    /// <summary>
    /// 手填容错对齐 Newtonsoft 的宽容度：键大小写不敏感（保留键）、容忍注释与尾逗号、
    /// 数字可写成字符串。度键本身不做大小写归一（原版 tag 名大小写敏感），仅在纯逻辑层告警。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    internal static IngredientCategoryTable IngredientTable { get; private set; } =
        IngredientCategoryTable.Empty();

    internal static FoodCategoryTable FoodTable { get; private set; } = FoodCategoryTable.Empty();

    internal static void Load(IModHelper helper, IMonitor monitor)
    {
        IngredientTable = LoadIngredientTable(helper, monitor);
        FoodTable = LoadFoodTable(helper, monitor);
    }

    private static IngredientCategoryTable LoadIngredientTable(IModHelper helper, IMonitor monitor)
    {
        var warn = CreateWarner(monitor);
        try
        {
            var text = ReadTableFile(helper, IngredientTablePath);
            var document = JsonSerializer.Deserialize<Dictionary<string, IngredientEntryDto>>(
                text,
                JsonOptions
            );
            var raw = new Dictionary<string, IngredientEntryDto>(StringComparer.Ordinal);
            foreach (var pair in document ?? new Dictionary<string, IngredientEntryDto>())
            {
                var dto = pair.Value;
                if (dto is null)
                {
                    warn($"[Food] ingredient.json 条目 {pair.Key} 为空，已跳过");
                    continue;
                }

                foreach (var extension in dto.ExtensionData ?? new Dictionary<string, JsonElement>())
                {
                    if (TryParseDegree(extension.Key, extension.Value, pair.Key, warn, out var degree))
                        dto.Degrees[extension.Key] = degree;
                }

                raw[pair.Key] = dto;
            }

            return IngredientCategoryTable.Build(raw, warn);
        }
        catch (Exception ex)
        {
            // 坏 JSON / 读盘失败：按空表兜底，但必须留可定位的日志（禁止静默吞掉）。
            monitor.Log(
                $"{IngredientTablePath} 读取或解析失败，食物分类表按空表处理：{ex.GetType().Name}: {ex.Message}",
                LogLevel.Error
            );
            return IngredientCategoryTable.Empty();
        }
    }

    private static FoodCategoryTable LoadFoodTable(IModHelper helper, IMonitor monitor)
    {
        var warn = CreateWarner(monitor);
        try
        {
            var text = ReadTableFile(helper, FoodTablePath);
            var document = JsonSerializer.Deserialize<Dictionary<string, FoodEntryDto>>(
                text,
                JsonOptions
            );
            var raw = new Dictionary<string, FoodEntryDto>(StringComparer.Ordinal);
            foreach (var pair in document ?? new Dictionary<string, FoodEntryDto>())
            {
                if (pair.Value is null)
                {
                    warn($"[Food] food.json 条目 {pair.Key} 为空，已跳过");
                    continue;
                }

                raw[pair.Key] = pair.Value;
            }

            return FoodCategoryTable.Build(raw, warn);
        }
        catch (Exception ex)
        {
            monitor.Log(
                $"{FoodTablePath} 读取或解析失败，成品食物分类表按空表处理：{ex.GetType().Name}: {ex.Message}",
                LogLevel.Error
            );
            return FoodCategoryTable.Empty();
        }
    }

    private static string ReadTableFile(IModHelper helper, string relativePath)
    {
        var fullPath = Path.Combine(helper.DirectoryPath, relativePath);
        return File.ReadAllText(fullPath);
    }

    /// <summary>度值解析：数字直接取；字符串数字按不变文化解析（手填容错）；其余告警丢弃。</summary>
    private static bool TryParseDegree(
        string degreeKey,
        JsonElement element,
        string itemId,
        Action<string> warn,
        out double degree
    )
    {
        degree = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                degree = element.GetDouble();
                return true;
            case JsonValueKind.String:
                var text = element.GetString();
                if (text is not null
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out degree))
                {
                    return true;
                }

                warn($"[Food] ingredient.json 条目 {itemId} 的度 {degreeKey} 不是数字，已忽略");
                return false;
            default:
                warn($"[Food] ingredient.json 条目 {itemId} 的度 {degreeKey} 不是数字，已忽略");
                return false;
        }
    }

    private static Action<string> CreateWarner(IMonitor monitor)
    {
        return message => monitor.Log(message, LogLevel.Debug);
    }
}
