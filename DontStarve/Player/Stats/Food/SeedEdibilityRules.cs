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

    /// <summary>Stardew's raw "inedible" sentinel; also the default of <c>Object.edibility</c>.</summary>
    internal const int NativeInedibleEdibility = -300;

    /// <summary>Object ID prefix used by <c>Item.QualifiedItemId</c> for plain objects.</summary>
    internal const string ObjectIdPrefix = "(O)";

    /// <summary>Stardrop: vanilla hardcodes 999 Energy and 0 Health.</summary>
    internal const string StardropItemId = "434";

    /// <summary>Vanilla hardcodes 0 Energy and 999 Health for this item.</summary>
    internal const string ZeroEnergyItemId = "773";

    /// <summary>Vanilla derives Health as 68% of Energy for this item.</summary>
    internal const string HealthAtSixtyEightPercentItemId = "874";

    /// <summary>Vanilla hardcodes 0 Health for this item.</summary>
    internal const string ZeroHealthItemId = "349";

    /// <summary>Energy multiplier applied before rounding, copied from vanilla.</summary>
    internal const double EnergyEdibilityMultiplier = 2.5d;

    /// <summary>Health share of Energy for the default branch, copied from vanilla.</summary>
    internal const float DefaultHealthRatio = 0.45f;

    /// <summary>Health share of Energy for the energy-drink branch, copied from vanilla.</summary>
    internal const float EnergyDrinkHealthRatio = 0.68f;

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

    /// <summary>
    /// Builds the vanilla-style qualified object ID. Kept here so the vanilla formula mirrors below
    /// compare against exactly the same string shape that <c>Item.QualifiedItemId</c> produces.
    /// </summary>
    internal static string QualifyObjectItemId(string itemId)
    {
        return string.Concat(ObjectIdPrefix, itemId);
    }

    /// <summary>
    /// Pure mirror of vanilla <c>Object.staminaRecoveredOnConsumption()</c> for a single object.
    /// Reproducing the original formula (including the Stardrop and Energy special cases and the
    /// quality term) is what lets a corrected seam return the number the player would have seen
    /// without our patches, instead of a hardcoded constant.
    /// </summary>
    internal static int GetVanillaStaminaRecovered(
        string? qualifiedItemId,
        int edibility,
        int quality
    )
    {
        if (
            string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(ZeroEnergyItemId),
                StringComparison.Ordinal
            )
        )
        {
            return 0;
        }

        if (
            string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(StardropItemId),
                StringComparison.Ordinal
            )
        )
        {
            return 999;
        }

        return (int)Math.Ceiling(edibility * EnergyEdibilityMultiplier) + quality * edibility;
    }

    /// <summary>
    /// Pure mirror of vanilla <c>Object.healthRecoveredOnConsumption()</c>. The leading
    /// <c>edibility &lt; 0 =&gt; 0</c> guard is vanilla's own and is why a raw -300 seed shows a
    /// negative Energy row but never a negative Health row.
    /// </summary>
    internal static int GetVanillaHealthRecovered(
        string? qualifiedItemId,
        int edibility,
        int quality
    )
    {
        if (edibility < 0)
            return 0;

        var stamina = GetVanillaStaminaRecovered(qualifiedItemId, edibility, quality);

        if (
            string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(HealthAtSixtyEightPercentItemId),
                StringComparison.Ordinal
            )
        )
        {
            return (int)(stamina * EnergyDrinkHealthRatio);
        }

        if (
            string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(StardropItemId),
                StringComparison.Ordinal
            )
            || string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(ZeroHealthItemId),
                StringComparison.Ordinal
            )
        )
        {
            return 0;
        }

        if (
            string.Equals(
                qualifiedItemId,
                QualifyObjectItemId(ZeroEnergyItemId),
                StringComparison.Ordinal
            )
        )
        {
            return 999;
        }

        return (int)(stamina * DefaultHealthRatio);
    }
}
