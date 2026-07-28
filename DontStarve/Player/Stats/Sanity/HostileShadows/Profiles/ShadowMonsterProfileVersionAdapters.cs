#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

internal static class ShadowMonsterProfileVersionFactIds
{
    internal const string Stardew16MonstersReturnType =
        "System.Collections.Generic.Dictionary`2[System.String,System.String]";

    internal static string DescribeMonstersReturnType(Type? returnType)
    {
        if (returnType is null)
            return string.Empty;

        if (returnType.IsGenericType)
        {
            var arguments = returnType.GetGenericArguments();
            if (
                returnType.GetGenericTypeDefinition()
                    == typeof(System.Collections.Generic.Dictionary<,>)
                && arguments.Length == 2
                && arguments[0] == typeof(string)
                && arguments[1] == typeof(string)
            )
            {
                return Stardew16MonstersReturnType;
            }
        }

        // Type.FullName embeds assembly-qualified generic arguments, so it can't be compared
        // with the frozen CLR shape. Keep every non-matching shape explainable and fail closed.
        return returnType.ToString();
    }
}

internal sealed record ShadowMonsterProfileVersionFacts(
    string GameVersion,
    string DataLoaderMonstersReturnType,
    bool HasBindableStardew17MonsterData,
    string? Stardew17MonsterDataTypeName
);

internal enum ShadowMonsterProfileAdapterCapabilityStatus
{
    Available,
    Unavailable,
}

internal enum ShadowMonsterProfileAdapterShape
{
    Unknown,
    Stardew16SlashString,
}

internal sealed record ShadowMonsterProfileAdapterCapability(
    ShadowMonsterProfileAdapterCapabilityStatus Status,
    ShadowMonsterProfileAdapterShape Shape,
    string Reason,
    IShadowMonsterProfileVersionAdapter? Adapter
)
{
    internal bool IsAvailable => Status == ShadowMonsterProfileAdapterCapabilityStatus.Available;
}

internal sealed class ShadowMonsterRuntimeProfile
{
    internal ShadowMonsterRuntimeProfile(
        string difficultyProfileId,
        string assetBindingId,
        int adapterVersion,
        int maxHealth,
        int baseDamage,
        double movementSpeed,
        int defense,
        double detectionRadiusTiles,
        double detectionRadiusPixels,
        double attackRangeTiles,
        double attackRangePixels,
        double attackIntervalSeconds,
        double naturalDespawnGameHours,
        string displayNameKey,
        string wallTraversalMode,
        System.Collections.Generic.IReadOnlyList<string> immunityTags,
        ShadowMonsterDropTable dropTable,
        int sanityReward,
        string animationProfileId,
        string cueSetId,
        string attackMotionPolicyId,
        string postAttackPolicyId,
        int experienceValue,
        string? killCounterId
    )
    {
        DifficultyProfileId = difficultyProfileId;
        AssetBindingId = assetBindingId;
        AdapterVersion = adapterVersion;
        MaxHealth = maxHealth;
        BaseDamage = baseDamage;
        MovementSpeed = movementSpeed;
        Defense = defense;
        DetectionRadiusTiles = detectionRadiusTiles;
        DetectionRadiusPixels = detectionRadiusPixels;
        AttackRangeTiles = attackRangeTiles;
        AttackRangePixels = attackRangePixels;
        AttackIntervalSeconds = attackIntervalSeconds;
        NaturalDespawnGameHours = naturalDespawnGameHours;
        DisplayNameKey = displayNameKey;
        WallTraversalMode = wallTraversalMode;
        ImmunityTags = immunityTags;
        DropTable = dropTable;
        SanityReward = sanityReward;
        AnimationProfileId = animationProfileId;
        CueSetId = cueSetId;
        AttackMotionPolicyId = attackMotionPolicyId;
        PostAttackPolicyId = postAttackPolicyId;
        ExperienceValue = experienceValue;
        KillCounterId = killCounterId;
    }

    internal string DifficultyProfileId { get; }
    internal string AssetBindingId { get; }
    internal int AdapterVersion { get; }
    internal int MaxHealth { get; }
    internal int BaseDamage { get; }
    internal double MovementSpeed { get; }
    internal int Defense { get; }
    internal double DetectionRadiusTiles { get; }
    internal double DetectionRadiusPixels { get; }
    internal double AttackRangeTiles { get; }
    internal double AttackRangePixels { get; }
    internal double AttackIntervalSeconds { get; }
    internal double NaturalDespawnGameHours { get; }
    internal string DisplayNameKey { get; }
    internal string WallTraversalMode { get; }
    internal System.Collections.Generic.IReadOnlyList<string> ImmunityTags { get; }
    internal ShadowMonsterDropTable DropTable { get; }
    internal int SanityReward { get; }
    internal string AnimationProfileId { get; }
    internal string CueSetId { get; }
    internal string AttackMotionPolicyId { get; }
    internal string PostAttackPolicyId { get; }
    internal int ExperienceValue { get; }
    internal string? KillCounterId { get; }
}

internal sealed record ShadowMonsterProfileAdapterResult(
    ShadowMonsterRuntimeProfile? Profile,
    string Reason
)
{
    internal bool Success => Profile is not null;
}

internal interface IShadowMonsterProfileVersionAdapter
{
    int AdapterVersion { get; }

    ShadowMonsterProfileAdapterResult Adapt(
        ShadowMonsterDifficultyProfile difficulty,
        string assetBindingId,
        int tileSize
    );
}

internal static class ShadowMonsterProfileVersionAdapterFactory
{
    internal static ShadowMonsterProfileAdapterCapability Resolve(
        ShadowMonsterProfileSchemaContract schema,
        ShadowMonsterProfileVersionFacts facts
    )
    {
        if (!Version.TryParse(facts.GameVersion, out var version))
            return Unavailable("shadow-profile.adapter.game-version-invalid");

        if (version.Major == 1 && version.Minor == 6)
        {
            if (
                !string.Equals(
                    facts.DataLoaderMonstersReturnType,
                    ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                    StringComparison.Ordinal
                )
                || facts.HasBindableStardew17MonsterData
                || !string.Equals(
                    schema.Stardew16DataShape,
                    ShadowMonsterProfileContractIds.Stardew16DataShape,
                    StringComparison.Ordinal
                )
            )
            {
                return Unavailable("shadow-profile.adapter.stardew-1.6-shape-drift");
            }

            return new ShadowMonsterProfileAdapterCapability(
                ShadowMonsterProfileAdapterCapabilityStatus.Available,
                ShadowMonsterProfileAdapterShape.Stardew16SlashString,
                "shadow-profile.adapter.stardew-1.6-available",
                new Stardew16ShadowMonsterProfileAdapter(schema.Stardew16AdapterVersion)
            );
        }

        if (version.Major == 1 && version.Minor == 7)
        {
            // 07-01 found no bindable 1.7 CLR contract. Even a future-looking fixture must stay
            // unavailable until a later fact-freeze verifies the released type and semantics.
            _ = facts.Stardew17MonsterDataTypeName;
            return Unavailable(schema.Stardew17Reason);
        }

        return Unavailable("shadow-profile.adapter.game-version-unsupported");
    }

    private static ShadowMonsterProfileAdapterCapability Unavailable(string reason)
    {
        return new ShadowMonsterProfileAdapterCapability(
            ShadowMonsterProfileAdapterCapabilityStatus.Unavailable,
            ShadowMonsterProfileAdapterShape.Unknown,
            reason,
            null
        );
    }
}

internal sealed class Stardew16ShadowMonsterProfileAdapter
    : IShadowMonsterProfileVersionAdapter
{
    internal Stardew16ShadowMonsterProfileAdapter(int adapterVersion)
    {
        AdapterVersion = adapterVersion;
    }

    public int AdapterVersion { get; }

    public ShadowMonsterProfileAdapterResult Adapt(
        ShadowMonsterDifficultyProfile difficulty,
        string assetBindingId,
        int tileSize
    )
    {
        if (tileSize <= 0)
            return Failure("shadow-profile.adapter.tile-size-invalid");
        if (!difficulty.TryGetMonster(assetBindingId, out var canonical) || canonical is null)
            return Failure("shadow-profile.adapter.monster-profile-missing");

        canonical.TryGetVersionOverride(
            ShadowMonsterProfileContractIds.Stardew16Override,
            out var versionOverride
        );

        var maxHealth = versionOverride?.MaxHealth ?? canonical.MaxHealth;
        var baseDamage = versionOverride?.BaseDamage ?? canonical.BaseDamage;
        var movementSpeed = versionOverride?.MovementSpeed ?? canonical.MovementSpeed;
        var defense = versionOverride?.Defense ?? canonical.Defense;
        var detectionRadiusTiles =
            versionOverride?.DetectionRadiusTiles ?? canonical.DetectionRadiusTiles;
        var attackRangeTiles = versionOverride?.AttackRangeTiles ?? canonical.AttackRangeTiles;
        var attackIntervalSeconds =
            versionOverride?.AttackIntervalSeconds ?? canonical.AttackIntervalSeconds;
        var naturalDespawnGameHours =
            versionOverride?.NaturalDespawnGameHours ?? canonical.NaturalDespawnGameHours;
        var experienceValue = versionOverride?.ExperienceValue ?? canonical.ExperienceValue;
        var wallTraversalMode =
            versionOverride?.WallTraversalMode ?? canonical.WallTraversalMode;

        return new ShadowMonsterProfileAdapterResult(
            new ShadowMonsterRuntimeProfile(
                difficulty.ProfileId,
                assetBindingId,
                AdapterVersion,
                maxHealth,
                baseDamage,
                movementSpeed,
                defense,
                detectionRadiusTiles,
                detectionRadiusTiles * tileSize,
                attackRangeTiles,
                attackRangeTiles * tileSize,
                attackIntervalSeconds,
                naturalDespawnGameHours,
                canonical.DisplayNameKey,
                wallTraversalMode,
                canonical.ImmunityTags,
                canonical.DropTable,
                canonical.SanityReward,
                canonical.AnimationProfileId,
                canonical.CueSetId,
                canonical.AttackMotionPolicyId,
                canonical.PostAttackPolicyId,
                experienceValue,
                canonical.KillCounterId
            ),
            "shadow-profile.adapter.success"
        );
    }

    private static ShadowMonsterProfileAdapterResult Failure(string reason)
    {
        return new ShadowMonsterProfileAdapterResult(null, reason);
    }
}
