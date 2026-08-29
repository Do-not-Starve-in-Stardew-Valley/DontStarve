#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

internal static class SanityAssetManifestParser
{
    internal const int SupportedSchemaVersion = 1;

    private static readonly HashSet<string> RootProperties = new(
        new[] { "SchemaVersion", "Slots" },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> SlotProperties = new(
        new[]
        {
            "SlotId",
            "Path",
            "Kind",
            "Sha256",
            "IsPlaceholder",
            "ContractVersion",
            "RequiredForRelease",
            "CreditGroup",
        },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> RequiredSlotProperties = new(
        new[]
        {
            "SlotId",
            "Path",
            "Kind",
            "IsPlaceholder",
            "ContractVersion",
            "RequiredForRelease",
            "CreditGroup",
        },
        StringComparer.Ordinal
    );

    internal static SanityAssetManifestParseResult Parse(string json)
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
            return SanityAssetManifestParseResult.Unavailable(
                "manifest.invalid-json",
                "The Sanity asset manifest is not valid strict JSON."
            );
        }

        using (document)
        {
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !TryCollectExactProperties(root, RootProperties, out var rootValues)
                || !TryReadInt32(rootValues, "SchemaVersion", out var schemaVersion)
                || !rootValues.TryGetValue("Slots", out var slotsElement)
                || slotsElement.ValueKind != JsonValueKind.Array
                || slotsElement.GetArrayLength() == 0
            )
            {
                return SanityAssetManifestParseResult.Unavailable(
                    "manifest.invalid-contract",
                    "The Sanity asset manifest root must contain only SchemaVersion and a non-empty Slots array."
                );
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                return SanityAssetManifestParseResult.Unavailable(
                    "manifest.unsupported-version",
                    $"Manifest schema version {schemaVersion} is unsupported."
                );
            }

            var slots = new List<SanityAssetSlot>();
            var index = 0;
            foreach (var element in slotsElement.EnumerateArray())
            {
                if (!TryParseSlot(element, out var slot))
                {
                    return SanityAssetManifestParseResult.Unavailable(
                        "manifest.invalid-slot",
                        $"Manifest slot at index {index} does not match the frozen slot schema."
                    );
                }

                slots.Add(slot!);
                index++;
            }

            return SanityAssetManifestParseResult.Available(
                new SanityAssetManifest(schemaVersion, slots)
            );
        }
    }

    private static bool TryParseSlot(JsonElement element, out SanityAssetSlot? slot)
    {
        slot = null;
        if (
            element.ValueKind != JsonValueKind.Object
            || !TryCollectSlotProperties(element, out var values)
            || !TryReadString(values, "SlotId", out var slotId)
            || !TryReadString(values, "Path", out var path)
            || !TryReadString(values, "Kind", out var kindText)
            || !TryReadNullableString(values, "Sha256", out var sha256)
            || !TryReadBoolean(values, "IsPlaceholder", out var isPlaceholder)
            || !TryReadInt32(values, "ContractVersion", out var contractVersion)
            || !TryReadBoolean(values, "RequiredForRelease", out var requiredForRelease)
            || !TryReadString(values, "CreditGroup", out var creditGroup)
            || !TryParseKind(kindText, out var kind)
        )
        {
            return false;
        }

        slot = new SanityAssetSlot(
            slotId,
            path,
            kind,
            sha256,
            isPlaceholder,
            contractVersion,
            requiredForRelease,
            creditGroup
        );
        return true;
    }

    private static bool TryCollectSlotProperties(
        JsonElement element,
        out Dictionary<string, JsonElement> values
    )
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (
                !SlotProperties.Contains(property.Name)
                || !values.TryAdd(property.Name, property.Value)
            )
            {
                return false;
            }
        }

        foreach (var requiredProperty in RequiredSlotProperties)
        {
            if (!values.ContainsKey(requiredProperty))
                return false;
        }

        return true;
    }

    private static bool TryParseKind(string text, out SanityAssetKind kind)
    {
        if (string.Equals(text, "Png", StringComparison.Ordinal))
        {
            kind = SanityAssetKind.Png;
            return true;
        }

        if (string.Equals(text, "Json", StringComparison.Ordinal))
        {
            kind = SanityAssetKind.Json;
            return true;
        }

        if (string.Equals(text, "Wav", StringComparison.Ordinal))
        {
            kind = SanityAssetKind.Wav;
            return true;
        }

        kind = default;
        return false;
    }

    internal static bool TryCollectExactProperties(
        JsonElement element,
        ISet<string> expectedNames,
        out Dictionary<string, JsonElement> values
    )
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (
                !expectedNames.Contains(property.Name)
                || !values.TryAdd(property.Name, property.Value)
            )
            {
                return false;
            }
        }

        return values.Count == expectedNames.Count;
    }

    internal static bool TryReadString(
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
        return true;
    }

    private static bool TryReadNullableString(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out string? value
    )
    {
        value = null;
        if (!values.TryGetValue(propertyName, out var element))
            return true;

        if (element.ValueKind == JsonValueKind.Null)
            return true;

        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString();
        return true;
    }

    internal static bool TryReadBoolean(
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

    internal static bool TryReadInt32(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out int value
    )
    {
        value = 0;
        return values.TryGetValue(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}

internal static class SanityCreditCatalogParser
{
    internal const int SupportedSchemaVersion = 1;

    private static readonly HashSet<string> RootProperties = new(
        new[] { "SchemaVersion", "Groups" },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> GroupProperties = new(
        new[]
        {
            "CreditGroup",
            "DisplayName",
            "AttributionText",
            "SourceEvidenceId",
            "PermissionScope",
            "IsPlaceholder",
        },
        StringComparer.Ordinal
    );

    internal static SanityCreditCatalogParseResult Parse(string json)
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
            return SanityCreditCatalogParseResult.Unavailable(
                "credits.invalid-json",
                "The Sanity credit catalog is not valid strict JSON."
            );
        }

        using (document)
        {
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !SanityAssetManifestParser.TryCollectExactProperties(
                    root,
                    RootProperties,
                    out var rootValues
                )
                || !SanityAssetManifestParser.TryReadInt32(
                    rootValues,
                    "SchemaVersion",
                    out var schemaVersion
                )
                || !rootValues.TryGetValue("Groups", out var groupsElement)
                || groupsElement.ValueKind != JsonValueKind.Array
                || groupsElement.GetArrayLength() == 0
            )
            {
                return SanityCreditCatalogParseResult.Unavailable(
                    "credits.invalid-contract",
                    "The credit catalog root must contain only SchemaVersion and a non-empty Groups array."
                );
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                return SanityCreditCatalogParseResult.Unavailable(
                    "credits.unsupported-version",
                    $"Credit schema version {schemaVersion} is unsupported."
                );
            }

            var groups = new List<SanityCreditGroup>();
            var index = 0;
            foreach (var element in groupsElement.EnumerateArray())
            {
                if (!TryParseGroup(element, out var group))
                {
                    return SanityCreditCatalogParseResult.Unavailable(
                        "credits.invalid-group",
                        $"Credit group at index {index} does not match the frozen credit schema."
                    );
                }

                groups.Add(group!);
                index++;
            }

            return SanityCreditCatalogParseResult.Available(
                new SanityCreditCatalog(schemaVersion, groups)
            );
        }
    }

    private static bool TryParseGroup(JsonElement element, out SanityCreditGroup? group)
    {
        group = null;
        if (
            element.ValueKind != JsonValueKind.Object
            || !SanityAssetManifestParser.TryCollectExactProperties(
                element,
                GroupProperties,
                out var values
            )
            || !SanityAssetManifestParser.TryReadString(values, "CreditGroup", out var creditGroup)
            || !SanityAssetManifestParser.TryReadString(values, "DisplayName", out var displayName)
            || !SanityAssetManifestParser.TryReadString(
                values,
                "AttributionText",
                out var attributionText
            )
            || !SanityAssetManifestParser.TryReadString(
                values,
                "SourceEvidenceId",
                out var sourceEvidenceId
            )
            || !SanityAssetManifestParser.TryReadString(
                values,
                "PermissionScope",
                out var permissionScope
            )
            || !SanityAssetManifestParser.TryReadBoolean(
                values,
                "IsPlaceholder",
                out var isPlaceholder
            )
        )
        {
            return false;
        }

        group = new SanityCreditGroup(
            creditGroup,
            displayName,
            attributionText,
            sourceEvidenceId,
            permissionScope,
            isPlaceholder
        );
        return true;
    }
}
