#nullable enable

using System;

namespace DontStarve.Player.Stats.Food;

internal enum SeedClassification
{
    NotTarget,
    TargetCropSeed,
    ExplicitlyExcluded,
    Unknown,
}

internal readonly record struct SeedMetadataFacts(
    string? ItemId,
    string? ObjectType,
    bool HasObjectData
);

/// <summary>
/// Pure seed boundary and Edibility policy. Runtime code supplies current Stardew object metadata;
/// keeping the decision here makes the fail-closed and non-target invariants directly testable.
/// </summary>
internal static class SeedEdibilityRules
{
    internal const string SeedsObjectType = "Seeds";
    internal const string ExcludedSproutingStoneFruitItemId = "DS_Sprouting_Stone_Fruit";
    internal const string FoodRuleDefaultKey = "Seeds";
    internal const int TargetEdibility = 1;

    internal static SeedClassification Classify(SeedMetadataFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.ItemId))
            return SeedClassification.Unknown;

        if (
            string.Equals(
                facts.ItemId,
                ExcludedSproutingStoneFruitItemId,
                StringComparison.Ordinal
            )
        )
        {
            return SeedClassification.ExplicitlyExcluded;
        }

        if (!facts.HasObjectData || string.IsNullOrWhiteSpace(facts.ObjectType))
            return SeedClassification.Unknown;

        return string.Equals(facts.ObjectType, SeedsObjectType, StringComparison.Ordinal)
            ? SeedClassification.TargetCropSeed
            : SeedClassification.NotTarget;
    }

    internal static int GetEffectiveEdibility(
        SeedClassification classification,
        bool enabled,
        int nativeEdibility
    )
    {
        return enabled && classification == SeedClassification.TargetCropSeed
            ? TargetEdibility
            : nativeEdibility;
    }
}
