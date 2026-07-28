#nullable enable

namespace DontStarve.Player.Stats.Sanity.Damage;

/// <summary>
/// Immutable command for the direct-floor mutation seam. It carries no requested physical damage,
/// defense or iframe state because those concepts must not influence ReduceToFloor.
/// </summary>
internal readonly record struct ReduceToFloorExecutionRequest(
    NonLethalDamageContext Context,
    int BeforeHealth,
    int MaximumHealth,
    int FloorHealth,
    int MaximumAllowedDamage
);

/// <summary>Actual direct-floor evidence. Rejected executions must report zero mutation.</summary>
internal readonly record struct ReduceToFloorExecutionResult(
    bool Applied,
    int AppliedDamage,
    int AfterHealth,
    string Reason
)
{
    internal static ReduceToFloorExecutionResult AppliedResult(
        int appliedDamage,
        int afterHealth
    )
    {
        return new ReduceToFloorExecutionResult(
            true,
            appliedDamage,
            afterHealth,
            "nonlethal.reduce-to-floor.applied"
        );
    }

    internal static ReduceToFloorExecutionResult Unavailable(
        int beforeHealth,
        string reason
    )
    {
        return new ReduceToFloorExecutionResult(
            false,
            AppliedDamage: 0,
            AfterHealth: beforeHealth,
            reason
        );
    }
}

/// <summary>
/// Dedicated boundary for one authority-owned write to the health floor. It cannot invoke or
/// substitute the physical ApplyDamageUpToFloor executor.
/// </summary>
internal interface IReduceToFloorExecutor
{
    ReduceToFloorExecutionResult Execute(ReduceToFloorExecutionRequest request);
}
