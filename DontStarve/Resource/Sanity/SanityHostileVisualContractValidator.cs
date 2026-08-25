using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

public sealed record SanityHostileVisualContractIssue(string Code, string Reason);

public sealed class SanityHostileVisualContractValidationResult
{
    internal SanityHostileVisualContractValidationResult(
        IReadOnlyList<SanityHostileVisualContractIssue> issues
    )
    {
        Issues = issues;
    }

    public IReadOnlyList<SanityHostileVisualContractIssue> Issues { get; }

    public bool Success => Issues.Count == 0;
}

/// <summary>
/// Validates the hostile visual/collision resource contract without loading textures or creating gameplay state.
/// Stage 06 may reuse this fail-closed seam before caching resources; it intentionally has no SMAPI dependency.
/// </summary>
public static class SanityHostileVisualContractValidator
{
    private const int SupportedSchemaVersion = 1;
    private const int SupportedContractVersion = 1;
    private const string AttackMotionPolicyId = "sanity.attack-motion.one-tile-v1";

    private static readonly HostileProfileExpectation[] ExpectedProfiles =
    {
        new(
            "sanity.binding.creeper-fear",
            "sanity.asset.creeper-fear.sprite",
            "sanity.animation.creeper-fear.profile",
            "sanity.animation.creeper-fear",
            "sanity.cue.creeper-fear",
            64,
            96,
            13
        ),
        new(
            "sanity.binding.terrorbeak",
            "sanity.asset.terrorbeak.sprite",
            "sanity.animation.terrorbeak.profile",
            "sanity.animation.terrorbeak",
            "sanity.cue.terrorbeak",
            48,
            64,
            13
        ),
    };

    private static readonly HashSet<string> ForbiddenResourceFieldNames = new(
        new[]
        {
            "hp",
            "damage",
            "speed",
            "defense",
            "drop",
            "reward",
            "sanitythreshold",
            "ownercap",
            "spawninterval",
            "ai",
            "aggro",
            "lasthit",
            "gameplayprofile",
        },
        StringComparer.OrdinalIgnoreCase
    );

    public static SanityHostileVisualContractValidationResult Validate(
        string animationMetadataJson,
        string resourceBindingsJson
    )
    {
        var issues = new List<SanityHostileVisualContractIssue>();
        try
        {
            using var animationDocument = ParseStrict(animationMetadataJson);
            using var bindingDocument = ParseStrict(resourceBindingsJson);
            ValidateRoot(animationDocument.RootElement, "hostile.animation", issues);
            ValidateRoot(bindingDocument.RootElement, "hostile.binding", issues);
            if (issues.Count == 0)
            {
                ValidateForbiddenFields(animationDocument.RootElement, "hostile.animation", issues);
                ValidateForbiddenFields(bindingDocument.RootElement, "hostile.binding", issues);
                ValidateProfiles(animationDocument.RootElement, issues);
                ValidateBindings(bindingDocument.RootElement, issues);
            }
        }
        catch (JsonException exception)
        {
            issues.Add(new("hostile.metadata.invalid-json", exception.Message));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or KeyNotFoundException
            or FormatException
            or OverflowException
        )
        {
            issues.Add(new("hostile.metadata.invalid-shape", exception.Message));
        }

        return new SanityHostileVisualContractValidationResult(issues);
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

    private static void ValidateRoot(
        JsonElement root,
        string codePrefix,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new($"{codePrefix}.invalid-root", "The metadata root must be an object."));
            return;
        }

        if (
            !root.TryGetProperty("SchemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || schemaVersion.GetInt32() != SupportedSchemaVersion
        )
        {
            issues.Add(new($"{codePrefix}.unsupported-schema", "SchemaVersion must be 1."));
        }
        if (
            !root.TryGetProperty("ContractVersion", out var contractVersion)
            || contractVersion.ValueKind != JsonValueKind.Number
            || contractVersion.GetInt32() != SupportedContractVersion
        )
        {
            issues.Add(new($"{codePrefix}.unsupported-contract", "ContractVersion must be 1."));
        }
    }

    private static void ValidateForbiddenFields(
        JsonElement value,
        string codePrefix,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);
                if (ForbiddenResourceFieldNames.Contains(normalized))
                {
                    issues.Add(
                        new(
                            $"{codePrefix}.gameplay-field-forbidden",
                            $"Resource metadata must not own gameplay field '{property.Name}'."
                        )
                    );
                }
                ValidateForbiddenFields(property.Value, codePrefix, issues);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateForbiddenFields(item, codePrefix, issues);
        }
    }

    private static void ValidateProfiles(
        JsonElement root,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (
            !root.TryGetProperty("AnimationProfiles", out var profilesElement)
            || profilesElement.ValueKind != JsonValueKind.Array
        )
        {
            issues.Add(new("hostile.animation.profiles-missing", "AnimationProfiles must be an array."));
            return;
        }

        var profiles = IndexUnique(
            profilesElement,
            "AnimationProfileId",
            "hostile.animation.duplicate-profile",
            issues
        );
        foreach (var expectation in ExpectedProfiles)
        {
            if (!profiles.TryGetValue(expectation.AnimationProfileId, out var profile))
            {
                issues.Add(
                    new(
                        "hostile.animation.profile-missing",
                        $"Missing hostile profile '{expectation.AnimationProfileId}'."
                    )
                );
                continue;
            }
            ValidateProfile(profile, expectation, issues);
        }
    }

    private static void ValidateProfile(
        JsonElement profile,
        HostileProfileExpectation expectation,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (
            profile.GetProperty("TextureSlotId").GetString() != expectation.SlotId
            || profile.GetProperty("FrameWidth").GetInt32() != expectation.FrameWidth
            || profile.GetProperty("FrameHeight").GetInt32() != expectation.FrameHeight
            || profile.GetProperty("SheetRows").GetInt32() != expectation.SheetRows
            || profile.GetProperty("DirectionMode").GetString() != "FourWayRows"
            || profile.GetProperty("OwnerLocalOnly").GetBoolean()
            || profile.GetProperty("ContractVersion").GetInt32() != SupportedContractVersion
        )
        {
            issues.Add(
                new(
                    "hostile.animation.profile-contract-mismatch",
                    $"Profile '{expectation.AnimationProfileId}' changed its frozen grid or resource identity."
                )
            );
        }

        var actorOrigin = profile.GetProperty("ActorOriginSourcePx");
        ValidatePointInsideFrame(
            actorOrigin,
            expectation.FrameWidth,
            expectation.FrameHeight,
            "hostile.animation.actor-origin-out-of-range",
            issues
        );
        ValidateCollision(profile.GetProperty("Collision"), issues);

        var expectedAnimationIds = new HashSet<string>(
            new[] { "move", "attack", "death", "spawn", "idle", "taunt" }
                .Select(state => $"{expectation.AnimationIdPrefix}.{state}"),
            StringComparer.Ordinal
        );
        var states = IndexUnique(
            profile.GetProperty("States"),
            "AnimationId",
            "hostile.animation.duplicate-state",
            issues
        );
        if (!expectedAnimationIds.SetEquals(states.Keys))
        {
            issues.Add(
                new(
                    "hostile.animation.state-set-mismatch",
                    $"Profile '{expectation.AnimationProfileId}' must expose exactly six stable state IDs."
                )
            );
        }

        var occupiedRows = new HashSet<int>();
        foreach (var pair in states)
        {
            var state = pair.Value;
            var frameCount = state.GetProperty("FrameCount").GetInt32();
            var duration = state.GetProperty("FrameDurationMs").GetInt32();
            var row = state.GetProperty("Row").GetInt32();
            if (frameCount <= 0 || duration <= 0)
            {
                issues.Add(
                    new(
                        "hostile.animation.invalid-frame-contract",
                        $"State '{pair.Key}' must have positive frame count and duration."
                    )
                );
            }
            ValidatePointInsideFrame(
                state.GetProperty("PivotSourcePx"),
                expectation.FrameWidth,
                expectation.FrameHeight,
                "hostile.animation.pivot-out-of-range",
                issues
            );
            if (state.GetProperty("DrawScale").GetDouble() <= 0)
            {
                issues.Add(new("hostile.animation.invalid-draw-scale", $"State '{pair.Key}' has a non-positive scale."));
            }

            var directionRows = state.GetProperty("DirectionRows");
            if (directionRows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"State '{pair.Key}' DirectionRows must be an array.");
            if (directionRows.GetArrayLength() == 0)
            {
                AddRow(row, pair.Key, occupiedRows, expectation.SheetRows, issues);
            }
            else
            {
                var directions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var directionRow in directionRows.EnumerateArray())
                {
                    var direction = directionRow.GetProperty("Direction").GetString() ?? string.Empty;
                    if (!directions.Add(direction))
                    {
                        issues.Add(new("hostile.animation.duplicate-direction", $"State '{pair.Key}' repeats '{direction}'."));
                    }
                    AddRow(directionRow.GetProperty("Row").GetInt32(), pair.Key, occupiedRows, expectation.SheetRows, issues);
                }
                if (
                    // DIAG-20260806: taunt 允许仅左右两方向（恐怖尖喙新图 Row 11=右、Row 12=左）。
                    !pair.Key.EndsWith(".taunt", StringComparison.Ordinal)
                    && !directions.SetEquals(new[] { "Down", "Right", "Up", "Left" })
                )
                {
                    issues.Add(new("hostile.animation.direction-set-mismatch", $"State '{pair.Key}' is not four-way."));
                }
            }

            var hitFrames = state.GetProperty("HitFrames").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (hitFrames.Any(frame => frame < 1 || frame > frameCount))
            {
                issues.Add(new("hostile.animation.hit-frame-out-of-range", $"State '{pair.Key}' has an invalid hit frame."));
            }
            var isAttack = pair.Key.EndsWith(".attack", StringComparison.Ordinal);
            if (isAttack ? !hitFrames.SequenceEqual(new[] { 3, 4 }) : hitFrames.Length != 0)
            {
                issues.Add(
                    new(
                        "hostile.animation.hit-frame-contract-mismatch",
                        $"State '{pair.Key}' must enable the attack box only on frames 3 and 4."
                    )
                );
            }
        }

        if (!occupiedRows.SetEquals(Enumerable.Range(0, expectation.SheetRows)))
        {
            issues.Add(
                new(
                    "hostile.animation.sheet-row-coverage-mismatch",
                    $"Profile '{expectation.AnimationProfileId}' must cover rows 0 through {expectation.SheetRows - 1} exactly once."
                )
            );
        }
    }

    private static void ValidateCollision(
        JsonElement collision,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (collision.GetProperty("CoordinateSpace").GetString() != "ActorOriginRelativeSourcePx")
        {
            issues.Add(new("hostile.collision.coordinate-space-mismatch", "Collision boxes must use actor-origin-relative source pixels."));
        }
        ValidateRectangle(collision.GetProperty("HurtBoxSourcePx"), "hurt", issues);
        ValidateRectangle(collision.GetProperty("AttackBoxSourcePx"), "attack", issues);
        var activeFrames = collision.GetProperty("AttackActiveFrames")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        if (!activeFrames.SequenceEqual(new[] { 3, 4 }))
        {
            issues.Add(new("hostile.collision.active-frame-mismatch", "The attack box may be active only on frames 3 and 4."));
        }
    }

    private static void ValidateRectangle(
        JsonElement rectangle,
        string name,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (rectangle.GetProperty("Width").GetInt32() <= 0 || rectangle.GetProperty("Height").GetInt32() <= 0)
        {
            issues.Add(new("hostile.collision.invalid-size", $"The {name} box must have positive width and height."));
        }
    }

    private static void ValidateBindings(
        JsonElement root,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        var policies = IndexUnique(
            root.GetProperty("AttackMotionPolicies"),
            "AttackMotionPolicyId",
            "hostile.binding.duplicate-policy",
            issues
        );
        foreach (var policy in policies.Values)
            ValidatePolicy(policy, issues);

        var bindings = IndexUnique(
            root.GetProperty("Bindings"),
            "AssetBindingId",
            "hostile.binding.duplicate-binding",
            issues
        );
        foreach (var binding in bindings.Values)
        {
            var policyId = binding.GetProperty("AttackMotionPolicyId").GetString() ?? string.Empty;
            if (!policies.ContainsKey(policyId))
            {
                issues.Add(
                    new(
                        "hostile.binding.unknown-policy",
                        $"Binding '{binding.GetProperty("AssetBindingId").GetString()}' references unknown policy '{policyId}'."
                    )
                );
            }
        }

        foreach (var expectation in ExpectedProfiles)
        {
            if (!bindings.TryGetValue(expectation.AssetBindingId, out var binding))
            {
                issues.Add(new("hostile.binding.missing", $"Missing binding '{expectation.AssetBindingId}'."));
                continue;
            }
            if (
                binding.GetProperty("SlotId").GetString() != expectation.SlotId
                || binding.GetProperty("AnimationProfileId").GetString() != expectation.AnimationProfileId
                || binding.GetProperty("CueSetId").GetString() != expectation.CueSetId
                || binding.GetProperty("AttackMotionPolicyId").GetString() != AttackMotionPolicyId
                || binding.GetProperty("ContractVersion").GetInt32() != SupportedContractVersion
            )
            {
                issues.Add(
                    new(
                        "hostile.binding.contract-mismatch",
                        $"Binding '{expectation.AssetBindingId}' changed its frozen resource IDs."
                    )
                );
            }
        }
    }

    private static void ValidatePolicy(
        JsonElement policy,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        var advances = policy.GetProperty("FrameAdvanceTiles")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        var total = policy.GetProperty("TotalAdvanceTiles").GetDouble();
        if (
            policy.GetProperty("ContractVersion").GetInt32() != SupportedContractVersion
            || advances.Length != 4
            || advances.Any(value => !double.IsFinite(value) || value < 0)
            || !double.IsFinite(total)
            || total <= 0
            || Math.Abs(advances.Sum() - total) > 0.000001
            || !policy.GetProperty("ResetAfterAnimation").GetBoolean()
        )
        {
            issues.Add(new("hostile.binding.invalid-policy", "Attack motion policy is incomplete or internally inconsistent."));
        }
    }

    private static Dictionary<string, JsonElement> IndexUnique(
        JsonElement array,
        string idProperty,
        string duplicateCode,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{idProperty} container must be an array.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty(idProperty).GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"{idProperty} must be a non-empty string.");
            if (!values.TryAdd(id, item))
                issues.Add(new(duplicateCode, $"Duplicate {idProperty} '{id}'."));
        }
        return values;
    }

    private static void AddRow(
        int row,
        string animationId,
        HashSet<int> occupiedRows,
        int sheetRows,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        if (row < 0 || row >= sheetRows)
        {
            issues.Add(new("hostile.animation.row-out-of-range", $"State '{animationId}' points outside rows 0 through {sheetRows - 1}."));
            return;
        }
        if (!occupiedRows.Add(row))
            issues.Add(new("hostile.animation.duplicate-row", $"Sheet row {row} is assigned more than once."));
    }

    private static void ValidatePointInsideFrame(
        JsonElement point,
        int frameWidth,
        int frameHeight,
        string code,
        List<SanityHostileVisualContractIssue> issues
    )
    {
        var x = point.GetProperty("X").GetInt32();
        var y = point.GetProperty("Y").GetInt32();
        if (x < 0 || y < 0 || x >= frameWidth || y >= frameHeight)
            issues.Add(new(code, $"Point ({x},{y}) is outside the {frameWidth}x{frameHeight} source frame."));
    }

    private sealed record HostileProfileExpectation(
        string AssetBindingId,
        string SlotId,
        string AnimationProfileId,
        string AnimationIdPrefix,
        string CueSetId,
        int FrameWidth,
        int FrameHeight,
        int SheetRows
    );
}
