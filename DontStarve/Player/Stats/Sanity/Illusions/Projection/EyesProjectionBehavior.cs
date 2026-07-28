#nullable enable

using System;
using System.Runtime.CompilerServices;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal static class EyesProjectionContract
{
    internal const string SpeciesId = "sanity.projection.eyes";
    internal const string AnimationProfileId = "sanity.animation.eyes.profile";
    internal const string TextureSlotId = "sanity.asset.eyes.sprite";
    internal const string BlinkStateId = "sanity.animation.eyes.blink";
    internal const int OwnerProximityPixels =
        HarmlessProjectionSpawnPointSelector.TileSize;

    internal static HarmlessProjectionPolicy CreatePolicy()
    {
        // These are fail-closed expectations for the existing metadata slot. The runtime loader
        // still owns the row, source rectangle, physical sheet, and placeholder identity; Eyes
        // gameplay code never names a file or selects a source frame itself.
        var blink = new HarmlessProjectionVisualStatePolicy(
            BlinkStateId,
            BlinkStateId,
            TextureSlotId,
            frameCount: 4,
            frameDurationMilliseconds: 200,
            loop: true,
            expectedPivotSourcePx: new SanityResourcePoint(32, 16),
            expectedDrawScale: 1d
        );
        return new HarmlessProjectionPolicy(
            SpeciesId,
            SanityTierIds.Eyes,
            BlinkStateId,
            BlinkStateId,
            minimumDistanceTiles: 5,
            maximumDistanceTiles: 15,
            attemptIntervalMinutes: 20,
            activeCap: 1,
            hardTtlMinutes: 20,
            candidateAttemptLimit: HarmlessProjectionPolicy.MaximumCandidateAttempts,
            HarmlessProjectionPlacementKind.Ground,
            clearOnTierExit: true,
            HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
            AnimationProfileId,
            Array.AsReadOnly(new[] { blink })
        );
    }
}

internal interface IEyesEnvironmentLightProbe
{
    EnvironmentLightResult Observe(HarmlessProjectionOwnerContext owner);
}

internal sealed class UnavailableEyesEnvironmentLightProbe
    : IEyesEnvironmentLightProbe
{
    public EnvironmentLightResult Observe(HarmlessProjectionOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return EnvironmentLightResult.Fallback(
            EnvironmentLightReasonIds.SnapshotUnavailable,
            capturedAtMinute: 0
        );
    }
}

internal readonly record struct EyesProjectionRuntimeSnapshot(
    EnvironmentLightLevel LightLevel,
    EnvironmentLightEvidenceStatus EvidenceStatus,
    string LightReason,
    bool IsPlaceholder,
    bool IsProvisional,
    string StatusReason
);

/// <summary>
/// Exact-owner Eyes blink consumer. A new instance requires confirmed pitch black; unavailable or
/// fallback evidence remains non-authoritative and can neither create darkness nor clear an active
/// projection as light.
/// </summary>
internal sealed class EyesProjectionBehavior
    : IHarmlessProjectionSpeciesBehavior,
        IHarmlessProjectionSpawnGate
{
    private const double OwnerProximitySquared =
        EyesProjectionContract.OwnerProximityPixels
        * EyesProjectionContract.OwnerProximityPixels;

    private readonly IEyesEnvironmentLightProbe lightProbe;
    private readonly ConditionalWeakTable<HarmlessProjectionInstance, RuntimeState>
        runtimeByInstance = new();

    internal EyesProjectionBehavior(IEyesEnvironmentLightProbe lightProbe)
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
                EyesProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "eyes.spawn-policy-mismatch";
            return false;
        }

        var result = ObserveLight(request.Owner);
        if (!IsConfirmedPitchBlack(result))
        {
            reason = "eyes.spawn-light-not-confirmed";
            return false;
        }

        reason = "eyes.spawn-confirmed-pitch-black";
        return true;
    }

    public HarmlessProjectionExitResolution Resolve(
        HarmlessProjectionInstance instance,
        HarmlessProjectionCleanupReason requestedReason
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        _ = requestedReason;
        return HarmlessProjectionExitResolution.Cleanup;
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
                "eyes.observer-not-owner"
            );
        }
        if (
            !string.Equals(
                instance.SpeciesId,
                EyesProjectionContract.SpeciesId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                instance.StateId,
                EyesProjectionContract.BlinkStateId,
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
                "eyes.owner-update-invalid"
            );
        }

        if (
            DistanceSquared(
                instance.SpawnWorldPixel,
                observerStandingWorldPixel
            ) <= OwnerProximitySquared
        )
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.OwnerApproached,
                "eyes.owner-approached"
            );
        }

        var light = ObserveLight(observer);
        var runtime = runtimeByInstance.GetOrCreateValue(instance);
        Record(runtime, instance, light);
        if (IsConfirmedLit(light))
        {
            runtime.StatusReason = "eyes.confirmed-light-cleanup";
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.LightRestored,
                runtime.StatusReason
            );
        }

        // Only the exact owner context reaches this line. Facing changes one private presentation
        // hint and leaves the immutable spawn origin and Stardew location untouched.
        instance.TryFaceToward(observerStandingWorldPixel);
        instance.SetBehaviorLabel(
            runtime.IsPlaceholder
                ? "eyes.blink.placeholder"
                : "eyes.blink"
        );
        instance.AdvanceVisualState(elapsedMilliseconds);
        runtime.StatusReason = IsConfirmedPitchBlack(light)
            ? "eyes.blink-confirmed-pitch-black"
            : "eyes.blink-light-unconfirmed";
        return Result(
            HarmlessProjectionSpeciesUpdateStatus.Active,
            null,
            runtime.StatusReason
        );
    }

    internal bool TryGetRuntimeSnapshot(
        HarmlessProjectionInstance instance,
        out EyesProjectionRuntimeSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!runtimeByInstance.TryGetValue(instance, out var runtime))
        {
            snapshot = default;
            return false;
        }

        snapshot = new EyesProjectionRuntimeSnapshot(
            runtime.LightLevel,
            runtime.EvidenceStatus,
            runtime.LightReason,
            runtime.IsPlaceholder,
            runtime.IsProvisional,
            runtime.StatusReason
        );
        return true;
    }

    private EnvironmentLightResult ObserveLight(
        HarmlessProjectionOwnerContext owner
    )
    {
        try
        {
            return lightProbe.Observe(owner)
                ?? EnvironmentLightResult.Fallback(
                    EnvironmentLightReasonIds.SnapshotUnavailable,
                    capturedAtMinute: 0
                );
        }
        catch (Exception)
        {
            return EnvironmentLightResult.Fallback(
                "environment-light.eyes-probe-failed",
                capturedAtMinute: 0
            );
        }
    }

    private static void Record(
        RuntimeState runtime,
        HarmlessProjectionInstance instance,
        EnvironmentLightResult light
    )
    {
        runtime.LightLevel = light.Level;
        runtime.EvidenceStatus = light.EvidenceStatus;
        runtime.LightReason = light.Reason;
        var preview = instance.VisualResource?.VisualPreview;
        runtime.IsPlaceholder = preview?.IsPlaceholder == true;
        runtime.IsProvisional = preview?.IsProvisional == true;
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

        internal bool IsPlaceholder { get; set; }

        internal bool IsProvisional { get; set; }

        internal string StatusReason { get; set; } = "eyes.not-observed";
    }
}
