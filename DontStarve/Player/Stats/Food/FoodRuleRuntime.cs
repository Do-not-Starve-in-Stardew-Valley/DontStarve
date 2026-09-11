#nullable enable

using System;
using System.Collections.Generic;
using StardewValley;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Food;

/// <summary>
/// Small runtime adapter around the pure food rule resolver. It is initialized by the two
/// existing EatFood behaviors and exposes the same result to settlement, Tooltip, and host truth
/// checks without reading JSON or scanning game data during a tick.
/// </summary>
internal static class FoodRuleRuntime
{
    private static readonly IReadOnlyDictionary<string, double> EmptyValues =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> hungerValues = EmptyValues;
    private static IReadOnlyDictionary<string, double> sanityValues = EmptyValues;
    private static FoodRuleResolver resolver = new(EmptyValues, EmptyValues);

    internal static void SetHunger(IReadOnlyDictionary<string, double> values)
    {
        hungerValues = Convert(values);
        Rebuild();
    }

    internal static void SetSanity(IReadOnlyDictionary<string, double> values)
    {
        sanityValues = values ?? throw new ArgumentNullException(nameof(values));
        Rebuild();
    }

    internal static bool TryGetHunger(Item? item, out double value)
    {
        value = 0;
        if (item is null || !IsObjectItem(item) || !CanResolveSeedFood(item))
            return false;

        return resolver.TryResolve(GetFacts(item), FoodValueDimension.Hunger, out value);
    }

    internal static bool TryGetSanity(Item? item, out double value)
    {
        value = 0;
        if (item is null || !IsObjectItem(item) || !CanResolveSeedFood(item))
            return false;

        return resolver.TryResolve(GetFacts(item), FoodValueDimension.Sanity, out value);
    }

    internal static bool TryGetHunger(string qualifiedItemId, out double value)
    {
        value = 0;
        if (!TryGetObjectItemId(qualifiedItemId, out var itemId))
            return false;

        return resolver.TryResolve(
            new FoodItemFacts(itemId, FoodRuleSourceKind.None, null),
            FoodValueDimension.Hunger,
            out value
        );
    }

    internal static bool TryGetSanity(string qualifiedItemId, out double value)
    {
        value = 0;
        if (!TryGetObjectItemId(qualifiedItemId, out var itemId))
            return false;

        return resolver.TryResolve(
            new FoodItemFacts(itemId, FoodRuleSourceKind.None, null),
            FoodValueDimension.Sanity,
            out value
        );
    }

    private static void Rebuild()
    {
        resolver = new FoodRuleResolver(hungerValues, sanityValues);
    }

    private static Dictionary<string, double> Convert(
        IReadOnlyDictionary<string, double> values
    )
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        var converted = new Dictionary<string, double>(values.Count, StringComparer.Ordinal);
        foreach (var pair in values)
            converted[pair.Key] = pair.Value;
        return converted;
    }

    private static FoodItemFacts GetFacts(Item item)
    {
        var seedClassification = SeedEdibilityRuntime.Classify(item);
        if (seedClassification == SeedClassification.TargetCropSeed)
        {
            return new FoodItemFacts(item.ItemId, FoodRuleSourceKind.Seed, null);
        }

        if (item is not SObject objectItem)
            return new FoodItemFacts(item.ItemId, FoodRuleSourceKind.None, null);

        var sourceKind = objectItem.preserve.Value switch
        {
            SObject.PreserveType.Wine => FoodRuleSourceKind.Wine,
            SObject.PreserveType.Juice => FoodRuleSourceKind.Juice,
            SObject.PreserveType.Pickle => FoodRuleSourceKind.Pickle,
            SObject.PreserveType.Jelly => FoodRuleSourceKind.Jelly,
            SObject.PreserveType.Roe => FoodRuleSourceKind.Roe,
            SObject.PreserveType.AgedRoe => FoodRuleSourceKind.AgedRoe,
            SObject.PreserveType.Honey => FoodRuleSourceKind.Honey,
            SObject.PreserveType.DriedFruit => FoodRuleSourceKind.DriedFruit,
            SObject.PreserveType.DriedMushroom => FoodRuleSourceKind.DriedMushroom,
            SObject.PreserveType.SmokedFish => FoodRuleSourceKind.SmokedFish,
            _ => FoodRuleSourceKind.None,
        };

        return new FoodItemFacts(
            item.ItemId,
            sourceKind,
            NormalizeSourceId(objectItem.GetPreservedItemId())
        );
    }

    private static bool CanResolveSeedFood(Item item)
    {
        var classification = SeedEdibilityRuntime.Classify(item);
        return classification != SeedClassification.ExplicitlyExcluded
            && (
                classification != SeedClassification.TargetCropSeed
                || SeedEdibilityRuntime.ShouldExposeFoodRules(item)
            );
    }

    private static bool IsObjectItem(Item item)
    {
        return TryGetObjectItemId(item.QualifiedItemId, out _);
    }

    private static bool TryGetObjectItemId(string? qualifiedItemId, out string itemId)
    {
        const string objectPrefix = "(O)";
        itemId = string.Empty;
        if (
            string.IsNullOrWhiteSpace(qualifiedItemId)
            || !qualifiedItemId.StartsWith(objectPrefix, StringComparison.Ordinal)
            || qualifiedItemId.Length <= objectPrefix.Length
        )
        {
            return false;
        }

        itemId = qualifiedItemId[objectPrefix.Length..];
        return true;
    }

    private static string? NormalizeSourceId(string? sourceItemId)
    {
        if (string.IsNullOrWhiteSpace(sourceItemId))
            return null;

        var value = sourceItemId.Trim();
        return value.StartsWith("(O)", StringComparison.Ordinal)
            ? value[3..]
            : value;
    }
}
