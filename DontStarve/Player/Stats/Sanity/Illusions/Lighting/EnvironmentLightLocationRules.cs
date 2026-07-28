#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal enum EnvironmentLightLocationRuleStatus
{
    Matched,
    Unmatched,
    Unavailable,
    Ambiguous,
}

internal enum EnvironmentLightLocationLightProfile
{
    FallbackOnly,
    OpaqueWhiteBase,
}

/// <summary>
/// Pure, bounded location evidence. It deliberately has no localized display name and retains no
/// GameLocation or game-data reference.
/// </summary>
internal sealed class EnvironmentLightLocationSnapshot
{
    private readonly IReadOnlyDictionary<string, string> locationCustomFields;
    private readonly IReadOnlyDictionary<string, string> contextCustomFields;

    internal EnvironmentLightLocationSnapshot(
        string runtimeTypeFullName,
        string contextId,
        string internalName,
        bool isOutdoors,
        bool isTemporary,
        bool isEventActive,
        bool isFestivalActive,
        IReadOnlyDictionary<string, string>? locationCustomFields = null,
        IReadOnlyDictionary<string, string>? contextCustomFields = null,
        string parentBuildingType = ""
    )
    {
        if (string.IsNullOrWhiteSpace(runtimeTypeFullName))
            throw new ArgumentException("A runtime location type is required.", nameof(runtimeTypeFullName));
        if (string.IsNullOrWhiteSpace(contextId))
            throw new ArgumentException("A location context ID is required.", nameof(contextId));
        if (string.IsNullOrWhiteSpace(internalName))
            throw new ArgumentException("An internal location name is required.", nameof(internalName));

        RuntimeTypeFullName = runtimeTypeFullName;
        ContextId = contextId;
        InternalName = internalName;
        IsOutdoors = isOutdoors;
        IsTemporary = isTemporary;
        IsEventActive = isEventActive;
        IsFestivalActive = isFestivalActive;
        ParentBuildingType = parentBuildingType ?? string.Empty;
        this.locationCustomFields = CopyFields(locationCustomFields);
        this.contextCustomFields = CopyFields(contextCustomFields);
    }

    internal string RuntimeTypeFullName { get; }
    internal string ContextId { get; }
    internal string InternalName { get; }
    internal bool IsOutdoors { get; }
    internal bool IsTemporary { get; }
    internal bool IsEventActive { get; }
    internal bool IsFestivalActive { get; }
    internal string ParentBuildingType { get; }
    internal IReadOnlyDictionary<string, string> LocationCustomFields => locationCustomFields;
    internal IReadOnlyDictionary<string, string> ContextCustomFields => contextCustomFields;

    private static IReadOnlyDictionary<string, string> CopyFields(
        IReadOnlyDictionary<string, string>? source
    )
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is not null)
        {
            foreach (var pair in source)
                copy[pair.Key] = pair.Value;
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}

internal sealed record EnvironmentLightLocationRuleResolution(
    EnvironmentLightLocationRuleStatus Status,
    int ContractVersion,
    string RuleId,
    EnvironmentLightLocationLightProfile LightProfile,
    bool TwoAmSpecialDeathSafe,
    bool DarknessAttackSafe,
    bool HostileShadowSafe,
    bool JunimoBlessingEligible,
    string Reason
)
{
    internal bool IsMatched => Status == EnvironmentLightLocationRuleStatus.Matched;
    internal string TwoAmSpecialDeathReason =>
        Status switch
        {
            EnvironmentLightLocationRuleStatus.Matched when TwoAmSpecialDeathSafe =>
                TwoAmSpecialDeathLocationReasonIds.SafeRuleMatched,
            EnvironmentLightLocationRuleStatus.Matched =>
                TwoAmSpecialDeathLocationReasonIds.UnsafeRuleMatched,
            EnvironmentLightLocationRuleStatus.Unmatched =>
                TwoAmSpecialDeathLocationReasonIds.UnmatchedDefaultUnsafe,
            EnvironmentLightLocationRuleStatus.Ambiguous =>
                TwoAmSpecialDeathLocationReasonIds.AmbiguousDefaultUnsafe,
            _ => TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe,
        };

    internal static EnvironmentLightLocationRuleResolution Unmatched(int version)
    {
        return new EnvironmentLightLocationRuleResolution(
            EnvironmentLightLocationRuleStatus.Unmatched,
            version,
            EnvironmentLightLocationRuleIds.Unmatched,
            EnvironmentLightLocationLightProfile.FallbackOnly,
            TwoAmSpecialDeathSafe: false,
            DarknessAttackSafe: false,
            HostileShadowSafe: false,
            JunimoBlessingEligible: false,
            EnvironmentLightReasonIds.LocationRuleUnmatched
        );
    }

    internal static EnvironmentLightLocationRuleResolution Unavailable(string reason)
    {
        return new EnvironmentLightLocationRuleResolution(
            EnvironmentLightLocationRuleStatus.Unavailable,
            0,
            EnvironmentLightLocationRuleIds.Unavailable,
            EnvironmentLightLocationLightProfile.FallbackOnly,
            TwoAmSpecialDeathSafe: false,
            DarknessAttackSafe: false,
            HostileShadowSafe: false,
            JunimoBlessingEligible: false,
            reason
        );
    }

    internal static EnvironmentLightLocationRuleResolution Ambiguous(int version)
    {
        return new EnvironmentLightLocationRuleResolution(
            EnvironmentLightLocationRuleStatus.Ambiguous,
            version,
            EnvironmentLightLocationRuleIds.Ambiguous,
            EnvironmentLightLocationLightProfile.FallbackOnly,
            TwoAmSpecialDeathSafe: false,
            DarknessAttackSafe: false,
            HostileShadowSafe: false,
            JunimoBlessingEligible: false,
            EnvironmentLightReasonIds.LocationRuleAmbiguous
        );
    }
}

internal readonly record struct EnvironmentLightLocationRuleLoadResult(
    bool IsAvailable,
    string Reason,
    EnvironmentLightLocationRuleCatalog Catalog
);

/// <summary>
/// Immutable load-once registry. Matching is exact, ordinal, and structural; equal-priority
/// matches fail closed instead of allowing file order to become a hidden authority.
/// </summary>
internal sealed class EnvironmentLightLocationRuleCatalog
{
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumRules = 64;
    internal const int MaximumCustomFieldsPerMatch = 8;
    internal const string RelativePath = "Asset/Sanity/Data/location-rules.json";

    private readonly IReadOnlyList<EnvironmentLightLocationRule> rules;
    private readonly IReadOnlyList<string> locationCustomFieldKeys;
    private readonly IReadOnlyList<string> contextCustomFieldKeys;

    private EnvironmentLightLocationRuleCatalog(
        bool isAvailable,
        int contractVersion,
        string loadReason,
        IReadOnlyList<EnvironmentLightLocationRule> rules,
        IReadOnlyList<string> locationCustomFieldKeys,
        IReadOnlyList<string> contextCustomFieldKeys
    )
    {
        IsAvailable = isAvailable;
        ContractVersion = contractVersion;
        LoadReason = loadReason;
        this.rules = rules;
        this.locationCustomFieldKeys = locationCustomFieldKeys;
        this.contextCustomFieldKeys = contextCustomFieldKeys;
    }

    internal bool IsAvailable { get; }
    internal int ContractVersion { get; }
    internal string LoadReason { get; }
    internal int Count => rules.Count;
    internal IReadOnlyList<string> LocationCustomFieldKeys => locationCustomFieldKeys;
    internal IReadOnlyList<string> ContextCustomFieldKeys => contextCustomFieldKeys;

    internal static EnvironmentLightLocationRuleCatalog Unavailable(string reason)
    {
        return new EnvironmentLightLocationRuleCatalog(
            false,
            0,
            reason,
            Array.Empty<EnvironmentLightLocationRule>(),
            Array.Empty<string>(),
            Array.Empty<string>()
        );
    }

    internal EnvironmentLightLocationRuleResolution Resolve(
        EnvironmentLightLocationSnapshot location
    )
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!IsAvailable)
            return EnvironmentLightLocationRuleResolution.Unavailable(LoadReason);

        EnvironmentLightLocationRule? best = null;
        var ambiguous = false;
        foreach (var rule in rules)
        {
            if (!rule.Matches(location))
                continue;
            if (best is null)
            {
                best = rule;
                continue;
            }
            if (rule.Priority < best.Priority)
                break;

            ambiguous = true;
        }

        if (ambiguous)
            return EnvironmentLightLocationRuleResolution.Ambiguous(ContractVersion);
        if (best is null)
            return EnvironmentLightLocationRuleResolution.Unmatched(ContractVersion);

        return new EnvironmentLightLocationRuleResolution(
            EnvironmentLightLocationRuleStatus.Matched,
            ContractVersion,
            best.Id,
            best.LightProfile,
            best.TwoAmSpecialDeathSafe,
            best.DarknessAttackSafe,
            best.HostileShadowSafe,
            best.JunimoBlessingEligible,
            EnvironmentLightReasonIds.LocationRuleMatched
        );
    }

    internal static EnvironmentLightLocationRuleLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed("environment-light.location-rules-json-empty");

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
                || !HasOnlyProperties(root, "SchemaVersion", "Rules")
            )
            {
                return Failed("environment-light.location-rules-root-invalid");
            }
            if (
                !root.TryGetProperty("SchemaVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != CurrentSchemaVersion
            )
            {
                return Failed("environment-light.location-rules-version-unsupported");
            }
            if (
                !root.TryGetProperty("Rules", out var rulesElement)
                || rulesElement.ValueKind != JsonValueKind.Array
                || rulesElement.GetArrayLength() > MaximumRules
            )
            {
                return Failed("environment-light.location-rules-array-invalid");
            }

            var parsed = new List<EnvironmentLightLocationRule>(rulesElement.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var locationKeys = new HashSet<string>(StringComparer.Ordinal);
            var contextKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in rulesElement.EnumerateArray())
            {
                if (!TryParseRule(element, out var rule, out var reason))
                    return Failed(reason);
                if (!ids.Add(rule.Id))
                    return Failed("environment-light.location-rule-id-duplicated");

                parsed.Add(rule);
                foreach (var key in rule.Match.LocationCustomFields.Keys)
                    locationKeys.Add(key);
                foreach (var key in rule.Match.ContextCustomFields.Keys)
                    contextKeys.Add(key);
            }

            parsed.Sort(EnvironmentLightLocationRule.Compare);
            var locationKeyArray = ToSortedArray(locationKeys);
            var contextKeyArray = ToSortedArray(contextKeys);
            var catalog = new EnvironmentLightLocationRuleCatalog(
                true,
                version,
                "environment-light.location-rules-loaded",
                parsed.AsReadOnly(),
                Array.AsReadOnly(locationKeyArray),
                Array.AsReadOnly(contextKeyArray)
            );
            return new EnvironmentLightLocationRuleLoadResult(
                true,
                catalog.LoadReason,
                catalog
            );
        }
        catch (JsonException)
        {
            return Failed("environment-light.location-rules-json-malformed");
        }
    }

    private static bool TryParseRule(
        JsonElement element,
        out EnvironmentLightLocationRule rule,
        out string reason
    )
    {
        rule = null!;
        reason = "environment-light.location-rule-invalid";
        if (
            element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                "Id",
                "Priority",
                "Match",
                "LightProfile",
                "TwoAmSpecialDeathSafe",
                "DarknessAttackSafe",
                "HostileShadowSafe",
                "JunimoBlessingEligible"
            )
        )
        {
            return false;
        }
        if (
            !TryReadRequiredString(element, "Id", 128, out var id)
            || !element.TryGetProperty("Priority", out var priorityElement)
            || priorityElement.ValueKind != JsonValueKind.Number
            || !priorityElement.TryGetInt32(out var priority)
            || priority is < -10_000 or > 10_000
            || !element.TryGetProperty("Match", out var matchElement)
            || !TryParseMatch(matchElement, out var match)
            || !TryReadRequiredString(element, "LightProfile", 64, out var profileName)
            || !Enum.TryParse<EnvironmentLightLocationLightProfile>(
                profileName,
                ignoreCase: false,
                out var lightProfile
            )
            || !TryReadRequiredBoolean(
                element,
                "TwoAmSpecialDeathSafe",
                out var twoAmSpecialDeathSafe
            )
            || !TryReadRequiredBoolean(element, "DarknessAttackSafe", out var darknessAttackSafe)
            || !TryReadRequiredBoolean(element, "HostileShadowSafe", out var hostileShadowSafe)
            || !TryReadRequiredBoolean(
                element,
                "JunimoBlessingEligible",
                out var junimoBlessingEligible
            )
        )
        {
            return false;
        }

        rule = new EnvironmentLightLocationRule(
            id,
            priority,
            match,
            lightProfile,
            twoAmSpecialDeathSafe,
            darknessAttackSafe,
            hostileShadowSafe,
            junimoBlessingEligible
        );
        reason = string.Empty;
        return true;
    }

    private static bool TryParseMatch(
        JsonElement element,
        out EnvironmentLightLocationRuleMatch match
    )
    {
        match = null!;
        if (
            element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                "RuntimeType",
                "ContextId",
                "InternalName",
                "ParentBuildingType",
                "IsOutdoors",
                "IsTemporary",
                "EventActive",
                "FestivalActive",
                "LocationCustomFields",
                "ContextCustomFields"
            )
        )
        {
            return false;
        }

        if (
            !TryReadOptionalString(element, "RuntimeType", 192, out var runtimeType)
            || !TryReadOptionalString(element, "ContextId", 128, out var contextId)
            || !TryReadOptionalString(element, "InternalName", 128, out var internalName)
            || !TryReadOptionalString(
                element,
                "ParentBuildingType",
                128,
                out var parentBuildingType
            )
            || !TryReadOptionalBoolean(element, "IsOutdoors", out var isOutdoors)
            || !TryReadOptionalBoolean(element, "IsTemporary", out var isTemporary)
            || !TryReadOptionalBoolean(element, "EventActive", out var eventActive)
            || !TryReadOptionalBoolean(element, "FestivalActive", out var festivalActive)
            || !TryReadStringMap(element, "LocationCustomFields", out var locationFields)
            || !TryReadStringMap(element, "ContextCustomFields", out var contextFields)
        )
        {
            return false;
        }

        if (
            runtimeType is null
            && contextId is null
            && internalName is null
            && parentBuildingType is null
            && !isOutdoors.HasValue
            && !isTemporary.HasValue
            && !eventActive.HasValue
            && !festivalActive.HasValue
            && locationFields.Count == 0
            && contextFields.Count == 0
        )
        {
            return false;
        }

        match = new EnvironmentLightLocationRuleMatch(
            runtimeType,
            contextId,
            internalName,
            parentBuildingType,
            isOutdoors,
            isTemporary,
            eventActive,
            festivalActive,
            locationFields,
            contextFields
        );
        return true;
    }

    private static bool TryReadStringMap(
        JsonElement owner,
        string propertyName,
        out IReadOnlyDictionary<string, string> values
    )
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        values = new ReadOnlyDictionary<string, string>(parsed);
        if (!owner.TryGetProperty(propertyName, out var element))
            return true;
        if (
            element.ValueKind != JsonValueKind.Object
            || CountProperties(element) > MaximumCustomFieldsPerMatch
        )
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.Trim();
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (
                key.Length is < 1 or > 128
                || value.Length is < 1 or > 256
                || !parsed.TryAdd(key, value)
            )
            {
                return false;
            }
        }
        values = new ReadOnlyDictionary<string, string>(parsed);
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

    private static bool TryReadOptionalString(
        JsonElement owner,
        string propertyName,
        int maximumLength,
        out string? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString()?.Trim();
        return value is not null && value.Length is > 0 && value.Length <= maximumLength;
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
        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalBoolean(
        JsonElement owner,
        string propertyName,
        out bool? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out var element))
            return true;
        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;

            var permitted = false;
            foreach (var name in allowed)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    permitted = true;
                    break;
                }
            }
            if (!permitted)
                return false;
        }
        return true;
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var unused in element.EnumerateObject())
            count++;
        return count;
    }

    private static string[] ToSortedArray(HashSet<string> values)
    {
        var result = new string[values.Count];
        values.CopyTo(result);
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static EnvironmentLightLocationRuleLoadResult Failed(string reason)
    {
        return new EnvironmentLightLocationRuleLoadResult(
            false,
            reason,
            Unavailable(reason)
        );
    }

    private sealed class EnvironmentLightLocationRule
    {
        internal EnvironmentLightLocationRule(
            string id,
            int priority,
            EnvironmentLightLocationRuleMatch match,
            EnvironmentLightLocationLightProfile lightProfile,
            bool twoAmSpecialDeathSafe,
            bool darknessAttackSafe,
            bool hostileShadowSafe,
            bool junimoBlessingEligible
        )
        {
            Id = id;
            Priority = priority;
            Match = match;
            LightProfile = lightProfile;
            TwoAmSpecialDeathSafe = twoAmSpecialDeathSafe;
            DarknessAttackSafe = darknessAttackSafe;
            HostileShadowSafe = hostileShadowSafe;
            JunimoBlessingEligible = junimoBlessingEligible;
        }

        internal string Id { get; }
        internal int Priority { get; }
        internal EnvironmentLightLocationRuleMatch Match { get; }
        internal EnvironmentLightLocationLightProfile LightProfile { get; }
        internal bool TwoAmSpecialDeathSafe { get; }
        internal bool DarknessAttackSafe { get; }
        internal bool HostileShadowSafe { get; }
        internal bool JunimoBlessingEligible { get; }

        internal bool Matches(EnvironmentLightLocationSnapshot location)
        {
            return Match.Matches(location);
        }

        internal static int Compare(
            EnvironmentLightLocationRule left,
            EnvironmentLightLocationRule right
        )
        {
            var priority = right.Priority.CompareTo(left.Priority);
            return priority != 0 ? priority : string.CompareOrdinal(left.Id, right.Id);
        }
    }

    private sealed class EnvironmentLightLocationRuleMatch
    {
        internal EnvironmentLightLocationRuleMatch(
            string? runtimeType,
            string? contextId,
            string? internalName,
            string? parentBuildingType,
            bool? isOutdoors,
            bool? isTemporary,
            bool? eventActive,
            bool? festivalActive,
            IReadOnlyDictionary<string, string> locationCustomFields,
            IReadOnlyDictionary<string, string> contextCustomFields
        )
        {
            RuntimeType = runtimeType;
            ContextId = contextId;
            InternalName = internalName;
            ParentBuildingType = parentBuildingType;
            IsOutdoors = isOutdoors;
            IsTemporary = isTemporary;
            EventActive = eventActive;
            FestivalActive = festivalActive;
            LocationCustomFields = locationCustomFields;
            ContextCustomFields = contextCustomFields;
        }

        internal string? RuntimeType { get; }
        internal string? ContextId { get; }
        internal string? InternalName { get; }
        internal string? ParentBuildingType { get; }
        internal bool? IsOutdoors { get; }
        internal bool? IsTemporary { get; }
        internal bool? EventActive { get; }
        internal bool? FestivalActive { get; }
        internal IReadOnlyDictionary<string, string> LocationCustomFields { get; }
        internal IReadOnlyDictionary<string, string> ContextCustomFields { get; }

        internal bool Matches(EnvironmentLightLocationSnapshot location)
        {
            return Matches(RuntimeType, location.RuntimeTypeFullName)
                && Matches(ContextId, location.ContextId)
                && Matches(InternalName, location.InternalName)
                && Matches(ParentBuildingType, location.ParentBuildingType)
                && Matches(IsOutdoors, location.IsOutdoors)
                && Matches(IsTemporary, location.IsTemporary)
                && Matches(EventActive, location.IsEventActive)
                && Matches(FestivalActive, location.IsFestivalActive)
                && ContainsAll(LocationCustomFields, location.LocationCustomFields)
                && ContainsAll(ContextCustomFields, location.ContextCustomFields);
        }

        private static bool Matches(string? expected, string actual)
        {
            return expected is null || string.Equals(expected, actual, StringComparison.Ordinal);
        }

        private static bool Matches(bool? expected, bool actual)
        {
            return !expected.HasValue || expected.Value == actual;
        }

        private static bool ContainsAll(
            IReadOnlyDictionary<string, string> expected,
            IReadOnlyDictionary<string, string> actual
        )
        {
            foreach (var pair in expected)
            {
                if (
                    !actual.TryGetValue(pair.Key, out var value)
                    || !string.Equals(pair.Value, value, StringComparison.Ordinal)
                )
                {
                    return false;
                }
            }
            return true;
        }
    }
}

internal static class EnvironmentLightLocationRuleIds
{
    internal const string Unmatched = "environment-light.location.unmatched";
    internal const string Unavailable = "environment-light.location-rules.unavailable";
    internal const string Ambiguous = "environment-light.location-rules.ambiguous";
}

internal static class TwoAmSpecialDeathLocationReasonIds
{
    internal const string SafeRuleMatched =
        "passout.location.two-am-special-death.safe-rule-matched";
    internal const string UnsafeRuleMatched =
        "passout.location.two-am-special-death.unsafe-rule-matched";
    internal const string UnmatchedDefaultUnsafe =
        "passout.location.two-am-special-death.unmatched-default-unsafe";
    internal const string RulesUnavailableDefaultUnsafe =
        "passout.location.two-am-special-death.rules-unavailable-default-unsafe";
    internal const string AmbiguousDefaultUnsafe =
        "passout.location.two-am-special-death.ambiguous-default-unsafe";
}
