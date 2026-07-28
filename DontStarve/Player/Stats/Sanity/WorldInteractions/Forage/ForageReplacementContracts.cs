#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;

internal static class ForagePickupContextIds
{
    internal const string NormalDirectObjectPickup =
        "stardew.game-location-check-action.normal-direct-object-pickup.v2";
}

internal static class ForageReplacementReasonIds
{
    internal const string CatalogLoaded = "forage.replacements-loaded";
    internal const string CatalogJsonEmpty = "forage.replacements-json-empty";
    internal const string CatalogJsonMalformed = "forage.replacements-json-malformed";
    internal const string CatalogRootInvalid = "forage.replacements-root-invalid";
    internal const string CatalogVersionUnsupported = "forage.replacements-version-unsupported";
    internal const string MappingArrayInvalid = "forage.replacements-array-invalid";
    internal const string MappingInvalid = "forage.replacement-invalid";
    internal const string MappingIdDuplicated = "forage.replacement-id-duplicated";
    internal const string MappingSourceDuplicated = "forage.replacement-source-duplicated";
    internal const string MappingNotAllowlisted = "forage.replacement-not-allowlisted";
    internal const string MappingDisabled = "forage.replacement-disabled";
    internal const string PickerInvalid = "forage.pickup-picker-invalid";
    internal const string HostAuthorityRequired = "forage.pickup-host-authority-required";
    internal const string LocationOrTileInvalid = "forage.pickup-location-or-tile-invalid";
    internal const string ObjectIdentityMismatch = "forage.pickup-object-identity-mismatch";
    internal const string ObjectFingerprintUnavailable =
        "forage.pickup-object-fingerprint-unavailable";
    internal const string DirectPickupBranchRequired =
        "forage.pickup-direct-object-branch-required";
    internal const string SpawnedOrErrorObjectRequired =
        "forage.pickup-spawned-or-error-object-required";
    internal const string LocationNotAllowlisted = "forage.pickup-location-not-allowlisted";
    internal const string ContextNotAllowlisted = "forage.pickup-context-not-allowlisted";
    internal const string TargetUnavailable = "forage.pickup-target-unavailable";
    internal const string Eligible = "forage.pickup-eligible";
}

internal readonly record struct ForageReplacementCatalogLoadResult(
    bool IsAvailable,
    string Reason,
    ForageReplacementCatalog Catalog
);

internal sealed class ForageReplacementMapping
{
    internal ForageReplacementMapping(
        string id,
        string sourceQualifiedItemId,
        string? targetQualifiedItemId,
        bool enabled,
        IReadOnlyList<string> locationAllowlist,
        IReadOnlyList<string> contextAllowlist,
        IReadOnlyList<string> evidence,
        string reason
    )
    {
        Id = id;
        SourceQualifiedItemId = sourceQualifiedItemId;
        TargetQualifiedItemId = targetQualifiedItemId;
        Enabled = enabled;
        LocationAllowlist = locationAllowlist;
        ContextAllowlist = contextAllowlist;
        Evidence = evidence;
        Reason = reason;
    }

    internal string Id { get; }
    internal string SourceQualifiedItemId { get; }
    internal string? TargetQualifiedItemId { get; }
    internal bool Enabled { get; }
    internal IReadOnlyList<string> LocationAllowlist { get; }
    internal IReadOnlyList<string> ContextAllowlist { get; }
    internal IReadOnlyList<string> Evidence { get; }
    internal string Reason { get; }

    internal bool AllowsLocation(string locationId)
    {
        return ContainsOrdinal(LocationAllowlist, locationId);
    }

    internal bool AllowsContext(string contextId)
    {
        return ContainsOrdinal(ContextAllowlist, contextId);
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Strict load-once catalog shared by owner-local projection and the host pickup transaction.
/// A catalog revision is part of every multiplayer request, so peers with drifting mapping data
/// fail closed before world or inventory mutation.
/// </summary>
internal sealed class ForageReplacementCatalog
{
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumMappings = 32;
    internal const int MaximumAllowlistEntries = 32;
    internal const int MaximumEvidenceEntries = 8;
    internal const string ContractId = "sanity.direct-pickup-replacements.v2";
    internal const string RelativePath = "Asset/Sanity/Data/forage-replacements.json";

    private readonly IReadOnlyList<ForageReplacementMapping> mappings;
    private readonly IReadOnlyDictionary<string, ForageReplacementMapping> bySource;

    private ForageReplacementCatalog(
        bool isAvailable,
        int schemaVersion,
        int revision,
        string reason,
        IReadOnlyList<ForageReplacementMapping> mappings,
        IReadOnlyDictionary<string, ForageReplacementMapping> bySource
    )
    {
        IsAvailable = isAvailable;
        SchemaVersion = schemaVersion;
        Revision = revision;
        Reason = reason;
        this.mappings = mappings;
        this.bySource = bySource;
    }

    internal bool IsAvailable { get; }
    internal int SchemaVersion { get; }
    internal int Revision { get; }
    internal string Reason { get; }
    internal IReadOnlyList<ForageReplacementMapping> Mappings => mappings;

    internal ForageReplacementMapping? FindBySource(string sourceQualifiedItemId)
    {
        return bySource.TryGetValue(sourceQualifiedItemId, out var mapping) ? mapping : null;
    }

    internal static ForageReplacementCatalog Unavailable(string reason)
    {
        return new ForageReplacementCatalog(
            false,
            0,
            0,
            reason,
            Array.Empty<ForageReplacementMapping>(),
            new ReadOnlyDictionary<string, ForageReplacementMapping>(
                new Dictionary<string, ForageReplacementMapping>(StringComparer.Ordinal)
            )
        );
    }

    internal static ForageReplacementCatalogLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed(ForageReplacementReasonIds.CatalogJsonEmpty);

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
            if (
                root.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(
                    root,
                    "SchemaVersion",
                    "ContractId",
                    "Revision",
                    "Mappings"
                )
            )
            {
                return Failed(ForageReplacementReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("SchemaVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != CurrentSchemaVersion
            )
            {
                return Failed(ForageReplacementReasonIds.CatalogVersionUnsupported);
            }
            if (
                !TryReadRequiredString(root, "ContractId", 128, out var contractId)
                || !string.Equals(contractId, ContractId, StringComparison.Ordinal)
            )
            {
                return Failed(ForageReplacementReasonIds.CatalogRootInvalid);
            }
            if (
                !TryReadRequiredInt(root, "Revision", out var revision)
                || revision <= 0
            )
            {
                return Failed(ForageReplacementReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("Mappings", out var mappingsElement)
                || mappingsElement.ValueKind != JsonValueKind.Array
                || mappingsElement.GetArrayLength() > MaximumMappings
            )
            {
                return Failed(ForageReplacementReasonIds.MappingArrayInvalid);
            }

            var parsed = new List<ForageReplacementMapping>(mappingsElement.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sources = new Dictionary<string, ForageReplacementMapping>(StringComparer.Ordinal);
            foreach (var element in mappingsElement.EnumerateArray())
            {
                if (!TryParseMapping(element, out var mapping))
                    return Failed(ForageReplacementReasonIds.MappingInvalid);
                if (!ids.Add(mapping.Id))
                    return Failed(ForageReplacementReasonIds.MappingIdDuplicated);
                if (!sources.TryAdd(mapping.SourceQualifiedItemId, mapping))
                    return Failed(ForageReplacementReasonIds.MappingSourceDuplicated);
                parsed.Add(mapping);
            }

            var catalog = new ForageReplacementCatalog(
                true,
                version,
                revision,
                ForageReplacementReasonIds.CatalogLoaded,
                parsed.AsReadOnly(),
                new ReadOnlyDictionary<string, ForageReplacementMapping>(sources)
            );
            return new ForageReplacementCatalogLoadResult(true, catalog.Reason, catalog);
        }
        catch (JsonException)
        {
            return Failed(ForageReplacementReasonIds.CatalogJsonMalformed);
        }
    }

    private static bool TryParseMapping(
        JsonElement element,
        out ForageReplacementMapping mapping
    )
    {
        mapping = null!;
        if (
            element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                "Id",
                "SourceQualifiedItemId",
                "TargetQualifiedItemId",
                "Enabled",
                "LocationAllowlist",
                "ContextAllowlist",
                "Evidence",
                "Reason"
            )
            || !TryReadRequiredString(element, "Id", 128, out var id)
            || !TryReadQualifiedItemId(element, "SourceQualifiedItemId", nullable: false, out var source)
            || !TryReadQualifiedItemId(element, "TargetQualifiedItemId", nullable: true, out var target)
            || !TryReadRequiredBoolean(element, "Enabled", out var enabled)
            || !TryReadStringArray(
                element,
                "LocationAllowlist",
                MaximumAllowlistEntries,
                128,
                out var locations
            )
            || !TryReadStringArray(
                element,
                "ContextAllowlist",
                MaximumAllowlistEntries,
                128,
                out var contexts
            )
            || !TryReadStringArray(
                element,
                "Evidence",
                MaximumEvidenceEntries,
                256,
                out var evidence
            )
            || !TryReadRequiredString(element, "Reason", 192, out var reason)
        )
        {
            return false;
        }

        if (evidence.Count == 0)
            return false;
        if (
            enabled
            && (
                target is null
                || locations.Count == 0
                || contexts.Count == 0
                || !ContainsOrdinal(
                    contexts,
                    ForagePickupContextIds.NormalDirectObjectPickup
                )
            )
        )
        {
            return false;
        }
        if (ContainsOrdinal(locations, "*") || ContainsOrdinal(contexts, "*"))
            return false;

        mapping = new ForageReplacementMapping(
            id,
            source!,
            target,
            enabled,
            locations,
            contexts,
            evidence,
            reason
        );
        return true;
    }

    private static bool TryReadQualifiedItemId(
        JsonElement owner,
        string propertyName,
        bool nullable,
        out string? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out var element))
            return false;
        if (nullable && element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString()?.Trim();
        return IsObjectQualifiedItemId(value);
    }

    internal static bool IsObjectQualifiedItemId(string? value)
    {
        if (value is null || value.Length is < 4 or > 128 || !value.StartsWith("(O)", StringComparison.Ordinal))
            return false;

        for (var index = 3; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '(' or ')')
                return false;
        }
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement owner,
        string propertyName,
        int maximumCount,
        int maximumLength,
        out IReadOnlyList<string> values
    )
    {
        values = Array.Empty<string>();
        if (
            !owner.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > maximumCount
        )
        {
            return false;
        }

        var parsed = new List<string>(element.GetArrayLength());
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var value = item.GetString()?.Trim() ?? string.Empty;
            if (value.Length is < 1 || value.Length > maximumLength || !unique.Add(value))
                return false;
            parsed.Add(value);
        }
        values = parsed.AsReadOnly();
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement owner,
        string propertyName,
        int maximumLength,
        out string value
    )
    {
        value = string.Empty;
        if (
            !owner.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }
        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maximumLength;
    }

    private static bool TryReadRequiredBoolean(
        JsonElement owner,
        string propertyName,
        out bool value
    )
    {
        value = false;
        if (!owner.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return element.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadRequiredInt(
        JsonElement owner,
        string propertyName,
        out int value
    )
    {
        value = 0;
        return owner.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (!names.Remove(property.Name))
                return false;
        }
        return count == allowed.Length && names.Count == 0;
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static ForageReplacementCatalogLoadResult Failed(string reason)
    {
        return new ForageReplacementCatalogLoadResult(
            false,
            reason,
            Unavailable(reason)
        );
    }
}

/// <summary>
/// Immutable host evidence captured at the exact Stardew 1.6.15 direct-object pickup seam before
/// vanilla changes quality, inventory, stats, or the location object dictionary.
/// </summary>
internal readonly record struct ForagePickupFactSnapshot(
    long PickerMultiplayerId,
    bool PickerMatchesRequest,
    bool IsHostAuthoritative,
    string LocationId,
    string LocationInstanceId,
    int TileX,
    int TileY,
    string ContextId,
    string SourceQualifiedItemId,
    bool ObjectIdentityMatchesLocationTile,
    long ObjectFingerprint,
    bool IsDirectPickupBranch,
    bool IsSpawnedObjectOrErrorItem,
    bool TargetQualifiedItemIdExists
);

internal readonly record struct ForageReplacementEligibility(
    bool IsEligible,
    string Reason,
    ForageReplacementMapping? Mapping
);

internal static class ForageReplacementEligibilityGate
{
    internal static ForageReplacementEligibility Evaluate(
        ForageReplacementCatalog catalog,
        ForagePickupFactSnapshot facts
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (facts.PickerMultiplayerId <= 0 || !facts.PickerMatchesRequest)
            return Rejected(ForageReplacementReasonIds.PickerInvalid);
        if (!facts.IsHostAuthoritative)
            return Rejected(ForageReplacementReasonIds.HostAuthorityRequired);
        if (
            string.IsNullOrWhiteSpace(facts.LocationId)
            || string.IsNullOrWhiteSpace(facts.LocationInstanceId)
            || facts.TileX < 0
            || facts.TileY < 0
            || string.IsNullOrWhiteSpace(facts.ContextId)
            || !ForageReplacementCatalog.IsObjectQualifiedItemId(facts.SourceQualifiedItemId)
        )
        {
            return Rejected(ForageReplacementReasonIds.LocationOrTileInvalid);
        }
        if (!facts.ObjectIdentityMatchesLocationTile)
            return Rejected(ForageReplacementReasonIds.ObjectIdentityMismatch);
        if (facts.ObjectFingerprint <= 0)
            return Rejected(ForageReplacementReasonIds.ObjectFingerprintUnavailable);
        if (!facts.IsDirectPickupBranch)
            return Rejected(ForageReplacementReasonIds.DirectPickupBranchRequired);
        if (!facts.IsSpawnedObjectOrErrorItem)
            return Rejected(ForageReplacementReasonIds.SpawnedOrErrorObjectRequired);

        var mapping = catalog.FindBySource(facts.SourceQualifiedItemId);
        if (mapping is null)
            return Rejected(ForageReplacementReasonIds.MappingNotAllowlisted);
        if (!mapping.Enabled)
            return Rejected(ForageReplacementReasonIds.MappingDisabled, mapping);
        if (!mapping.AllowsLocation(facts.LocationId))
            return Rejected(ForageReplacementReasonIds.LocationNotAllowlisted, mapping);
        if (!mapping.AllowsContext(facts.ContextId))
            return Rejected(ForageReplacementReasonIds.ContextNotAllowlisted, mapping);
        if (mapping.TargetQualifiedItemId is null || !facts.TargetQualifiedItemIdExists)
            return Rejected(ForageReplacementReasonIds.TargetUnavailable, mapping);

        return new ForageReplacementEligibility(
            true,
            ForageReplacementReasonIds.Eligible,
            mapping
        );
    }

    private static ForageReplacementEligibility Rejected(
        string reason,
        ForageReplacementMapping? mapping = null
    )
    {
        return new ForageReplacementEligibility(false, reason, mapping);
    }
}
