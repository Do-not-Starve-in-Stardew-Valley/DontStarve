#nullable enable

using System;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Damage;

/// <summary>
/// Game adapter for the direct health-floor operation used by the later special-death flow. It
/// intentionally ignores defense and iframes and never enters the physical damage pipeline.
/// </summary>
internal sealed class SmapiReduceToFloorExecutor : IReduceToFloorExecutor
{
    private readonly Func<string, Farmer?> resolvePlayer;

    internal SmapiReduceToFloorExecutor(Func<string, Farmer?> resolvePlayer)
    {
        this.resolvePlayer = resolvePlayer ?? throw new ArgumentNullException(nameof(resolvePlayer));
    }

    public ReduceToFloorExecutionResult Execute(ReduceToFloorExecutionRequest request)
    {
        Farmer? player;
        try
        {
            player = resolvePlayer(request.Context.PlayerKey);
        }
        catch (Exception)
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.player-resolver-failed");
        }

        if (player is null)
            return Unavailable(request, "nonlethal.reduce-to-floor.player-unavailable");

        var resolvedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!string.Equals(
                resolvedPlayerKey,
                request.Context.PlayerKey,
                StringComparison.Ordinal
            ))
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.player-key-mismatch");
        }
        if (
            player.health != request.BeforeHealth
            || player.maxHealth != request.MaximumHealth
        )
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.health-snapshot-drift");
        }

        if (
            request.FloorHealth < 0
            || request.FloorHealth > request.MaximumHealth
            || request.BeforeHealth <= request.FloorHealth
            || request.MaximumAllowedDamage
                != request.BeforeHealth - request.FloorHealth
        )
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.target-invariant-failed");
        }

        // Recheck immediately before the only write. Direct-floor semantics deliberately bypass
        // defense, iframes and Farmer.takeDamage instead of simulating them with repeated damage.
        if (
            player.health != request.BeforeHealth
            || player.maxHealth != request.MaximumHealth
        )
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.health-snapshot-drift");
        }

        player.health = request.FloorHealth;
        if (player.health != request.FloorHealth)
        {
            return Unavailable(request, "nonlethal.reduce-to-floor.health-write-not-observed");
        }

        return ReduceToFloorExecutionResult.AppliedResult(
            request.MaximumAllowedDamage,
            request.FloorHealth
        );
    }

    private static ReduceToFloorExecutionResult Unavailable(
        ReduceToFloorExecutionRequest request,
        string reason
    )
    {
        return ReduceToFloorExecutionResult.Unavailable(
            request.BeforeHealth,
            reason
        );
    }
}
