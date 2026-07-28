using System;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// 只分类 legacy 单对象存档形状，不执行迁移或裁剪。
/// Classifies the legacy single-object save shape without applying migration or clamping.
/// </summary>
internal static class LegacySanityDataInspector
{
    internal const string SanityPropertyName = "Sanity";

    internal static LegacySanityInspection Inspect(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result(
                    LegacySanityInspectionStatus.InvalidRoot,
                    "legacy-root-must-be-an-object"
                );
            }

            if (!root.TryGetProperty(SanityPropertyName, out var sanityElement))
            {
                return Result(
                    LegacySanityInspectionStatus.MissingSanityKey,
                    "legacy-sanity-key-is-missing"
                );
            }

            if (sanityElement.ValueKind == JsonValueKind.Null)
            {
                return Result(
                    LegacySanityInspectionStatus.NullSanityValue,
                    "legacy-sanity-value-is-null"
                );
            }

            // Newtonsoft 存档可能把特殊浮点值保留为字符串；这里明确拒绝这些 marker，
            // 避免后续迁移把 non-finite value 当成可用数值。
            if (
                sanityElement.ValueKind == JsonValueKind.String
                && IsNamedNonFiniteValue(sanityElement.GetString() ?? string.Empty)
            )
            {
                return Result(
                    LegacySanityInspectionStatus.NonFiniteSanityValue,
                    "legacy-sanity-value-is-non-finite"
                );
            }

            if (sanityElement.ValueKind != JsonValueKind.Number)
            {
                return Result(
                    LegacySanityInspectionStatus.InvalidSanityType,
                    "legacy-sanity-value-must-be-a-number"
                );
            }

            double value;
            try
            {
                value = sanityElement.GetDouble();
            }
            catch (FormatException)
            {
                return Result(
                    LegacySanityInspectionStatus.NonFiniteSanityValue,
                    "legacy-sanity-value-is-non-finite"
                );
            }

            if (!double.IsFinite(value))
            {
                return Result(
                    LegacySanityInspectionStatus.NonFiniteSanityValue,
                    "legacy-sanity-value-is-non-finite"
                );
            }

            return new LegacySanityInspection(
                LegacySanityInspectionStatus.FiniteSanityValue,
                value,
                "legacy-sanity-value-is-finite"
            );
        }
        catch (JsonException)
        {
            return Result(
                LegacySanityInspectionStatus.MalformedJson,
                "legacy-json-is-malformed"
            );
        }
        catch (ArgumentException)
        {
            return Result(
                LegacySanityInspectionStatus.MalformedJson,
                "legacy-json-is-malformed"
            );
        }
    }

    private static bool IsNamedNonFiniteValue(string value)
    {
        return value is "NaN" or "Infinity" or "-Infinity";
    }

    private static LegacySanityInspection Result(
        LegacySanityInspectionStatus status,
        string reason
    )
    {
        return new LegacySanityInspection(status, null, reason);
    }
}

internal enum LegacySanityInspectionStatus
{
    FiniteSanityValue,
    MissingSanityKey,
    NullSanityValue,
    InvalidSanityType,
    NonFiniteSanityValue,
    InvalidRoot,
    MalformedJson,
}

internal readonly record struct LegacySanityInspection(
    LegacySanityInspectionStatus Status,
    double? Value,
    string Reason
);
