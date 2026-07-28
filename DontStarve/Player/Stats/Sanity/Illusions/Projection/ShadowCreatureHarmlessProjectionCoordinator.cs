#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal enum ShadowCreatureProjectionUpdateStatus
{
    Inactive,
    Waiting,
    AtCap,
    Spawned,
    ConversionLocked,
    Unavailable,
}

internal readonly record struct ShadowCreatureProjectionUpdateResult(
    ShadowCreatureProjectionUpdateStatus Status,
    int ProximityCleanupCount,
    int Occupancy,
    int SpawnedCount,
    string Reason
);

internal readonly record struct ShadowCreatureProjectionTransitionResult(
    int LocalCleanupCount,
    int IntentCount,
    bool Duplicate,
    string Reason
);

/// <summary>
/// Pure stage-08 adapter between the existing per-owner shadow budget and mod-private harmless
/// projections. It owns no Stardew collection and its conversion seam records responsibility only.
/// </summary>
internal sealed class ShadowCreatureHarmlessProjectionCoordinator
{
    private const int MaximumRetainedConversionEvidence = 64;
    private const double OwnerProximitySquared =
        ShadowCreatureHarmlessProjectionCatalog.OwnerProximityPixels
        * ShadowCreatureHarmlessProjectionCatalog.OwnerProximityPixels;

    private sealed class OwnerPhase
    {
        internal bool ShadowTierActive { get; set; }

        internal bool DangerTierActive { get; set; }

        internal bool RequiresFutureReverseResolution { get; set; }

        internal int NextSpeciesIndex { get; set; }

        internal long? LastBudgetEvaluationMinute { get; set; }

        internal int LastBudgetEvaluationOccupancy { get; set; } = -1;

        internal ShadowCreatureProjectionUpdateStatus LastBudgetStatus { get; set; } =
            ShadowCreatureProjectionUpdateStatus.Waiting;
    }

    private readonly ShadowCreatureHarmlessProjectionIndex index;
    private readonly IShadowCreatureProjectionBudgetAuthority budgetAuthority;
    private readonly IShadowProjectionConversionIntentSink conversionIntentSink;
    private readonly IShadowProjectionCorrelationSource correlationSource;
    private readonly List<ShadowCreatureHarmlessProjectionPolicy> policies = new();
    private readonly HashSet<string> policySpeciesIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnerPhase> phasesByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShadowProjectionConversionEvidence>
        evidenceByCorrelation = new(StringComparer.Ordinal);
    private readonly Queue<string> evidenceOrder = new();

    internal ShadowCreatureHarmlessProjectionCoordinator(
        ShadowCreatureHarmlessProjectionIndex index,
        IShadowCreatureProjectionBudgetAuthority budgetAuthority,
        IShadowProjectionConversionIntentSink conversionIntentSink,
        IShadowProjectionCorrelationSource correlationSource
    )
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        this.budgetAuthority = budgetAuthority
            ?? throw new ArgumentNullException(nameof(budgetAuthority));
        this.conversionIntentSink = conversionIntentSink
            ?? throw new ArgumentNullException(nameof(conversionIntentSink));
        this.correlationSource = correlationSource
            ?? throw new ArgumentNullException(nameof(correlationSource));
    }

    internal ShadowCreatureHarmlessProjectionIndex Index => index;

    internal int ConversionEvidenceCount => evidenceByCorrelation.Count;

    internal IReadOnlyList<ShadowCreatureHarmlessProjectionPolicy> Policies =>
        policies.AsReadOnly();

    internal bool RegisterPolicy(
        ShadowCreatureHarmlessProjectionPolicy policy,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(policy.SpeciesId))
        {
            reason = "shadow-policy.species-not-authorized";
            return false;
        }
        if (!policySpeciesIds.Add(policy.SpeciesId))
        {
            reason = "shadow-policy.species-duplicate";
            return false;
        }

        policies.Add(policy);
        reason = "shadow-policy.registered";
        return true;
    }

    internal ShadowCreatureProjectionTransitionResult ApplyStateEvent(
        SanityStateEvent stateEvent,
        long gameMinute
    )
    {
        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemEnabled:
                return Transition("shadow-state.system-enabled");
            case SanityStateEventKind.SystemDisabled:
                return new ShadowCreatureProjectionTransitionResult(
                    CleanupAll(
                        HarmlessProjectionCleanupReason.ConfigDisabled,
                        clearOwnerPhases: true,
                        clearConversionEvidence: false
                    ),
                    0,
                    false,
                    "shadow-state.system-disabled"
                );
            case SanityStateEventKind.WorldCleanup:
                return new ShadowCreatureProjectionTransitionResult(
                    CleanupAll(
                        HarmlessProjectionCleanupReason.WorldCleanup,
                        clearOwnerPhases: true,
                        clearConversionEvidence: true
                    ),
                    0,
                    false,
                    "shadow-state.world-cleaned"
                );
            case SanityStateEventKind.OwnerInvalidated:
                return new ShadowCreatureProjectionTransitionResult(
                    CleanupOwner(
                        stateEvent.PlayerKey,
                        HarmlessProjectionCleanupReason.OwnerInvalidated,
                        forgetOwnerPhase: true
                    ),
                    0,
                    false,
                    "shadow-state.owner-invalidated"
                );
            case SanityStateEventKind.TierEntered:
            case SanityStateEventKind.TierExited:
                break;
            default:
                return Transition("shadow-state.event-unsupported");
        }

        if (!SanityPlayerKey.IsCanonical(stateEvent.PlayerKey))
            return Transition("shadow-state.player-key-invalid");

        var entered = stateEvent.Kind == SanityStateEventKind.TierEntered;
        if (string.Equals(stateEvent.TierId, SanityTierIds.ShadowCreatures, StringComparison.Ordinal))
        {
            if (entered)
            {
                var phase = GetOrCreatePhase(stateEvent.PlayerKey);
                var wasActive = phase.ShadowTierActive;
                phase.ShadowTierActive = true;
                // A real shadow-tier re-entry is the only stage-08 path that unlocks fresh local
                // scheduling. A 17.5% danger exit alone remains reserved for task family 07.
                if (!wasActive)
                {
                    phase.RequiresFutureReverseResolution = phase.DangerTierActive;
                    phase.LastBudgetEvaluationMinute = null;
                    phase.LastBudgetEvaluationOccupancy = -1;
                    phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Waiting;
                }
                return Transition("shadow-state.shadow-tier-entered");
            }

            var removed = CleanupOwner(
                stateEvent.PlayerKey,
                HarmlessProjectionCleanupReason.TierExited,
                forgetOwnerPhase: true
            );
            return new ShadowCreatureProjectionTransitionResult(
                removed,
                0,
                false,
                "shadow-state.shadow-tier-exited"
            );
        }

        if (string.Equals(stateEvent.TierId, SanityTierIds.Danger, StringComparison.Ordinal))
        {
            var phase = GetOrCreatePhase(stateEvent.PlayerKey);
            if (!entered)
            {
                phase.DangerTierActive = false;
                phase.RequiresFutureReverseResolution = phase.ShadowTierActive;
                return Transition("shadow-state.danger-tier-exited-future-resolution-required");
            }

            if (phase.DangerTierActive)
            {
                return new ShadowCreatureProjectionTransitionResult(
                    0,
                    0,
                    true,
                    "shadow-conversion.danger-entry-duplicate"
                );
            }

            phase.DangerTierActive = true;
            phase.RequiresFutureReverseResolution = true;
            return RecordDangerEntry(stateEvent.PlayerKey, gameMinute);
        }

        // The 10% tier changes only the task-family 02 hostile cap. Stage 08 never changes local
        // harmless eligibility or refresh behavior in response to it.
        return Transition("shadow-state.tier-not-consumed-by-stage-08");
    }

    internal ShadowCreatureProjectionUpdateResult UpdateOwner(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        long gameMinute,
        int elapsedMilliseconds,
        IShadowCreatureHarmlessProjectionSpawnFactory spawnFactory
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(spawnFactory);
        if (
            !ownerStandingWorldPixel.IsFinite
            || gameMinute < 0
            || elapsedMilliseconds < 0
        )
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                0,
                owner.PlayerKey,
                0,
                "shadow-projection.owner-update-invalid"
            );
        }

        var proximityCleanup = UpdateLocalInstances(
            owner,
            ownerStandingWorldPixel,
            elapsedMilliseconds
        );
        if (!phasesByOwner.TryGetValue(owner.PlayerKey, out var phase))
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Inactive,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.owner-phase-missing"
            );
        }
        if (!phase.ShadowTierActive)
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Inactive,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.shadow-tier-inactive"
            );
        }
        if (phase.DangerTierActive || phase.RequiresFutureReverseResolution)
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.ConversionLocked,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.future-reverse-resolution-required"
            );
        }
        if (policies.Count == 0)
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.no-species-registered"
            );
        }

        var occupancy = index.CountForOwner(owner.PlayerKey);
        if (
            phase.LastBudgetEvaluationMinute == gameMinute
            && phase.LastBudgetEvaluationOccupancy == occupancy
        )
        {
            return UpdateResult(
                phase.LastBudgetStatus,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.budget-minute-already-evaluated"
            );
        }
        SanityShadowBudgetEvaluationResult budget;
        try
        {
            budget = budgetAuthority.EvaluateShadowBudget(
                owner.PlayerKey,
                gameMinute,
                occupancy
            );
        }
        catch (Exception exception)
        {
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                string.Concat(
                    "shadow-projection.budget-threw-",
                    exception.GetType().Name
                )
            );
        }

        phase.LastBudgetEvaluationMinute = gameMinute;
        phase.LastBudgetEvaluationOccupancy = occupancy;

        if (
            budget.Status != SanityShadowBudgetEvaluationStatus.PermitGranted
            || !budget.Permit.HasValue
        )
        {
            var status = budget.Status == SanityShadowBudgetEvaluationStatus.PausedAtCap
                ? ShadowCreatureProjectionUpdateStatus.AtCap
                : budget.Status is SanityShadowBudgetEvaluationStatus.Waiting
                    ? ShadowCreatureProjectionUpdateStatus.Waiting
                    : budget.Status is SanityShadowBudgetEvaluationStatus.Inactive
                        or SanityShadowBudgetEvaluationStatus.SystemDisabled
                        ? ShadowCreatureProjectionUpdateStatus.Inactive
                        : ShadowCreatureProjectionUpdateStatus.Unavailable;
            phase.LastBudgetStatus = status;
            return UpdateResult(
                status,
                proximityCleanup,
                owner.PlayerKey,
                0,
                budget.Reason
            );
        }

        var policy = policies[phase.NextSpeciesIndex % policies.Count];
        phase.NextSpeciesIndex = (phase.NextSpeciesIndex + 1) % policies.Count;
        var permit = budget.Permit.Value;
        if (
            !ShadowCreatureProjectionPermitGate.TryAuthorize(
                policy.SpeciesId,
                owner.PlayerKey,
                gameMinute,
                occupancy,
                permit,
                out var permitReason
            )
        )
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Unavailable;
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                permitReason
            );
        }

        string correlationId;
        try
        {
            correlationId = correlationSource.Next(owner.PlayerKey, policy.SpeciesId);
        }
        catch (Exception exception)
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Unavailable;
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                string.Concat(
                    "shadow-projection.correlation-threw-",
                    exception.GetType().Name
                )
            );
        }
        if (
            string.IsNullOrWhiteSpace(correlationId)
            || index.ContainsCorrelation(correlationId)
            || evidenceByCorrelation.ContainsKey(correlationId)
        )
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Unavailable;
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.correlation-invalid-or-duplicate"
            );
        }

        var request = new ShadowCreatureHarmlessProjectionSpawnRequest(
            correlationId,
            owner,
            policy,
            ownerStandingWorldPixel,
            gameMinute,
            permit
        );
        ShadowCreatureHarmlessProjectionSpawnResult result;
        try
        {
            result = spawnFactory.TrySpawn(request)
                ?? ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                    "shadow-projection.spawn-factory-returned-null"
                );
        }
        catch (Exception exception)
        {
            result = ShadowCreatureHarmlessProjectionSpawnResult.Failed(
                string.Concat(
                    "shadow-projection.spawn-factory-threw-",
                    exception.GetType().Name
                )
            );
        }

        if (!result.Success || result.Instance is null)
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Waiting;
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                result.Reason
            );
        }
        if (!IsValidSpawnedInstance(request, result.Instance))
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Unavailable;
            result.Instance.TryMarkCleaned(
                HarmlessProjectionCleanupReason.OwnerInvalidated
            );
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.spawn-instance-invalid"
            );
        }
        if (!index.TryAdd(result.Instance, out var addReason))
        {
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Unavailable;
            result.Instance.TryMarkCleaned(HarmlessProjectionCleanupReason.WorldCleanup);
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Unavailable,
                proximityCleanup,
                owner.PlayerKey,
                0,
                addReason
            );
        }

        phase.LastBudgetEvaluationOccupancy = occupancy + 1;
        phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Waiting;
        return UpdateResult(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            proximityCleanup,
            owner.PlayerKey,
            1,
            result.Reason
        );
    }

    internal bool TryGetConversionEvidence(
        string correlationId,
        out ShadowProjectionConversionEvidence? evidence
    )
    {
        return evidenceByCorrelation.TryGetValue(correlationId, out evidence);
    }

    internal IReadOnlyList<ShadowProjectionConversionEvidence>
        SnapshotConversionEvidence()
    {
        var snapshot = new List<ShadowProjectionConversionEvidence>(
            evidenceByCorrelation.Count
        );
        foreach (var correlationId in evidenceOrder)
        {
            if (evidenceByCorrelation.TryGetValue(correlationId, out var evidence))
                snapshot.Add(evidence);
        }
        return snapshot.AsReadOnly();
    }

    internal int CleanupOwner(
        string playerKey,
        HarmlessProjectionCleanupReason reason,
        bool forgetOwnerPhase = false
    )
    {
        var removed = index.CleanupOwner(playerKey, reason);
        if (forgetOwnerPhase)
            phasesByOwner.Remove(playerKey);
        return removed;
    }

    internal int CleanupAll(
        HarmlessProjectionCleanupReason reason,
        bool clearOwnerPhases,
        bool clearConversionEvidence
    )
    {
        var removed = index.CleanupAll(reason);
        if (clearOwnerPhases)
            phasesByOwner.Clear();
        if (clearConversionEvidence)
        {
            evidenceByCorrelation.Clear();
            evidenceOrder.Clear();
        }
        return removed;
    }

    internal int CleanupMismatchedLocation(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionCleanupReason reason
    )
    {
        return index.CleanupMismatchedLocation(owner, reason);
    }

    internal int CleanupInvalidScreens(
        Func<int, bool> isScreenValid,
        HarmlessProjectionCleanupReason reason
    )
    {
        return index.CleanupInvalidScreens(isScreenValid, reason);
    }

    private ShadowCreatureProjectionTransitionResult RecordDangerEntry(
        string playerKey,
        long gameMinute
    )
    {
        // Detach and mark every local appearance before the responsibility seam is invoked. A sink
        // callback can therefore prove occupancy is already zero even when it delays or fails.
        var removed = index.CleanupOwnerWithSnapshot(
            playerKey,
            HarmlessProjectionCleanupReason.ConversionRequested
        );
        var intentCount = 0;
        foreach (var instance in removed)
        {
            if (evidenceByCorrelation.ContainsKey(instance.CorrelationId))
                continue;

            var intent = new ShadowProjectionConversionIntent(
                instance.CorrelationId,
                instance.Owner.PlayerKey,
                instance.SpeciesId,
                gameMinute
            );
            ShadowProjectionConversionSubmissionResult submission;
            try
            {
                submission = conversionIntentSink.Record(intent);
            }
            catch (Exception exception)
            {
                submission = new ShadowProjectionConversionSubmissionResult(
                    ShadowProjectionConversionSubmissionStatus.Failed,
                    string.Concat(
                        "shadow-conversion.sink-threw-",
                        exception.GetType().Name
                    )
                );
            }

            AddEvidence(new ShadowProjectionConversionEvidence(intent, submission));
            intentCount++;
        }

        return new ShadowCreatureProjectionTransitionResult(
            removed.Count,
            intentCount,
            false,
            "shadow-conversion.local-projections-removed"
        );
    }

    private int UpdateLocalInstances(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        int elapsedMilliseconds
    )
    {
        if (!index.TryGetContextInstances(owner, out var instances) || instances is null)
            return 0;

        List<string>? proximityCleanup = null;
        foreach (var instance in instances)
        {
            if (instance.IsCleanedUp || !instance.Owner.Matches(owner))
                continue;

            var deltaX = ownerStandingWorldPixel.X - instance.SpawnWorldPixel.X;
            var deltaY = ownerStandingWorldPixel.Y - instance.SpawnWorldPixel.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) <= OwnerProximitySquared)
            {
                proximityCleanup ??= new List<string>();
                proximityCleanup.Add(instance.CorrelationId);
                continue;
            }
            instance.AdvanceFrame(elapsedMilliseconds);
        }

        if (proximityCleanup is null)
            return 0;
        var removed = 0;
        foreach (var correlationId in proximityCleanup)
        {
            if (
                index.TryRemove(
                    correlationId,
                    HarmlessProjectionCleanupReason.OwnerApproached,
                    out _
                )
            )
            {
                removed++;
            }
        }
        return removed;
    }

    private OwnerPhase GetOrCreatePhase(string playerKey)
    {
        if (!phasesByOwner.TryGetValue(playerKey, out var phase))
        {
            phase = new OwnerPhase();
            phasesByOwner.Add(playerKey, phase);
        }
        return phase;
    }

    private void AddEvidence(ShadowProjectionConversionEvidence evidence)
    {
        var correlationId = evidence.Intent.CorrelationId;
        if (evidenceByCorrelation.ContainsKey(correlationId))
            return;

        while (evidenceOrder.Count >= MaximumRetainedConversionEvidence)
        {
            var oldest = evidenceOrder.Dequeue();
            evidenceByCorrelation.Remove(oldest);
        }
        evidenceByCorrelation.Add(correlationId, evidence);
        evidenceOrder.Enqueue(correlationId);
    }

    private ShadowCreatureProjectionUpdateResult UpdateResult(
        ShadowCreatureProjectionUpdateStatus status,
        int proximityCleanup,
        string playerKey,
        int spawned,
        string reason
    )
    {
        return new ShadowCreatureProjectionUpdateResult(
            status,
            proximityCleanup,
            index.CountForOwner(playerKey),
            spawned,
            reason
        );
    }

    private static ShadowCreatureProjectionTransitionResult Transition(string reason)
    {
        return new ShadowCreatureProjectionTransitionResult(0, 0, false, reason);
    }

    private static bool IsValidSpawnedInstance(
        ShadowCreatureHarmlessProjectionSpawnRequest request,
        ShadowCreatureHarmlessProjectionInstance instance
    )
    {
        return instance.Owner.Matches(request.Owner)
            && ReferenceEquals(instance.Policy, request.Policy)
            && string.Equals(
                instance.CorrelationId,
                request.CorrelationId,
                StringComparison.Ordinal
            )
            && instance.SpawnedAtMinute == request.GameMinute
            && !instance.IsCleanedUp;
    }
}
