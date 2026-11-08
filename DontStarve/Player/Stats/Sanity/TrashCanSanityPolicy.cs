#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// Facts captured from one vanilla garbage-can search. The client may report these facts,
/// but the host must validate them before using the resulting plan.
/// </summary>
internal sealed class TrashCanSanityFacts
{
    public string LocationNameOrUniqueName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public string GarbageCanId { get; set; } = string.Empty;

    public bool ReactNpcs { get; set; }

    public bool ResidentSaw { get; set; }

    public bool HasGarbageHat { get; set; }

    public bool HasTrashBook { get; set; }

    public bool HasItem { get; set; }

    public string ItemQualifiedId { get; set; } = string.Empty;

    public int ItemBasePrice { get; set; }

    public int ItemEdibility { get; set; } = -300;

    public bool IsDoubleMegaSuccess { get; set; }

    public string SelectedEntryId { get; set; } = string.Empty;
}

/// <summary>
/// The two ordered parts of a garbage-can Sanity transaction. Applying them separately is
/// intentional: vanilla-style floor-at-zero behavior must happen before any recovery.
/// </summary>
internal readonly record struct TrashCanSanityPlan(
    int Penalty,
    int Recovery
)
{
    internal double NetDelta => Penalty + Recovery;
}

internal static class TrashCanSanityPolicy
{
    internal const string GarbageHatQualifiedItemId = "(H)66";
    internal const string TrashCatalogueQualifiedItemId = "(F)TrashCatalogue";

    internal static string NormalizeGarbageCanId(string id)
    {
        return id switch
        {
            "0" => "JodiAndKent",
            "1" => "EmilyAndHaley",
            "2" => "Mayor",
            "3" => "Museum",
            "4" => "Blacksmith",
            "5" => "Saloon",
            "6" => "Evelyn",
            "7" => "JojaMart",
            _ => id ?? string.Empty,
        };
    }

    internal static bool IsSpecialItem(string? qualifiedItemId)
    {
        return string.Equals(
                qualifiedItemId,
                GarbageHatQualifiedItemId,
                StringComparison.Ordinal
            )
            || string.Equals(
                qualifiedItemId,
                TrashCatalogueQualifiedItemId,
                StringComparison.Ordinal
            );
    }

    internal static TrashCanSanityPlan Evaluate(TrashCanSanityFacts facts)
    {
        if (facts is null)
            throw new ArgumentNullException(nameof(facts));

        var penalty = 0;
        if (!facts.HasGarbageHat)
        {
            penalty -= 10;
            if (facts.ReactNpcs && facts.ResidentSaw)
                penalty -= 10;
        }

        if (!facts.HasItem)
            return new TrashCanSanityPlan(penalty, 0);

        var specialItem = IsSpecialItem(facts.ItemQualifiedId);
        if (facts.IsDoubleMegaSuccess || specialItem)
        {
            // Garbage Hat and Trash Catalogue are always treated as the explosion result,
            // even if another mod changes their item metadata or the selected entry flags.
            return new TrashCanSanityPlan(penalty, 20);
        }

        var recovery = PriceRecovery(facts.ItemBasePrice);
        if (facts.HasTrashBook && facts.ItemEdibility != -300)
            recovery += 5;

        return new TrashCanSanityPlan(penalty, recovery);
    }

    private static int PriceRecovery(int basePrice)
    {
        if (basePrice >= 250)
            return 15;
        if (basePrice >= 100)
            return 10;
        if (basePrice >= 30)
            return 5;
        if (basePrice >= 0)
            return 3;
        return 0;
    }
}
