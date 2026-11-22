#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DontStarve.Config;

internal static class ConfigSchemaLoader
{
    private const int SupportedSchemaVersion = 1;

    private static readonly HashSet<string> RootProperties = new(
        new[] { "SchemaVersion", "Options" },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> OptionProperties = new(
        new[]
        {
            "Key",
            "Type",
            "Default",
            "AllowedValues",
            "ExposeToContentPatcher",
            "AffectsWorldState",
            "ParentPageI18n",
            "PageI18n",
            "NameI18n",
            "TooltipI18n",
            "ParentPageOrder",
            "PageOrder",
            "Order",
            // Number 通道专用：滑轮边界与步长。其它类型出现这三个属性按契约违例 fail-closed。
            "Min",
            "Max",
            "Interval",
        },
        StringComparer.Ordinal
    );

    internal static ConfigSchemaLoadResult LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return ConfigSchemaLoadResult.Unavailable("schema.missing");

        try
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false, true));
            return Parse(json);
        }
        catch (DecoderFallbackException)
        {
            return ConfigSchemaLoadResult.Unavailable("schema.invalid-json");
        }
        catch (IOException)
        {
            return ConfigSchemaLoadResult.Unavailable("schema.read-failed");
        }
        catch (UnauthorizedAccessException)
        {
            return ConfigSchemaLoadResult.Unavailable("schema.read-failed");
        }
    }

    internal static ConfigSchemaLoadResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
        }
        catch (JsonException)
        {
            return ConfigSchemaLoadResult.Unavailable("schema.invalid-json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !TryCollectProperties(root, RootProperties, out var rootValues)
                || rootValues.Count != RootProperties.Count
                || rootValues["SchemaVersion"].ValueKind != JsonValueKind.Number
                || !rootValues["SchemaVersion"].TryGetInt32(out var schemaVersion)
                || rootValues["Options"].ValueKind != JsonValueKind.Array
                || rootValues["Options"].GetArrayLength() == 0
            )
            {
                return ConfigSchemaLoadResult.Unavailable("schema.invalid-contract");
            }

            if (schemaVersion != SupportedSchemaVersion)
                return ConfigSchemaLoadResult.Unavailable("schema.unsupported-version");

            var elements = new List<JsonElement>();
            foreach (var element in rootValues["Options"].EnumerateArray())
                elements.Add(element);

            var keyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var candidateKeys = new string?[elements.Count];
            for (var index = 0; index < elements.Count; index++)
            {
                candidateKeys[index] = TryReadCandidateKey(elements[index]);
                if (candidateKeys[index] is null)
                    continue;

                keyCounts.TryGetValue(candidateKeys[index]!, out var count);
                keyCounts[candidateKeys[index]!] = count + 1;
            }

            var diagnostics = new List<string>();
            var parsed = new List<ConfigOptionDefinition>();
            for (var index = 0; index < elements.Count; index++)
            {
                var candidateKey = candidateKeys[index];
                if (
                    candidateKey is not null
                    && keyCounts.TryGetValue(candidateKey, out var collisionCount)
                    && collisionCount > 1
                )
                {
                    diagnostics.Add($"schema.duplicate-key:{candidateKey}");
                    continue;
                }

                if (TryParseOption(elements[index], index, out var option, out var reason))
                    parsed.Add(option!);
                else
                    diagnostics.Add(reason);
            }

            RemoveDuplicateOrders(parsed, diagnostics);
            var status = diagnostics.Count == 0
                ? ConfigSchemaStatus.Available
                : ConfigSchemaStatus.Degraded;
            var overallReason = diagnostics.Count == 0
                ? "schema.available"
                : "schema.invalid-options";
            return new ConfigSchemaLoadResult(
                status,
                overallReason,
                new ConfigRegistry(schemaVersion, parsed),
                diagnostics
            );
        }
    }

    private static string? TryReadCandidateKey(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        string? key = null;
        var keyCount = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "Key", StringComparison.Ordinal))
                continue;

            keyCount++;
            if (property.Value.ValueKind == JsonValueKind.String)
                key = property.Value.GetString();
        }

        return keyCount == 1 && !string.IsNullOrWhiteSpace(key) ? key : null;
    }

    private static bool TryParseOption(
        JsonElement element,
        int index,
        out ConfigOptionDefinition? option,
        out string reason
    )
    {
        option = null;
        var candidateKey = TryReadCandidateKey(element);
        var fallbackReason = $"schema.invalid-option:{candidateKey ?? index.ToString()}";
        reason = fallbackReason;
        if (
            element.ValueKind != JsonValueKind.Object
            || !TryCollectProperties(element, OptionProperties, out var values)
            || !TryRequiredString(values, "Key", out var key)
        )
        {
            return false;
        }

        reason = $"schema.invalid-option:{key}";
        if (!ConfigKeys.IsFrozen(key))
            return false;

        if (!TryRequiredString(values, "Type", out var typeText))
            return false;

        ConfigOptionType type;
        if (string.Equals(typeText, "Boolean", StringComparison.Ordinal))
            type = ConfigOptionType.Boolean;
        else if (string.Equals(typeText, "Enum", StringComparison.Ordinal))
            type = ConfigOptionType.Enum;
        else if (string.Equals(typeText, "Keybind", StringComparison.Ordinal))
            type = ConfigOptionType.Keybind;
        else if (string.Equals(typeText, "Number", StringComparison.Ordinal))
            type = ConfigOptionType.Number;
        else
        {
            reason = $"schema.unsupported-type:{key}";
            return false;
        }

        if (
            !values.TryGetValue("Default", out var defaultElement)
            || !TryRequiredBoolean(values, "ExposeToContentPatcher", out var exposeToCp)
            || !TryRequiredBoolean(values, "AffectsWorldState", out var affectsWorldState)
            || !TryRequiredString(values, "ParentPageI18n", out var parentPageI18n)
            || !TryRequiredString(values, "PageI18n", out var pageI18n)
            || !TryRequiredString(values, "NameI18n", out var nameI18n)
            || !TryRequiredString(values, "TooltipI18n", out var tooltipI18n)
            || !TryRequiredInt32(values, "ParentPageOrder", out var parentPageOrder)
            || !TryRequiredInt32(values, "PageOrder", out var pageOrder)
            || !values.TryGetValue("Order", out var orderElement)
            || orderElement.ValueKind != JsonValueKind.Number
            || !orderElement.TryGetInt32(out var order)
        )
        {
            return false;
        }

        ConfigValue defaultValue;
        IReadOnlyList<string> allowedValues;
        if (type == ConfigOptionType.Boolean)
        {
            if (
                values.ContainsKey("AllowedValues")
                || values.ContainsKey("Min")
                || values.ContainsKey("Max")
                || values.ContainsKey("Interval")
                || (
                    defaultElement.ValueKind != JsonValueKind.True
                    && defaultElement.ValueKind != JsonValueKind.False
                )
            )
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            defaultValue = ConfigValue.Boolean(defaultElement.GetBoolean());
            allowedValues = Array.Empty<string>();
        }
        else if (type == ConfigOptionType.Keybind)
        {
            // Keybind 的值域是任意合法按钮名，没有封闭 AllowedValues 表；Default 必须是
            // 合法按钮名字符串（"None" 表示未绑定，是合法默认值）。出现 AllowedValues 或
            // Number 专属的 Min/Max/Interval 按契约违例处理，与 Boolean 一致 fail-closed。
            if (
                values.ContainsKey("AllowedValues")
                || values.ContainsKey("Min")
                || values.ContainsKey("Max")
                || values.ContainsKey("Interval")
                || defaultElement.ValueKind != JsonValueKind.String
            )
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            var keybindDefault = defaultElement.GetString();
            if (!ConfigKeybind.IsValidButtonName(keybindDefault))
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            defaultValue = ConfigValue.Keybind(keybindDefault!);
            allowedValues = Array.Empty<string>();
        }
        else if (type == ConfigOptionType.Number)
        {
            // Number 通道：Default/Min/Max/Interval 全部必填整数，fail-closed 校验
            // interval > 0、min < max、default 落在闭区间内。这三个属性对非 Number
            // 类型按契约违例拒绝（见下方统一检查）。
            if (
                defaultElement.ValueKind != JsonValueKind.Number
                || !defaultElement.TryGetInt32(out var numberDefault)
                || !TryRequiredInt32(values, "Min", out var min)
                || !TryRequiredInt32(values, "Max", out var max)
                || !TryRequiredInt32(values, "Interval", out var interval)
                || interval <= 0
                || min >= max
                || numberDefault < min
                || numberDefault > max
            )
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            defaultValue = ConfigValue.Number(numberDefault);
            allowedValues = Array.Empty<string>();
            option = new ConfigOptionDefinition(
                key,
                type,
                defaultValue,
                allowedValues,
                exposeToCp,
                affectsWorldState,
                parentPageI18n,
                pageI18n,
                nameI18n,
                tooltipI18n,
                parentPageOrder,
                pageOrder,
                order,
                min,
                max,
                interval
            );
            return true;
        }
        else
        {
            // Min/Max/Interval 是 Number 专属；Enum 出现这三个属性按契约违例 fail-closed，
            // 防止 schema 作者误配出静默无效字段（AllowedValues 是 Enum 的合法属性）。
            if (
                values.ContainsKey("Min")
                || values.ContainsKey("Max")
                || values.ContainsKey("Interval")
            )
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            if (!TryReadAllowedValues(values, out allowedValues))
                return false;

            if (defaultElement.ValueKind != JsonValueKind.String)
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            var defaultText = defaultElement.GetString();
            if (
                string.IsNullOrWhiteSpace(defaultText)
                || !ContainsOrdinal(allowedValues, defaultText)
            )
            {
                reason = $"schema.invalid-default:{key}";
                return false;
            }

            defaultValue = ConfigValue.Enum(defaultText);
        }

        option = new ConfigOptionDefinition(
            key,
            type,
            defaultValue,
            allowedValues,
            exposeToCp,
            affectsWorldState,
            parentPageI18n,
            pageI18n,
            nameI18n,
            tooltipI18n,
            parentPageOrder,
            pageOrder,
            order
        );
        return true;
    }

    private static bool TryReadAllowedValues(
        IReadOnlyDictionary<string, JsonElement> values,
        out IReadOnlyList<string> allowedValues
    )
    {
        allowedValues = Array.Empty<string>();
        if (
            !values.TryGetValue("AllowedValues", out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() == 0
        )
        {
            return false;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                return false;

            result.Add(value);
        }

        allowedValues = result.AsReadOnly();
        return true;
    }

    private static bool TryCollectProperties(
        JsonElement element,
        ISet<string> allowedNames,
        out Dictionary<string, JsonElement> values
    )
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                return false;
        }

        return true;
    }

    private static bool TryRequiredString(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out string value
    )
    {
        value = string.Empty;
        if (
            !values.TryGetValue(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryRequiredBoolean(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out bool value
    )
    {
        value = false;
        if (
            !values.TryGetValue(propertyName, out var element)
            || (
                element.ValueKind != JsonValueKind.True
                && element.ValueKind != JsonValueKind.False
            )
        )
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryRequiredInt32(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out int value
    )
    {
        value = 0;
        if (
            !values.TryGetValue(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out value)
        )
        {
            return false;
        }

        return true;
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string expected)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], expected, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void RemoveDuplicateOrders(
        List<ConfigOptionDefinition> options,
        ICollection<string> diagnostics
    )
    {
        var orderCounts = new Dictionary<int, int>();
        foreach (var option in options)
        {
            orderCounts.TryGetValue(option.Order, out var count);
            orderCounts[option.Order] = count + 1;
        }

        for (var index = options.Count - 1; index >= 0; index--)
        {
            var option = options[index];
            if (orderCounts[option.Order] <= 1)
                continue;

            diagnostics.Add($"schema.duplicate-order:{option.Order}");
            options.RemoveAt(index);
        }
    }
}
