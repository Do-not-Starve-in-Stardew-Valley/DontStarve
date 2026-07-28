#nullable enable

using System;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Damage;

/// <summary>
/// Game adapter for the original Sanity 4.0 darkness-damage rule. It intentionally performs one
/// controlled health subtraction instead of calling Farmer.takeDamage, whose unrelated random and
/// revival side effects cannot enforce a strict health floor.
/// </summary>
internal sealed class SmapiApplyDamageUpToFloorExecutor : IApplyDamageUpToFloorExecutor
{
    private readonly Func<string, Farmer?> resolvePlayer;

    internal SmapiApplyDamageUpToFloorExecutor(Func<string, Farmer?> resolvePlayer)
    {
        this.resolvePlayer = resolvePlayer ?? throw new ArgumentNullException(nameof(resolvePlayer));
    }

    public ApplyDamageUpToFloorExecutionResult Execute(
        ApplyDamageUpToFloorExecutionRequest request
    )
    {
        Farmer? player;
        try
        {
            player = resolvePlayer(request.Context.PlayerKey);
        }
        catch (Exception)
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.player-resolver-failed"
            );
        }

        if (player is null)
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.player-unavailable"
            );
        }

        var resolvedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!string.Equals(
                resolvedPlayerKey,
                request.Context.PlayerKey,
                StringComparison.Ordinal
            ))
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.player-key-mismatch"
            );
        }
        if (
            player.health != request.BeforeHealth
            || player.maxHealth != request.MaximumHealth
        )
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.health-snapshot-drift"
            );
        }

        var defense = Math.Max(0, player.buffs.Defense);
        if (player.stats.Get("Book_Defense") != 0 && defense < int.MaxValue)
            defense++;

        if (
            !ControlledPhysicalDamageCalculator.TryCalculateAppliedDamage(
                request.RequestedDamage,
                defense,
                request.MaximumAllowedDamage,
                out var appliedDamage,
                out var calculationReason
            )
        )
        {
            return Unavailable(request, calculationReason);
        }
        if (appliedDamage <= 0)
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.no-applicable-damage"
            );
        }

        var afterHealth = request.BeforeHealth - appliedDamage;
        if (afterHealth < request.FloorHealth)
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.floor-invariant-failed"
            );
        }

        // Recheck immediately before the single write so a stale request cannot overwrite another
        // health change observed between resolver lookup and calculation.
        if (
            player.health != request.BeforeHealth
            || player.maxHealth != request.MaximumHealth
        )
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.health-snapshot-drift"
            );
        }

        player.health = afterHealth;
        if (player.health != afterHealth)
        {
            return Unavailable(
                request,
                "nonlethal.apply-damage-up-to-floor.health-write-not-observed"
            );
        }

        return ApplyDamageUpToFloorExecutionResult.AppliedResult(
            appliedDamage,
            afterHealth
        );
    }

    private static ApplyDamageUpToFloorExecutionResult Unavailable(
        ApplyDamageUpToFloorExecutionRequest request,
        string reason
    )
    {
        return ApplyDamageUpToFloorExecutionResult.Unavailable(
            request.BeforeHealth,
            reason
        );
    }
}
