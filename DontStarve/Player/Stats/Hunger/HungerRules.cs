#nullable enable

using System;

namespace DontStarve.Player.Stats.Hunger;

internal enum HungerDamageMode
{
    Default,
    NonLethal,
    Disabled,
}

internal readonly record struct HungerNaturalMinuteResult(
    double RawCurrent,
    double BoundedCurrent,
    bool DamageDue
);

internal readonly record struct HungerDamageResult(
    int Damage,
    int RemainingHealth,
    int NonLethalFloor
);

/// <summary>
/// 睡眠阶段的 Hunger→Sanity 计费输入。它不参与普通自然 Hunger 消耗。
/// </summary>
internal readonly record struct HungerSleepCostRule(double HungerCostPerSanity)
{
    internal static HungerSleepCostRule Default => new(1d);

    internal double CalculateHungerCost(double sanityRecovery)
    {
        if (!double.IsFinite(sanityRecovery) || sanityRecovery <= 0d)
            return 0d;

        return sanityRecovery * NormalizeCostPerSanity(HungerCostPerSanity);
    }

    internal double CalculateSanityRecovery(double availableHunger)
    {
        if (!double.IsFinite(availableHunger) || availableHunger <= 0d)
            return 0d;

        return availableHunger / NormalizeCostPerSanity(HungerCostPerSanity);
    }

    private static double NormalizeCostPerSanity(double value) =>
        double.IsFinite(value) && value > 0d ? value : 1d;
}

internal static class HungerRules
{
    internal const double DefaultMaximum = 150d;
    internal const double NaturalDecayPerMinute = 0.052d;
    internal const long MinutesPerDay = 1_440L;
    internal const double DailyNaturalDecay = NaturalDecayPerMinute * MinutesPerDay;
    internal const double ReviveFraction = 2d / 3d;
    internal const double NonLethalFloorFraction = 0.2d;
    internal const int DamagePerMinute = 1;

    internal static double CalculateNaturalLoss(
        long validMinutes,
        double consumptionMultiplier = 1d
    )
    {
        if (validMinutes <= 0)
            return 0d;

        var multiplier = NormalizeConsumptionMultiplier(consumptionMultiplier);
        return NaturalDecayPerMinute * validMinutes * multiplier;
    }

    internal static HungerNaturalMinuteResult ApplyNaturalMinute(
        double current,
        double maximum = DefaultMaximum,
        double consumptionMultiplier = 1d,
        double fixedRecovery = 0d
    )
    {
        var safeFixedRecovery = double.IsFinite(fixedRecovery) && fixedRecovery > 0d
            ? fixedRecovery
            : 0d;
        var rawCurrent = current
            - CalculateNaturalLoss(1, consumptionMultiplier)
            + safeFixedRecovery;

        return new(
            rawCurrent,
            ClampForBoundary(rawCurrent, maximum),
            double.IsFinite(current) && current <= 0d
        );
    }

    internal static double ClampForBoundary(
        double value,
        double maximum = DefaultMaximum
    )
    {
        if (!double.IsFinite(maximum) || maximum <= 0d)
            return 0d;
        if (!double.IsFinite(value) || value <= 0d)
            return 0d;
        if (value >= maximum)
            return maximum;
        return value;
    }

    internal static double CalculateReviveValue(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 0d)
            return 0d;

        return maximum * ReviveFraction;
    }

    internal static HungerDamageResult CalculateDamage(
        HungerDamageMode mode,
        int currentHealth,
        int maximumHealth
    )
    {
        var safeCurrentHealth = Math.Max(0, currentHealth);
        var safeMaximumHealth = Math.Max(0, maximumHealth);
        var nonLethalFloor = CalculateNonLethalFloor(safeMaximumHealth);

        if (mode == HungerDamageMode.Disabled || safeCurrentHealth == 0)
            return new(0, safeCurrentHealth, nonLethalFloor);

        var floor = mode == HungerDamageMode.NonLethal ? nonLethalFloor : 0;
        if (safeCurrentHealth <= floor)
            return new(0, safeCurrentHealth, nonLethalFloor);

        var damage = Math.Min(DamagePerMinute, safeCurrentHealth - floor);
        return new(damage, safeCurrentHealth - damage, nonLethalFloor);
    }

    private static int CalculateNonLethalFloor(int maximumHealth)
    {
        if (maximumHealth <= 0)
            return 0;

        return (int)Math.Ceiling(maximumHealth * NonLethalFloorFraction);
    }

    private static double NormalizeConsumptionMultiplier(double value) =>
        double.IsFinite(value) && value > 0d ? value : 1d;

    internal static bool TryParseDamageMode(
        string? value,
        out HungerDamageMode mode
    )
    {
        if (
            !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<HungerDamageMode>(value, ignoreCase: false, out mode)
            && Enum.IsDefined(typeof(HungerDamageMode), mode)
        )
        {
            return true;
        }

        mode = HungerDamageMode.Default;
        return false;
    }
}
