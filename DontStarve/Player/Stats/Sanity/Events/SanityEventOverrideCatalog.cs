#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity.Events;

internal readonly record struct SanityEventOverrideKey(
    string LocationName,
    string EventId
);

internal readonly record struct SanityEventOverrideLoadResult(
    bool IsAvailable,
    string Reason,
    SanityEventOverrideCatalog Catalog
);

/// <summary>
/// Immutable, load-once event-ID exceptions. A malformed or unsupported document disables
/// the entire override catalog so partial data can never broaden the gate unexpectedly.
/// </summary>
internal sealed class SanityEventOverrideCatalog
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumEntries = 256;
    internal const string AnyLocation = "*";

    private readonly IReadOnlyDictionary<
        SanityEventOverrideKey,
        SanityEventClassification
    > entries;

    private SanityEventOverrideCatalog(
        IReadOnlyDictionary<SanityEventOverrideKey, SanityEventClassification> entries
    )
    {
        this.entries = entries;
    }

    internal int Count => entries.Count;

    internal static SanityEventOverrideCatalog Empty { get; } =
        new SanityEventOverrideCatalog(
            new Dictionary<SanityEventOverrideKey, SanityEventClassification>()
        );

    internal bool TryResolve(
        string locationName,
        string eventId,
        out SanityEventClassification classification
    )
    {
        if (
            entries.TryGetValue(
                new SanityEventOverrideKey(locationName, eventId),
                out classification
            )
        )
        {
            return true;
        }

        return entries.TryGetValue(
            new SanityEventOverrideKey(AnyLocation, eventId),
            out classification
        );
    }

    internal static SanityEventOverrideLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Unavailable("event-override-json-is-empty");

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unavailable("event-override-root-must-be-an-object");
            if (
                !root.TryGetProperty("SchemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion
            )
            {
                return Unavailable("event-override-schema-version-is-unsupported");
            }
            if (
                !root.TryGetProperty("Overrides", out var overridesElement)
                || overridesElement.ValueKind != JsonValueKind.Array
            )
            {
                return Unavailable("event-override-array-is-missing");
            }
            if (overridesElement.GetArrayLength() > MaximumEntries)
                return Unavailable("event-override-entry-cap-exceeded");

            var parsed = new Dictionary<
                SanityEventOverrideKey,
                SanityEventClassification
            >();
            foreach (var entry in overridesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    return Unavailable("event-override-entry-must-be-an-object");
                if (
                    !TryReadBoundedString(entry, "LocationName", out var locationName)
                    || !TryReadBoundedString(entry, "EventId", out var eventId)
                    || !TryReadBoundedString(
                        entry,
                        "Classification",
                        out var classificationName
                    )
                )
                {
                    return Unavailable("event-override-entry-field-is-invalid");
                }
                if (
                    !Enum.TryParse<SanityEventClassification>(
                        classificationName,
                        ignoreCase: true,
                        out var classification
                    )
                    || classification
                        is not SanityEventClassification.Friendship
                            and not SanityEventClassification.NonFriendship
                )
                {
                    return Unavailable("event-override-classification-is-invalid");
                }

                var key = new SanityEventOverrideKey(locationName, eventId);
                if (!parsed.TryAdd(key, classification))
                    return Unavailable("event-override-key-is-duplicated");
            }

            return new SanityEventOverrideLoadResult(
                true,
                "event-override-catalog-loaded",
                new SanityEventOverrideCatalog(parsed)
            );
        }
        catch (JsonException)
        {
            return Unavailable("event-override-json-is-malformed");
        }
    }

    private static bool TryReadBoundedString(
        JsonElement entry,
        string propertyName,
        out string value
    )
    {
        value = string.Empty;
        if (
            !entry.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 128;
    }

    private static SanityEventOverrideLoadResult Unavailable(string reason)
    {
        return new SanityEventOverrideLoadResult(false, reason, Empty);
    }
}
