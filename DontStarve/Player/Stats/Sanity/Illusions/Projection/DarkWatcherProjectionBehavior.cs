#nullable enable

using System;
using System.Runtime.CompilerServices;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal static class DarkWatcherProjectionContract
{
    internal const string SpeciesId = "sanity.projection.dark-watcher";
    internal const string AnimationProfileId =
        "sanity.animation.dark-watcher.profile";
    internal const string TextureSlotId = "sanity.asset.dark-watcher.sprite";
    internal const string AppearStateId =
        "sanity.animation.dark-watcher.appear";
    internal const string IdleStateId = "sanity.animation.dark-watcher.idle";
    internal const string DisappearStateId =
        "sanity.animation.dark-watcher.disappear";
    internal const int OwnerProximityPixels =
        HarmlessProjectionSpawnPointSelector.TileSize;

    internal static HarmlessProjectionPolicy CreatePolicy()
    {
        var pivot = new SanityResourcePoint(452, 124);
        return new HarmlessProjectionPolicy(
            SpeciesId,
            SanityTierIds.DarkWatcher,
            AppearStateId,
            AppearStateId,
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
                    State(AppearStateId, 510, loop: false, pivot),
                    State(IdleStateId, 510, loop: true, pivot),
                    State(DisappearStateId, 150, loop: false, pivot),
                }
            )
        );
    }

    private static HarmlessProjectionVisualStatePolicy State(
        string stateId,
        int frameDurationMilliseconds,
        bool loop,
        SanityResourcePoint pivot
    )
    {
        return new HarmlessProjectionVisualStatePolicy(
            stateId,
            stateId,
            TextureSlotId,
            frameCount: 4,
            frameDurationMilliseconds,
            loop,
            pivot,
            expectedDrawScale: 1d
        );
    }
}

internal enum DarkWatcherFacingTarget
{
    Resting,
    ExplainableLight,
    OwnerFallback,
}

/// <summary>
/// Pure read-only summary from the one environment-light service. A direction is optional because
/// the current public diagnostic does not expose a candidate position; consumers must not rebuild
/// that direction by scanning the world.
/// </summary>
internal readonly record struct DarkWatcherLightObservation(
    EnvironmentLightResult Result,
    HarmlessProjectionWorldPoint? NearestExplainableLightWorldPixel,
    string DirectionReason
)
{
    internal static DarkWatcherLightObservation WithoutDirection(
        EnvironmentLightResult result,
        string reason
    )
    {
        return new DarkWatcherLightObservation(result, null, reason);
    }

    internal static DarkWatcherLightObservation WithExplainableDirection(
        EnvironmentLightResult result,
        HarmlessProjectionWorldPoint worldPixel,
        string reason
    )
    {
        return new DarkWatcherLightObservation(result, worldPixel, reason);
    }
}

internal interface IDarkWatcherEnvironmentLightProbe
{
    DarkWatcherLightObservation Observe(HarmlessProjectionOwnerContext owner);
}

internal sealed class UnavailableDarkWatcherEnvironmentLightProbe
    : IDarkWatcherEnvironmentLightProbe
{
    public DarkWatcherLightObservation Observe(HarmlessProjectionOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return DarkWatcherLightObservation.WithoutDirection(
            EnvironmentLightResult.Fallback(
                EnvironmentLightReasonIds.SnapshotUnavailable,
                capturedAtMinute: 0
            ),
            "dark-watcher.light-direction-unavailable"
        );
    }
}

internal readonly record struct DarkWatcherProjectionRuntimeSnapshot(
    EnvironmentLightLevel LightLevel,
    EnvironmentLightEvidenceStatus EvidenceStatus,
    string LightReason,
    DarkWatcherFacingTarget FacingTarget,
    string DirectionReason,
    HarmlessProjectionWorldPoint? DirectionWorldPixel,
    HarmlessProjectionCleanupReason? ExitTrigger
);

/// <summary>
/// Owner-local harmless Watcher state machine. Only confirmed pitch black permits a new instance;
/// confirmed light exits it. Fallback evidence never becomes darkness or a facing direction.
/// </summary>
internal sealed class DarkWatcherProjectionBehavior
    : IHarmlessProjectionSpeciesBehavior,
        IHarmlessProjectionSpawnGate
{
    private const double OwnerProximitySquared =
        DarkWatcherProjectionContract.OwnerProximityPixels
        * DarkWatcherProjectionContract.OwnerProximityPixels;

    private readonly IDarkWatcherEnvironmentLightProbe lightProbe;
    private readonly ConditionalWeakTable<HarmlessProjectionInstance, RuntimeState>
        runtimeByInstance = new();

    internal DarkWatcherProjectionBehavior(
        IDarkWatcherEnvironmentLightProbe lightProbe
    )
    {
        this.lightProbe = lightProbe
            ?? throw new ArgumentNullException(nameof(lightProbe));
    }

    public bool CanSpawn(HarmlessProjectionSpawnRequest request, out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            !string.Equals(
                request.Policy.SpeciesId,
                DarkWatcherProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "dark-watcher.spawn-policy-mismatch";
            return false;
        }

        var observation = ObserveLight(request.Owner);
        if (!IsConfirmedPitchBlack(observation.Result))
        {
            reason = "dark-watcher.spawn-light-not-confirmed";
            return false;
        }

        reason = "dark-watcher.spawn-confirmed-pitch-black";
        return true;
    }

    public HarmlessProjectionExitResolution Resolve(
        HarmlessProjectionInstance instance,
        HarmlessProjectionCleanupReason requestedReason
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (
            !string.Equals(
                instance.SpeciesId,
                DarkWatcherProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
            || (
                requestedReason != HarmlessProjectionCleanupReason.OwnerApproached
                && requestedReason != HarmlessProjectionCleanupReason.LightRestored
                && requestedReason != HarmlessProjectionCleanupReason.TierExited
            )
        )
        {
            return HarmlessProjectionExitResolution.Cleanup;
        }

        instance.TryBeginExit(
            DarkWatcherProjectionContract.DisappearStateId,
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
                "dark-watcher.observer-not-owner"
            );
        }
        if (
            !string.Equals(
                instance.SpeciesId,
                DarkWatcherProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
            || !observerStandingWorldPixel.IsFinite
            || elapsedMilliseconds < 0
            || instance.IsCleanedUp
        )
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.OwnerInvalidated,
                "dark-watcher.owner-update-invalid"
            );
        }

        if (
            string.Equals(
                instance.StateId,
                DarkWatcherProjectionContract.DisappearStateId,
                StringComparison.Ordinal
            )
        )
        {
            if (!instance.AdvanceVisualState(elapsedMilliseconds))
            {
                return Result(
                    HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                    null,
                    "dark-watcher.disappearing"
                );
            }
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                instance.PendingExitReason
                    ?? HarmlessProjectionCleanupReason.OwnerInvalidated,
                "dark-watcher.disappear-complete"
            );
        }

        var isAppear = string.Equals(
            instance.StateId,
            DarkWatcherProjectionContract.AppearStateId,
            StringComparison.Ordinal
        );
        var isIdle = string.Equals(
            instance.StateId,
            DarkWatcherProjectionContract.IdleStateId,
            StringComparison.Ordinal
        );
        if (!isAppear && !isIdle)
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.OwnerInvalidated,
                "dark-watcher.state-unknown"
            );
        }

        var runtime = runtimeByInstance.GetOrCreateValue(instance);
        var observation = ObserveLight(observer);
        RecordObservation(runtime, observation);
        if (IsConfirmedLit(observation.Result))
        {
            Resolve(instance, HarmlessProjectionCleanupReason.LightRestored);
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                null,
                "dark-watcher.confirmed-light-disappear"
            );
        }

        if (
            DistanceSquared(
                instance.SpawnWorldPixel,
                observerStandingWorldPixel
            ) <= OwnerProximitySquared
        )
        {
            Resolve(instance, HarmlessProjectionCleanupReason.OwnerApproached);
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                null,
                "dark-watcher.owner-approached"
            );
        }

        ApplyFacing(
            instance,
            runtime,
            observation,
            observerStandingWorldPixel
        );
        if (isAppear)
        {
            if (instance.AdvanceVisualState(elapsedMilliseconds))
                instance.TransitionState(DarkWatcherProjectionContract.IdleStateId);
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                null,
                "dark-watcher.appearing"
            );
        }

        instance.AdvanceVisualState(elapsedMilliseconds);
        return Result(
            HarmlessProjectionSpeciesUpdateStatus.Active,
            null,
            "dark-watcher.idle"
        );
    }

    internal bool TryGetRuntimeSnapshot(
        HarmlessProjectionInstance instance,
        out DarkWatcherProjectionRuntimeSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!runtimeByInstance.TryGetValue(instance, out var runtime))
        {
            snapshot = default;
            return false;
        }

        snapshot = new DarkWatcherProjectionRuntimeSnapshot(
            runtime.LightLevel,
            runtime.EvidenceStatus,
            runtime.LightReason,
            runtime.FacingTarget,
            runtime.DirectionReason,
            runtime.DirectionWorldPixel,
            instance.PendingExitReason
        );
        return true;
    }

    private DarkWatcherLightObservation ObserveLight(
        HarmlessProjectionOwnerContext owner
    )
    {
        try
        {
            var observation = lightProbe.Observe(owner);
            if (observation.Result is null)
            {
                return DarkWatcherLightObservation.WithoutDirection(
                    EnvironmentLightResult.Fallback(
                        EnvironmentLightReasonIds.SnapshotUnavailable,
                        capturedAtMinute: 0
                    ),
                    "dark-watcher.light-probe-result-missing"
                );
            }
            return observation;
        }
        catch (Exception)
        {
            return DarkWatcherLightObservation.WithoutDirection(
                EnvironmentLightResult.Fallback(
                    "environment-light.dark-watcher-probe-failed",
                    capturedAtMinute: 0
                ),
                "dark-watcher.light-probe-failed"
            );
        }
    }

    private static void RecordObservation(
        RuntimeState runtime,
        DarkWatcherLightObservation observation
    )
    {
        runtime.LightLevel = observation.Result.Level;
        runtime.EvidenceStatus = observation.Result.EvidenceStatus;
        runtime.LightReason = observation.Result.Reason;
        runtime.DirectionReason = string.IsNullOrWhiteSpace(
            observation.DirectionReason
        )
            ? "dark-watcher.light-direction-reason-missing"
            : observation.DirectionReason;
        runtime.DirectionWorldPixel = null;
        runtime.FacingTarget = DarkWatcherFacingTarget.Resting;
    }

    private static void ApplyFacing(
        HarmlessProjectionInstance instance,
        RuntimeState runtime,
        DarkWatcherLightObservation observation,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel
    )
    {
        if (!IsConfirmedPitchBlack(observation.Result))
        {
            runtime.DirectionReason = "dark-watcher.light-not-confirmed-resting";
            return;
        }

        if (
            observation.NearestExplainableLightWorldPixel is { } lightWorldPixel
        )
        {
            if (lightWorldPixel.IsFinite)
            {
                instance.TryFaceToward(lightWorldPixel);
                runtime.FacingTarget = DarkWatcherFacingTarget.ExplainableLight;
                runtime.DirectionWorldPixel = lightWorldPixel;
                return;
            }
            runtime.DirectionReason =
                "dark-watcher.light-direction-invalid-owner-fallback";
        }

        // Confirmed darkness may permit the visual even when the existing diagnostic cannot
        // expose a source position. Facing the exact owner is deterministic and adds no scan.
        instance.TryFaceToward(ownerStandingWorldPixel);
        runtime.FacingTarget = DarkWatcherFacingTarget.OwnerFallback;
        if (!observation.NearestExplainableLightWorldPixel.HasValue)
        {
            runtime.DirectionReason = string.IsNullOrWhiteSpace(
                observation.DirectionReason
            )
                ? "dark-watcher.light-direction-unavailable-owner-fallback"
                : observation.DirectionReason;
        }
    }

    private static bool IsConfirmedPitchBlack(EnvironmentLightResult result)
    {
        return result.Level == EnvironmentLightLevel.PitchBlack
            && result.EvidenceStatus == EnvironmentLightEvidenceStatus.Confirmed;
    }

    private static bool IsConfirmedLit(EnvironmentLightResult result)
    {
        return result.Level == EnvironmentLightLevel.Lit
            && result.EvidenceStatus == EnvironmentLightEvidenceStatus.Confirmed;
    }

    private static double DistanceSquared(
        HarmlessProjectionWorldPoint left,
        HarmlessProjectionWorldPoint right
    )
    {
        var deltaX = left.X - right.X;
        var deltaY = left.Y - right.Y;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private static HarmlessProjectionSpeciesUpdateResult Result(
        HarmlessProjectionSpeciesUpdateStatus status,
        HarmlessProjectionCleanupReason? cleanupReason,
        string reason
    )
    {
        return new HarmlessProjectionSpeciesUpdateResult(
            status,
            cleanupReason,
            reason
        );
    }

    private sealed class RuntimeState
    {
        internal EnvironmentLightLevel LightLevel { get; set; } =
            EnvironmentLightLevel.Dim;

        internal EnvironmentLightEvidenceStatus EvidenceStatus { get; set; } =
            EnvironmentLightEvidenceStatus.Fallback;

        internal string LightReason { get; set; } =
            EnvironmentLightReasonIds.SnapshotUnavailable;

        internal DarkWatcherFacingTarget FacingTarget { get; set; } =
            DarkWatcherFacingTarget.Resting;

        internal string DirectionReason { get; set; } =
            "dark-watcher.light-not-observed";

        internal HarmlessProjectionWorldPoint? DirectionWorldPixel { get; set; }
    }
}
