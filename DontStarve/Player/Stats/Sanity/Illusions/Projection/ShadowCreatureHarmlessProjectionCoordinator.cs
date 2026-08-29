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
    private readonly ISanityShadowRealTimeBudgetAuthority? realTimeBudgetAuthority;
    private readonly IShadowProjectionConversionIntentSink conversionIntentSink;
    private readonly IShadowProjectionCorrelationSource correlationSource;
    private readonly List<ShadowCreatureHarmlessProjectionPolicy> policies = new();
    private readonly HashSet<string> policySpeciesIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnerPhase> phasesByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShadowProjectionConversionEvidence>
        evidenceByCorrelation = new(StringComparer.Ordinal);
    private readonly Queue<string> evidenceOrder = new();
    // DIAG-20260809: 驱赶补偿队列——被玩家驱赶/远离消失的影怪延迟 7 秒补刷一只
    // （防玩家把无害影怪清光；不占预算 60 分钟 timer）。
    // DIAG-20260810: 带到期时间（累计真实毫秒），由 ConsumePendingCompensations 按到期过滤。
    private const double CompensationDelayMilliseconds = 7000d;
    private readonly Dictionary<string, double> pendingCompensationDueBySpecies =
        new(StringComparer.Ordinal);
    private double accumulatedElapsedMilliseconds;

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
        realTimeBudgetAuthority = budgetAuthority as ISanityShadowRealTimeBudgetAuthority;
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

    /// <summary>
    /// Reconciles the owner phase with the current Sanity tier snapshot. The initial tier event can
    /// be published before the SMAPI projection host has finished its session wiring, and debug
    /// commands can also arrive after that edge. A snapshot repair must preserve the
    /// future-resolution lock after Danger exits, while a newly observed ShadowCreatures phase
    /// may resume normal scheduling.
    /// </summary>
    internal void SynchronizeOwnerTierState(
        string playerKey,
        bool shadowTierActive,
        bool dangerTierActive
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return;

        var phase = GetOrCreatePhase(playerKey);
        var wasShadowTierActive = phase.ShadowTierActive;
        phase.ShadowTierActive = shadowTierActive;
        phase.DangerTierActive = dangerTierActive;

        if (!wasShadowTierActive && shadowTierActive)
        {
            phase.RequiresFutureReverseResolution = dangerTierActive;
            phase.LastBudgetEvaluationMinute = null;
            phase.LastBudgetEvaluationOccupancy = -1;
            phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Waiting;
        }
        else if (dangerTierActive)
        {
            phase.RequiresFutureReverseResolution = true;
        }
        else if (!shadowTierActive)
        {
            phase.RequiresFutureReverseResolution = false;
        }
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

            // DIAG-20260809: 高理智（>50%）不立即清除——标记全部实例 1 秒淡出，
            // 由行为 tick 完成移除；保留 phase（ShadowTierActive=false 停止新刷），
            // 下次重新进入 ShadowCreatures 档时按 entered 分支解锁调度。
            var exitingPhase = GetOrCreatePhase(stateEvent.PlayerKey);
            exitingPhase.ShadowTierActive = false;
            var fading = index.MarkAllFadingOut(
                stateEvent.PlayerKey,
                ShadowCreatureHarmlessProjectionCatalog.HighSanFadeOutMilliseconds,
                ShadowCreatureHarmlessProjectionInstance
                    .ShadowCreatureProjectionFadeOutKind.HighSan
            );
            return new ShadowCreatureProjectionTransitionResult(
                fading,
                0,
                false,
                "shadow-state.shadow-tier-exited-fade-out"
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
        IShadowCreatureHarmlessProjectionSpawnFactory spawnFactory,
        bool advanceMovement = true
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

        accumulatedElapsedMilliseconds += Math.Max(0, elapsedMilliseconds);
        if (!phasesByOwner.TryGetValue(owner.PlayerKey, out var phase))
        {
            if (index.CountForOwner(owner.PlayerKey) > 0)
            {
                index.MarkAllFadingOut(
                    owner.PlayerKey,
                    ShadowCreatureHarmlessProjectionCatalog.HighSanFadeOutMilliseconds,
                    ShadowCreatureHarmlessProjectionInstance
                        .ShadowCreatureProjectionFadeOutKind.HighSan
                );
            }
            var inactiveCleanup = UpdateLocalInstances(
                owner,
                ownerStandingWorldPixel,
                elapsedMilliseconds,
                advanceMovement
            );
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Inactive,
                inactiveCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.owner-phase-missing"
            );
        }
        if (!phase.ShadowTierActive)
        {
            // DIAG-20260812: 高理智（ShadowCreatures 档未激活）时场上仍可能有调试召唤
            // （ds_spawn harmless）的投影——自然投影只在档内生成、退出时已由 TierExited
            // 触发淡出；调试投影生成时档位可能早已退出（无退出事件），必须在此兜底标记
            // 高理智淡出，否则投影永远卡住不消失（用户实测“高于50%不进入消失流程”）。
            if (index.CountForOwner(owner.PlayerKey) > 0)
            {
                index.MarkAllFadingOut(
                    owner.PlayerKey,
                    ShadowCreatureHarmlessProjectionCatalog.HighSanFadeOutMilliseconds
                );
            }
            var inactiveCleanup = UpdateLocalInstances(
                owner,
                ownerStandingWorldPixel,
                elapsedMilliseconds,
                advanceMovement
            );
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.Inactive,
                inactiveCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.shadow-tier-inactive"
            );
        }
        if (phase.DangerTierActive || phase.RequiresFutureReverseResolution)
        {
            // DIAG-20260826: Danger 下的投影必须先播完生成过渡。生成中的实例仍留在
            // index，由本次更新推进帧；已经进入静息态的实例才提交转换并从本地索引摘除。
            // Danger 退出后仍要推进未完成的生成动画，否则实例会在 future-resolution lock
            // 中永久停留在 Spawning；但只有 Danger 重新激活时才允许提交转换。
            // DIAG-20260827: future-resolution lock 只冻结普通实例的转换/新刷调度。
            // 已经代表隐藏危险实体的绑定投影仍是可见的无害行为实例；Danger 退出后必须
            // 继续走 AdvanceBehavior，否则保护期、游荡、逃离和淡出都会永久停止。
            var bindingCleanup = 0;
            if (index.CountForOwner(owner.PlayerKey) > 0)
            {
                AdvanceSpawnAnimations(owner, elapsedMilliseconds);
                if (phase.DangerTierActive)
                    RecordDangerEntry(owner.PlayerKey, gameMinute);
                else
                {
                    bindingCleanup = UpdateLocalInstances(
                        owner,
                        ownerStandingWorldPixel,
                        elapsedMilliseconds,
                        advanceMovement,
                        bindingOnly: true
                    );
                }
            }
            return UpdateResult(
                ShadowCreatureProjectionUpdateStatus.ConversionLocked,
                bindingCleanup,
                owner.PlayerKey,
                0,
                "shadow-projection.future-reverse-resolution-required"
            );
        }
        var proximityCleanup = UpdateLocalInstances(
            owner,
            ownerStandingWorldPixel,
            elapsedMilliseconds,
            advanceMovement
        );
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
            && realTimeBudgetAuthority is null
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
            budget = realTimeBudgetAuthority is not null
                ? realTimeBudgetAuthority.EvaluateShadowBudgetRealTime(
                    owner.PlayerKey,
                    gameMinute,
                    occupancy,
                    elapsedMilliseconds,
                    requestedSpecies: null
                )
                : budgetAuthority.EvaluateShadowBudget(
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

    /// <summary>
    /// Registers a caller-selected position only after the same harmless-pool permit checks used
    /// by the normal refresh path. The debug command may choose the point, but it cannot bypass
    /// tier state, the real-time interval, the shared cap, or the conversion lock.
    /// </summary>
    internal bool TryRegisterAuthorizedProjection(
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionInstance instance,
        SanityShadowSpawnPermit permit,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.IsCleanedUp || !instance.Owner.Matches(owner))
        {
            reason = "shadow-projection.owner-context-invalid";
            return false;
        }
        if (!phasesByOwner.TryGetValue(owner.PlayerKey, out var phase))
        {
            reason = "shadow-projection.shadow-tier-inactive";
            return false;
        }
        if (!phase.ShadowTierActive)
        {
            reason = "shadow-projection.shadow-tier-inactive";
            return false;
        }
        if (phase.DangerTierActive || phase.RequiresFutureReverseResolution)
        {
            reason = "shadow-projection.future-reverse-resolution-required";
            return false;
        }
        if (
            !policies.Contains(instance.Policy)
            || !ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(
                instance.SpeciesId
            )
        )
        {
            reason = "shadow-projection.species-not-registered";
            return false;
        }

        var occupancy = index.CountForOwner(owner.PlayerKey);
        if (
            !ShadowCreatureProjectionPermitGate.TryAuthorize(
                instance.SpeciesId,
                owner.PlayerKey,
                permit.IssuedAtMinute,
                occupancy,
                permit,
                out reason
            )
        )
        {
            return false;
        }
        if (instance.SpawnedAtMinute != permit.IssuedAtMinute)
        {
            reason = "shadow-projection.spawn-minute-mismatch";
            return false;
        }
        if (!index.TryAdd(instance, out reason))
            return false;

        phase.LastBudgetEvaluationMinute = permit.IssuedAtMinute;
        phase.LastBudgetEvaluationOccupancy = occupancy + 1;
        phase.LastBudgetStatus = ShadowCreatureProjectionUpdateStatus.Waiting;
        reason = "shadow-projection.registered";
        return true;
    }

    /// <summary>
    /// Debug-only registration. It deliberately skips the natural permit, timer, cap and
    /// conversion-lock checks while retaining owner, species and index integrity checks.
    /// </summary>
    internal bool TryRegisterDebugProjection(
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionInstance instance,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.IsCleanedUp || !instance.Owner.Matches(owner))
        {
            reason = "shadow-projection.owner-context-invalid";
            return false;
        }
        if (
            !policies.Contains(instance.Policy)
            || !ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(instance.SpeciesId)
        )
        {
            reason = "shadow-projection.species-not-registered";
            return false;
        }
        return index.TryAdd(instance, out reason);
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

    /// <summary>
    /// DIAG-20260809: 取走并清空到期的驱赶补偿（host 在 UpdateOwner 后调用补刷）。
    /// DIAG-20260810: 延迟 7 秒到期才返回；远离 20 格消失（Far）也计入补偿。
    /// </summary>
    internal IReadOnlyList<string> ConsumePendingCompensations()
    {
        if (pendingCompensationDueBySpecies.Count == 0)
            return Array.Empty<string>();
        var snapshot = new List<string>();
        foreach (var pair in pendingCompensationDueBySpecies)
        {
            if (pair.Value <= accumulatedElapsedMilliseconds)
                snapshot.Add(pair.Key);
        }
        foreach (var speciesId in snapshot)
            pendingCompensationDueBySpecies.Remove(speciesId);
        return snapshot.AsReadOnly();
    }

    /// <summary>DIAG-20260811: 外部登记补偿（切图清投影时调用）——物种在补偿延迟后到期补刷。</summary>
    internal void RecordCompensation(string speciesId)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            return;
        pendingCompensationDueBySpecies[speciesId] =
            accumulatedElapsedMilliseconds + CompensationDelayMilliseconds;
    }

    private ShadowCreatureProjectionTransitionResult RecordDangerEntry(
        string playerKey,
        long gameMinute
    )
    {
        // 生成动画未完成的实例仍是可见投影，不能在转换请求提交前摘掉；只先摘除已经
        // 进入静息/绑定状态的实例。这样 sink 看到的 occupancy 仍只包含未就绪投影。
        var removed = index.CleanupOwnerWithSnapshot(
            playerKey,
            HarmlessProjectionCleanupReason.ConversionRequested,
            instance =>
                instance.AnimationState
                != ShadowCreatureHarmlessProjectionInstance
                    .ShadowCreatureProjectionAnimationState.Spawning
        );
        var intentCount = 0;
        foreach (var instance in removed)
        {
            // DIAG-20260812: 绑定投影代表隐藏的危险实体（已有实体，不参与转化）——
            // 摘除会导致绑定丢失、隐藏实体被连带清除（0san 强制脱战后直接消失 bug）。
            // 重置清理标记放回 Index，由低理智恢复（RestoreAllBindings）或绑定流程管理。
            if (instance.IsBindingProjection)
            {
                if (instance.TryRestoreFromCleanup())
                    index.TryAdd(instance, out _);
                continue;
            }
            if (
                evidenceByCorrelation.TryGetValue(
                    instance.CorrelationId,
                    out var existingEvidence
                )
                && existingEvidence.Submission.Status
                    is not ShadowProjectionConversionSubmissionStatus.Failed
                    and not ShadowProjectionConversionSubmissionStatus.Rejected
            )
            {
                // Confirmed, delayed, and unconfirmed submissions retain their original
                // responsibility evidence and must not be submitted twice. Failed/rejected
                // bridges are retryable: the restored local instance reaches this path again on
                // the next Danger update and the new result replaces the stale evidence.
                continue;
            }

            var intent = new ShadowProjectionConversionIntent(
                instance.CorrelationId,
                instance.Owner.PlayerKey,
                instance.SpeciesId,
                gameMinute,
                // DIAG-20260812: 携带投影原位——转化请求据此在无害影怪原位置生成
                // 危险实体（不再瞬移到玩家脚下）。
                instance.WorldPixel.X,
                instance.WorldPixel.Y
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
            if (
                submission.Status
                    is ShadowProjectionConversionSubmissionStatus.Failed
                    or ShadowProjectionConversionSubmissionStatus.Rejected
            )
            {
                // A definitive failed/rejected bridge did not create a hostile replacement. Put
                // the harmless instance back so a bridge failure cannot make it vanish silently.
                if (instance.TryRestoreFromCleanup())
                    index.TryAdd(instance, out _);
            }
        }

        return new ShadowCreatureProjectionTransitionResult(
            removed.Count,
            intentCount,
            false,
            "shadow-conversion.local-projections-removed"
        );
    }

    private void AdvanceSpawnAnimations(
        HarmlessProjectionOwnerContext owner,
        int elapsedMilliseconds
    )
    {
        if (
            elapsedMilliseconds <= 0
            || !index.TryGetContextInstances(owner, out var instances)
            || instances is null
        )
        {
            return;
        }

        foreach (var instance in instances)
        {
            if (
                !instance.IsCleanedUp
                && instance.AnimationState
                    == ShadowCreatureHarmlessProjectionInstance
                        .ShadowCreatureProjectionAnimationState.Spawning
            )
            {
                instance.AdvanceFrame(elapsedMilliseconds);
            }
        }
    }

    private int UpdateLocalInstances(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        int elapsedMilliseconds,
        bool advanceMovement,
        bool bindingOnly = false
    )
    {
        if (!index.TryGetContextInstances(owner, out var instances) || instances is null)
            return 0;

        // DIAG-20260809: 行为化改造——实例由 AdvanceBehavior 驱动（游荡/驱赶/远离/淡出），
        // 不再用旧的距离触发即清除逻辑；淡出完成的实例在此移除，被驱赶的记入补偿队列。
        List<string>? completedCleanup = null;
        foreach (var instance in instances)
        {
            if (instance.IsCleanedUp || !instance.Owner.Matches(owner))
                continue;
            if (bindingOnly && !instance.IsBindingProjection)
                continue;

            var behaviorEvent = advanceMovement
                ? instance.AdvanceBehavior(
                    elapsedMilliseconds,
                    ownerStandingWorldPixel
                )
                : instance.AdvanceFadeOutOnly(elapsedMilliseconds);
            if (
                behaviorEvent
                != ShadowCreatureHarmlessProjectionInstance
                    .ShadowCreatureProjectionBehaviorEvent.FadeOutCompleted
            )
            {
                continue;
            }

            completedCleanup ??= new List<string>();
            completedCleanup.Add(instance.CorrelationId);
            // DIAG-20260810: 驱赶（Flee）与远离 20 格（Far）淡出完成都计入补偿；
            // 高理智淡出不补偿。补偿延迟 7 秒到期后由宿主补刷。
            if (
                instance.FadeOutKind
                    is ShadowCreatureHarmlessProjectionInstance
                        .ShadowCreatureProjectionFadeOutKind.Flee
                    or ShadowCreatureHarmlessProjectionInstance
                        .ShadowCreatureProjectionFadeOutKind.Far
            )
            {
                pendingCompensationDueBySpecies[instance.SpeciesId] =
                    accumulatedElapsedMilliseconds + CompensationDelayMilliseconds;
            }
        }

        if (completedCleanup is null)
            return 0;
        var removed = 0;
        foreach (var correlationId in completedCleanup)
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
        if (
            evidenceByCorrelation.TryGetValue(correlationId, out var existingEvidence)
        )
        {
            // A failed/rejected submission may be retried after the harmless instance is
            // restored. Keep one bounded queue entry per correlation while replacing only that
            // retryable outcome; a confirmed or otherwise retained result is immutable evidence.
            if (
                existingEvidence.Submission.Status
                    is ShadowProjectionConversionSubmissionStatus.Failed
                    or ShadowProjectionConversionSubmissionStatus.Rejected
            )
            {
                evidenceByCorrelation[correlationId] = evidence;
            }
            return;
        }

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
