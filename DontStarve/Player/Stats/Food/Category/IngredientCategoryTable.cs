#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DontStarve.Player.Stats.Food.Category;

/// <summary>
/// ingredient.json 的单条原始数据。序列化面用 .NET 自带 System.Text.Json（主工程与测试工程
/// 都无需额外依赖）；度名是自由键，全部经 <see cref="ExtensionData"/> 捕获后由加载器转进
/// <see cref="Degrees"/>。类本身 internal，公共属性只为序列化绑定，不外泄类型。
/// </summary>
internal sealed class IngredientEntryDto
{
    [JsonPropertyName("foodtype")]
    public string? FoodType { get; set; }

    [JsonPropertyName("secondaryFoodtype")]
    public string? SecondaryFoodtype { get; set; }

    [JsonPropertyName("cookVariants")]
    public List<string>? CookVariants { get; set; }

    [JsonPropertyName("dryVariants")]
    public List<string>? DryVariants { get; set; }

    /// <summary>保留键（foodtype/secondaryFoodtype/cookVariants/dryVariants）以外的全部 JSON 键，即「度」。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    /// <summary>解析后的度表（键=度名，原版 tag 名约定，大小写敏感）。</summary>
    public Dictionary<string, double> Degrees { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>展开后的单条食材分类：度表 + 食物类型。原版里 foodtype（谁吃谁）与度（烹饪贡献）是两套独立系统。</summary>
internal sealed record IngredientCategoryEntry(
    IReadOnlyDictionary<string, double> Degrees,
    string FoodType,
    string? SecondaryFoodtype
);

/// <summary>
/// 镜像原版 cooking.GetIngredientValues（DST cooking.lua:217-231）的产物：
/// Names 对出现过的每个 id 计数（未注册食材也计数但不贡献度——这是原版行为，
/// 菜谱 test 可以用 names 判断「放了什么」）；Tags 是所有已注册食材度值的逐项累加。
/// </summary>
internal sealed record IngredientValues(
    IReadOnlyDictionary<string, int> Names,
    IReadOnlyDictionary<string, double> Tags
);

/// <summary>
/// 食材属性表（Asset/Food/ingredient.json）。建表时一次性展开 cookVariants/dryVariants 派生，
/// 对应原版 AddIngredientValues 的 cancook/candry：变体复制源条目全部度值并追加
/// precook=1 / dried=1（DST cooking.lua:45-72，四版本逐字一致）。运行期只做字典查询，
/// 只读静态内容，无存档、无多人状态（设计稿 §4.2）。
/// </summary>
internal sealed class IngredientCategoryTable
{
    private readonly Dictionary<string, IngredientCategoryEntry> entries;

    private IngredientCategoryTable(Dictionary<string, IngredientCategoryEntry> entries)
    {
        this.entries = entries;
    }

    internal static IngredientCategoryTable Empty()
    {
        return Build(
            new Dictionary<string, IngredientEntryDto>(StringComparer.Ordinal),
            _ => { }
        );
    }

    /// <summary>
    /// 建表规则（确定性展开，警告经 warn 回调上抛，加载器负责落日志）：
    /// 1) 显式条目先入表；2) 再按源条目顺序展开变体派生；3) 变体 id 已有显式条目或已被派生时跳过
    ///    并告警（显式优先）。JSON 里手填条目与变体派生冲突在原版是不存在的输入（原版按注册顺序
    ///    后者覆盖），这里取「显式优先」的确定性规则并在日志可见。
    /// 派生条目继承源条目的 foodtype/secondaryFoodtype（原版烤肉等熟变体的 edible.foodtype 与
    /// 生食材一致）；变体不再二次派生，防止链式展开出歧义。
    /// </summary>
    internal static IngredientCategoryTable Build(
        IReadOnlyDictionary<string, IngredientEntryDto> raw,
        Action<string> warn
    )
    {
        var entries = new Dictionary<string, IngredientCategoryEntry>(StringComparer.Ordinal);
        var derivedIds = new HashSet<string>(StringComparer.Ordinal);
        // 源条目的副类型只校验一次，派生时复用，避免同一源条目重复告警。
        var resolvedSecondaries = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var pair in raw)
        {
            var dto = pair.Value;
            if (dto is null)
            {
                warn($"[Food] ingredient.json 条目 {pair.Key} 为空，已跳过");
                continue;
            }

            WarnUnknownDegrees(pair.Key, dto.Degrees, warn);
            var secondary = ResolveSecondary(pair.Key, dto.FoodType, dto.SecondaryFoodtype, warn);
            resolvedSecondaries[pair.Key] = secondary;
            entries[pair.Key] = new IngredientCategoryEntry(
                CopyDegrees(dto.Degrees),
                dto.FoodType ?? FoodTypes.Generic,
                secondary
            );
        }

        // 变体派生：只从显式条目出发；raw 的枚举顺序即加载器插入顺序（JSON 书写顺序），
        // 同一变体被多个源声明时先到先得并告警。
        foreach (var pair in raw)
        {
            var dto = pair.Value;
            if (dto is null)
                continue;

            DeriveVariants(
                pair.Key,
                dto,
                dto.CookVariants,
                "precook",
                resolvedSecondaries[pair.Key],
                entries,
                derivedIds,
                warn
            );
            DeriveVariants(
                pair.Key,
                dto,
                dto.DryVariants,
                "dried",
                resolvedSecondaries[pair.Key],
                entries,
                derivedIds,
                warn
            );
        }

        return new IngredientCategoryTable(entries);
    }

    internal bool TryGetEntry(string itemId, out IngredientCategoryEntry entry)
    {
        return entries.TryGetValue(itemId, out entry!);
    }

    /// <summary>原版 GetIngredientValues 的逐字语义：每个 id 必计数；度值只从已注册条目累加。</summary>
    internal IngredientValues GetIngredientValues(IEnumerable<string> itemIds)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var tags = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            if (string.IsNullOrEmpty(itemId))
                continue;

            names[itemId] = names.TryGetValue(itemId, out var count) ? count + 1 : 1;
            if (!entries.TryGetValue(itemId, out var entry))
                continue;

            foreach (var pair in entry.Degrees)
                tags[pair.Key] = tags.TryGetValue(pair.Key, out var sum) ? sum + pair.Value : pair.Value;
        }

        return new IngredientValues(names, tags);
    }

    private static void DeriveVariants(
        string sourceId,
        IngredientEntryDto dto,
        List<string>? variantIds,
        string markerDegree,
        string? resolvedSecondary,
        Dictionary<string, IngredientCategoryEntry> entries,
        HashSet<string> derivedIds,
        Action<string> warn
    )
    {
        if (variantIds is null)
            return;

        foreach (var variantId in variantIds)
        {
            if (string.IsNullOrWhiteSpace(variantId))
            {
                warn($"[Food] ingredient.json 条目 {sourceId} 的变体列表包含空 id，已跳过");
                continue;
            }

            if (string.Equals(variantId, sourceId, StringComparison.Ordinal))
            {
                warn($"[Food] ingredient.json 条目 {sourceId} 把自己声明为变体，已跳过");
                continue;
            }

            // 判据顺序：先查派生登记（派生条目也进了 entries，不能只查 entries），
            // 再查显式条目；两条路径都必须可见地告警。
            if (derivedIds.Contains(variantId))
            {
                warn($"[Food] ingredient.json 变体 {variantId} 已被更早的源条目派生，忽略来自 {sourceId} 的重复声明");
                continue;
            }

            if (entries.ContainsKey(variantId))
            {
                warn($"[Food] ingredient.json 变体 {variantId} 已有显式条目，显式优先，忽略来自 {sourceId} 的派生");
                continue;
            }

            derivedIds.Add(variantId);

            var degrees = CopyDegrees(dto.Degrees);
            degrees[markerDegree] = 1;
            entries[variantId] = new IngredientCategoryEntry(
                degrees,
                dto.FoodType ?? FoodTypes.Generic,
                resolvedSecondary
            );
        }
    }

    /// <summary>主副类型同值在原版是 assert 拒绝（DST edible.lua:14）；这里降级为告警并丢弃副类型，不崩游戏。</summary>
    private static string? ResolveSecondary(
        string itemId,
        string? foodType,
        string? secondary,
        Action<string> warn
    )
    {
        if (string.IsNullOrWhiteSpace(secondary))
            return null;

        if (string.Equals(foodType, secondary, StringComparison.Ordinal))
        {
            warn($"[Food] ingredient.json 条目 {itemId} 的 foodtype 与 secondaryFoodtype 同值，副类型已丢弃");
            return null;
        }

        return secondary;
    }

    private static void WarnUnknownDegrees(
        string itemId,
        Dictionary<string, double> degrees,
        Action<string> warn
    )
    {
        foreach (var degree in degrees.Keys)
        {
            if (!KnownIngredientDegrees.Names.Contains(degree))
                warn($"[Food] ingredient.json 条目 {itemId} 使用了不在原版已知列表的度名 {degree}（自创度合法，请确认拼写）");
        }
    }

    private static Dictionary<string, double> CopyDegrees(Dictionary<string, double> degrees)
    {
        return new Dictionary<string, double>(degrees, StringComparer.Ordinal);
    }
}
