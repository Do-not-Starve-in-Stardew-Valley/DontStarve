#nullable enable

using System;

namespace DontStarve.Player.Stats.Hunger;

internal readonly record struct HungerEquipmentEffect(
    double? ConsumptionMultiplier = null,
    double? FixedRecoveryPerMinute = null
)
{
    internal double EffectiveConsumptionMultiplier =>
        ConsumptionMultiplier is double value
        && double.IsFinite(value)
        && value > 0d
            ? value
            : 1d;

    internal double EffectiveFixedRecoveryPerMinute =>
        FixedRecoveryPerMinute is double value
        && double.IsFinite(value)
        && value > 0d
            ? value
            : 0d;
}

internal readonly record struct HungerEquipmentLoadout
{
    internal HungerEquipmentEffect Hat { get; }
    internal HungerEquipmentEffect Shirt { get; }
    internal HungerEquipmentEffect Pants { get; }
    internal HungerEquipmentEffect Boots { get; }
    internal HungerEquipmentEffect LeftRing { get; }
    internal HungerEquipmentEffect RightRing { get; }
    internal HungerEquipmentEffect Trinket { get; }

    internal HungerEquipmentLoadout(
        HungerEquipmentEffect hat = default,
        HungerEquipmentEffect shirt = default,
        HungerEquipmentEffect pants = default,
        HungerEquipmentEffect boots = default,
        HungerEquipmentEffect leftRing = default,
        HungerEquipmentEffect rightRing = default,
        HungerEquipmentEffect trinket = default
    )
    {
        Hat = hat;
        Shirt = shirt;
        Pants = pants;
        Boots = boots;
        LeftRing = leftRing;
        RightRing = rightRing;
        Trinket = trinket;
    }
}

internal static class HungerEquipmentRules
{
    internal static double CalculateConsumptionMultiplier(HungerEquipmentLoadout loadout)
    {
        return loadout.Hat.EffectiveConsumptionMultiplier
            * loadout.Shirt.EffectiveConsumptionMultiplier
            * loadout.Pants.EffectiveConsumptionMultiplier
            * loadout.Boots.EffectiveConsumptionMultiplier
            * loadout.LeftRing.EffectiveConsumptionMultiplier
            * loadout.RightRing.EffectiveConsumptionMultiplier
            * loadout.Trinket.EffectiveConsumptionMultiplier;
    }

    internal static double CalculateFixedRecoveryPerMinute(HungerEquipmentLoadout loadout)
    {
        return loadout.Hat.EffectiveFixedRecoveryPerMinute
            + loadout.Shirt.EffectiveFixedRecoveryPerMinute
            + loadout.Pants.EffectiveFixedRecoveryPerMinute
            + loadout.Boots.EffectiveFixedRecoveryPerMinute
            + loadout.LeftRing.EffectiveFixedRecoveryPerMinute
            + loadout.RightRing.EffectiveFixedRecoveryPerMinute
            + loadout.Trinket.EffectiveFixedRecoveryPerMinute;
    }

    internal static double CalculateFixedRecoveryForMinutes(
        HungerEquipmentLoadout loadout,
        long validMinutes
    )
    {
        if (validMinutes <= 0)
            return 0d;

        return CalculateFixedRecoveryPerMinute(loadout) * validMinutes;
    }
}
