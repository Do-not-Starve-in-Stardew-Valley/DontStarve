#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Damage;

/// <summary>
/// Immutable command for the one stage-04 mutation seam. The executor must verify the live player
/// still matches this snapshot before changing health.
/// </summary>
internal readonly record struct ApplyDamageUpToFloorExecutionRequest(
    NonLethalDamageContext Context,
    int BeforeHealth,
    int MaximumHealth,
    int RequestedDamage,
    int FloorHealth,
    int MaximumAllowedDamage
);

/// <summary>Actual executor evidence. Rejected executions must report zero mutation.</summary>
internal readonly record struct ApplyDamageUpToFloorExecutionResult(
    bool Applied,
    int AppliedDamage,
    int AfterHealth,
    string Reason
)
{
    internal static ApplyDamageUpToFloorExecutionResult AppliedResult(
        int appliedDamage,
        int afterHealth
    )
    {
        return new ApplyDamageUpToFloorExecutionResult(
            true,
            appliedDamage,
            afterHealth,
            "nonlethal.apply-damage-up-to-floor.applied"
        );
    }

    internal static ApplyDamageUpToFloorExecutionResult Unavailable(
        int beforeHealth,
        string reason
    )
    {
        return new ApplyDamageUpToFloorExecutionResult(
            false,
            AppliedDamage: 0,
            AfterHealth: beforeHealth,
            reason
        );
    }
}

/// <summary>
/// Injectable boundary between the pure authority/receipt service and the game-owned health field.
/// It is deliberately dedicated to ApplyDamageUpToFloor and cannot execute ReduceToFloor.
/// </summary>
internal interface IApplyDamageUpToFloorExecutor
{
    ApplyDamageUpToFloorExecutionResult Execute(
        ApplyDamageUpToFloorExecutionRequest request
    );
}

/// <summary>
/// Deterministic darkness-damage rule from the original 4.0 requirement: defense absorbs raw
/// physical damage, then the remaining damage is capped by the twenty-percent health floor.
/// </summary>
internal static class ControlledPhysicalDamageCalculator
{
    internal static bool TryCalculateAppliedDamage(
        int requestedDamage,
        int defense,
        int maximumAllowedDamage,
        out int appliedDamage,
        out string reason
    )
    {
        if (requestedDamage <= 0)
        {
            appliedDamage = 0;
            reason = "nonlethal.controlled-damage.requested-damage-invalid";
            return false;
        }
        if (defense < 0)
        {
            appliedDamage = 0;
            reason = "nonlethal.controlled-damage.defense-invalid";
            return false;
        }
        if (maximumAllowedDamage < 0 || maximumAllowedDamage > requestedDamage)
        {
            appliedDamage = 0;
            reason = "nonlethal.controlled-damage.maximum-allowed-invalid";
            return false;
        }
        if (maximumAllowedDamage == 0)
        {
            appliedDamage = 0;
            reason = "nonlethal.controlled-damage.floor-reached";
            return true;
        }

        // Stardew health is integral. Mirror its deterministic defense rule without importing the
        // unrelated jitter, Yoba, trinket, Phoenix or iframe side effects from Farmer.takeDamage.
        var afterDefense = Math.Max(1L, (long)requestedDamage - defense);
        appliedDamage = (int)Math.Min(afterDefense, maximumAllowedDamage);
        reason = "nonlethal.controlled-damage.calculated";
        return true;
    }
}
