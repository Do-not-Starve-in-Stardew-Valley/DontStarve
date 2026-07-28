#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

internal static class ShadowMonsterProfilePaths
{
    internal const string Schema = "Asset/Sanity/Data/ShadowMonsters/schema.json";
    internal const string Animations = "Asset/Sanity/Data/animations.json";
    internal const string ResourceBindings = "Asset/Sanity/Data/resource-bindings.json";
    internal const string AudioCues = "Asset/Sanity/Audio/audio-cues.json";

    private static readonly IReadOnlyDictionary<string, string> FrozenProfileFiles =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ShadowMonsterDifficultyProfileIds.Stardew] =
                    "Asset/Sanity/Data/ShadowMonsters/Stardew.json",
                [ShadowMonsterDifficultyProfileIds.Compatible] =
                    "Asset/Sanity/Data/ShadowMonsters/Compatible.json",
                [ShadowMonsterDifficultyProfileIds.DontStarve] =
                    "Asset/Sanity/Data/ShadowMonsters/DontStarve.json",
                [ShadowMonsterDifficultyProfileIds.Fusion] =
                    "Asset/Sanity/Data/ShadowMonsters/Fusion.json",
            }
        );

    internal static IReadOnlyDictionary<string, string> ProfileFiles => FrozenProfileFiles;

    internal static bool TryGetExpectedProfileId(string relativePath, out string profileId)
    {
        foreach (var pair in FrozenProfileFiles)
        {
            if (string.Equals(pair.Value, relativePath, StringComparison.Ordinal))
            {
                profileId = pair.Key;
                return true;
            }
        }

        profileId = string.Empty;
        return false;
    }
}

internal static class ShadowMonsterDifficultyProfileIds
{
    internal const string Stardew = "Stardew";
    internal const string Compatible = "Compatible";
    internal const string DontStarve = "DontStarve";
    internal const string Fusion = "Fusion";

    internal static readonly IReadOnlyList<string> All = Array.AsReadOnly(
        new[] { Stardew, Compatible, DontStarve, Fusion }
    );
}

internal static class ShadowMonsterAssetBindingIds
{
    internal const string CreeperFear = "sanity.binding.creeper-fear";
    internal const string Terrorbeak = "sanity.binding.terrorbeak";

    internal static readonly IReadOnlyList<string> All = Array.AsReadOnly(
        new[] { CreeperFear, Terrorbeak }
    );
}

internal static class ShadowMonsterProfileContractIds
{
    internal const string SchemaId = "sanity.shadow-monster-profile-set.v1";
    internal const string UnknownFieldPolicy = "RejectForKnownSchemaVersion";
    internal const string Stardew16DataShape = "Dictionary<string,string>/slash-string";
    internal const string Stardew17Capability = "Unavailable";

    internal const string DirectThroughTerrain = "DirectThroughTerrain";
    internal const string KnockbackImmunity = "Knockback";
    internal const string FrozenImmunity = "Frozen";
    internal const string VoidEssenceDropTable = "sanity.drop.void-essence-v1";
    internal const string VoidEssenceSemanticItem = "stardew.item.void-essence";
    internal const string TauntOrDelayPostAttack =
        "sanity.post-attack.taunt-or-delay-v1";

    internal const string Stardew16Override = "Stardew1.6";
    internal const string Stardew17Override = "Stardew1.7";
    internal const string Stardew17UnavailableReason =
        "shadow-profile.adapter.stardew-1.7-shape-unavailable";
}

internal sealed class ShadowMonsterProfileSchemaContract
{
    internal ShadowMonsterProfileSchemaContract(
        string schemaId,
        int schemaVersion,
        int canonicalModelVersion,
        int dropTableSchemaVersion,
        string unknownFieldPolicy,
        int stardew16AdapterVersion,
        string stardew16DataShape,
        string stardew17Capability,
        string stardew17Reason
    )
    {
        SchemaId = schemaId;
        SchemaVersion = schemaVersion;
        CanonicalModelVersion = canonicalModelVersion;
        DropTableSchemaVersion = dropTableSchemaVersion;
        UnknownFieldPolicy = unknownFieldPolicy;
        Stardew16AdapterVersion = stardew16AdapterVersion;
        Stardew16DataShape = stardew16DataShape;
        Stardew17Capability = stardew17Capability;
        Stardew17Reason = stardew17Reason;
    }

    internal string SchemaId { get; }
    internal int SchemaVersion { get; }
    internal int CanonicalModelVersion { get; }
    internal int DropTableSchemaVersion { get; }
    internal string UnknownFieldPolicy { get; }
    internal int Stardew16AdapterVersion { get; }
    internal string Stardew16DataShape { get; }
    internal string Stardew17Capability { get; }
    internal string Stardew17Reason { get; }
}

internal sealed class ShadowMonsterDropTable
{
    internal ShadowMonsterDropTable(
        int schemaVersion,
        string dropTableId,
        string itemSemanticId,
        int guaranteedQuantity,
        int bonusQuantity,
        double bonusChance
    )
    {
        SchemaVersion = schemaVersion;
        DropTableId = dropTableId;
        ItemSemanticId = itemSemanticId;
        GuaranteedQuantity = guaranteedQuantity;
        BonusQuantity = bonusQuantity;
        BonusChance = bonusChance;
    }

    internal int SchemaVersion { get; }
    internal string DropTableId { get; }
    internal string ItemSemanticId { get; }
    internal int GuaranteedQuantity { get; }
    internal int BonusQuantity { get; }
    internal double BonusChance { get; }
}

internal sealed class ShadowMonsterGameVersionOverride
{
    internal ShadowMonsterGameVersionOverride(
        string gameVersionId,
        int? maxHealth,
        int? baseDamage,
        double? movementSpeed,
        int? defense,
        double? detectionRadiusTiles,
        double? attackRangeTiles,
        double? attackIntervalSeconds,
        double? naturalDespawnGameHours,
        int? experienceValue,
        string? wallTraversalMode
    )
    {
        GameVersionId = gameVersionId;
        MaxHealth = maxHealth;
        BaseDamage = baseDamage;
        MovementSpeed = movementSpeed;
        Defense = defense;
        DetectionRadiusTiles = detectionRadiusTiles;
        AttackRangeTiles = attackRangeTiles;
        AttackIntervalSeconds = attackIntervalSeconds;
        NaturalDespawnGameHours = naturalDespawnGameHours;
        ExperienceValue = experienceValue;
        WallTraversalMode = wallTraversalMode;
    }

    internal string GameVersionId { get; }
    internal int? MaxHealth { get; }
    internal int? BaseDamage { get; }
    internal double? MovementSpeed { get; }
    internal int? Defense { get; }
    internal double? DetectionRadiusTiles { get; }
    internal double? AttackRangeTiles { get; }
    internal double? AttackIntervalSeconds { get; }
    internal double? NaturalDespawnGameHours { get; }
    internal int? ExperienceValue { get; }
    internal string? WallTraversalMode { get; }
}

/// <summary>
/// Version-neutral gameplay data. It deliberately contains no owner, entity, state, revision,
/// source-pixel collision, texture, or game CLR object.
/// </summary>
internal sealed class ShadowMonsterProfile
{
    private readonly IReadOnlyList<string> immunityTags;
    private readonly IReadOnlyDictionary<string, ShadowMonsterGameVersionOverride> versionOverrides;

    internal ShadowMonsterProfile(
        int maxHealth,
        int baseDamage,
        double movementSpeed,
        int defense,
        double detectionRadiusTiles,
        double attackRangeTiles,
        double attackIntervalSeconds,
        double naturalDespawnGameHours,
        string displayNameKey,
        string wallTraversalMode,
        IReadOnlyList<string> immunityTags,
        ShadowMonsterDropTable dropTable,
        int sanityReward,
        string assetBindingId,
        string animationProfileId,
        string cueSetId,
        string attackMotionPolicyId,
        string postAttackPolicyId,
        int experienceValue,
        string? killCounterId,
        IReadOnlyDictionary<string, ShadowMonsterGameVersionOverride> versionOverrides
    )
    {
        MaxHealth = maxHealth;
        BaseDamage = baseDamage;
        MovementSpeed = movementSpeed;
        Defense = defense;
        DetectionRadiusTiles = detectionRadiusTiles;
        AttackRangeTiles = attackRangeTiles;
        AttackIntervalSeconds = attackIntervalSeconds;
        NaturalDespawnGameHours = naturalDespawnGameHours;
        DisplayNameKey = displayNameKey;
        WallTraversalMode = wallTraversalMode;
        this.immunityTags = Array.AsReadOnly(Copy(immunityTags));
        DropTable = dropTable;
        SanityReward = sanityReward;
        AssetBindingId = assetBindingId;
        AnimationProfileId = animationProfileId;
        CueSetId = cueSetId;
        AttackMotionPolicyId = attackMotionPolicyId;
        PostAttackPolicyId = postAttackPolicyId;
        ExperienceValue = experienceValue;
        KillCounterId = killCounterId;
        this.versionOverrides = new ReadOnlyDictionary<
            string,
            ShadowMonsterGameVersionOverride
        >(
            new Dictionary<string, ShadowMonsterGameVersionOverride>(
                versionOverrides,
                StringComparer.Ordinal
            )
        );
    }

    internal int MaxHealth { get; }
    internal int BaseDamage { get; }
    internal double MovementSpeed { get; }
    internal int Defense { get; }
    internal double DetectionRadiusTiles { get; }
    internal double AttackRangeTiles { get; }
    internal double AttackIntervalSeconds { get; }
    internal double NaturalDespawnGameHours { get; }
    internal string DisplayNameKey { get; }
    internal string WallTraversalMode { get; }
    internal IReadOnlyList<string> ImmunityTags => immunityTags;
    internal ShadowMonsterDropTable DropTable { get; }
    internal int SanityReward { get; }
    internal string AssetBindingId { get; }
    internal string AnimationProfileId { get; }
    internal string CueSetId { get; }
    internal string AttackMotionPolicyId { get; }
    internal string PostAttackPolicyId { get; }
    internal int ExperienceValue { get; }
    internal string? KillCounterId { get; }
    internal IReadOnlyDictionary<string, ShadowMonsterGameVersionOverride> GameVersionOverrides =>
        versionOverrides;

    internal bool TryGetVersionOverride(
        string gameVersionId,
        out ShadowMonsterGameVersionOverride? value
    )
    {
        return versionOverrides.TryGetValue(gameVersionId, out value);
    }

    private static string[] Copy(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (var index = 0; index < source.Count; index++)
            copy[index] = source[index];
        return copy;
    }
}

internal sealed class ShadowMonsterDifficultyProfile
{
    private readonly IReadOnlyDictionary<string, ShadowMonsterProfile> monsters;

    internal ShadowMonsterDifficultyProfile(
        int schemaVersion,
        string profileId,
        bool isBetaBalance,
        IReadOnlyDictionary<string, ShadowMonsterProfile> monsters
    )
    {
        SchemaVersion = schemaVersion;
        ProfileId = profileId;
        IsBetaBalance = isBetaBalance;
        this.monsters = new ReadOnlyDictionary<string, ShadowMonsterProfile>(
            new Dictionary<string, ShadowMonsterProfile>(monsters, StringComparer.Ordinal)
        );
    }

    internal int SchemaVersion { get; }
    internal string ProfileId { get; }
    internal bool IsBetaBalance { get; }
    internal IReadOnlyDictionary<string, ShadowMonsterProfile> Monsters => monsters;

    internal bool TryGetMonster(string assetBindingId, out ShadowMonsterProfile? profile)
    {
        return monsters.TryGetValue(assetBindingId, out profile);
    }
}

internal sealed class ShadowMonsterProfileCatalog
{
    private readonly IReadOnlyDictionary<string, ShadowMonsterDifficultyProfile> profiles;

    internal ShadowMonsterProfileCatalog(
        ShadowMonsterProfileSchemaContract schema,
        IReadOnlyDictionary<string, ShadowMonsterDifficultyProfile> profiles
    )
    {
        Schema = schema;
        this.profiles = new ReadOnlyDictionary<string, ShadowMonsterDifficultyProfile>(
            new Dictionary<string, ShadowMonsterDifficultyProfile>(profiles, StringComparer.Ordinal)
        );
    }

    internal ShadowMonsterProfileSchemaContract Schema { get; }
    internal IReadOnlyDictionary<string, ShadowMonsterDifficultyProfile> Profiles => profiles;

    internal bool TryGetProfile(
        string profileId,
        out ShadowMonsterDifficultyProfile? profile
    )
    {
        return profiles.TryGetValue(profileId, out profile);
    }
}

internal sealed record ShadowMonsterProfileDocument(string RelativePath, string Json);

internal sealed record ShadowMonsterProfileIssue(
    string Code,
    string FilePath,
    string Key,
    string Reason
);

internal enum ShadowMonsterProfileDiagnosticSeverity
{
    Info,
    Warning,
}

internal sealed record ShadowMonsterProfileDiagnostic(
    ShadowMonsterProfileDiagnosticSeverity Severity,
    string Code,
    string ProfileId,
    string AssetBindingId,
    string Reason
);

internal sealed class ShadowMonsterProfileValidationResult
{
    internal ShadowMonsterProfileValidationResult(
        ShadowMonsterProfileCatalog? catalog,
        IReadOnlyList<ShadowMonsterProfileIssue> issues,
        IReadOnlyList<ShadowMonsterProfileDiagnostic> diagnostics
    )
    {
        Catalog = catalog;
        Issues = Array.AsReadOnly(Copy(issues));
        Diagnostics = Array.AsReadOnly(Copy(diagnostics));
    }

    internal ShadowMonsterProfileCatalog? Catalog { get; }
    internal IReadOnlyList<ShadowMonsterProfileIssue> Issues { get; }
    internal IReadOnlyList<ShadowMonsterProfileDiagnostic> Diagnostics { get; }
    internal bool Success => Catalog is not null && Issues.Count == 0;

    private static T[] Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (var index = 0; index < source.Count; index++)
            copy[index] = source[index];
        return copy;
    }
}

internal sealed class ShadowMonsterProfileLoadResult
{
    internal ShadowMonsterProfileLoadResult(
        ShadowMonsterProfileCatalog? catalog,
        IReadOnlyList<ShadowMonsterProfileIssue> issues,
        IReadOnlyList<ShadowMonsterProfileDiagnostic> diagnostics
    )
    {
        Catalog = catalog;
        Issues = Array.AsReadOnly(Copy(issues));
        Diagnostics = Array.AsReadOnly(Copy(diagnostics));
    }

    internal ShadowMonsterProfileCatalog? Catalog { get; }
    internal IReadOnlyList<ShadowMonsterProfileIssue> Issues { get; }
    internal IReadOnlyList<ShadowMonsterProfileDiagnostic> Diagnostics { get; }
    internal bool Success => Catalog is not null && Issues.Count == 0;

    internal static ShadowMonsterProfileLoadResult FromValidation(
        ShadowMonsterProfileValidationResult validation
    )
    {
        return new ShadowMonsterProfileLoadResult(
            validation.Catalog,
            validation.Issues,
            validation.Diagnostics
        );
    }

    private static T[] Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (var index = 0; index < source.Count; index++)
            copy[index] = source[index];
        return copy;
    }
}
