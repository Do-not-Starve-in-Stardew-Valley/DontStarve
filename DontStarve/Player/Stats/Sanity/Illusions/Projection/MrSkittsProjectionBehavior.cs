#nullable enable

using System;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Frozen owner-only Mr.Skitts policy. It consumes the ordinary projection lane and never reads
/// hostile intensity, permit, lighting, or shared-world state.
/// </summary>
internal static class MrSkittsProjectionContract
{
    internal const string SpeciesId = "sanity.projection.mr-skitts";
    internal const string AnimationProfileId = "sanity.animation.mr-skitts.profile";
    internal const string TextureSlotId = "sanity.asset.mr-skitts.sprite";
    internal const string IdleAnimationId = "sanity.animation.mr-skitts.idle";
    internal const string DisappearAnimationId = "sanity.animation.mr-skitts.disappear";
    internal const int OwnerProximityPixels = HarmlessProjectionSpawnPointSelector.TileSize;

    internal static HarmlessProjectionPolicy CreatePolicy()
    {
        return new HarmlessProjectionPolicy(
            SpeciesId,
            SanityTierIds.MrSkitts,
            IdleAnimationId,
            IdleAnimationId,
            minimumDistanceTiles: 5,
            maximumDistanceTiles: 10,
            attemptIntervalMinutes: 20,
            activeCap: 1,
            hardTtlMinutes: 20,
            candidateAttemptLimit: HarmlessProjectionPolicy.MaximumCandidateAttempts,
            HarmlessProjectionPlacementKind.Ground,
            clearOnTierExit: true,
            HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
            AnimationProfileId,
            Array.AsReadOnly(
                new[]
                {
                    new HarmlessProjectionVisualStatePolicy(
                        IdleAnimationId,
                        IdleAnimationId,
                        TextureSlotId,
                        frameCount: 4,
                        frameDurationMilliseconds: 420,
                        loop: true,
                        new SanityResourcePoint(92, 108),
                        expectedDrawScale: 1d
                    ),
                    new HarmlessProjectionVisualStatePolicy(
                        DisappearAnimationId,
                        DisappearAnimationId,
                        TextureSlotId,
                        frameCount: 4,
                        frameDurationMilliseconds: 100,
                        loop: false,
                        new SanityResourcePoint(92, 108),
                        expectedDrawScale: 1d
                    ),
                }
            )
        );
    }
}

/// <summary>
/// Pure owner gate and animation clock. Only exact owner proximity and the tier-exit hook enter
/// disappear; lifecycle/resource failures remain immediate generic cleanup in the host.
/// </summary>
internal sealed class MrSkittsProjectionBehavior : IHarmlessProjectionSpeciesBehavior
{
    private const double OwnerProximitySquared =
        MrSkittsProjectionContract.OwnerProximityPixels
        * MrSkittsProjectionContract.OwnerProximityPixels;

    public HarmlessProjectionExitResolution Resolve(
        HarmlessProjectionInstance instance,
        HarmlessProjectionCleanupReason requestedReason
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (
            !string.Equals(
                instance.SpeciesId,
                MrSkittsProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
            || (
                requestedReason != HarmlessProjectionCleanupReason.OwnerApproached
                && requestedReason != HarmlessProjectionCleanupReason.TierExited
            )
        )
        {
            return HarmlessProjectionExitResolution.Cleanup;
        }

        // First reason wins: owner proximity cannot replace an already-recorded tier exit (or the
        // reverse), which keeps cleanup diagnostics stable across adjacent callbacks.
        instance.TryBeginExit(
            MrSkittsProjectionContract.DisappearAnimationId,
            requestedReason
        );
        return HarmlessProjectionExitResolution.RetainForSpeciesTransition;
    }

    public HarmlessProjectionSpeciesUpdateResult Update(
        HarmlessProjectionInstance instance,
        HarmlessProjectionOwnerContext observer,
        HarmlessProjectionWorldPoint observerStandingWorldPixel,
        int elapsedMilliseconds
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(observer);
        if (!instance.Owner.Matches(observer))
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.IgnoredObserver,
                null,
                "mr-skitts.observer-not-owner"
            );
        }
        if (!observerStandingWorldPixel.IsFinite || elapsedMilliseconds < 0)
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Unavailable,
                null,
                "mr-skitts.owner-update-invalid"
            );
        }
        if (instance.IsCleanedUp)
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Unavailable,
                null,
                "mr-skitts.instance-cleaned"
            );
        }

        if (
            string.Equals(
                instance.StateId,
                MrSkittsProjectionContract.IdleAnimationId,
                StringComparison.Ordinal
            )
        )
        {
            var deltaX = observerStandingWorldPixel.X - instance.SpawnWorldPixel.X;
            var deltaY = observerStandingWorldPixel.Y - instance.SpawnWorldPixel.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) <= OwnerProximitySquared)
            {
                Resolve(instance, HarmlessProjectionCleanupReason.OwnerApproached);
                return Result(
                    HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                    null,
                    "mr-skitts.owner-approached"
                );
            }

            instance.AdvanceVisualState(elapsedMilliseconds);
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Active,
                null,
                "mr-skitts.idle"
            );
        }

        if (
            string.Equals(
                instance.StateId,
                MrSkittsProjectionContract.DisappearAnimationId,
                StringComparison.Ordinal
            )
        )
        {
            if (!instance.AdvanceVisualState(elapsedMilliseconds))
            {
                return Result(
                    HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                    null,
                    "mr-skitts.disappearing"
                );
            }

            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                instance.PendingExitReason
                    ?? HarmlessProjectionCleanupReason.OwnerInvalidated,
                "mr-skitts.disappear-complete"
            );
        }

        return Result(
            HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
            HarmlessProjectionCleanupReason.OwnerInvalidated,
            "mr-skitts.state-unknown"
        );
    }

    private static HarmlessProjectionSpeciesUpdateResult Result(
        HarmlessProjectionSpeciesUpdateStatus status,
        HarmlessProjectionCleanupReason? cleanupReason,
        string reason
    )
    {
        return new HarmlessProjectionSpeciesUpdateResult(status, cleanupReason, reason);
    }
}
