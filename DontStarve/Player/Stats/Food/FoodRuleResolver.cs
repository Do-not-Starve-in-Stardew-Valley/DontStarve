#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Food;

internal enum FoodValueDimension
{
    Hunger,
    Sanity,
}

internal enum FoodRuleSourceKind
{
    None,
    Wine,
    Juice,
    Pickle,
    Jelly,
    Roe,
    AgedRoe,
    Honey,
    Seed,
    DriedFruit,
    DriedMushroom,
    SmokedFish,
}

internal readonly record struct FoodItemFacts(
    string ItemId,
    FoodRuleSourceKind SourceKind,
    string? SourceItemId
);

/// <summary>
/// Resolves one food dimension without knowing Stardew or SMAPI types. The dictionaries are the
/// shipped food.json tables; source metadata only selects a formula family and never overrides an
/// exact JSON key.
/// </summary>
internal sealed class FoodRuleResolver
{
    private readonly IReadOnlyDictionary<string, double> hungerValues;
    private readonly IReadOnlyDictionary<string, double> sanityValues;

    internal FoodRuleResolver(
        IReadOnlyDictionary<string, double> hungerValues,
        IReadOnlyDictionary<string, double> sanityValues
    )
    {
        this.hungerValues = hungerValues ?? throw new ArgumentNullException(nameof(hungerValues));
        this.sanityValues = sanityValues ?? throw new ArgumentNullException(nameof(sanityValues));
    }

    internal bool TryResolve(
        FoodItemFacts facts,
        FoodValueDimension dimension,
        out double value
    )
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(facts.ItemId))
            return false;

        var values = dimension == FoodValueDimension.Hunger
            ? this.hungerValues
            : this.sanityValues;
        var defaultKey = GetDefaultKey(facts);

        // A product-specific composite key is allowed to be more specific than its preserve
        // family (for example Custom/FancyWine/Apple). The family key supports canonical entries
        // such as DriedFruit/282 and 340/591. Check both before treating a category base entry
        // such as 340 or DriedFruit as the fallback, otherwise the base entry would hide its
        // source-specific override and formula.
        if (TryGetExact(values, facts, defaultKey, out value))
            return true;

        if (TryCalculate(values, facts, dimension, out value) && IsFinite(value))
            return true;

        // A known family with an untraceable or unsupported source falls back to its explicit
        // base JSON value. No family rule is applied when the source kind itself is unknown.
        return values.TryGetValue(defaultKey, out value);
    }

    private static bool TryGetExact(
        IReadOnlyDictionary<string, double> values,
        FoodItemFacts facts,
        string defaultKey,
        out double value
    )
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(facts.SourceItemId))
        {
            return !string.Equals(defaultKey, facts.ItemId, StringComparison.Ordinal)
                && values.TryGetValue(facts.ItemId, out value);
        }

        var sourceKey = string.Concat(facts.ItemId, "/", facts.SourceItemId);
        if (values.TryGetValue(sourceKey, out value))
            return true;

        if (!string.Equals(defaultKey, facts.ItemId, StringComparison.Ordinal))
        {
            var familyKey = string.Concat(defaultKey, "/", facts.SourceItemId);
            if (values.TryGetValue(familyKey, out value))
                return true;
        }

        // A non-default item key is still an exact item override. Category base keys are left
        // for the final fallback after source-based calculation has had a chance to run.
        if (!string.Equals(defaultKey, facts.ItemId, StringComparison.Ordinal)
            && values.TryGetValue(facts.ItemId, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryCalculate(
        IReadOnlyDictionary<string, double> values,
        FoodItemFacts facts,
        FoodValueDimension dimension,
        out double value
    )
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(facts.SourceItemId))
            return false;

        var sourceFacts = new FoodItemFacts(
            facts.SourceItemId,
            FoodRuleSourceKind.None,
            null
        );
        if (!values.TryGetValue(sourceFacts.ItemId, out var sourceValue))
            return false;

        switch (facts.SourceKind)
        {
            case FoodRuleSourceKind.Juice:
                if (dimension != FoodValueDimension.Hunger)
                    return false;
                value = sourceValue * 2.5d;
                return true;

            case FoodRuleSourceKind.Pickle:
            case FoodRuleSourceKind.Jelly:
                if (dimension != FoodValueDimension.Hunger)
                    return false;
                value = sourceValue;
                return true;

            case FoodRuleSourceKind.DriedFruit:
                value = dimension == FoodValueDimension.Hunger
                    ? sourceValue * 3d
                    : sourceValue * 3d + 50d;
                return true;

            case FoodRuleSourceKind.SmokedFish:
                value = dimension == FoodValueDimension.Hunger
                    ? sourceValue
                    : sourceValue + 20d;
                return true;

            default:
                return false;
        }
    }

    private static string GetDefaultKey(FoodItemFacts facts)
    {
        return facts.SourceKind switch
        {
            FoodRuleSourceKind.Wine => "348",
            FoodRuleSourceKind.Juice => "350",
            FoodRuleSourceKind.Pickle => "342",
            FoodRuleSourceKind.Jelly => "344",
            FoodRuleSourceKind.Roe => "812",
            FoodRuleSourceKind.AgedRoe => "447",
            FoodRuleSourceKind.Honey => "340",
            FoodRuleSourceKind.Seed => SeedEdibilityRules.FoodRuleDefaultKey,
            FoodRuleSourceKind.DriedFruit => "DriedFruit",
            FoodRuleSourceKind.DriedMushroom => "DriedMushrooms",
            FoodRuleSourceKind.SmokedFish => "SmokedFish",
            _ => facts.ItemId,
        };
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
