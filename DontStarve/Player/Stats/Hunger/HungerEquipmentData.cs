#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace DontStarve.Player.Stats.Hunger;

internal enum HungerEquipmentDataStatus
{
    Available,
    Degraded,
    Unavailable,
}

internal sealed class HungerEquipmentDataLoadResult
{
    internal HungerEquipmentDataLoadResult(
        HungerEquipmentDataStatus status,
        string reason,
        IReadOnlyDictionary<string, HungerEquipmentEffect> values,
        IReadOnlyCollection<string> diagnostics
    )
    {
        Status = status;
        Reason = reason;
        Values = new ReadOnlyDictionary<string, HungerEquipmentEffect>(
            new Dictionary<string, HungerEquipmentEffect>(values, StringComparer.Ordinal)
        );
        Diagnostics = new ReadOnlyCollection<string>(new List<string>(diagnostics));
    }

    internal HungerEquipmentDataStatus Status { get; }
    internal string Reason { get; }
    internal IReadOnlyDictionary<string, HungerEquipmentEffect> Values { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// Strict, SMAPI-free parser for Hunger equipment data. Missing fields mean neutral effects;
/// malformed records are diagnosed and ignored so one bad item cannot corrupt the whole table.
/// </summary>
internal static class HungerEquipmentDataLoader
{
    private static readonly HashSet<string> EntryProperties = new(
        new[] { "ConsumptionMultiplier", "FixedRecovery" },
        StringComparer.Ordinal
    );

    internal static HungerEquipmentDataLoadResult Parse(string json)
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
        catch (ArgumentException)
        {
            return Unavailable("equipment.invalid-json", "equipment.invalid-json");
        }
        catch (JsonException)
        {
            return Unavailable("equipment.invalid-json", "equipment.invalid-json");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unavailable("equipment.invalid-root", "equipment.invalid-root");

            var values = new Dictionary<string, HungerEquipmentEffect>(
                StringComparer.Ordinal
            );
            var diagnostics = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (
                    string.IsNullOrWhiteSpace(property.Name)
                    || !TryParseEntry(property.Value, out var effect)
                )
                {
                    diagnostics.Add($"equipment.invalid-entry:{property.Name}");
                    continue;
                }

                values[property.Name] = effect;
            }

            return new HungerEquipmentDataLoadResult(
                diagnostics.Count == 0
                    ? HungerEquipmentDataStatus.Available
                    : HungerEquipmentDataStatus.Degraded,
                diagnostics.Count == 0 ? "equipment.available" : "equipment.invalid-entries",
                values,
                diagnostics
            );
        }
    }

    private static bool TryParseEntry(
        JsonElement element,
        out HungerEquipmentEffect effect
    )
    {
        effect = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        double? multiplier = null;
        double? recovery = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!EntryProperties.Contains(property.Name) || !seen.Add(property.Name))
                return false;

            if (
                property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out var value)
                || !double.IsFinite(value)
                || (
                    string.Equals(
                        property.Name,
                        "ConsumptionMultiplier",
                        StringComparison.Ordinal
                    )
                    ? value <= 0d
                    : value < 0d
                )
            )
            {
                return false;
            }

            if (string.Equals(property.Name, "ConsumptionMultiplier", StringComparison.Ordinal))
                multiplier = value;
            else
                recovery = value;
        }

        effect = new HungerEquipmentEffect(multiplier, recovery);
        return true;
    }

    private static HungerEquipmentDataLoadResult Unavailable(
        string reason,
        string diagnostic
    )
    {
        return new HungerEquipmentDataLoadResult(
            HungerEquipmentDataStatus.Unavailable,
            reason,
            new Dictionary<string, HungerEquipmentEffect>(StringComparer.Ordinal),
            new[] { diagnostic }
        );
    }
}
