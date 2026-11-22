#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Damage;
using DontStarve.Player.Stats.Sanity.Darkness;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Host-owned, bounded transition authority for the owner-local darkness event. It only records
/// phase and emits idempotent directives; Farmer, audio, event and save side effects stay outside.
/// </summary>
internal sealed class SanityTwoAmSpecialDeathStateMachine
{
    private readonly Dictionary<string, FlowState> flows =
        new(StringComparer.Ordinal);

    internal int Count => flows.Count;

    internal SanityTwoAmSpecialDeathMutation Begin(
        SanityTwoAmSpecialDeathStartRequest request
    )
    {
        var invalid = ValidateStart(request);
        if (invalid is not null)
            return Rejected(invalid);
        if (request.IsLocationSafe)
            return NoChange("passout.two-am.location-safe");
        if (request.Mode == DarknessDamageMode.Off)
            return NoChange("passout.two-am.mode-off");

        if (flows.TryGetValue(request.PlayerKey, out var existing))
        {
            if (
                string.Equals(existing.Snapshot.CorrelationId, request.CorrelationId, StringComparison.Ordinal)
                && string.Equals(existing.Snapshot.SessionId, request.SessionId, StringComparison.Ordinal)
            )
            {
                return Duplicate(existing.Snapshot, "passout.two-am.start-duplicate");
            }
            if (existing.Snapshot.Phase == SanityTwoAmSpecialDeathPhase.Completed)
                flows.Remove(request.PlayerKey);
            else
                return Rejected("passout.two-am.player-correlation-conflict", existing.Snapshot);
        }

        if (flows.Count >= SanityTwoAmSpecialDeathContract.MaximumFlows)
            return Rejected("passout.two-am.flow-capacity-exceeded");

        var kind = request.Mode == DarknessDamageMode.NonLethal
            ? SanityTwoAmSpecialDeathFlowKind.NonLethalHome
            : SanityTwoAmSpecialDeathFlowKind.DefaultDeath;
        var snapshot = new SanityTwoAmSpecialDeathSnapshot(
            request.SessionId,
            request.CorrelationId,
            request.PlayerKey,
            request.ScreenId,
            request.Authority,
            request.AuthorityRevision,
            kind,
            SanityTwoAmSpecialDeathPhase.PromptEntered,
            request.CurrentHealth,
            request.MaximumHealth,
            1,
            "passout.two-am.started"
        );
        flows.Add(request.PlayerKey, new FlowState(snapshot));
        return Applied(
            snapshot,
            "passout.two-am.started",
            Action(snapshot, SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay),
            Action(snapshot, SanityTwoAmSpecialDeathActionKind.ShowPromptEntered)
        );
    }

    internal SanityTwoAmSpecialDeathMutation Signal(
        string playerKey,
        string correlationId,
        SanityTwoAmSpecialDeathSignal signal,
        string signalId
    )
    {
        if (!flows.TryGetValue(playerKey, out var state))
            return NoChange("passout.two-am.flow-not-found");
        if (!string.Equals(state.Snapshot.CorrelationId, correlationId, StringComparison.Ordinal))
            return Rejected("passout.two-am.signal-correlation-conflict", state.Snapshot);
        if (signal != SanityTwoAmSpecialDeathSignal.AdvancePresentation)
            return Rejected("passout.two-am.signal-unsupported", state.Snapshot);
        if (string.IsNullOrWhiteSpace(signalId) || signalId.Length > 128)
            return Rejected("passout.two-am.signal-id-invalid", state.Snapshot);
        if (!state.ProcessedSignalIds.Add(signalId))
            return Duplicate(state.Snapshot, "passout.two-am.signal-duplicate");

        var mutation = AdvancePresentation(state.Snapshot);
        if (mutation.Status == SanityTwoAmSpecialDeathMutationStatus.Applied)
            state.Snapshot = mutation.Snapshot!;
        else
            state.ProcessedSignalIds.Remove(signalId);
        return mutation;
    }

    internal bool TryGetSnapshot(
        string playerKey,
        out SanityTwoAmSpecialDeathSnapshot snapshot
    )
    {
        if (flows.TryGetValue(playerKey, out var state))
        {
            snapshot = state.Snapshot;
            return true;
        }
        snapshot = null!;
        return false;
    }

    internal IReadOnlyList<SanityTwoAmSpecialDeathSnapshot> SnapshotAll()
    {
        if (flows.Count == 0)
            return Array.Empty<SanityTwoAmSpecialDeathSnapshot>();

        var snapshots = new List<SanityTwoAmSpecialDeathSnapshot>(flows.Count);
        foreach (var state in flows.Values)
            snapshots.Add(state.Snapshot);
        snapshots.Sort((left, right) => string.CompareOrdinal(left.PlayerKey, right.PlayerKey));
        return snapshots.AsReadOnly();
    }

    internal SanityTwoAmSpecialDeathMutation CancelPlayer(
        string playerKey,
        string correlationId,
        string reason
    )
    {
        if (!flows.TryGetValue(playerKey, out var state))
            return NoChange("passout.two-am.cancel-flow-not-found");
        if (!string.Equals(state.Snapshot.CorrelationId, correlationId, StringComparison.Ordinal))
            return Rejected("passout.two-am.cancel-correlation-conflict", state.Snapshot);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128)
            return Rejected("passout.two-am.cancel-reason-invalid", state.Snapshot);

        flows.Remove(playerKey);
        return Applied(
            state.Snapshot with { Reason = reason },
            reason,
            Action(state.Snapshot, SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay)
        );
    }

    internal void ClearSession() => flows.Clear();

    /// <summary>
    /// Old recovery records are deliberately not resumed. They are recognized as a safe discard
    /// so a stale clinic/mail state cannot create a new side effect after this rewrite.
    /// </summary>
    internal SanityTwoAmSpecialDeathMutation ImportRecovery(
        SanityTwoAmSpecialDeathPersistedFlow persisted,
        string currentSessionId
    )
    {
        _ = persisted;
        _ = currentSessionId;
        return NoChange("passout.two-am.legacy-recovery-discarded");
    }

    private static SanityTwoAmSpecialDeathMutation AdvancePresentation(
        SanityTwoAmSpecialDeathSnapshot snapshot
    )
    {
        return snapshot.Phase switch
        {
            SanityTwoAmSpecialDeathPhase.PromptEntered => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.PromptWarning,
                "passout.two-am.warning",
                SanityTwoAmSpecialDeathActionKind.PlayWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptWarning
            ),
            SanityTwoAmSpecialDeathPhase.PromptWarning => snapshot.Kind
                == SanityTwoAmSpecialDeathFlowKind.DefaultDeath
                ? Transition(
                    snapshot,
                    SanityTwoAmSpecialDeathPhase.PromptResolved,
                    "passout.two-am.default-resolved",
                    SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack,
                    SanityTwoAmSpecialDeathActionKind.PlayDeathSound,
                    SanityTwoAmSpecialDeathActionKind.ShowPromptResolved
                )
                : Transition(
                    snapshot,
                    SanityTwoAmSpecialDeathPhase.PromptResolved,
                    "passout.two-am.nonlethal-resolved",
                    SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack,
                    SanityTwoAmSpecialDeathActionKind.ShowPromptResolved
                ),
            SanityTwoAmSpecialDeathPhase.PromptResolved => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.Completed,
                "passout.two-am.outcome-started",
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                snapshot.Kind == SanityTwoAmSpecialDeathFlowKind.DefaultDeath
                    ? SanityTwoAmSpecialDeathActionKind.BeginPendingDarknessDeath
                    : SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut
            ),
            SanityTwoAmSpecialDeathPhase.Completed => Duplicate(
                snapshot,
                "passout.two-am.already-completed"
            ),
            _ => Rejected("passout.two-am.phase-cannot-advance", snapshot),
        };
    }

    private static string? ValidateStart(SanityTwoAmSpecialDeathStartRequest request)
    {
        if (!SanityProtocol.IsValidSessionId(request.SessionId))
            return "passout.two-am.session-invalid";
        if (string.IsNullOrWhiteSpace(request.CorrelationId) || request.CorrelationId.Length > 128)
            return "passout.two-am.correlation-invalid";
        if (!SanityPlayerKey.IsCanonical(request.PlayerKey))
            return "passout.two-am.player-key-invalid";
        if (request.ScreenId < 0)
            return "passout.two-am.screen-id-invalid";
        if (request.Authority != SanityAuthorityRole.Host)
            return "passout.two-am.host-authority-required";
        if (request.AuthorityRevision < 0)
            return "passout.two-am.authority-revision-invalid";
        if (!Enum.IsDefined(typeof(DarknessDamageMode), request.Mode))
            return "passout.two-am.mode-invalid";
        if (string.IsNullOrWhiteSpace(request.LocationReason) || request.LocationReason.Length > 128)
            return "passout.two-am.location-reason-invalid";
        if (
            request.MaximumHealth <= 0
            || request.CurrentHealth < 0
            || request.CurrentHealth > request.MaximumHealth
        )
            return "passout.two-am.health-snapshot-invalid";
        return null;
    }

    private static SanityTwoAmSpecialDeathMutation Transition(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathPhase phase,
        string reason,
        params SanityTwoAmSpecialDeathActionKind[] actionKinds
    ) => Applied(
        Next(snapshot, phase, reason),
        reason,
        CreateActions(snapshot, actionKinds)
    );

    private static SanityTwoAmSpecialDeathAction[] CreateActions(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        IReadOnlyList<SanityTwoAmSpecialDeathActionKind> kinds
    )
    {
        var actions = new SanityTwoAmSpecialDeathAction[kinds.Count];
        for (var index = 0; index < kinds.Count; index++)
            actions[index] = Action(snapshot, kinds[index]);
        return actions;
    }

    private static SanityTwoAmSpecialDeathSnapshot Next(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathPhase phase,
        string reason
    ) => snapshot with
    {
        Phase = phase,
        Revision = snapshot.Revision + 1,
        Reason = reason,
    };

    private static SanityTwoAmSpecialDeathAction Action(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathActionKind kind
    ) => new(kind, snapshot.PlayerKey, snapshot.ScreenId, snapshot.CorrelationId);

    private static SanityTwoAmSpecialDeathMutation Applied(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        string reason,
        params SanityTwoAmSpecialDeathAction[] actions
    ) => new(
        SanityTwoAmSpecialDeathMutationStatus.Applied,
        reason,
        snapshot,
        actions
    );

    private static SanityTwoAmSpecialDeathMutation Duplicate(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        string reason
    ) => new(SanityTwoAmSpecialDeathMutationStatus.Duplicate, reason, snapshot);

    private static SanityTwoAmSpecialDeathMutation NoChange(
        string reason,
        SanityTwoAmSpecialDeathSnapshot? snapshot = null
    ) => new(SanityTwoAmSpecialDeathMutationStatus.NoChange, reason, snapshot);

    private static SanityTwoAmSpecialDeathMutation Rejected(
        string reason,
        SanityTwoAmSpecialDeathSnapshot? snapshot = null
    ) => new(SanityTwoAmSpecialDeathMutationStatus.Rejected, reason, snapshot);

    private sealed class FlowState
    {
        internal FlowState(SanityTwoAmSpecialDeathSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        internal SanityTwoAmSpecialDeathSnapshot Snapshot { get; set; }

        internal HashSet<string> ProcessedSignalIds { get; } = new(StringComparer.Ordinal);
    }
}
