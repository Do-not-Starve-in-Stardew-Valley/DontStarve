#nullable enable

using System;
using System.Runtime.CompilerServices;
using DontStarve.Config;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal static class DarkHandProjectionContract
{
    internal const string SpeciesId = "sanity.projection.dark-hand";
    internal const string AnimationProfileId = "sanity.animation.dark-hand.profile";
    internal const string TextureSlotId = "sanity.asset.dark-hand.sprite";
    internal const string AppearStateId = "sanity.animation.dark-hand.appear";
    internal const string ApproachStateId = "sanity.animation.dark-hand.move";
    // The catch/interact row is owner-private action feedback; shared world changes remain behind
    // the host lease/commit boundary and never execute from this visual state machine.
    internal const string ReturnStateId = "sanity.animation.dark-hand.interact";
    internal const string RetreatStateId = "sanity.animation.dark-hand.retreat";
    internal const string DisappearStateId = "sanity.animation.dark-hand.disappear";
    internal const int OwnerProximityPixels = HarmlessProjectionSpawnPointSelector.TileSize;
    internal const int TargetObservationCadenceMilliseconds = 1000;
    internal const double MovementPixelsPerSecond = 128d;
    internal const double RetreatDistancePixels = 128d;
    internal const double ArrivalTolerancePixels = 4d;

    internal static HarmlessProjectionPolicy CreatePolicy()
    {
        var pivot = new SanityResourcePoint(96, 172);
        return new HarmlessProjectionPolicy(
            SpeciesId,
            SanityTierIds.DarkHand,
            AppearStateId,
            AppearStateId,
            minimumDistanceTiles: 10,
            maximumDistanceTiles: 20,
            attemptIntervalMinutes: 20,
            activeCap: 1,
            hardTtlMinutes: 40,
            candidateAttemptLimit: HarmlessProjectionPolicy.MaximumCandidateAttempts,
            HarmlessProjectionPlacementKind.Ground,
            clearOnTierExit: false,
            HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
            AnimationProfileId,
            Array.AsReadOnly(
                new[]
                {
                    State(AppearStateId, 275, loop: false, pivot),
                    State(ApproachStateId, 300, loop: true, pivot),
                    State(RetreatStateId, 275, loop: false, pivot),
                    State(ReturnStateId, 175, loop: false, pivot),
                    State(DisappearStateId, 90, loop: false, pivot),
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

internal enum DarkHandProjectionMode
{
    Off,
    FireThief,
    Harassment,
    Thief,
}

internal readonly record struct DarkHandModeResolution(
    bool IsAvailable,
    DarkHandProjectionMode Mode,
    string BehaviorLabel,
    string Reason
)
{
    internal bool AllowsVisual => IsAvailable && Mode != DarkHandProjectionMode.Off;
}

internal interface IDarkHandModeResolver
{
    DarkHandModeResolution Resolve();
}

internal sealed class TypedConfigDarkHandModeResolver : IDarkHandModeResolver
{
    private readonly TypedConfigResolver resolver;

    internal TypedConfigDarkHandModeResolver(TypedConfigResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public DarkHandModeResolution Resolve()
    {
        var resolution = resolver.GetEnum(ConfigKeys.DarkHandMode);
        if (!resolution.HasValue)
            return Unavailable(resolution.Reason);

        return resolution.Value switch
        {
            "Off" => Available(DarkHandProjectionMode.Off, "Off"),
            "FireThief" => Available(DarkHandProjectionMode.FireThief, "Fire Thief"),
            "Harassment" => Available(DarkHandProjectionMode.Harassment, "Harassment"),
            "Thief" => Available(DarkHandProjectionMode.Thief, "Thief"),
            _ => Unavailable("dark-hand.mode-value-unknown"),
        };
    }

    private static DarkHandModeResolution Available(
        DarkHandProjectionMode mode,
        string label
    )
    {
        return new DarkHandModeResolution(
            true,
            mode,
            label,
            "dark-hand.mode-available"
        );
    }

    private static DarkHandModeResolution Unavailable(string reason)
    {
        return new DarkHandModeResolution(
            false,
            DarkHandProjectionMode.Off,
            "Off",
            string.IsNullOrWhiteSpace(reason) ? "dark-hand.mode-unavailable" : reason
        );
    }
}

internal sealed class UnavailableDarkHandModeResolver : IDarkHandModeResolver
{
    public DarkHandModeResolution Resolve()
    {
        return new DarkHandModeResolution(
            false,
            DarkHandProjectionMode.Off,
            "Off",
            "dark-hand.mode-resolver-unavailable"
        );
    }
}

internal enum DarkHandLocalTargetStatus
{
    FrozenExplainable,
    Unavailable,
    Rejected,
}

/// <summary>
/// A target observation contains only an immutable summary ID and point. It cannot carry a
/// GameLocation/Object reference, mutation delegate, or any world-capability handle.
/// </summary>
internal readonly record struct DarkHandLocalTargetObservation(
    DarkHandLocalTargetStatus Status,
    string SummaryId,
    HarmlessProjectionWorldPoint? WorldPixel,
    string Reason,
    string OperationId = "",
    long TargetRevision = 0
)
{
    internal static DarkHandLocalTargetObservation FrozenExplainable(
        string summaryId,
        HarmlessProjectionWorldPoint worldPixel,
        string reason
    )
    {
        return new DarkHandLocalTargetObservation(
            DarkHandLocalTargetStatus.FrozenExplainable,
            summaryId,
            worldPixel,
            reason,
            string.Empty,
            0
        );
    }

    internal static DarkHandLocalTargetObservation FrozenExplainable(
        string summaryId,
        string operationId,
        long targetRevision,
        HarmlessProjectionWorldPoint worldPixel,
        string reason
    ) =>
        new(
            DarkHandLocalTargetStatus.FrozenExplainable,
            summaryId,
            worldPixel,
            reason,
            operationId,
            targetRevision
        );

    internal static DarkHandLocalTargetObservation Unavailable(string reason)
    {
        return new DarkHandLocalTargetObservation(
            DarkHandLocalTargetStatus.Unavailable,
            string.Empty,
            null,
            reason
        );
    }

    internal static DarkHandLocalTargetObservation Rejected(string reason)
    {
        return new DarkHandLocalTargetObservation(
            DarkHandLocalTargetStatus.Rejected,
            string.Empty,
            null,
            reason
        );
    }
}

internal interface IDarkHandLocalTargetObserver
{
    DarkHandLocalTargetObservation Observe(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        DarkHandProjectionMode mode
    );

    DarkHandLocalActionFeedback Commit(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        long targetRevision
    );

    bool TryTakeFeedback(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        out DarkHandLocalActionFeedback feedback
    );
}

internal readonly record struct DarkHandLocalActionFeedback(
    bool Submitted,
    bool Applied,
    string Label,
    string Reason
);

/// <summary>
/// Fail-closed fallback used only when the production observer couldn't be constructed. The normal
/// stage-05 path injects SmapiDarkHandLeaseCoordinatorService instead.
/// </summary>
internal sealed class UnavailableDarkHandLocalTargetObserver : IDarkHandLocalTargetObserver
{
    public DarkHandLocalTargetObservation Observe(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        DarkHandProjectionMode mode
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        return DarkHandLocalTargetObservation.Unavailable(
            "dark-hand.target-summary-contract-unavailable"
        );
    }

    public DarkHandLocalActionFeedback Commit(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        long targetRevision
    ) =>
        new(
            Submitted: false,
            Applied: false,
            "Unavailable",
            "dark-hand.commit-transport-unavailable"
        );

    public bool TryTakeFeedback(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        out DarkHandLocalActionFeedback feedback
    )
    {
        feedback = default;
        return false;
    }
}

internal interface IDarkHandEnvironmentLightProbe
{
    EnvironmentLightResult Observe(HarmlessProjectionOwnerContext owner);
}

internal sealed class UnavailableDarkHandEnvironmentLightProbe
    : IDarkHandEnvironmentLightProbe
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

internal readonly record struct DarkHandProjectionRuntimeSnapshot(
    DarkHandProjectionMode Mode,
    string BehaviorLabel,
    DarkHandLocalTargetStatus TargetStatus,
    string TargetReason,
    HarmlessProjectionWorldPoint? TargetWorldPixel,
    HarmlessProjectionCleanupReason? ExitTrigger
);

/// <summary>
/// Pure owner-local DarkHand state machine. It moves only the mod-private projection position,
/// consumes the existing no-damage light result, and never touches world entities or machines.
/// </summary>
internal sealed class DarkHandProjectionBehavior
    : IHarmlessProjectionSpeciesBehavior,
        IHarmlessProjectionSpawnGate
{
    private const double OwnerProximitySquared =
        DarkHandProjectionContract.OwnerProximityPixels
        * DarkHandProjectionContract.OwnerProximityPixels;
    private const double MaximumTargetDistanceSquared =
        20d
        * HarmlessProjectionSpawnPointSelector.TileSize
        * 20d
        * HarmlessProjectionSpawnPointSelector.TileSize;

    private readonly IDarkHandModeResolver modeResolver;
    private readonly IDarkHandEnvironmentLightProbe lightProbe;
    private readonly IDarkHandLocalTargetObserver targetObserver;
    private readonly ConditionalWeakTable<HarmlessProjectionInstance, RuntimeState>
        runtimeByInstance = new();

    internal DarkHandProjectionBehavior(
        IDarkHandModeResolver modeResolver,
        IDarkHandEnvironmentLightProbe lightProbe,
        IDarkHandLocalTargetObserver targetObserver
    )
    {
        this.modeResolver = modeResolver ?? throw new ArgumentNullException(nameof(modeResolver));
        this.lightProbe = lightProbe ?? throw new ArgumentNullException(nameof(lightProbe));
        this.targetObserver = targetObserver
            ?? throw new ArgumentNullException(nameof(targetObserver));
    }

    public bool CanSpawn(HarmlessProjectionSpawnRequest request, out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Policy.SpeciesId, DarkHandProjectionContract.SpeciesId, StringComparison.Ordinal))
        {
            reason = "dark-hand.spawn-policy-mismatch";
            return false;
        }

        var mode = ResolveMode();
        if (!mode.IsAvailable)
        {
            reason = "dark-hand.mode-unavailable";
            return false;
        }
        if (!mode.AllowsVisual)
        {
            reason = "dark-hand.mode-off";
            return false;
        }

        reason = "dark-hand.mode-visual-allowed";
        return true;
    }

    public HarmlessProjectionExitResolution Resolve(
        HarmlessProjectionInstance instance,
        HarmlessProjectionCleanupReason requestedReason
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(instance.SpeciesId, DarkHandProjectionContract.SpeciesId, StringComparison.Ordinal))
            return HarmlessProjectionExitResolution.Cleanup;

        if (requestedReason == HarmlessProjectionCleanupReason.TierExited)
        {
            // DarkHand's frozen >75% rule stops new permits only. Existing instances still obey
            // their return conditions and hard TTL, while generic lifecycle cleanup remains hard.
            return HarmlessProjectionExitResolution.RetainForSpeciesTransition;
        }
        if (
            requestedReason != HarmlessProjectionCleanupReason.OwnerApproached
            && requestedReason != HarmlessProjectionCleanupReason.LightRestored
            && requestedReason != HarmlessProjectionCleanupReason.DarkHandReturned
        )
        {
            return HarmlessProjectionExitResolution.Cleanup;
        }

        instance.TryBeginExit(DarkHandProjectionContract.RetreatStateId, requestedReason);
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
            return Result(HarmlessProjectionSpeciesUpdateStatus.IgnoredObserver, null, "dark-hand.observer-not-owner");
        if (
            !string.Equals(instance.SpeciesId, DarkHandProjectionContract.SpeciesId, StringComparison.Ordinal)
            || !observerStandingWorldPixel.IsFinite
            || elapsedMilliseconds < 0
            || instance.IsCleanedUp
        )
        {
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.OwnerInvalidated,
                "dark-hand.owner-update-invalid"
            );
        }

        var mode = ResolveMode();
        if (!mode.AllowsVisual)
        {
            instance.SetBehaviorLabel("Off");
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.ConfigDisabled,
                mode.IsAvailable ? "dark-hand.mode-off" : "dark-hand.mode-unavailable"
            );
        }

        var runtime = runtimeByInstance.GetOrCreateValue(instance);
        runtime.Mode = mode.Mode;
        runtime.BehaviorLabel = mode.BehaviorLabel;
        RefreshActionFeedback(runtime, observer);
        instance.SetBehaviorLabel(
            string.IsNullOrWhiteSpace(runtime.ActionLabel)
                ? mode.BehaviorLabel
                : string.Concat(mode.BehaviorLabel, " · ", runtime.ActionLabel)
        );

        var confirmedLit = IsConfirmedLit(ObserveLight(observer));
        if (
            confirmedLit
            && (
                string.Equals(instance.StateId, DarkHandProjectionContract.AppearStateId, StringComparison.Ordinal)
                || string.Equals(instance.StateId, DarkHandProjectionContract.ApproachStateId, StringComparison.Ordinal)
            )
        )
        {
            BeginRetreat(
                instance,
                runtime,
                observerStandingWorldPixel,
                HarmlessProjectionCleanupReason.LightRestored
            );
            return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.light-restored-retreat");
        }

        if (string.Equals(instance.StateId, DarkHandProjectionContract.AppearStateId, StringComparison.Ordinal))
        {
            if (instance.AdvanceVisualState(elapsedMilliseconds))
                instance.TransitionState(DarkHandProjectionContract.ApproachStateId);
            return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.appearing");
        }

        if (string.Equals(instance.StateId, DarkHandProjectionContract.ApproachStateId, StringComparison.Ordinal))
        {
            if (DistanceSquared(instance.SpawnWorldPixel, observerStandingWorldPixel) <= OwnerProximitySquared)
            {
                BeginRetreat(
                    instance,
                    runtime,
                    observerStandingWorldPixel,
                    HarmlessProjectionCleanupReason.OwnerApproached
                );
                return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.owner-approached-retreat");
            }

            RefreshTarget(runtime, observer, observerStandingWorldPixel, elapsedMilliseconds);
            var target = runtime.TargetWorldPixel ?? observerStandingWorldPixel;
            var reached = MoveTowards(instance, target, elapsedMilliseconds);
            instance.AdvanceVisualState(elapsedMilliseconds);
            if (
                DistanceSquared(instance.SpawnWorldPixel, observerStandingWorldPixel) <= OwnerProximitySquared
            )
            {
                BeginRetreat(
                    instance,
                    runtime,
                    observerStandingWorldPixel,
                    HarmlessProjectionCleanupReason.OwnerApproached
                );
                return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.owner-approached-retreat");
            }
            if (reached && runtime.TargetWorldPixel.HasValue)
            {
                var feedback = CommitTarget(runtime, observer);
                runtime.TargetReason = feedback.Reason;
                runtime.ActionLabel = feedback.Label;
                instance.SetBehaviorLabel(
                    string.IsNullOrWhiteSpace(feedback.Label)
                        ? mode.BehaviorLabel
                        : string.Concat(mode.BehaviorLabel, " · ", feedback.Label)
                );
                BeginRetreat(
                    instance,
                    runtime,
                    observerStandingWorldPixel,
                    HarmlessProjectionCleanupReason.DarkHandReturned
                );
                return Result(
                    HarmlessProjectionSpeciesUpdateStatus.Transitioning,
                    null,
                    feedback.Reason
                );
            }
            return Result(HarmlessProjectionSpeciesUpdateStatus.Active, null, "dark-hand.approaching");
        }

        if (string.Equals(instance.StateId, DarkHandProjectionContract.RetreatStateId, StringComparison.Ordinal))
        {
            runtime.RetreatDestination ??= CreateRetreatDestination(
                instance.SpawnWorldPixel,
                observerStandingWorldPixel,
                instance.OriginWorldPixel
            );
            MoveTowards(instance, runtime.RetreatDestination.Value, elapsedMilliseconds);
            if (instance.AdvanceVisualState(elapsedMilliseconds))
                instance.TransitionState(DarkHandProjectionContract.ReturnStateId);
            return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.retreating");
        }

        if (string.Equals(instance.StateId, DarkHandProjectionContract.ReturnStateId, StringComparison.Ordinal))
        {
            var reachedOrigin = MoveTowards(instance, instance.OriginWorldPixel, elapsedMilliseconds);
            instance.AdvanceVisualState(elapsedMilliseconds);
            if (reachedOrigin)
            {
                instance.TransitionState(DarkHandProjectionContract.DisappearStateId);
                return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.returned-to-origin");
            }
            return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.returning");
        }

        if (string.Equals(instance.StateId, DarkHandProjectionContract.DisappearStateId, StringComparison.Ordinal))
        {
            if (!instance.AdvanceVisualState(elapsedMilliseconds))
                return Result(HarmlessProjectionSpeciesUpdateStatus.Transitioning, null, "dark-hand.disappearing");
            return Result(
                HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
                HarmlessProjectionCleanupReason.DarkHandReturned,
                "dark-hand.disappear-complete"
            );
        }

        return Result(
            HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
            HarmlessProjectionCleanupReason.OwnerInvalidated,
            "dark-hand.state-unknown"
        );
    }

    internal bool TryGetRuntimeSnapshot(
        HarmlessProjectionInstance instance,
        out DarkHandProjectionRuntimeSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!runtimeByInstance.TryGetValue(instance, out var runtime))
        {
            snapshot = default;
            return false;
        }

        snapshot = new DarkHandProjectionRuntimeSnapshot(
            runtime.Mode,
            runtime.BehaviorLabel,
            runtime.TargetStatus,
            runtime.TargetReason,
            runtime.TargetWorldPixel,
            instance.PendingExitReason
        );
        return true;
    }

    private DarkHandModeResolution ResolveMode()
    {
        try
        {
            return modeResolver.Resolve();
        }
        catch (Exception)
        {
            return new DarkHandModeResolution(
                false,
                DarkHandProjectionMode.Off,
                "Off",
                "dark-hand.mode-resolver-failed"
            );
        }
    }

    private EnvironmentLightResult ObserveLight(HarmlessProjectionOwnerContext owner)
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
                "environment-light.dark-hand-probe-failed",
                capturedAtMinute: 0
            );
        }
    }

    private void RefreshTarget(
        RuntimeState runtime,
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        int elapsedMilliseconds
    )
    {
        runtime.TargetElapsedMilliseconds = SaturatingAdd(
            runtime.TargetElapsedMilliseconds,
            elapsedMilliseconds
        );
        if (
            !runtime.TargetObservationPending
            && runtime.TargetElapsedMilliseconds
                < DarkHandProjectionContract.TargetObservationCadenceMilliseconds
        )
        {
            return;
        }

        runtime.TargetObservationPending = false;
        runtime.TargetElapsedMilliseconds = 0;
        DarkHandLocalTargetObservation observation;
        try
        {
            observation = targetObserver.Observe(
                owner,
                ownerStandingWorldPixel,
                runtime.Mode
            );
        }
        catch (Exception)
        {
            observation = DarkHandLocalTargetObservation.Rejected(
                "dark-hand.target-observer-failed"
            );
        }

        runtime.TargetStatus = observation.Status;
        runtime.TargetReason = string.IsNullOrWhiteSpace(observation.Reason)
            ? "dark-hand.target-observation-reason-missing"
            : observation.Reason;
        runtime.TargetWorldPixel = null;
        runtime.TargetSummaryId = string.Empty;
        runtime.TargetOperationId = string.Empty;
        runtime.TargetRevision = 0;
        if (
            observation.Status != DarkHandLocalTargetStatus.FrozenExplainable
            || string.IsNullOrWhiteSpace(observation.SummaryId)
            || observation.WorldPixel is not { } target
            || !target.IsFinite
            || DistanceSquared(target, ownerStandingWorldPixel) > MaximumTargetDistanceSquared
        )
        {
            if (observation.Status == DarkHandLocalTargetStatus.FrozenExplainable)
            {
                runtime.TargetStatus = DarkHandLocalTargetStatus.Rejected;
                runtime.TargetReason = "dark-hand.target-summary-invalid";
            }
            return;
        }

        runtime.TargetWorldPixel = target;
        runtime.TargetSummaryId = observation.SummaryId;
        runtime.TargetOperationId = observation.OperationId;
        runtime.TargetRevision = observation.TargetRevision;
    }

    private DarkHandLocalActionFeedback CommitTarget(
        RuntimeState runtime,
        HarmlessProjectionOwnerContext owner
    )
    {
        if (
            string.IsNullOrWhiteSpace(runtime.TargetSummaryId)
            || string.IsNullOrWhiteSpace(runtime.TargetOperationId)
            || runtime.TargetRevision <= 0
        )
        {
            return new DarkHandLocalActionFeedback(
                Submitted: false,
                Applied: false,
                "No action",
                "dark-hand.target-has-no-operation-binding"
            );
        }
        try
        {
            return targetObserver.Commit(
                owner,
                runtime.TargetSummaryId,
                runtime.TargetOperationId,
                runtime.TargetRevision
            );
        }
        catch (Exception)
        {
            return new DarkHandLocalActionFeedback(
                Submitted: false,
                Applied: false,
                "Rejected",
                "dark-hand.commit-observer-failed"
            );
        }
    }

    private void RefreshActionFeedback(
        RuntimeState runtime,
        HarmlessProjectionOwnerContext owner
    )
    {
        if (
            string.IsNullOrWhiteSpace(runtime.TargetSummaryId)
            || string.IsNullOrWhiteSpace(runtime.TargetOperationId)
        )
        {
            return;
        }
        try
        {
            if (
                targetObserver.TryTakeFeedback(
                    owner,
                    runtime.TargetSummaryId,
                    runtime.TargetOperationId,
                    out var feedback
                )
            )
            {
                runtime.ActionLabel = feedback.Label;
                runtime.TargetReason = feedback.Reason;
            }
        }
        catch (Exception)
        {
            runtime.ActionLabel = "Rejected";
            runtime.TargetReason = "dark-hand.feedback-observer-failed";
        }
    }

    private void BeginRetreat(
        HarmlessProjectionInstance instance,
        RuntimeState runtime,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        HarmlessProjectionCleanupReason reason
    )
    {
        if (
            instance.TryBeginExit(DarkHandProjectionContract.RetreatStateId, reason)
            || !runtime.RetreatDestination.HasValue
        )
        {
            runtime.RetreatDestination = CreateRetreatDestination(
                instance.SpawnWorldPixel,
                ownerStandingWorldPixel,
                instance.OriginWorldPixel
            );
        }
    }

    private static HarmlessProjectionWorldPoint CreateRetreatDestination(
        HarmlessProjectionWorldPoint current,
        HarmlessProjectionWorldPoint owner,
        HarmlessProjectionWorldPoint origin
    )
    {
        var deltaX = current.X - owner.X;
        var deltaY = current.Y - owner.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(distance) || distance <= double.Epsilon)
        {
            deltaX = origin.X - owner.X;
            deltaY = origin.Y - owner.Y;
            distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }
        if (!double.IsFinite(distance) || distance <= double.Epsilon)
        {
            deltaX = 1d;
            deltaY = 0d;
            distance = 1d;
        }

        return new HarmlessProjectionWorldPoint(
            current.X + ((deltaX / distance) * DarkHandProjectionContract.RetreatDistancePixels),
            current.Y + ((deltaY / distance) * DarkHandProjectionContract.RetreatDistancePixels)
        );
    }

    private static bool MoveTowards(
        HarmlessProjectionInstance instance,
        HarmlessProjectionWorldPoint target,
        int elapsedMilliseconds
    )
    {
        var current = instance.SpawnWorldPixel;
        var deltaX = target.X - current.X;
        var deltaY = target.Y - current.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(distance))
            return false;
        if (distance <= DarkHandProjectionContract.ArrivalTolerancePixels)
        {
            instance.TryMoveTo(target);
            return true;
        }

        var maximumDistance = DarkHandProjectionContract.MovementPixelsPerSecond
            * elapsedMilliseconds
            / 1000d;
        if (maximumDistance <= 0d)
            return false;
        if (distance <= maximumDistance)
        {
            instance.TryMoveTo(target);
            return true;
        }

        instance.TryMoveTo(
            new HarmlessProjectionWorldPoint(
                current.X + ((deltaX / distance) * maximumDistance),
                current.Y + ((deltaY / distance) * maximumDistance)
            )
        );
        return false;
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

    private static int SaturatingAdd(int current, int elapsed)
    {
        return current > int.MaxValue - elapsed ? int.MaxValue : current + elapsed;
    }

    private static HarmlessProjectionSpeciesUpdateResult Result(
        HarmlessProjectionSpeciesUpdateStatus status,
        HarmlessProjectionCleanupReason? cleanupReason,
        string reason
    )
    {
        return new HarmlessProjectionSpeciesUpdateResult(status, cleanupReason, reason);
    }

    private sealed class RuntimeState
    {
        internal bool TargetObservationPending { get; set; } = true;

        internal int TargetElapsedMilliseconds { get; set; }

        internal DarkHandProjectionMode Mode { get; set; }

        internal string BehaviorLabel { get; set; } = string.Empty;

        internal DarkHandLocalTargetStatus TargetStatus { get; set; } =
            DarkHandLocalTargetStatus.Unavailable;

        internal string TargetReason { get; set; } =
            "dark-hand.target-not-observed";

        internal HarmlessProjectionWorldPoint? TargetWorldPixel { get; set; }

        internal string TargetSummaryId { get; set; } = string.Empty;

        internal string TargetOperationId { get; set; } = string.Empty;

        internal long TargetRevision { get; set; }

        internal string ActionLabel { get; set; } = string.Empty;

        internal HarmlessProjectionWorldPoint? RetreatDestination { get; set; }
    }
}
