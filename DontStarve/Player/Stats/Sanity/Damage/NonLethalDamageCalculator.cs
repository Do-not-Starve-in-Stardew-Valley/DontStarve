#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Damage;

internal readonly record struct NonLethalDamageCalculation(
    NonLethalDamageOperation Operation,
    int BeforeHealth,
    int MaximumHealth,
    int? RequestedDamage,
    int? TargetHealth,
    int FloorHealth,
    int MaximumAllowedDamage
);

internal readonly record struct NonLethalDamageCalculationResult(
    bool Success,
    string Reason,
    NonLethalDamageCalculation? Calculation
);

/// <summary>
/// Pure integer health math shared by both operations. It computes an upper bound or target only;
/// it never reports actual damage and never touches a Farmer or any game state.
/// </summary>
internal static class NonLethalDamageCalculator
{
    internal static bool TryCalculateFloor(
        int maximumHealth,
        out int floorHealth,
        out string reason
    )
    {
        if (maximumHealth <= 0)
        {
            floorHealth = 0;
            reason = "nonlethal.maximum-health-invalid";
            return false;
        }

        // ceil(max * 0.20) for integral health without floating-point drift or int overflow.
        floorHealth = (int)Math.Clamp(((long)maximumHealth + 4L) / 5L, 0L, maximumHealth);
        reason = "nonlethal.floor-calculated";
        return true;
    }

    internal static NonLethalDamageCalculationResult Calculate(
        ApplyDamageUpToFloorRequest request
    )
    {
        if (
            !TryValidateHealth(
                request.BeforeHealth,
                request.MaximumHealth,
                out var floorHealth,
                out var reason
            )
        )
        {
            return Invalid(reason);
        }
        if (request.RequestedDamage <= 0)
            return Invalid("nonlethal.requested-damage-invalid");

        var nonLethalLimit = Math.Max(0, request.BeforeHealth - floorHealth);
        return Valid(
            new NonLethalDamageCalculation(
                NonLethalDamageOperation.ApplyDamageUpToFloor,
                request.BeforeHealth,
                request.MaximumHealth,
                request.RequestedDamage,
                TargetHealth: null,
                floorHealth,
                Math.Min(request.RequestedDamage, nonLethalLimit)
            )
        );
    }

    internal static NonLethalDamageCalculationResult Calculate(
        ReduceToFloorRequest request
    )
    {
        if (
            !TryValidateHealth(
                request.BeforeHealth,
                request.MaximumHealth,
                out var floorHealth,
                out var reason
            )
        )
        {
            return Invalid(reason);
        }

        return Valid(
            new NonLethalDamageCalculation(
                NonLethalDamageOperation.ReduceToFloor,
                request.BeforeHealth,
                request.MaximumHealth,
                RequestedDamage: null,
                TargetHealth: floorHealth,
                floorHealth,
                Math.Max(0, request.BeforeHealth - floorHealth)
            )
        );
    }

    private static bool TryValidateHealth(
        int beforeHealth,
        int maximumHealth,
        out int floorHealth,
        out string reason
    )
    {
        if (!TryCalculateFloor(maximumHealth, out floorHealth, out reason))
            return false;
        if (beforeHealth < 0 || beforeHealth > maximumHealth)
        {
            reason = "nonlethal.before-health-out-of-range";
            return false;
        }

        reason = "nonlethal.health-input-valid";
        return true;
    }

    private static NonLethalDamageCalculationResult Valid(
        NonLethalDamageCalculation calculation
    )
    {
        return new NonLethalDamageCalculationResult(
            true,
            "nonlethal.calculation-valid",
            calculation
        );
    }

    private static NonLethalDamageCalculationResult Invalid(string reason)
    {
        return new NonLethalDamageCalculationResult(false, reason, null);
    }
}
