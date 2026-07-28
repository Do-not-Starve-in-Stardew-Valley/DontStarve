#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

internal sealed class ShadowMonsterAssetBindingReference
{
    internal ShadowMonsterAssetBindingReference(
        string assetBindingId,
        string slotId,
        string animationProfileId,
        string cueSetId,
        string attackMotionPolicyId
    )
    {
        AssetBindingId = assetBindingId;
        SlotId = slotId;
        AnimationProfileId = animationProfileId;
        CueSetId = cueSetId;
        AttackMotionPolicyId = attackMotionPolicyId;
    }

    internal string AssetBindingId { get; }
    internal string SlotId { get; }
    internal string AnimationProfileId { get; }
    internal string CueSetId { get; }
    internal string AttackMotionPolicyId { get; }
}

/// <summary>
/// A startup-only projection of the stage-03 authority. It retains only stable IDs needed to
/// validate gameplay profiles and never copies sprite, frame, collision, cue, or cache data.
/// </summary>
internal sealed class ShadowMonsterProfileReferenceCatalog
{
    private readonly IReadOnlyDictionary<string, ShadowMonsterAssetBindingReference> bindings;

    private ShadowMonsterProfileReferenceCatalog(
        IReadOnlyDictionary<string, ShadowMonsterAssetBindingReference> bindings
    )
    {
        this.bindings = new ReadOnlyDictionary<string, ShadowMonsterAssetBindingReference>(
            new Dictionary<string, ShadowMonsterAssetBindingReference>(
                bindings,
                StringComparer.Ordinal
            )
        );
    }

    internal bool TryGetBinding(
        string assetBindingId,
        out ShadowMonsterAssetBindingReference? binding
    )
    {
        return bindings.TryGetValue(assetBindingId, out binding);
    }

    internal static bool TryCreate(
        string animationsJson,
        string resourceBindingsJson,
        string audioCuesJson,
        out ShadowMonsterProfileReferenceCatalog? catalog,
        out IReadOnlyList<ShadowMonsterProfileIssue> issues
    )
    {
        catalog = null;
        var mutableIssues = new List<ShadowMonsterProfileIssue>();
        var visualContract = SanityHostileVisualContractValidator.Validate(
            animationsJson,
            resourceBindingsJson
        );
        foreach (var issue in visualContract.Issues)
        {
            mutableIssues.Add(
                new ShadowMonsterProfileIssue(
                    "shadow-profile.reference.visual-contract-invalid",
                    ShadowMonsterProfilePaths.ResourceBindings,
                    issue.Code,
                    issue.Reason
                )
            );
        }

        if (mutableIssues.Count > 0)
        {
            issues = mutableIssues.AsReadOnly();
            return false;
        }

        try
        {
            using var animationDocument = ParseStrict(animationsJson);
            using var bindingDocument = ParseStrict(resourceBindingsJson);
            using var audioDocument = ParseStrict(audioCuesJson);

            var animationIds = IndexIds(
                animationDocument.RootElement,
                "AnimationProfiles",
                "AnimationProfileId",
                ShadowMonsterProfilePaths.Animations,
                mutableIssues
            );
            var cueSetIds = IndexIds(
                audioDocument.RootElement,
                "CueSets",
                "CueSetId",
                ShadowMonsterProfilePaths.AudioCues,
                mutableIssues
            );
            var policyIds = IndexIds(
                bindingDocument.RootElement,
                "AttackMotionPolicies",
                "AttackMotionPolicyId",
                ShadowMonsterProfilePaths.ResourceBindings,
                mutableIssues
            );
            var parsedBindings = new Dictionary<
                string,
                ShadowMonsterAssetBindingReference
            >(StringComparer.Ordinal);
            if (
                !bindingDocument.RootElement.TryGetProperty("Bindings", out var bindingArray)
                || bindingArray.ValueKind != JsonValueKind.Array
            )
            {
                mutableIssues.Add(
                    Issue(
                        "shadow-profile.reference.bindings-invalid",
                        ShadowMonsterProfilePaths.ResourceBindings,
                        "Bindings",
                        "The stage-03 binding array is missing or invalid."
                    )
                );
            }
            else
            {
                foreach (var element in bindingArray.EnumerateArray())
                {
                    if (
                        !TryGetNonEmptyString(element, "AssetBindingId", out var bindingId)
                        || !TryGetNonEmptyString(element, "SlotId", out var slotId)
                        || !TryGetNonEmptyString(
                            element,
                            "AnimationProfileId",
                            out var animationId
                        )
                        || !TryGetNonEmptyString(element, "CueSetId", out var cueSetId)
                        || !TryGetNonEmptyString(
                            element,
                            "AttackMotionPolicyId",
                            out var attackMotionPolicyId
                        )
                    )
                    {
                        mutableIssues.Add(
                            Issue(
                                "shadow-profile.reference.binding-invalid",
                                ShadowMonsterProfilePaths.ResourceBindings,
                                "Bindings",
                                "A stage-03 binding is missing a stable ID."
                            )
                        );
                        continue;
                    }

                    if (!animationIds.Contains(animationId))
                    {
                        mutableIssues.Add(
                            Issue(
                                "shadow-profile.reference.animation-unknown",
                                ShadowMonsterProfilePaths.ResourceBindings,
                                bindingId + ".AnimationProfileId",
                                $"Animation profile '{animationId}' does not exist."
                            )
                        );
                    }
                    if (!cueSetIds.Contains(cueSetId))
                    {
                        mutableIssues.Add(
                            Issue(
                                "shadow-profile.reference.cue-set-unknown",
                                ShadowMonsterProfilePaths.ResourceBindings,
                                bindingId + ".CueSetId",
                                $"Cue set '{cueSetId}' does not exist."
                            )
                        );
                    }
                    if (!policyIds.Contains(attackMotionPolicyId))
                    {
                        mutableIssues.Add(
                            Issue(
                                "shadow-profile.reference.attack-motion-unknown",
                                ShadowMonsterProfilePaths.ResourceBindings,
                                bindingId + ".AttackMotionPolicyId",
                                $"Attack motion policy '{attackMotionPolicyId}' does not exist."
                            )
                        );
                    }
                    if (
                        !parsedBindings.TryAdd(
                            bindingId,
                            new ShadowMonsterAssetBindingReference(
                                bindingId,
                                slotId,
                                animationId,
                                cueSetId,
                                attackMotionPolicyId
                            )
                        )
                    )
                    {
                        mutableIssues.Add(
                            Issue(
                                "shadow-profile.reference.binding-duplicate",
                                ShadowMonsterProfilePaths.ResourceBindings,
                                bindingId,
                                "Asset binding IDs must be unique."
                            )
                        );
                    }
                }
            }

            if (mutableIssues.Count == 0)
                catalog = new ShadowMonsterProfileReferenceCatalog(parsedBindings);
        }
        catch (JsonException exception)
        {
            mutableIssues.Add(
                Issue(
                    "shadow-profile.reference.json-malformed",
                    ShadowMonsterProfilePaths.ResourceBindings,
                    "$",
                    exception.Message
                )
            );
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or FormatException
            or OverflowException
        )
        {
            mutableIssues.Add(
                Issue(
                    "shadow-profile.reference.shape-invalid",
                    ShadowMonsterProfilePaths.ResourceBindings,
                    "$",
                    exception.Message
                )
            );
        }

        issues = mutableIssues.AsReadOnly();
        return catalog is not null && mutableIssues.Count == 0;
    }

    private static HashSet<string> IndexIds(
        JsonElement root,
        string arrayProperty,
        string idProperty,
        string filePath,
        List<ShadowMonsterProfileIssue> issues
    )
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(arrayProperty, out var array)
            || array.ValueKind != JsonValueKind.Array
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.reference.array-invalid",
                    filePath,
                    arrayProperty,
                    $"The '{arrayProperty}' array is missing or invalid."
                )
            );
            return ids;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (!TryGetNonEmptyString(element, idProperty, out var id))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.reference.id-invalid",
                        filePath,
                        arrayProperty + "." + idProperty,
                        "A referenced stable ID is missing or invalid."
                    )
                );
                continue;
            }
            if (!ids.Add(id))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.reference.id-duplicate",
                        filePath,
                        id,
                        "Referenced stable IDs must be unique."
                    )
                );
            }
        }

        return ids;
    }

    private static bool TryGetNonEmptyString(
        JsonElement owner,
        string propertyName,
        out string value
    )
    {
        value = string.Empty;
        if (
            owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0 && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private static JsonDocument ParseStrict(string json)
    {
        return JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static ShadowMonsterProfileIssue Issue(
        string code,
        string filePath,
        string key,
        string reason
    )
    {
        return new ShadowMonsterProfileIssue(code, filePath, key, reason);
    }
}

internal static class ShadowMonsterProfileValidator
{
    internal const int CurrentSchemaVersion = 1;
    internal const int CurrentCanonicalModelVersion = 1;
    internal const int CurrentDropTableSchemaVersion = 1;
    internal const int CurrentStardew16AdapterVersion = 1;

    private const int MaximumHealth = 1_000_000;
    private const int MaximumDamage = 100_000;
    private const int MaximumDefense = 10_000;
    private const int MaximumExperience = 1_000_000;
    private const int MaximumSanityReward = 1_000;
    private const double MaximumMovementSpeed = 64d;
    private const double MaximumRadiusTiles = 256d;
    private const double MaximumAttackIntervalSeconds = 60d;
    private const double MaximumDespawnGameHours = 168d;

    private static readonly HashSet<string> AllowedRootProperties = new(
        new[] { "SchemaVersion", "ProfileId", "IsBetaBalance", "Monsters" },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> AllowedMonsterProperties = new(
        new[]
        {
            "MaxHealth",
            "BaseDamage",
            "MovementSpeed",
            "Defense",
            "DetectionRadiusTiles",
            "AttackRangeTiles",
            "AttackIntervalSeconds",
            "NaturalDespawnGameHours",
            "DisplayNameKey",
            "WallTraversalMode",
            "ImmunityTags",
            "DropTable",
            "SanityReward",
            "AssetBindingId",
            "AnimationProfileId",
            "CueSetId",
            "AttackMotionPolicyId",
            "PostAttackPolicyId",
            "ExperienceValue",
            "KillCounterId",
            "GameVersionOverrides",
        },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> AllowedDropTableProperties = new(
        new[]
        {
            "SchemaVersion",
            "DropTableId",
            "ItemSemanticId",
            "GuaranteedQuantity",
            "BonusQuantity",
            "BonusChance",
        },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> AllowedOverrideProperties = new(
        new[]
        {
            "GameVersionId",
            "MaxHealth",
            "BaseDamage",
            "MovementSpeed",
            "Defense",
            "DetectionRadiusTiles",
            "AttackRangeTiles",
            "AttackIntervalSeconds",
            "NaturalDespawnGameHours",
            "ExperienceValue",
            "WallTraversalMode",
        },
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> AllowedSchemaProperties = new(
        new[]
        {
            "SchemaId",
            "SchemaVersion",
            "CanonicalModelVersion",
            "DropTableSchemaVersion",
            "UnknownFieldPolicy",
            "Stardew16AdapterVersion",
            "Stardew16DataShape",
            "Stardew17Capability",
            "Stardew17Reason",
        },
        StringComparer.Ordinal
    );

    internal static ShadowMonsterProfileValidationResult Validate(
        string schemaJson,
        IReadOnlyList<ShadowMonsterProfileDocument> documents,
        ShadowMonsterProfileReferenceCatalog references
    )
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(references);

        var issues = new List<ShadowMonsterProfileIssue>();
        var diagnostics = new List<ShadowMonsterProfileDiagnostic>();
        if (!TryParseSchema(schemaJson, issues, out var schema))
            return new ShadowMonsterProfileValidationResult(null, issues, diagnostics);

        var byPath = new Dictionary<string, ShadowMonsterProfileDocument>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document is null || string.IsNullOrWhiteSpace(document.RelativePath))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.file-path-invalid",
                        string.Empty,
                        "$",
                        "Every profile document requires a deployment-relative path."
                    )
                );
                continue;
            }
            if (!ShadowMonsterProfilePaths.TryGetExpectedProfileId(document.RelativePath, out _))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.file-unknown",
                        document.RelativePath,
                        "$",
                        "Only the four frozen difficulty profile files are accepted."
                    )
                );
                continue;
            }
            if (!byPath.TryAdd(document.RelativePath, document))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.file-duplicate",
                        document.RelativePath,
                        "$",
                        "A difficulty profile file was supplied more than once."
                    )
                );
            }
        }

        foreach (var pair in ShadowMonsterProfilePaths.ProfileFiles)
        {
            if (!byPath.ContainsKey(pair.Value))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.file-missing",
                        pair.Value,
                        "$",
                        $"Difficulty profile '{pair.Key}' is missing."
                    )
                );
            }
        }

        var profiles = new Dictionary<string, ShadowMonsterDifficultyProfile>(
            StringComparer.Ordinal
        );
        foreach (var pair in ShadowMonsterProfilePaths.ProfileFiles)
        {
            if (!byPath.TryGetValue(pair.Value, out var document))
                continue;
            if (
                TryParseDifficultyProfile(
                    document,
                    pair.Key,
                    references,
                    issues,
                    diagnostics,
                    out var profile
                )
                && profile is not null
                && !profiles.TryAdd(profile.ProfileId, profile)
            )
            {
                issues.Add(
                    Issue(
                        "shadow-profile.profile-id-duplicate",
                        document.RelativePath,
                        "ProfileId",
                        $"ProfileId '{profile.ProfileId}' is duplicated."
                    )
                );
            }
        }

        if (issues.Count > 0 || profiles.Count != ShadowMonsterProfilePaths.ProfileFiles.Count)
            return new ShadowMonsterProfileValidationResult(null, issues, diagnostics);

        return new ShadowMonsterProfileValidationResult(
            new ShadowMonsterProfileCatalog(schema!, profiles),
            issues,
            diagnostics
        );
    }

    private static bool TryParseSchema(
        string schemaJson,
        List<ShadowMonsterProfileIssue> issues,
        out ShadowMonsterProfileSchemaContract? schema
    )
    {
        schema = null;
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            issues.Add(
                Issue(
                    "shadow-profile.schema-empty",
                    ShadowMonsterProfilePaths.Schema,
                    "$",
                    "The schema/version document is empty."
                )
            );
            return false;
        }

        try
        {
            using var document = ParseStrict(schemaJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.schema-root-invalid",
                        ShadowMonsterProfilePaths.Schema,
                        "$",
                        "The schema/version root must be an object."
                    )
                );
                return false;
            }

            ValidateAllowedProperties(
                root,
                AllowedSchemaProperties,
                ShadowMonsterProfilePaths.Schema,
                "$",
                issues
            );
            var valid = true;
            valid &= TryReadRequiredString(
                root,
                "SchemaId",
                ShadowMonsterProfilePaths.Schema,
                "SchemaId",
                issues,
                out var schemaId
            );
            valid &= TryReadRequiredInt(
                root,
                "SchemaVersion",
                1,
                int.MaxValue,
                ShadowMonsterProfilePaths.Schema,
                "SchemaVersion",
                issues,
                out var schemaVersion
            );
            valid &= TryReadRequiredInt(
                root,
                "CanonicalModelVersion",
                1,
                int.MaxValue,
                ShadowMonsterProfilePaths.Schema,
                "CanonicalModelVersion",
                issues,
                out var canonicalModelVersion
            );
            valid &= TryReadRequiredInt(
                root,
                "DropTableSchemaVersion",
                1,
                int.MaxValue,
                ShadowMonsterProfilePaths.Schema,
                "DropTableSchemaVersion",
                issues,
                out var dropTableSchemaVersion
            );
            valid &= TryReadRequiredString(
                root,
                "UnknownFieldPolicy",
                ShadowMonsterProfilePaths.Schema,
                "UnknownFieldPolicy",
                issues,
                out var unknownFieldPolicy
            );
            valid &= TryReadRequiredInt(
                root,
                "Stardew16AdapterVersion",
                1,
                int.MaxValue,
                ShadowMonsterProfilePaths.Schema,
                "Stardew16AdapterVersion",
                issues,
                out var stardew16AdapterVersion
            );
            valid &= TryReadRequiredString(
                root,
                "Stardew16DataShape",
                ShadowMonsterProfilePaths.Schema,
                "Stardew16DataShape",
                issues,
                out var stardew16DataShape
            );
            valid &= TryReadRequiredString(
                root,
                "Stardew17Capability",
                ShadowMonsterProfilePaths.Schema,
                "Stardew17Capability",
                issues,
                out var stardew17Capability
            );
            valid &= TryReadRequiredString(
                root,
                "Stardew17Reason",
                ShadowMonsterProfilePaths.Schema,
                "Stardew17Reason",
                issues,
                out var stardew17Reason
            );

            if (
                !valid
                || schemaId != ShadowMonsterProfileContractIds.SchemaId
                || schemaVersion != CurrentSchemaVersion
                || canonicalModelVersion != CurrentCanonicalModelVersion
                || dropTableSchemaVersion != CurrentDropTableSchemaVersion
                || unknownFieldPolicy != ShadowMonsterProfileContractIds.UnknownFieldPolicy
                || stardew16AdapterVersion != CurrentStardew16AdapterVersion
                || stardew16DataShape != ShadowMonsterProfileContractIds.Stardew16DataShape
                || stardew17Capability != ShadowMonsterProfileContractIds.Stardew17Capability
                || stardew17Reason != ShadowMonsterProfileContractIds.Stardew17UnavailableReason
            )
            {
                issues.Add(
                    Issue(
                        "shadow-profile.schema-contract-unsupported",
                        ShadowMonsterProfilePaths.Schema,
                        "$",
                        "The schema/version contract does not match the supported version-1 data chain."
                    )
                );
                return false;
            }

            schema = new ShadowMonsterProfileSchemaContract(
                schemaId,
                schemaVersion,
                canonicalModelVersion,
                dropTableSchemaVersion,
                unknownFieldPolicy,
                stardew16AdapterVersion,
                stardew16DataShape,
                stardew17Capability,
                stardew17Reason
            );
            return issues.Count == 0;
        }
        catch (JsonException exception)
        {
            issues.Add(
                Issue(
                    "shadow-profile.schema-json-malformed",
                    ShadowMonsterProfilePaths.Schema,
                    "$",
                    exception.Message
                )
            );
            return false;
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or InvalidOperationException
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.schema-shape-invalid",
                    ShadowMonsterProfilePaths.Schema,
                    "$",
                    exception.Message
                )
            );
            return false;
        }
    }

    private static bool TryParseDifficultyProfile(
        ShadowMonsterProfileDocument document,
        string expectedProfileId,
        ShadowMonsterProfileReferenceCatalog references,
        List<ShadowMonsterProfileIssue> issues,
        List<ShadowMonsterProfileDiagnostic> diagnostics,
        out ShadowMonsterDifficultyProfile? profile
    )
    {
        profile = null;
        var issueCountBefore = issues.Count;
        try
        {
            using var json = ParseStrict(document.Json);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.root-invalid",
                        document.RelativePath,
                        "$",
                        "A difficulty profile root must be an object."
                    )
                );
                return false;
            }
            ValidateAllowedProperties(root, AllowedRootProperties, document.RelativePath, "$", issues);

            var valid = true;
            valid &= TryReadRequiredInt(
                root,
                "SchemaVersion",
                1,
                int.MaxValue,
                document.RelativePath,
                "SchemaVersion",
                issues,
                out var schemaVersion
            );
            valid &= TryReadRequiredString(
                root,
                "ProfileId",
                document.RelativePath,
                "ProfileId",
                issues,
                out var profileId
            );
            valid &= TryReadRequiredBoolean(
                root,
                "IsBetaBalance",
                document.RelativePath,
                "IsBetaBalance",
                issues,
                out var isBetaBalance
            );
            if (
                !root.TryGetProperty("Monsters", out var monstersElement)
                || monstersElement.ValueKind != JsonValueKind.Array
            )
            {
                issues.Add(MissingOrInvalid(document.RelativePath, "Monsters"));
                valid = false;
            }

            if (schemaVersion != CurrentSchemaVersion)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.schema-version-unsupported",
                        document.RelativePath,
                        "SchemaVersion",
                        $"SchemaVersion {schemaVersion} is not supported."
                    )
                );
                valid = false;
            }
            if (!ShadowMonsterDifficultyProfileIds.All.ContainsOrdinal(profileId))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.profile-id-unknown",
                        document.RelativePath,
                        "ProfileId",
                        $"ProfileId '{profileId}' is not a frozen config value."
                    )
                );
                valid = false;
            }
            else if (!string.Equals(profileId, expectedProfileId, StringComparison.Ordinal))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.profile-id-file-mismatch",
                        document.RelativePath,
                        "ProfileId",
                        $"ProfileId '{profileId}' does not match file '{expectedProfileId}'."
                    )
                );
                valid = false;
            }

            var monsters = new Dictionary<string, ShadowMonsterProfile>(StringComparer.Ordinal);
            if (monstersElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var element in monstersElement.EnumerateArray())
                {
                    if (
                        TryParseMonster(
                            element,
                            document.RelativePath,
                            $"Monsters[{index}]",
                            references,
                            issues,
                            out var monster
                        )
                        && monster is not null
                        && !monsters.TryAdd(monster.AssetBindingId, monster)
                    )
                    {
                        issues.Add(
                            Issue(
                                "shadow-profile.asset-binding-id-duplicate",
                                document.RelativePath,
                                $"Monsters[{index}].AssetBindingId",
                                $"AssetBindingId '{monster.AssetBindingId}' is duplicated."
                            )
                        );
                    }
                    index++;
                }
            }

            foreach (var bindingId in ShadowMonsterAssetBindingIds.All)
            {
                if (!monsters.ContainsKey(bindingId))
                {
                    issues.Add(
                        Issue(
                            "shadow-profile.monster-missing",
                            document.RelativePath,
                            "Monsters",
                            $"Required monster binding '{bindingId}' is missing."
                        )
                    );
                }
            }
            if (monsters.Count != ShadowMonsterAssetBindingIds.All.Count)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.monster-count-invalid",
                        document.RelativePath,
                        "Monsters",
                        "Each difficulty file must contain exactly the two frozen hostile species."
                    )
                );
            }

            if (!valid || issues.Count != issueCountBefore)
                return false;

            profile = new ShadowMonsterDifficultyProfile(
                schemaVersion,
                profileId,
                isBetaBalance,
                monsters
            );
            if (isBetaBalance)
            {
                diagnostics.Add(
                    new ShadowMonsterProfileDiagnostic(
                        ShadowMonsterProfileDiagnosticSeverity.Warning,
                        "shadow-profile.balance.beta",
                        profileId,
                        string.Empty,
                        "This difficulty profile uses explicit test values and is not balance-approved."
                    )
                );
            }
            return true;
        }
        catch (JsonException exception)
        {
            issues.Add(
                Issue(
                    "shadow-profile.json-malformed",
                    document.RelativePath,
                    "$",
                    exception.Message
                )
            );
            return false;
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or InvalidOperationException
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.shape-invalid",
                    document.RelativePath,
                    "$",
                    exception.Message
                )
            );
            return false;
        }
    }

    private static bool TryParseMonster(
        JsonElement element,
        string filePath,
        string keyPrefix,
        ShadowMonsterProfileReferenceCatalog references,
        List<ShadowMonsterProfileIssue> issues,
        out ShadowMonsterProfile? profile
    )
    {
        profile = null;
        var issueCountBefore = issues.Count;
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(
                Issue(
                    "shadow-profile.monster-invalid",
                    filePath,
                    keyPrefix,
                    "A monster profile must be an object."
                )
            );
            return false;
        }
        ValidateAllowedProperties(element, AllowedMonsterProperties, filePath, keyPrefix, issues);

        var valid = true;
        valid &= TryReadRequiredInt(element, "MaxHealth", 1, MaximumHealth, filePath, Key(keyPrefix, "MaxHealth"), issues, out var maxHealth);
        valid &= TryReadRequiredInt(element, "BaseDamage", 0, MaximumDamage, filePath, Key(keyPrefix, "BaseDamage"), issues, out var baseDamage);
        valid &= TryReadRequiredDouble(element, "MovementSpeed", double.Epsilon, MaximumMovementSpeed, filePath, Key(keyPrefix, "MovementSpeed"), issues, out var movementSpeed);
        valid &= TryReadRequiredInt(element, "Defense", 0, MaximumDefense, filePath, Key(keyPrefix, "Defense"), issues, out var defense);
        valid &= TryReadRequiredDouble(element, "DetectionRadiusTiles", double.Epsilon, MaximumRadiusTiles, filePath, Key(keyPrefix, "DetectionRadiusTiles"), issues, out var detectionRadiusTiles);
        valid &= TryReadRequiredDouble(element, "AttackRangeTiles", double.Epsilon, MaximumRadiusTiles, filePath, Key(keyPrefix, "AttackRangeTiles"), issues, out var attackRangeTiles);
        valid &= TryReadRequiredDouble(element, "AttackIntervalSeconds", 0.05d, MaximumAttackIntervalSeconds, filePath, Key(keyPrefix, "AttackIntervalSeconds"), issues, out var attackIntervalSeconds);
        valid &= TryReadRequiredDouble(element, "NaturalDespawnGameHours", 0.1d, MaximumDespawnGameHours, filePath, Key(keyPrefix, "NaturalDespawnGameHours"), issues, out var naturalDespawnGameHours);
        valid &= TryReadRequiredString(element, "DisplayNameKey", filePath, Key(keyPrefix, "DisplayNameKey"), issues, out var displayNameKey);
        valid &= TryReadRequiredString(element, "WallTraversalMode", filePath, Key(keyPrefix, "WallTraversalMode"), issues, out var wallTraversalMode);
        valid &= TryReadImmunityTags(element, filePath, keyPrefix, issues, out var immunityTags);
        valid &= TryParseDropTable(element, filePath, keyPrefix, issues, out var dropTable);
        valid &= TryReadRequiredInt(element, "SanityReward", 0, MaximumSanityReward, filePath, Key(keyPrefix, "SanityReward"), issues, out var sanityReward);
        valid &= TryReadRequiredString(element, "AssetBindingId", filePath, Key(keyPrefix, "AssetBindingId"), issues, out var assetBindingId);
        valid &= TryReadRequiredString(element, "AnimationProfileId", filePath, Key(keyPrefix, "AnimationProfileId"), issues, out var animationProfileId);
        valid &= TryReadRequiredString(element, "CueSetId", filePath, Key(keyPrefix, "CueSetId"), issues, out var cueSetId);
        valid &= TryReadRequiredString(element, "AttackMotionPolicyId", filePath, Key(keyPrefix, "AttackMotionPolicyId"), issues, out var attackMotionPolicyId);
        valid &= TryReadRequiredString(element, "PostAttackPolicyId", filePath, Key(keyPrefix, "PostAttackPolicyId"), issues, out var postAttackPolicyId);
        valid &= TryReadOptionalInt(element, "ExperienceValue", 0, MaximumExperience, filePath, Key(keyPrefix, "ExperienceValue"), issues, out var experienceValue);
        valid &= TryReadOptionalString(element, "KillCounterId", filePath, Key(keyPrefix, "KillCounterId"), issues, out var killCounterId);

        if (attackRangeTiles > detectionRadiusTiles)
        {
            issues.Add(
                Issue(
                    "shadow-profile.range-order-invalid",
                    filePath,
                    Key(keyPrefix, "AttackRangeTiles"),
                    "Attack range cannot exceed detection radius."
                )
            );
            valid = false;
        }
        if (!IsStableDisplayNameKey(displayNameKey))
        {
            issues.Add(
                Issue(
                    "shadow-profile.display-name-key-invalid",
                    filePath,
                    Key(keyPrefix, "DisplayNameKey"),
                    "DisplayNameKey must be a stable ASCII i18n key, not localized text."
                )
            );
            valid = false;
        }
        if (wallTraversalMode != ShadowMonsterProfileContractIds.DirectThroughTerrain)
        {
            issues.Add(
                Issue(
                    "shadow-profile.wall-traversal-mode-unknown",
                    filePath,
                    Key(keyPrefix, "WallTraversalMode"),
                    $"Wall traversal mode '{wallTraversalMode}' is not supported."
                )
            );
            valid = false;
        }
        if (postAttackPolicyId != ShadowMonsterProfileContractIds.TauntOrDelayPostAttack)
        {
            issues.Add(
                Issue(
                    "shadow-profile.post-attack-policy-unknown",
                    filePath,
                    Key(keyPrefix, "PostAttackPolicyId"),
                    $"Post-attack policy '{postAttackPolicyId}' is not supported."
                )
            );
            valid = false;
        }
        if (killCounterId is not null && !IsStableNamespacedId(killCounterId, "sanity.kill-counter."))
        {
            issues.Add(
                Issue(
                    "shadow-profile.kill-counter-id-invalid",
                    filePath,
                    Key(keyPrefix, "KillCounterId"),
                    "KillCounterId must be a stable namespaced ASCII ID."
                )
            );
            valid = false;
        }

        if (!references.TryGetBinding(assetBindingId, out var binding) || binding is null)
        {
            issues.Add(
                Issue(
                    "shadow-profile.asset-binding-unknown",
                    filePath,
                    Key(keyPrefix, "AssetBindingId"),
                    $"AssetBindingId '{assetBindingId}' does not exist in stage-03 metadata."
                )
            );
            valid = false;
        }
        else
        {
            valid &= ValidateReference(
                animationProfileId,
                binding.AnimationProfileId,
                "shadow-profile.animation-profile-mismatch",
                filePath,
                Key(keyPrefix, "AnimationProfileId"),
                issues
            );
            valid &= ValidateReference(
                cueSetId,
                binding.CueSetId,
                "shadow-profile.cue-set-mismatch",
                filePath,
                Key(keyPrefix, "CueSetId"),
                issues
            );
            valid &= ValidateReference(
                attackMotionPolicyId,
                binding.AttackMotionPolicyId,
                "shadow-profile.attack-motion-policy-mismatch",
                filePath,
                Key(keyPrefix, "AttackMotionPolicyId"),
                issues
            );
        }

        valid &= TryReadGameVersionOverrides(
            element,
            filePath,
            keyPrefix,
            maxHealth,
            baseDamage,
            movementSpeed,
            defense,
            detectionRadiusTiles,
            attackRangeTiles,
            attackIntervalSeconds,
            naturalDespawnGameHours,
            experienceValue,
            wallTraversalMode,
            issues,
            out var overrides
        );

        if (!valid || issues.Count != issueCountBefore || dropTable is null)
            return false;

        profile = new ShadowMonsterProfile(
            maxHealth,
            baseDamage,
            movementSpeed,
            defense,
            detectionRadiusTiles,
            attackRangeTiles,
            attackIntervalSeconds,
            naturalDespawnGameHours,
            displayNameKey,
            wallTraversalMode,
            immunityTags,
            dropTable,
            sanityReward,
            assetBindingId,
            animationProfileId,
            cueSetId,
            attackMotionPolicyId,
            postAttackPolicyId,
            experienceValue,
            killCounterId,
            overrides
        );
        return true;
    }

    private static bool TryParseDropTable(
        JsonElement owner,
        string filePath,
        string keyPrefix,
        List<ShadowMonsterProfileIssue> issues,
        out ShadowMonsterDropTable? dropTable
    )
    {
        dropTable = null;
        var key = Key(keyPrefix, "DropTable");
        if (
            !owner.TryGetProperty("DropTable", out var element)
            || element.ValueKind != JsonValueKind.Object
        )
        {
            issues.Add(MissingOrInvalid(filePath, key));
            return false;
        }
        ValidateAllowedProperties(element, AllowedDropTableProperties, filePath, key, issues);

        var valid = true;
        valid &= TryReadRequiredInt(element, "SchemaVersion", 1, int.MaxValue, filePath, Key(key, "SchemaVersion"), issues, out var schemaVersion);
        valid &= TryReadRequiredString(element, "DropTableId", filePath, Key(key, "DropTableId"), issues, out var dropTableId);
        valid &= TryReadRequiredString(element, "ItemSemanticId", filePath, Key(key, "ItemSemanticId"), issues, out var itemSemanticId);
        valid &= TryReadRequiredInt(element, "GuaranteedQuantity", 0, 999, filePath, Key(key, "GuaranteedQuantity"), issues, out var guaranteedQuantity);
        valid &= TryReadRequiredInt(element, "BonusQuantity", 0, 999, filePath, Key(key, "BonusQuantity"), issues, out var bonusQuantity);
        valid &= TryReadRequiredDouble(element, "BonusChance", 0d, 1d, filePath, Key(key, "BonusChance"), issues, out var bonusChance);

        if (
            schemaVersion != CurrentDropTableSchemaVersion
            || dropTableId != ShadowMonsterProfileContractIds.VoidEssenceDropTable
            || itemSemanticId != ShadowMonsterProfileContractIds.VoidEssenceSemanticItem
            || guaranteedQuantity != 1
            || bonusQuantity != 1
            || Math.Abs(bonusChance - 0.5d) > 0.0000001d
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.drop-table-contract-invalid",
                    filePath,
                    key,
                    "DropTable must express guaranteed one Void Essence plus an independent 50% bonus one."
                )
            );
            valid = false;
        }
        if (!valid)
            return false;

        dropTable = new ShadowMonsterDropTable(
            schemaVersion,
            dropTableId,
            itemSemanticId,
            guaranteedQuantity,
            bonusQuantity,
            bonusChance
        );
        return true;
    }

    private static bool TryReadImmunityTags(
        JsonElement owner,
        string filePath,
        string keyPrefix,
        List<ShadowMonsterProfileIssue> issues,
        out IReadOnlyList<string> tags
    )
    {
        var parsed = new List<string>();
        tags = parsed;
        var key = Key(keyPrefix, "ImmunityTags");
        if (
            !owner.TryGetProperty("ImmunityTags", out var element)
            || element.ValueKind != JsonValueKind.Array
        )
        {
            issues.Add(MissingOrInvalid(filePath, key));
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.immunity-tag-invalid",
                        filePath,
                        key,
                        "Immunity tags must be strings."
                    )
                );
                valid = false;
                continue;
            }
            var tag = item.GetString() ?? string.Empty;
            if (
                tag != ShadowMonsterProfileContractIds.KnockbackImmunity
                && tag != ShadowMonsterProfileContractIds.FrozenImmunity
            )
            {
                issues.Add(
                    Issue(
                        "shadow-profile.immunity-tag-unknown",
                        filePath,
                        key,
                        $"Immunity tag '{tag}' is not supported."
                    )
                );
                valid = false;
                continue;
            }
            if (!seen.Add(tag))
            {
                issues.Add(
                    Issue(
                        "shadow-profile.immunity-tag-duplicate",
                        filePath,
                        key,
                        $"Immunity tag '{tag}' is duplicated."
                    )
                );
                valid = false;
                continue;
            }
            parsed.Add(tag);
        }

        if (
            !seen.Contains(ShadowMonsterProfileContractIds.KnockbackImmunity)
            || !seen.Contains(ShadowMonsterProfileContractIds.FrozenImmunity)
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.immunity-required-tag-missing",
                    filePath,
                    key,
                    "Both Knockback and Frozen immunity must be explicit."
                )
            );
            valid = false;
        }
        tags = parsed.AsReadOnly();
        return valid;
    }

    private static bool TryReadGameVersionOverrides(
        JsonElement owner,
        string filePath,
        string keyPrefix,
        int baseMaxHealth,
        int baseDamage,
        double baseMovementSpeed,
        int baseDefense,
        double baseDetectionRadius,
        double baseAttackRange,
        double baseAttackInterval,
        double baseDespawnHours,
        int baseExperience,
        string baseWallTraversalMode,
        List<ShadowMonsterProfileIssue> issues,
        out IReadOnlyDictionary<string, ShadowMonsterGameVersionOverride> overrides
    )
    {
        var parsed = new Dictionary<string, ShadowMonsterGameVersionOverride>(StringComparer.Ordinal);
        overrides = new ReadOnlyDictionary<string, ShadowMonsterGameVersionOverride>(parsed);
        if (!owner.TryGetProperty("GameVersionOverrides", out var array))
            return true;
        var rootKey = Key(keyPrefix, "GameVersionOverrides");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 4)
        {
            issues.Add(
                Issue(
                    "shadow-profile.game-version-overrides-invalid",
                    filePath,
                    rootKey,
                    "GameVersionOverrides must be a bounded array."
                )
            );
            return false;
        }

        var valid = true;
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var key = $"{rootKey}[{index}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.game-version-override-invalid",
                        filePath,
                        key,
                        "A game-version override must be an object."
                    )
                );
                valid = false;
                index++;
                continue;
            }
            ValidateAllowedProperties(element, AllowedOverrideProperties, filePath, key, issues);
            var propertyCount = CountProperties(element);
            var itemValid = TryReadRequiredString(element, "GameVersionId", filePath, Key(key, "GameVersionId"), issues, out var versionId);
            if (
                versionId != ShadowMonsterProfileContractIds.Stardew16Override
                && versionId != ShadowMonsterProfileContractIds.Stardew17Override
            )
            {
                issues.Add(
                    Issue(
                        "shadow-profile.game-version-id-unknown",
                        filePath,
                        Key(key, "GameVersionId"),
                        $"GameVersionId '{versionId}' is not supported by schema version 1."
                    )
                );
                itemValid = false;
            }
            if (propertyCount < 2)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.game-version-override-empty",
                        filePath,
                        key,
                        "An override must replace at least one canonical field."
                    )
                );
                itemValid = false;
            }

            itemValid &= TryReadNullableInt(element, "MaxHealth", 1, MaximumHealth, filePath, Key(key, "MaxHealth"), issues, out var maxHealth);
            itemValid &= TryReadNullableInt(element, "BaseDamage", 0, MaximumDamage, filePath, Key(key, "BaseDamage"), issues, out var overrideDamage);
            itemValid &= TryReadNullableDouble(element, "MovementSpeed", double.Epsilon, MaximumMovementSpeed, filePath, Key(key, "MovementSpeed"), issues, out var movementSpeed);
            itemValid &= TryReadNullableInt(element, "Defense", 0, MaximumDefense, filePath, Key(key, "Defense"), issues, out var defense);
            itemValid &= TryReadNullableDouble(element, "DetectionRadiusTiles", double.Epsilon, MaximumRadiusTiles, filePath, Key(key, "DetectionRadiusTiles"), issues, out var detectionRadius);
            itemValid &= TryReadNullableDouble(element, "AttackRangeTiles", double.Epsilon, MaximumRadiusTiles, filePath, Key(key, "AttackRangeTiles"), issues, out var attackRange);
            itemValid &= TryReadNullableDouble(element, "AttackIntervalSeconds", 0.05d, MaximumAttackIntervalSeconds, filePath, Key(key, "AttackIntervalSeconds"), issues, out var attackInterval);
            itemValid &= TryReadNullableDouble(element, "NaturalDespawnGameHours", 0.1d, MaximumDespawnGameHours, filePath, Key(key, "NaturalDespawnGameHours"), issues, out var despawnHours);
            itemValid &= TryReadNullableInt(element, "ExperienceValue", 0, MaximumExperience, filePath, Key(key, "ExperienceValue"), issues, out var experience);
            itemValid &= TryReadNullableString(element, "WallTraversalMode", filePath, Key(key, "WallTraversalMode"), issues, out var wallMode);

            wallMode ??= baseWallTraversalMode;
            if (wallMode != ShadowMonsterProfileContractIds.DirectThroughTerrain)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.wall-traversal-mode-unknown",
                        filePath,
                        Key(key, "WallTraversalMode"),
                        $"Wall traversal mode '{wallMode}' is not supported."
                    )
                );
                itemValid = false;
            }
            var effectiveDetection = detectionRadius ?? baseDetectionRadius;
            var effectiveAttack = attackRange ?? baseAttackRange;
            if (effectiveAttack > effectiveDetection)
            {
                issues.Add(
                    Issue(
                        "shadow-profile.range-order-invalid",
                        filePath,
                        Key(key, "AttackRangeTiles"),
                        "The effective attack range cannot exceed the effective detection radius."
                    )
                );
                itemValid = false;
            }

            if (itemValid)
            {
                var item = new ShadowMonsterGameVersionOverride(
                    versionId,
                    maxHealth,
                    overrideDamage,
                    movementSpeed,
                    defense,
                    detectionRadius,
                    attackRange,
                    attackInterval,
                    despawnHours,
                    experience,
                    wallMode == baseWallTraversalMode && !element.TryGetProperty("WallTraversalMode", out _)
                        ? null
                        : wallMode
                );
                if (!parsed.TryAdd(versionId, item))
                {
                    issues.Add(
                        Issue(
                            "shadow-profile.game-version-id-duplicate",
                            filePath,
                            Key(key, "GameVersionId"),
                            $"GameVersionId '{versionId}' is duplicated."
                        )
                    );
                    itemValid = false;
                }
            }
            valid &= itemValid;
            index++;
        }

        overrides = new ReadOnlyDictionary<string, ShadowMonsterGameVersionOverride>(parsed);
        _ = baseMaxHealth;
        _ = baseDamage;
        _ = baseMovementSpeed;
        _ = baseDefense;
        _ = baseAttackInterval;
        _ = baseDespawnHours;
        _ = baseExperience;
        return valid;
    }

    private static bool ValidateReference(
        string actual,
        string expected,
        string code,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues
    )
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return true;
        issues.Add(
            Issue(
                code,
                filePath,
                key,
                $"Reference '{actual}' does not match stage-03 binding value '{expected}'."
            )
        );
        return false;
    }

    private static bool TryReadRequiredBoolean(
        JsonElement owner,
        string propertyName,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out bool value
    )
    {
        value = false;
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            issues.Add(Missing(filePath, key));
            return false;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            issues.Add(InvalidType(filePath, key, "boolean"));
            return false;
        }
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement owner,
        string propertyName,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out string value
    )
    {
        value = string.Empty;
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            issues.Add(Missing(filePath, key));
            return false;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(InvalidType(filePath, key, "string"));
            return false;
        }
        value = element.GetString() ?? string.Empty;
        if (
            value.Length is < 1 or > 192
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
        )
        {
            issues.Add(
                Issue(
                    "shadow-profile.string-invalid",
                    filePath,
                    key,
                    "Strings must be non-empty, trimmed, and at most 192 characters."
                )
            );
            return false;
        }
        return true;
    }

    private static bool TryReadOptionalString(
        JsonElement owner,
        string propertyName,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out string? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out _))
            return true;
        if (!TryReadRequiredString(owner, propertyName, filePath, key, issues, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryReadNullableString(
        JsonElement owner,
        string propertyName,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out string? value
    )
    {
        return TryReadOptionalString(owner, propertyName, filePath, key, issues, out value);
    }

    private static bool TryReadRequiredInt(
        JsonElement owner,
        string propertyName,
        int minimum,
        int maximum,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out int value
    )
    {
        value = 0;
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            issues.Add(Missing(filePath, key));
            return false;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            issues.Add(InvalidType(filePath, key, "32-bit integer"));
            return false;
        }
        if (value < minimum || value > maximum)
        {
            issues.Add(OutOfRange(filePath, key, minimum, maximum));
            return false;
        }
        return true;
    }

    private static bool TryReadOptionalInt(
        JsonElement owner,
        string propertyName,
        int minimum,
        int maximum,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out int value
    )
    {
        value = 0;
        return !owner.TryGetProperty(propertyName, out _)
            || TryReadRequiredInt(owner, propertyName, minimum, maximum, filePath, key, issues, out value);
    }

    private static bool TryReadNullableInt(
        JsonElement owner,
        string propertyName,
        int minimum,
        int maximum,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out int? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out _))
            return true;
        if (!TryReadRequiredInt(owner, propertyName, minimum, maximum, filePath, key, issues, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryReadRequiredDouble(
        JsonElement owner,
        string propertyName,
        double minimum,
        double maximum,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out double value
    )
    {
        value = 0d;
        if (!owner.TryGetProperty(propertyName, out var element))
        {
            issues.Add(Missing(filePath, key));
            return false;
        }
        if (element.ValueKind != JsonValueKind.Number)
        {
            issues.Add(InvalidType(filePath, key, "finite number"));
            return false;
        }
        try
        {
            value = element.GetDouble();
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            issues.Add(
                Issue(
                    "shadow-profile.value-non-finite",
                    filePath,
                    key,
                    "Numeric values must be finite."
                )
            );
            return false;
        }
        if (!double.IsFinite(value))
        {
            issues.Add(
                Issue(
                    "shadow-profile.value-non-finite",
                    filePath,
                    key,
                    "Numeric values must be finite."
                )
            );
            return false;
        }
        if (value < minimum || value > maximum)
        {
            issues.Add(OutOfRange(filePath, key, minimum, maximum));
            return false;
        }
        return true;
    }

    private static bool TryReadNullableDouble(
        JsonElement owner,
        string propertyName,
        double minimum,
        double maximum,
        string filePath,
        string key,
        List<ShadowMonsterProfileIssue> issues,
        out double? value
    )
    {
        value = null;
        if (!owner.TryGetProperty(propertyName, out _))
            return true;
        if (!TryReadRequiredDouble(owner, propertyName, minimum, maximum, filePath, key, issues, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static void ValidateAllowedProperties(
        JsonElement element,
        HashSet<string> allowed,
        string filePath,
        string keyPrefix,
        List<ShadowMonsterProfileIssue> issues
    )
    {
        foreach (var property in element.EnumerateObject())
        {
            if (allowed.Contains(property.Name))
                continue;
            issues.Add(
                Issue(
                    "shadow-profile.unknown-field",
                    filePath,
                    Key(keyPrefix, property.Name),
                    $"Field '{property.Name}' is unknown for schema version 1."
                )
            );
        }
    }

    private static bool IsStableDisplayNameKey(string value)
    {
        return value.StartsWith("sanity.shadow-monster.", StringComparison.Ordinal)
            && value.EndsWith(".name", StringComparison.Ordinal)
            && IsLowerAsciiId(value);
    }

    private static bool IsStableNamespacedId(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && value.Length > prefix.Length
            && IsLowerAsciiId(value);
    }

    private static bool IsLowerAsciiId(string value)
    {
        foreach (var character in value)
        {
            if (
                character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is '.' or '-'
            )
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
            count++;
        return count;
    }

    private static JsonDocument ParseStrict(string json)
    {
        return JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static string Key(string prefix, string property)
    {
        return prefix == "$" ? property : prefix + "." + property;
    }

    private static ShadowMonsterProfileIssue Missing(string filePath, string key)
    {
        return Issue(
            "shadow-profile.required-field-missing",
            filePath,
            key,
            $"Required field '{key}' is missing."
        );
    }

    private static ShadowMonsterProfileIssue MissingOrInvalid(string filePath, string key)
    {
        return Issue(
            "shadow-profile.required-field-missing-or-invalid",
            filePath,
            key,
            $"Required field '{key}' is missing or has the wrong shape."
        );
    }

    private static ShadowMonsterProfileIssue InvalidType(
        string filePath,
        string key,
        string expected
    )
    {
        return Issue(
            "shadow-profile.value-type-invalid",
            filePath,
            key,
            $"Field '{key}' must be a {expected}."
        );
    }

    private static ShadowMonsterProfileIssue OutOfRange(
        string filePath,
        string key,
        double minimum,
        double maximum
    )
    {
        return Issue(
            "shadow-profile.value-out-of-range",
            filePath,
            key,
            $"Field '{key}' must be in [{minimum}, {maximum}]."
        );
    }

    private static ShadowMonsterProfileIssue Issue(
        string code,
        string filePath,
        string key,
        string reason
    )
    {
        return new ShadowMonsterProfileIssue(code, filePath, key, reason);
    }
}

internal static class ShadowMonsterProfileCollectionExtensions
{
    internal static bool ContainsOrdinal(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
