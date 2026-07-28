#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity;

internal enum SanityChangeSource
{
    Unknown = 0,
    Food = 1,
    Equipment = 2,
    Npc = 3,
    Junimo = 4,
    Monster = 5,
    Night = 6,
    Mine = 7,
    Sleep = 8,
    Buff = 9,
    Migration = 10,
    Administration = 11,
    DarknessAttack = 12,
    HostileShadowKill = 13,
    VoluntarySleep = 14,
    TimeLimitPassOut = 15,
    ExhaustionPassOut = 16,
    HealthDeath = 17,
    SanityDarknessSpecialDeath = 18,
}

internal enum SanityChangeStatus
{
    Applied,
    NoChange,
    RequestQueued,
    Rejected,
}

internal sealed class SanityChangeResult
{
    private SanityChangeResult(
        SanityChangeStatus status,
        string reason,
        SanityChangeSource source,
        SanityPlayerSnapshot? snapshot,
        SanityChangeRequest? request
    )
    {
        Status = status;
        Reason = reason;
        Source = source;
        Snapshot = snapshot;
        Request = request;
    }

    internal SanityChangeStatus Status { get; }

    internal string Reason { get; }

    internal SanityChangeSource Source { get; }

    internal SanityPlayerSnapshot? Snapshot { get; }

    internal SanityChangeRequest? Request { get; }

    internal static SanityChangeResult Applied(
        SanityPlayerSnapshot snapshot,
        bool changed,
        string reason,
        SanityChangeSource source
    )
    {
        return new SanityChangeResult(
            changed ? SanityChangeStatus.Applied : SanityChangeStatus.NoChange,
            reason,
            source,
            snapshot,
            null
        );
    }

    internal static SanityChangeResult Queued(SanityChangeRequest request)
    {
        return new SanityChangeResult(
            SanityChangeStatus.RequestQueued,
            "client-interaction-request-queued",
            request.Source,
            null,
            request
        );
    }

    internal static SanityChangeResult Rejected(
        string reason,
        SanityChangeSource source = SanityChangeSource.Unknown
    )
    {
        return new SanityChangeResult(
            SanityChangeStatus.Rejected,
            reason,
            source,
            null,
            null
        );
    }
}

internal readonly record struct SanityStateChanged(
    SanityChangeSource Source,
    SanityPlayerSnapshot Snapshot
);

internal readonly record struct SanityRequestPolicy(
    bool ClientRequestAllowed,
    long MinimumIntervalMilliseconds,
    double MaximumAbsoluteDelta,
    string Reason
)
{
    internal static SanityRequestPolicy For(SanityChangeSource source)
    {
        return source == SanityChangeSource.Food
            ? new SanityRequestPolicy(
                true,
                1000,
                SanitySaveData.CurrentDefaultMaxSanity,
                "food-interaction-must-be-recomputed-by-host"
            )
            : new SanityRequestPolicy(
                false,
                0,
                0,
                "change-source-is-host-only"
            );
    }
}

internal interface ISanityRequestTruthSource
{
    SanityRequestTruth Resolve(SanityChangeRequest request, long senderPlayerId);
}

internal readonly record struct SanityRequestTruth(
    bool Success,
    double Delta,
    string Reason
)
{
    internal static SanityRequestTruth Accepted(double delta)
    {
        return new SanityRequestTruth(true, delta, "request-truth-recomputed-by-host");
    }

    internal static SanityRequestTruth Rejected(string reason)
    {
        return new SanityRequestTruth(false, 0, reason);
    }
}

internal sealed class SanityHostRequestResult
{
    internal SanityHostRequestResult(
        bool accepted,
        bool needsSnapshot,
        string reason,
        SanityPlayerSnapshot? snapshot
    )
    {
        Accepted = accepted;
        NeedsSnapshot = needsSnapshot;
        Reason = reason;
        Snapshot = snapshot;
    }

    internal bool Accepted { get; }

    internal bool NeedsSnapshot { get; }

    internal string Reason { get; }

    internal SanityPlayerSnapshot? Snapshot { get; }
}

/// <summary>
/// 唯一 Sanity 变更职责。主机直接结算并递增 revision；客户端不做乐观写入，
/// 只能为白名单交互排队不含 delta 的请求。
/// </summary>
internal sealed class SanityChangeService
{
    private readonly SanityRuntimeStateStore state;
    private readonly SanityTierStateMachine tierState = new();
    private readonly SanityShadowBudgetGovernor shadowBudget;
    private readonly SanityRequestValidator requestValidator = new();
    private readonly HashSet<string> effectiveOverlayOwners =
        new(StringComparer.Ordinal);
    private long nextClientNonce;
    private bool sanitySystemEnabled = true;

    internal SanityChangeService(
        ISanityMaximumProvider maximumProvider,
        ISanityMonsterIntensityProvider? intensityProvider = null
    )
    {
        state = new SanityRuntimeStateStore(maximumProvider);
        shadowBudget = new SanityShadowBudgetGovernor(
            intensityProvider
                ?? new UnavailableSanityMonsterIntensityProvider(
                    "config.runtime-unavailable"
                )
        );
    }

    internal event Action<SanityStateChanged>? HostStateChanged;

    internal event Action<SanityChangeRequest>? ClientRequestCreated;

    internal event Action<SanityStateEvent>? TierStateEventPublished;

    /// <summary>
    /// Publishes the final owner tier snapshot once per accepted revision, including revisions
    /// that do not cross a tier edge. Presentation consumers can stay event-driven instead of
    /// allocating a reconstructed tier list on every update tick.
    /// </summary>
    internal event Action<SanityTierOwnerStateSnapshot>? TierStateObserved;

    internal event Action<string>? TierStateDiagnosticRaised;

    internal event Action<string>? ShadowBudgetDiagnosticRaised;

    internal SanityAuthorityRole Role => state.Role;

    internal string SessionId => state.SessionId;

    internal bool HasActiveSession => state.HasActiveSession;

    internal bool IsSystemEnabled => sanitySystemEnabled;

    internal bool BeginHostSession(
        string sessionId,
        SanityPersistenceResult persistence,
        string masterPlayerKey,
        out string reason
    )
    {
        PublishTierResult(tierState.CleanupWorld());
        nextClientNonce = 0;
        requestValidator.Clear();
        var started = state.BeginHostSession(
            sessionId,
            persistence,
            masterPlayerKey,
            out reason
        );
        if (started && state.TryGetSnapshot(masterPlayerKey, out var snapshot))
            ObserveTierSnapshot(snapshot);
        return started;
    }

    internal bool BeginClientSession(string localPlayerKey, out string reason)
    {
        PublishTierResult(tierState.CleanupWorld());
        nextClientNonce = 0;
        requestValidator.Clear();
        return state.BeginClientSession(localPlayerKey, out reason);
    }

    internal void ClearSession()
    {
        effectiveOverlayOwners.Clear();
        PublishTierResult(tierState.CleanupWorld());
        nextClientNonce = 0;
        requestValidator.Clear();
        state.Clear();
    }

    internal void ForgetPeer(string playerKey)
    {
        requestValidator.Forget(playerKey);
        effectiveOverlayOwners.Remove(playerKey);
        PublishTierResult(tierState.InvalidateOwner(playerKey));
    }

    internal bool TryEnsureHostPlayer(
        string playerKey,
        out SanityPlayerSnapshot snapshot,
        out string reason
    )
    {
        var ensured = state.TryEnsureHostPlayer(playerKey, out snapshot, out reason);
        if (ensured)
            ObserveTierSnapshot(snapshot);
        return ensured;
    }

    internal SanityTierEvaluationResult SetTierSystemEnabled(bool enabled)
    {
        sanitySystemEnabled = enabled;
        var result = tierState.SetSystemEnabled(enabled);
        PublishTierResult(result);
        return result;
    }

    internal bool SetEffectiveOverlayOwnerActive(string playerKey, bool active)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return false;

        if (active)
        {
            if (!effectiveOverlayOwners.Add(playerKey))
                return false;
            PublishTierResult(tierState.InvalidateOwner(playerKey));
            return true;
        }

        if (!effectiveOverlayOwners.Remove(playerKey))
            return false;

        if (state.TryGetSnapshot(playerKey, out var snapshot))
            ObserveTierSnapshot(snapshot);
        return true;
    }

    internal void CleanupAndRebuildDerivedState()
    {
        PublishTierResult(tierState.CleanupWorld());

        var snapshots = state.CreateFullSnapshot().Players;
        if (snapshots.Count == 0)
        {
            PublishTierResult(tierState.SetSystemEnabled(sanitySystemEnabled));
            return;
        }

        foreach (var snapshot in snapshots)
        {
            if (!effectiveOverlayOwners.Contains(snapshot.PlayerKey))
                ObserveTierSnapshot(snapshot);
        }
    }

    internal SanityTierEvaluationResult InvalidateTierOwner(string playerKey)
    {
        var result = tierState.InvalidateOwner(playerKey);
        PublishTierResult(result);
        return result;
    }

    internal bool TryGetTierState(
        string playerKey,
        out SanityTierOwnerStateSnapshot? snapshot
    )
    {
        return tierState.TryGetOwnerState(playerKey, out snapshot);
    }

    /// <summary>
    /// 实体阶段提交同一 owner 的两种影怪总 occupancy；本阶段只返回许可，
    /// 不触碰 location/Monster/Critter 集合。
    /// </summary>
    internal SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
        string playerKey,
        long gameMinute,
        int occupancy
    )
    {
        var result = shadowBudget.Evaluate(playerKey, gameMinute, occupancy);
        if (result.Status == SanityShadowBudgetEvaluationStatus.Unavailable)
            ShadowBudgetDiagnosticRaised?.Invoke(result.Reason);
        return result;
    }

    internal SanityShadowBudgetEvaluationResult EvaluateHostileShadowBudget(
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpecies requestedSpecies
    )
    {
        var result = shadowBudget.EvaluateHostileSpawn(
            playerKey,
            gameMinute,
            occupancy,
            requestedSpecies
        );
        if (
            result.Status is SanityShadowBudgetEvaluationStatus.Unavailable
                or SanityShadowBudgetEvaluationStatus.SpeciesIneligible
        )
        {
            ShadowBudgetDiagnosticRaised?.Invoke(result.Reason);
        }
        return result;
    }

    internal bool TryGetShadowBudgetState(
        string playerKey,
        out SanityShadowBudgetOwnerSnapshot? snapshot
    )
    {
        return shadowBudget.TryGetOwnerState(playerKey, out snapshot);
    }

    internal void UpdatePersistenceBase(SanityPersistenceResult persistence)
    {
        state.UpdatePersistenceBase(persistence);
    }

    internal double GetCurrent(string playerKey)
    {
        if (
            state.Role == SanityAuthorityRole.Host
            && TryEnsureHostPlayer(playerKey, out var snapshot, out _)
        )
        {
            return snapshot.Current;
        }

        return state.GetCurrentOrDefault(playerKey);
    }

    internal double GetMaximum(string playerKey)
    {
        if (
            state.Role == SanityAuthorityRole.Host
            && TryEnsureHostPlayer(playerKey, out var snapshot, out _)
        )
        {
            return snapshot.Maximum;
        }

        return state.GetMaximumOrDefault(playerKey);
    }

    internal SanityChangeResult Change(
        string playerKey,
        double delta,
        SanityChangeSource source,
        string interactionId = ""
    )
    {
        if (source == SanityChangeSource.Unknown)
            return SanityChangeResult.Rejected("change-source-is-unknown");
        if (!double.IsFinite(delta))
            return SanityChangeResult.Rejected("sanity-delta-must-be-finite", source);

        if (state.Role == SanityAuthorityRole.Host)
        {
            if (
                !TryEnsureHostPlayer(
                    playerKey,
                    out var hostSnapshot,
                    out var reason
                )
            )
                return SanityChangeResult.Rejected(reason, source);
            if (!sanitySystemEnabled)
            {
                return SanityChangeResult.Applied(
                    hostSnapshot,
                    false,
                    "sanity-system-disabled",
                    source
                );
            }
            if (effectiveOverlayOwners.Contains(playerKey))
            {
                return SanityChangeResult.Applied(
                    hostSnapshot,
                    false,
                    "sanity-change-frozen-by-effective-overlay",
                    source
                );
            }
            var current = hostSnapshot.Current;
            var requested = current + delta;
            if (!double.IsFinite(requested))
                return SanityChangeResult.Rejected(
                    "sanity-change-result-must-be-finite",
                    source
                );
            return ApplyHostValue(playerKey, requested, source);
        }

        if (state.Role != SanityAuthorityRole.Client)
            return SanityChangeResult.Rejected("sanity-session-is-not-active", source);

        var policy = SanityRequestPolicy.For(source);
        if (!policy.ClientRequestAllowed)
            return SanityChangeResult.Rejected(policy.Reason, source);
        if (string.IsNullOrWhiteSpace(interactionId) || interactionId.Length > 128)
        {
            return SanityChangeResult.Rejected(
                "interaction-id-is-missing-or-too-long",
                source
            );
        }
        if (!SanityProtocol.IsValidSessionId(state.SessionId))
        {
            return SanityChangeResult.Rejected(
                "client-authority-session-is-not-ready",
                source
            );
        }
        if (!state.TryGetSnapshot(playerKey, out var clientSnapshot))
            return SanityChangeResult.Rejected("client-player-state-is-unknown", source);
        if (!sanitySystemEnabled)
        {
            return SanityChangeResult.Applied(
                clientSnapshot,
                false,
                "sanity-system-disabled",
                source
            );
        }
        if (effectiveOverlayOwners.Contains(playerKey))
        {
            return SanityChangeResult.Applied(
                clientSnapshot,
                false,
                "sanity-change-frozen-by-effective-overlay",
                source
            );
        }
        if (nextClientNonce == long.MaxValue)
        {
            return SanityChangeResult.Rejected(
                "client-request-nonce-is-exhausted",
                source
            );
        }

        var request = new SanityChangeRequest
        {
            SessionId = state.SessionId,
            PlayerKey = playerKey,
            Source = source,
            InteractionId = interactionId,
            Nonce = ++nextClientNonce,
            ExpectedRevision = clientSnapshot.Revision,
        };
        ClientRequestCreated?.Invoke(request);
        return SanityChangeResult.Queued(request);
    }

    internal SanityChangeResult Set(
        string playerKey,
        double value,
        SanityChangeSource source
    )
    {
        if (source == SanityChangeSource.Unknown)
            return SanityChangeResult.Rejected("change-source-is-unknown");
        if (state.Role != SanityAuthorityRole.Host)
            return SanityChangeResult.Rejected("set-operation-is-host-only", source);
        if (!double.IsFinite(value))
        {
            return SanityChangeResult.Rejected(
                "requested-sanity-value-must-be-finite",
                source
            );
        }
        if (!TryEnsureHostPlayer(playerKey, out var snapshot, out var reason))
            return SanityChangeResult.Rejected(reason, source);
        if (!sanitySystemEnabled)
        {
            return SanityChangeResult.Applied(
                snapshot,
                false,
                "sanity-system-disabled",
                source
            );
        }
        if (effectiveOverlayOwners.Contains(playerKey))
        {
            return SanityChangeResult.Applied(
                snapshot,
                false,
                "sanity-change-frozen-by-effective-overlay",
                source
            );
        }
        return ApplyHostValue(playerKey, value, source);
    }

    internal SanityHostRequestResult HandleHostRequest(
        SanityChangeRequest request,
        long senderPlayerId,
        ISanityRequestTruthSource truthSource,
        long nowMilliseconds
    )
    {
        if (state.Role != SanityAuthorityRole.Host)
        {
            return new SanityHostRequestResult(
                false,
                false,
                "host-authority-session-is-not-active",
                null
            );
        }
        if (request is null)
        {
            return new SanityHostRequestResult(
                false,
                false,
                "change-request-is-missing",
                null
            );
        }
        if (
            !SanityProtocol.IsValidSessionId(request.SessionId)
            || !string.Equals(
                request.SessionId,
                state.SessionId,
                StringComparison.Ordinal
            )
        )
        {
            return new SanityHostRequestResult(
                false,
                true,
                "request-session-does-not-match",
                null
            );
        }
        if (
            !SanityPlayerKey.IsCanonical(request.PlayerKey)
            || !long.TryParse(
                request.PlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
        {
            return new SanityHostRequestResult(
                false,
                false,
                "request-sender-does-not-own-player",
                null
            );
        }
        if (!TryEnsureHostPlayer(request.PlayerKey, out var current, out var reason))
            return new SanityHostRequestResult(false, false, reason, null);

        var validation = requestValidator.Validate(
            request,
            senderPlayerId,
            state.SessionId,
            current,
            truthSource,
            nowMilliseconds
        );
        if (!validation.Accepted)
        {
            return new SanityHostRequestResult(
                false,
                validation.NeedsSnapshot,
                validation.Reason,
                current
            );
        }

        var applied = Change(
            request.PlayerKey,
            validation.Delta,
            request.Source
        );
        if (
            applied.Status is not SanityChangeStatus.Applied
                and not SanityChangeStatus.NoChange
            || applied.Snapshot is null
        )
        {
            return new SanityHostRequestResult(
                false,
                true,
                applied.Reason,
                current
            );
        }

        requestValidator.MarkAccepted(
            request.PlayerKey,
            request.Nonce,
            nowMilliseconds
        );
        return new SanityHostRequestResult(
            true,
            false,
            applied.Reason,
            applied.Snapshot
        );
    }

    internal SanitySnapshotApplyResult ApplyClientSnapshot(
        SanitySnapshotMessage message
    )
    {
        var result = state.ApplyClientSnapshot(message);
        if (
            result.Status
                is SanitySnapshotApplyStatus.AppliedFull
                    or SanitySnapshotApplyStatus.AppliedDelta
            && message?.Players is not null
        )
        {
            foreach (var snapshot in message.Players)
                ObserveTierSnapshot(snapshot);
        }
        return result;
    }

    internal SanitySnapshotMessage CreateFullSnapshot()
    {
        return state.CreateFullSnapshot();
    }

    internal SanitySnapshotMessage CreateDeltaSnapshot(
        SanityPlayerSnapshot snapshot
    )
    {
        return state.CreateDeltaSnapshot(snapshot);
    }

    internal IReadOnlyList<SanityPlayerSaveInput> CaptureSaveInputs()
    {
        return state.CaptureSaveInputs();
    }

    internal bool TryGetSnapshot(string playerKey, out SanityPlayerSnapshot snapshot)
    {
        return state.TryGetSnapshot(playerKey, out snapshot);
    }

    private SanityChangeResult ApplyHostValue(
        string playerKey,
        double value,
        SanityChangeSource source
    )
    {
        if (
            !state.TrySetHostValue(
                playerKey,
                value,
                out var snapshot,
                out var changed,
                out var reason
            )
        )
        {
            return SanityChangeResult.Rejected(reason, source);
        }

        if (changed)
        {
            ObserveTierSnapshot(snapshot);
            HostStateChanged?.Invoke(
                new SanityStateChanged(source, snapshot.Clone())
            );
        }
        return SanityChangeResult.Applied(snapshot, changed, reason, source);
    }

    private void ObserveTierSnapshot(SanityPlayerSnapshot snapshot)
    {
        if (effectiveOverlayOwners.Contains(snapshot.PlayerKey))
            return;

        // HUD 等只读路径会反复 Ensure 同一 owner；同 revision 在这里无分配短路。
        if (tierState.IsCurrentObservation(snapshot))
            return;

        PublishTierResult(tierState.Observe(snapshot, sanitySystemEnabled));
        if (
            tierState.TryGetOwnerState(snapshot.PlayerKey, out var observed)
            && observed is not null
        )
        {
            TierStateObserved?.Invoke(observed);
        }
    }

    private void PublishTierResult(SanityTierEvaluationResult result)
    {
        foreach (var stateEvent in result.Events)
        {
            if (!shadowBudget.ApplyStateEvent(stateEvent, out var budgetReason))
                ShadowBudgetDiagnosticRaised?.Invoke(budgetReason);
            TierStateEventPublished?.Invoke(stateEvent);
        }
        if (result.Status == SanityTierEvaluationStatus.Unavailable)
            TierStateDiagnosticRaised?.Invoke(result.Reason);
    }
}

internal readonly record struct SanityRequestValidation(
    bool Accepted,
    bool NeedsSnapshot,
    double Delta,
    string Reason
);

/// <summary>
/// source-specific 请求门：当前只有食物交互可请求，且请求自身没有 delta；
/// 其他来源均由主机观察/自产，客户端提交一律拒绝。
/// </summary>
internal sealed class SanityRequestValidator
{
    private readonly Dictionary<string, long> highestSeenNonce =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastAcceptedAt =
        new(StringComparer.Ordinal);

    internal SanityRequestValidation Validate(
        SanityChangeRequest request,
        long senderPlayerId,
        string hostSessionId,
        SanityPlayerSnapshot current,
        ISanityRequestTruthSource truthSource,
        long nowMilliseconds
    )
    {
        if (
            !SanityProtocol.IsValidSessionId(request.SessionId)
            || !string.Equals(request.SessionId, hostSessionId, StringComparison.Ordinal)
        )
        {
            return Reject("request-session-does-not-match", true);
        }
        if (!SanityPlayerKey.IsCanonical(request.PlayerKey))
            return Reject("request-player-key-is-not-canonical", false);
        if (
            !long.TryParse(
                request.PlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
        {
            return Reject("request-sender-does-not-own-player", false);
        }

        var policy = SanityRequestPolicy.For(request.Source);
        if (!policy.ClientRequestAllowed)
            return Reject(policy.Reason, false);
        if (
            string.IsNullOrWhiteSpace(request.InteractionId)
            || request.InteractionId.Length > 128
        )
        {
            return Reject("interaction-id-is-missing-or-too-long", false);
        }
        if (request.Nonce <= 0)
            return Reject("request-nonce-must-be-positive", false);
        if (
            highestSeenNonce.TryGetValue(request.PlayerKey, out var seenNonce)
            && request.Nonce <= seenNonce
        )
        {
            return Reject("request-nonce-is-replayed-or-out-of-order", false);
        }

        // 通过 session/owner/source 基本门后立即消费 nonce；失败请求不能稍后重放。
        highestSeenNonce[request.PlayerKey] = request.Nonce;
        if (request.ExpectedRevision != current.Revision)
            return Reject("request-expected-revision-does-not-match", true);
        if (
            lastAcceptedAt.TryGetValue(request.PlayerKey, out var lastAccepted)
            && (
                nowMilliseconds < lastAccepted
                || nowMilliseconds - lastAccepted < policy.MinimumIntervalMilliseconds
            )
        )
        {
            return Reject("request-source-cooldown-is-active", false);
        }

        var truth = truthSource.Resolve(request, senderPlayerId);
        if (!truth.Success)
            return Reject($"request-context-rejected:{truth.Reason}", false);
        if (!double.IsFinite(truth.Delta))
            return Reject("host-recomputed-delta-must-be-finite", false);
        var allowedMagnitude = Math.Min(
            policy.MaximumAbsoluteDelta,
            current.Maximum
        );
        if (Math.Abs(truth.Delta) > allowedMagnitude)
            return Reject("host-recomputed-delta-exceeds-source-range", false);

        return new SanityRequestValidation(
            true,
            false,
            truth.Delta,
            "request-validated-by-host"
        );
    }

    internal void MarkAccepted(
        string playerKey,
        long nonce,
        long nowMilliseconds
    )
    {
        highestSeenNonce[playerKey] = nonce;
        lastAcceptedAt[playerKey] = nowMilliseconds;
    }

    internal void Forget(string playerKey)
    {
        highestSeenNonce.Remove(playerKey);
        lastAcceptedAt.Remove(playerKey);
    }

    internal void Clear()
    {
        highestSeenNonce.Clear();
        lastAcceptedAt.Clear();
    }

    private static SanityRequestValidation Reject(
        string reason,
        bool needsSnapshot
    )
    {
        return new SanityRequestValidation(false, needsSnapshot, 0, reason);
    }
}
