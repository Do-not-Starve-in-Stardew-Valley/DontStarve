#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Common, gameplay-readable semantics extracted from the already validated stage-03 animation,
/// collision, binding, and attack-motion metadata. It owns no texture and performs no I/O.
/// </summary>
internal sealed class SanityHostileAttackMetadataDefinition
{
    internal SanityHostileAttackMetadataDefinition(
        string assetBindingId,
        string animationProfileId,
        string attackMotionPolicyId,
        SanityHostileAnimationStateDefinition idle,
        SanityHostileAnimationStateDefinition chase,
        SanityHostileAnimationStateDefinition spawn,
        SanityHostileAnimationStateDefinition taunt,
        SanityHostileAnimationStateDefinition attack,
        SanityHostileAnimationStateDefinition? hitResponseVisual,
        SanityHostileCollisionDefinition collision,
        SanityHostileAttackMotionDefinition motion
    )
    {
        AssetBindingId = assetBindingId;
        AnimationProfileId = animationProfileId;
        AttackMotionPolicyId = attackMotionPolicyId;
        Idle = idle;
        Chase = chase;
        Spawn = spawn;
        Taunt = taunt;
        Attack = attack;
        HitResponseVisual = hitResponseVisual;
        Collision = collision;
        Motion = motion;
    }

    internal string AssetBindingId { get; }
    internal string AnimationProfileId { get; }
    internal string AttackMotionPolicyId { get; }
    internal SanityHostileAnimationStateDefinition Idle { get; }
    internal SanityHostileAnimationStateDefinition Chase { get; }
    internal SanityHostileAnimationStateDefinition Spawn { get; }
    internal SanityHostileAnimationStateDefinition Taunt { get; }
    internal SanityHostileAnimationStateDefinition Attack { get; }
    /// <summary>
    /// Optional visual-only transition frames. Runtime hit/death branching never depends on the
    /// animation ID or row name, and remains available when this metadata is absent.
    /// </summary>
    internal SanityHostileAnimationStateDefinition? HitResponseVisual { get; }
    internal SanityHostileCollisionDefinition Collision { get; }
    internal SanityHostileAttackMotionDefinition Motion { get; }
}

internal sealed class SanityHostileAnimationStateDefinition
{
    internal SanityHostileAnimationStateDefinition(
        string animationId,
        int row,
        int frameCount,
        int frameDurationMilliseconds,
        SanityResourcePoint pivotSourcePx,
        double drawScale,
        IReadOnlyList<int> hitFrames,
        IReadOnlyDictionary<string, int> directionRows
    )
    {
        AnimationId = animationId;
        Row = row;
        FrameCount = frameCount;
        FrameDurationMilliseconds = frameDurationMilliseconds;
        PivotSourcePx = pivotSourcePx;
        DrawScale = drawScale;
        HitFrames = Copy(hitFrames);
        DirectionRows = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(directionRows, StringComparer.Ordinal)
        );
    }

    internal string AnimationId { get; }
    internal int Row { get; }
    internal int FrameCount { get; }
    internal int FrameDurationMilliseconds { get; }
    internal SanityResourcePoint PivotSourcePx { get; }
    internal double DrawScale { get; }
    internal IReadOnlyList<int> HitFrames { get; }
    internal IReadOnlyDictionary<string, int> DirectionRows { get; }

    internal bool TryGetDirectionRow(string direction, out int row)
    {
        return DirectionRows.TryGetValue(direction, out row);
    }

    private static IReadOnlyList<int> Copy(IReadOnlyList<int> values)
    {
        var copy = new int[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];
        return Array.AsReadOnly(copy);
    }
}

internal sealed class SanityHostileCollisionDefinition
{
    internal SanityHostileCollisionDefinition(
        string coordinateSpace,
        SanityResourcePoint actorOriginSourcePx,
        SanityResourceRectangle hurtBoxSourcePx,
        SanityResourceRectangle attackBoxSourcePx,
        IReadOnlyList<int> attackActiveFrames
    )
    {
        CoordinateSpace = coordinateSpace;
        ActorOriginSourcePx = actorOriginSourcePx;
        HurtBoxSourcePx = hurtBoxSourcePx;
        AttackBoxSourcePx = attackBoxSourcePx;
        AttackActiveFrames = Copy(attackActiveFrames);
    }

    internal string CoordinateSpace { get; }
    internal SanityResourcePoint ActorOriginSourcePx { get; }
    internal SanityResourceRectangle HurtBoxSourcePx { get; }
    internal SanityResourceRectangle AttackBoxSourcePx { get; }
    internal IReadOnlyList<int> AttackActiveFrames { get; }

    private static IReadOnlyList<int> Copy(IReadOnlyList<int> values)
    {
        var copy = new int[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];
        return Array.AsReadOnly(copy);
    }
}

internal sealed class SanityHostileAttackMotionDefinition
{
    internal SanityHostileAttackMotionDefinition(
        string policyId,
        int contractVersion,
        double totalAdvanceTiles,
        IReadOnlyList<double> frameAdvanceTiles,
        bool resetAfterAnimation
    )
    {
        PolicyId = policyId;
        ContractVersion = contractVersion;
        TotalAdvanceTiles = totalAdvanceTiles;
        var copy = new double[frameAdvanceTiles.Count];
        for (var index = 0; index < frameAdvanceTiles.Count; index++)
            copy[index] = frameAdvanceTiles[index];
        FrameAdvanceTiles = Array.AsReadOnly(copy);
        ResetAfterAnimation = resetAfterAnimation;
    }

    internal string PolicyId { get; }
    internal int ContractVersion { get; }
    internal double TotalAdvanceTiles { get; }
    internal IReadOnlyList<double> FrameAdvanceTiles { get; }
    internal bool ResetAfterAnimation { get; }
}

internal sealed class SanityHostileAttackMetadataCatalog
{
    private const int SupportedContractVersion = 1;
    private const string ActorOriginCoordinateSpace =
        "ActorOriginRelativeSourcePx";
    private readonly IReadOnlyDictionary<string, SanityHostileAttackMetadataDefinition> byBinding;

    private SanityHostileAttackMetadataCatalog(
        Dictionary<string, SanityHostileAttackMetadataDefinition> definitions
    )
    {
        byBinding = new ReadOnlyDictionary<string, SanityHostileAttackMetadataDefinition>(
            definitions
        );
    }

    internal static bool TryParse(
        string animationsJson,
        string bindingsJson,
        out SanityHostileAttackMetadataCatalog? catalog,
        out string reason
    )
    {
        catalog = null;
        try
        {
            using var animations = JsonDocument.Parse(animationsJson, DocumentOptions);
            using var bindings = JsonDocument.Parse(bindingsJson, DocumentOptions);
            if (
                animations.RootElement.GetProperty("SchemaVersion").GetInt32() != 1
                || bindings.RootElement.GetProperty("SchemaVersion").GetInt32() != 1
            )
            {
                reason = "resource.hostile-attack-metadata.schema-unsupported";
                return false;
            }
            var profiles = Index(
                animations.RootElement.GetProperty("AnimationProfiles"),
                "AnimationProfileId"
            );
            var policies = Index(
                bindings.RootElement.GetProperty("AttackMotionPolicies"),
                "AttackMotionPolicyId"
            );
            var definitions = new Dictionary<string, SanityHostileAttackMetadataDefinition>(
                StringComparer.Ordinal
            );

            foreach (var binding in bindings.RootElement.GetProperty("Bindings").EnumerateArray())
            {
                var bindingId = RequiredString(binding, "AssetBindingId");
                var animationProfileId = RequiredString(binding, "AnimationProfileId");
                var motionPolicyId = RequiredString(binding, "AttackMotionPolicyId");
                if (
                    binding.GetProperty("ContractVersion").GetInt32()
                        != SupportedContractVersion
                    || !profiles.TryGetValue(animationProfileId, out var profile)
                    || !policies.TryGetValue(motionPolicyId, out var policy)
                )
                {
                    reason = "resource.hostile-attack-metadata.binding-invalid";
                    return false;
                }

                var actorOrigin = ReadPoint(profile.GetProperty("ActorOriginSourcePx"));
                var collisionElement = profile.GetProperty("Collision");
                var coordinateSpace = RequiredString(collisionElement, "CoordinateSpace");
                var hurtBox = ReadRectangle(
                    collisionElement.GetProperty("HurtBoxSourcePx")
                );
                var attackBox = ReadRectangle(
                    collisionElement.GetProperty("AttackBoxSourcePx")
                );
                var activeFrames = ReadIntArray(
                    collisionElement.GetProperty("AttackActiveFrames")
                );
                var states = Index(profile.GetProperty("States"), "AnimationId");
                var idle = ReadState(FindState(states, ".idle"));
                var chase = ReadState(FindState(states, ".move"));
                var spawn = ReadState(FindState(states, ".spawn"));
                var taunt = ReadState(FindState(states, ".taunt"));
                var attack = ReadState(FindState(states, ".attack"));
                var hitResponseVisual = TryFindState(states, ".death", out var death)
                    ? ReadState(death)
                    : null;
                var motion = ReadMotion(policy);

                if (
                    !string.Equals(
                        coordinateSpace,
                        ActorOriginCoordinateSpace,
                        StringComparison.Ordinal
                    )
                    || hurtBox.Width <= 0
                    || hurtBox.Height <= 0
                    || attackBox.Width <= 0
                    || attackBox.Height <= 0
                    || activeFrames.Count == 0
                    || !activeFrames.SequenceEqual(attack.HitFrames)
                    || activeFrames.Any(frame => frame < 1 || frame > attack.FrameCount)
                    || motion.FrameAdvanceTiles.Count != attack.FrameCount
                    || !string.Equals(
                        motion.PolicyId,
                        motionPolicyId,
                        StringComparison.Ordinal
                    )
                )
                {
                    reason = "resource.hostile-attack-metadata.common-contract-invalid";
                    return false;
                }

                var definition = new SanityHostileAttackMetadataDefinition(
                    bindingId,
                    animationProfileId,
                    motionPolicyId,
                    idle,
                    chase,
                    spawn,
                    taunt,
                    attack,
                    hitResponseVisual,
                    new SanityHostileCollisionDefinition(
                        coordinateSpace,
                        actorOrigin,
                        hurtBox,
                        attackBox,
                        activeFrames
                    ),
                    motion
                );
                if (!definitions.TryAdd(bindingId, definition))
                {
                    reason = "resource.hostile-attack-metadata.binding-duplicate";
                    return false;
                }
            }

            if (definitions.Count == 0)
            {
                reason = "resource.hostile-attack-metadata.binding-missing";
                return false;
            }
            catalog = new SanityHostileAttackMetadataCatalog(definitions);
            reason = "resource.hostile-attack-metadata.available";
            return true;
        }
        catch (JsonException)
        {
            reason = "resource.hostile-attack-metadata.invalid-json";
            return false;
        }
        catch (InvalidOperationException)
        {
            reason = "resource.hostile-attack-metadata.invalid-shape";
            return false;
        }
        catch (KeyNotFoundException)
        {
            reason = "resource.hostile-attack-metadata.missing-field";
            return false;
        }
        catch (ArgumentException)
        {
            reason = "resource.hostile-attack-metadata.invalid-field";
            return false;
        }
    }

    internal bool TryGet(
        string assetBindingId,
        out SanityHostileAttackMetadataDefinition? definition
    )
    {
        return byBinding.TryGetValue(assetBindingId, out definition);
    }

    private static SanityHostileAnimationStateDefinition ReadState(JsonElement state)
    {
        var frameCount = state.GetProperty("FrameCount").GetInt32();
        var frameDuration = state.GetProperty("FrameDurationMs").GetInt32();
        var drawScale = state.GetProperty("DrawScale").GetDouble();
        var row = state.GetProperty("Row").GetInt32();
        if (
            row < 0
            ||
            frameCount <= 0
            || frameDuration <= 0
            || !double.IsFinite(drawScale)
            || drawScale <= 0d
        )
        {
            throw new InvalidOperationException("Invalid hostile animation state.");
        }
        return new SanityHostileAnimationStateDefinition(
            RequiredString(state, "AnimationId"),
            row,
            frameCount,
            frameDuration,
            ReadPoint(state.GetProperty("PivotSourcePx")),
            drawScale,
            ReadIntArray(state.GetProperty("HitFrames")),
            ReadDirectionRows(state.GetProperty("DirectionRows"))
        );
    }

    private static IReadOnlyDictionary<string, int> ReadDirectionRows(
        JsonElement element
    )
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected a direction-row array.");
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            var direction = RequiredString(item, "Direction");
            var row = item.GetProperty("Row").GetInt32();
            if (row < 0 || !rows.TryAdd(direction, row))
                throw new InvalidOperationException("Invalid hostile direction row.");
        }
        return new ReadOnlyDictionary<string, int>(rows);
    }

    private static SanityHostileAttackMotionDefinition ReadMotion(JsonElement policy)
    {
        var id = RequiredString(policy, "AttackMotionPolicyId");
        var version = policy.GetProperty("ContractVersion").GetInt32();
        var total = policy.GetProperty("TotalAdvanceTiles").GetDouble();
        var advances = ReadDoubleArray(policy.GetProperty("FrameAdvanceTiles"));
        var reset = policy.GetProperty("ResetAfterAnimation").GetBoolean();
        if (
            version != SupportedContractVersion
            || !double.IsFinite(total)
            || total <= 0d
            || advances.Count == 0
            || advances.Any(value => !double.IsFinite(value) || value < 0d)
            || Math.Abs(advances.Sum() - total) > 0.000001d
            || !reset
        )
        {
            throw new InvalidOperationException("Invalid hostile attack motion policy.");
        }
        return new SanityHostileAttackMotionDefinition(
            id,
            version,
            total,
            advances,
            reset
        );
    }

    private static JsonElement FindState(
        IReadOnlyDictionary<string, JsonElement> states,
        string suffix
    )
    {
        var matches = states.Where(pair => pair.Key.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Hostile animation state is missing or ambiguous.");
        return matches[0].Value;
    }

    private static bool TryFindState(
        IReadOnlyDictionary<string, JsonElement> states,
        string suffix,
        out JsonElement state
    )
    {
        var matches = states.Where(
            pair => pair.Key.EndsWith(suffix, StringComparison.Ordinal)
        ).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("Hostile animation state is ambiguous.");
        if (matches.Length == 1)
        {
            state = matches[0].Value;
            return true;
        }
        state = default;
        return false;
    }

    private static Dictionary<string, JsonElement> Index(
        JsonElement array,
        string keyName
    )
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected a metadata array.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var key = RequiredString(item, keyName);
            if (!result.TryAdd(key, item))
                throw new InvalidOperationException("Duplicate metadata key.");
        }
        return result;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A required metadata string is empty.");
        return value;
    }

    private static SanityResourcePoint ReadPoint(JsonElement element)
    {
        return new SanityResourcePoint(
            element.GetProperty("X").GetInt32(),
            element.GetProperty("Y").GetInt32()
        );
    }

    private static SanityResourceRectangle ReadRectangle(JsonElement element)
    {
        return new SanityResourceRectangle(
            element.GetProperty("X").GetInt32(),
            element.GetProperty("Y").GetInt32(),
            element.GetProperty("Width").GetInt32(),
            element.GetProperty("Height").GetInt32()
        );
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected an integer array.");
        return Array.AsReadOnly(element.EnumerateArray().Select(value => value.GetInt32()).ToArray());
    }

    private static IReadOnlyList<double> ReadDoubleArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected a number array.");
        return Array.AsReadOnly(element.EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    private static readonly JsonDocumentOptions DocumentOptions =
        new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };
}
