#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Runtime owner for the six Hunger equipment tables. It loads once at mod startup and exposes
/// only current-slot facts to the minute consumer; it never invents values for missing records.
/// </summary>
internal sealed class HungerEquipmentRuntime
{
    private const string HatPath = "Asset/Hunger/hat.json";
    private const string ShirtPath = "Asset/Hunger/shirt.json";
    private const string PantsPath = "Asset/Hunger/pants.json";
    private const string BootsPath = "Asset/Hunger/boots.json";
    private const string RingPath = "Asset/Hunger/ring.json";
    private const string TrinketPath = "Asset/Hunger/trinket.json";

    private IReadOnlyDictionary<string, HungerEquipmentEffect> hat = Empty;
    private IReadOnlyDictionary<string, HungerEquipmentEffect> shirt = Empty;
    private IReadOnlyDictionary<string, HungerEquipmentEffect> pants = Empty;
    private IReadOnlyDictionary<string, HungerEquipmentEffect> boots = Empty;
    private IReadOnlyDictionary<string, HungerEquipmentEffect> ring = Empty;
    private IReadOnlyDictionary<string, HungerEquipmentEffect> trinket = Empty;

    private static IReadOnlyDictionary<string, HungerEquipmentEffect> Empty =>
        new Dictionary<string, HungerEquipmentEffect>(StringComparer.Ordinal);

    internal void Load(IModHelper helper, IMonitor monitor)
    {
        hat = LoadTable(helper, monitor, HatPath);
        shirt = LoadTable(helper, monitor, ShirtPath);
        pants = LoadTable(helper, monitor, PantsPath);
        boots = LoadTable(helper, monitor, BootsPath);
        ring = LoadTable(helper, monitor, RingPath);
        trinket = LoadTable(helper, monitor, TrinketPath);
    }

    internal HungerEquipmentLoadout Capture(Farmer player)
    {
        return new HungerEquipmentLoadout(
            Get(hat, player.hat.Value),
            Get(shirt, player.shirtItem.Value),
            Get(pants, player.pants.Value),
            Get(boots, player.boots.Value),
            Get(ring, player.leftRing.Value),
            Get(ring, player.rightRing.Value),
            Get(
                trinket,
                player.trinketItems.Count > 0 ? player.trinketItems[0] : null
            )
        );
    }

    private static IReadOnlyDictionary<string, HungerEquipmentEffect> LoadTable(
        IModHelper helper,
        IMonitor monitor,
        string path
    )
    {
        try
        {
            var raw = helper.ModContent.Load<Dictionary<string, HungerEquipmentJsonEntry>>(path);
            if (raw is null)
            {
                monitor.Log(
                    $"Hunger equipment data unavailable ({path}; equipment.missing); using neutral defaults.",
                    LogLevel.Warn
                );
                return Empty;
            }

            var values = new Dictionary<string, HungerEquipmentEffect>(
                StringComparer.Ordinal
            );
            foreach (var pair in raw)
            {
                if (
                    string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Value is null
                    || !IsValid(pair.Value.ConsumptionMultiplier, isMultiplier: true)
                    || !IsValid(pair.Value.FixedRecovery, isMultiplier: false)
                )
                {
                    monitor.Log(
                        $"Hunger equipment data entry was ignored ({path}; equipment.invalid-entry:{pair.Key}).",
                        LogLevel.Warn
                    );
                    continue;
                }

                values[pair.Key] = new HungerEquipmentEffect(
                    pair.Value.ConsumptionMultiplier,
                    pair.Value.FixedRecovery
                );
            }

            return values;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Hunger equipment data failed closed ({path}; equipment.load-failed:{exception.GetType().Name}); using neutral defaults.",
                LogLevel.Error
            );
            return Empty;
        }
    }

    private static HungerEquipmentEffect Get(
        IReadOnlyDictionary<string, HungerEquipmentEffect> values,
        Item? item
    )
    {
        if (item is null)
            return default;

        if (
            item is Ring
            && item.QualifiedItemId.StartsWith("(O)", StringComparison.Ordinal)
        )
            return values.TryGetValue(item.ItemId, out var ringValue) ? ringValue : default;

        return values.TryGetValue(item.ItemId, out var value) ? value : default;
    }

    private static HungerEquipmentEffect Get(
        IReadOnlyDictionary<string, HungerEquipmentEffect> values,
        string? itemId
    )
    {
        return !string.IsNullOrWhiteSpace(itemId)
            && values.TryGetValue(itemId, out var value)
            ? value
            : default;
    }

    private static bool IsValid(double? value, bool isMultiplier)
    {
        return value is null
            || (
                double.IsFinite(value.Value)
                && (isMultiplier ? value.Value > 0d : value.Value >= 0d)
            );
    }

    private sealed class HungerEquipmentJsonEntry
    {
        public double? ConsumptionMultiplier { get; set; }

        public double? FixedRecovery { get; set; }
    }
}
