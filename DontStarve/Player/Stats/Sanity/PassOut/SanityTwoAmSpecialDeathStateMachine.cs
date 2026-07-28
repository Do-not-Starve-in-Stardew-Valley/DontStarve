#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Damage;
using DontStarve.Player.Stats.Sanity.Darkness;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Host-owned, bounded transition authority for the 2:00 flow. It records decisions and emits
/// one-shot directives, but never touches Farmer, mail, audio, events, or the save API directly.
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
                string.Equals(
                    existing.Snapshot.CorrelationId,
                    request.CorrelationId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    existing.Snapshot.SessionId,
                    request.SessionId,
                    StringComparison.Ordinal
                )
            )
            {
                return Duplicate(
                    existing.Snapshot,
                    "passout.two-am.start-duplicate"
                );
            }
            if (
                existing.Snapshot.Phase
                    is SanityTwoAmSpecialDeathPhase.Recovered
                        or SanityTwoAmSpecialDeathPhase.Completed
            )
            {
                // The clinic consequence has already been applied. Retire that receipt so a
                // later game day can start a fresh event; the permanent mail ID remains the
                // one-time guard at the SMAPI boundary.
                flows.Remove(request.PlayerKey);
            }
            else
            {
                return Rejected(
                    "passout.two-am.player-correlation-conflict",
                    existing.Snapshot
                );
            }
        }
        if (flows.Count >= SanityTwoAmSpecialDeathContract.MaximumFlows)
            return Rejected("passout.two-am.flow-capacity-exceeded");

        var kind = request.Mode == DarknessDamageMode.NonLethal
            ? SanityTwoAmSpecialDeathFlowKind.NonLethalHome
            : SanityTwoAmSpecialDeathFlowKind.DefaultClinic;
        var phase = kind == SanityTwoAmSpecialDeathFlowKind.NonLethalHome
            ? SanityTwoAmSpecialDeathPhase.NonLethalHomePending
            : SanityTwoAmSpecialDeathPhase.PromptA;
        var snapshot = new SanityTwoAmSpecialDeathSnapshot(
            request.SessionId,
            request.CorrelationId,
            request.PlayerKey,
            request.ScreenId,
            request.Authority,
            request.AuthorityRevision,
            kind,
            phase,
            request.CurrentHealth,
            request.MaximumHealth,
            false,
            SanityTwoAmSpecialDeathContract.MailId,
            1,
            request.LocationReason
        );
        flows.Add(request.PlayerKey, new FlowState(snapshot));

        return kind == SanityTwoAmSpecialDeathFlowKind.NonLethalHome
            ? Applied(
                snapshot,
                "passout.two-am.nonlethal-home-started",
                Action(snapshot, SanityTwoAmSpecialDeathActionKind.ReduceToFloor)
            )
            : Applied(
                snapshot,
                "passout.two-am.default-special-started",
                Action(snapshot, SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay),
                Action(snapshot, SanityTwoAmSpecialDeathActionKind.ShowPromptA)
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
        if (
            !string.Equals(
                state.Snapshot.CorrelationId,
                correlationId,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(
                "passout.two-am.signal-correlation-conflict",
                state.Snapshot
            );
        }
        if (string.IsNullOrWhiteSpace(signalId) || signalId.Length > 128)
            return Rejected("passout.two-am.signal-id-invalid", state.Snapshot);
        if (state.ProcessedSignalIds.Contains(signalId))
            return Duplicate(state.Snapshot, "passout.two-am.signal-duplicate");

        var mutation = ApplySignal(state.Snapshot, signal);
        if (mutation.Status == SanityTwoAmSpecialDeathMutationStatus.Applied)
        {
            state.ProcessedSignalIds.Add(signalId);
            state.Snapshot = mutation.Snapshot!;
        }
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
        if (
            !string.Equals(
                state.Snapshot.CorrelationId,
                correlationId,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(
                "passout.two-am.cancel-correlation-conflict",
                state.Snapshot
            );
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128)
            return Rejected("passout.two-am.cancel-reason-invalid", state.Snapshot);

        flows.Remove(playerKey);
        var snapshot = state.Snapshot with { Reason = reason };
        return snapshot.Kind == SanityTwoAmSpecialDeathFlowKind.DefaultClinic
            ? Applied(
                snapshot,
                reason,
                Action(snapshot, SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay),
                Action(snapshot, SanityTwoAmSpecialDeathActionKind.StopWarningCue)
            )
            : Applied(snapshot, reason);
    }

    internal void ClearSession()
    {
        flows.Clear();
    }

    internal SanityTwoAmSpecialDeathMutation ImportRecovery(
        SanityTwoAmSpecialDeathPersistedFlow persisted,
        string currentSessionId
    )
    {
        var validation = SanityTwoAmSpecialDeathPersistence.ValidateFlow(persisted);
        if (validation is not null)
            return Rejected(validation);
        if (!SanityProtocol.IsValidSessionId(currentSessionId))
            return Rejected("passout.two-am.recovery-session-invalid");
        if (flows.ContainsKey(persisted.PlayerKey))
        {
            var existing = flows[persisted.PlayerKey].Snapshot;
            return string.Equals(
                    existing.CorrelationId,
                    persisted.CorrelationId,
                    StringComparison.Ordinal
                )
                ? Duplicate(existing, "passout.two-am.recovery-duplicate")
                : Rejected("passout.two-am.recovery-player-conflict", existing);
        }
        if (flows.Count >= SanityTwoAmSpecialDeathContract.MaximumFlows)
            return Rejected("passout.two-am.flow-capacity-exceeded");

        var phase = Enum.Parse<SanityTwoAmSpecialDeathPhase>(persisted.Phase);
        var snapshot = new SanityTwoAmSpecialDeathSnapshot(
            currentSessionId,
            persisted.CorrelationId,
            persisted.PlayerKey,
            persisted.ScreenId,
            SanityAuthorityRole.Host,
            persisted.AuthorityRevision,
            SanityTwoAmSpecialDeathFlowKind.DefaultClinic,
            phase,
            0,
            0,
            persisted.MailQueued,
            persisted.MailId,
            Math.Max(1, persisted.Revision),
            "passout.two-am.recovery-imported"
        );
        flows.Add(persisted.PlayerKey, new FlowState(snapshot));
        return Applied(snapshot, "passout.two-am.recovery-imported");
    }

    private static SanityTwoAmSpecialDeathMutation ApplySignal(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathSignal signal
    )
    {
        if (signal == SanityTwoAmSpecialDeathSignal.MailQueued)
        {
            if (snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic)
                return Rejected("passout.two-am.mail-not-valid-for-flow", snapshot);
            if (snapshot.MailQueued)
                return Duplicate(snapshot, "passout.two-am.mail-already-queued");
            var queued = Next(snapshot, snapshot.Phase, true, "passout.two-am.mail-queued");
            return Applied(queued, queued.Reason);
        }

        return (snapshot.Phase, signal) switch
        {
            (
                SanityTwoAmSpecialDeathPhase.PromptA,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.WarningCue,
                "passout.two-am.warning-cue",
                SanityTwoAmSpecialDeathActionKind.PlayWarningCue
            ),
            (
                SanityTwoAmSpecialDeathPhase.WarningCue,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.PromptB,
                "passout.two-am.prompt-b",
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptB
            ),
            (
                SanityTwoAmSpecialDeathPhase.PromptB,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.PromptC,
                "passout.two-am.prompt-c",
                SanityTwoAmSpecialDeathActionKind.ShowPromptC,
                SanityTwoAmSpecialDeathActionKind.ReportMissingDeathCue
            ),
            (
                SanityTwoAmSpecialDeathPhase.PromptC,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.AwaitingDayEnding,
                "passout.two-am.official-new-day-requested",
                SanityTwoAmSpecialDeathActionKind.QueueMail,
                SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay
            ),
            (
                SanityTwoAmSpecialDeathPhase.NonLethalHomePending,
                SanityTwoAmSpecialDeathSignal.NonLethalSettled
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.Completed,
                "passout.two-am.nonlethal-home-settled"
            ),
            (
                SanityTwoAmSpecialDeathPhase.AwaitingDayEnding,
                SanityTwoAmSpecialDeathSignal.DayEnding
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.AwaitingSaving,
                "passout.two-am.day-ending-observed"
            ),
            (
                SanityTwoAmSpecialDeathPhase.AwaitingSaving,
                SanityTwoAmSpecialDeathSignal.Saving
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.AwaitingRecovery,
                "passout.two-am.saving-observed"
            ),
            (
                SanityTwoAmSpecialDeathPhase.AwaitingRecovery,
                SanityTwoAmSpecialDeathSignal.DayStarted
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.Recovered,
                "passout.two-am.clinic-recovery",
                SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic,
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue
            ),
            (
                SanityTwoAmSpecialDeathPhase.Recovered,
                SanityTwoAmSpecialDeathSignal.LaterSaving
            ) => Transition(
                snapshot,
                SanityTwoAmSpecialDeathPhase.Completed,
                "passout.two-am.recovery-persisted"
            ),
            _ => NoChange("passout.two-am.signal-not-applicable", snapshot),
        };
    }

    private static string? ValidateStart(SanityTwoAmSpecialDeathStartRequest request)
    {
        if (!SanityProtocol.IsValidSessionId(request.SessionId))
            return "passout.two-am.session-invalid";
        if (!NonLethalDamageContract.IsValidCorrelationId(request.CorrelationId))
            return "passout.two-am.correlation-invalid";
        if (!SanityPlayerKey.IsCanonical(request.PlayerKey))
            return "passout.two-am.player-invalid";
        if (request.ScreenId < 0)
            return "passout.two-am.screen-invalid";
        if (request.Authority != SanityAuthorityRole.Host || request.AuthorityRevision < 0)
            return "passout.two-am.host-authority-required";
        if (!Enum.IsDefined(typeof(DarknessDamageMode), request.Mode))
            return "passout.two-am.mode-invalid";
        if (string.IsNullOrWhiteSpace(request.LocationReason))
            return "passout.two-am.location-reason-missing";
        if (
            request.CurrentHealth < 0
            || request.MaximumHealth <= 0
            || request.CurrentHealth > request.MaximumHealth
        )
        {
            return "passout.two-am.health-snapshot-invalid";
        }
        return null;
    }

    private static SanityTwoAmSpecialDeathMutation Transition(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathPhase phase,
        string reason,
        params SanityTwoAmSpecialDeathActionKind[] actionKinds
    )
    {
        var next = Next(snapshot, phase, snapshot.MailQueued, reason);
        var actions = new SanityTwoAmSpecialDeathAction[actionKinds.Length];
        for (var index = 0; index < actionKinds.Length; index++)
            actions[index] = Action(next, actionKinds[index]);
        return Applied(next, reason, actions);
    }

    private static SanityTwoAmSpecialDeathSnapshot Next(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathPhase phase,
        bool mailQueued,
        string reason
    )
    {
        return snapshot with
        {
            Phase = phase,
            MailQueued = mailQueued,
            Revision = checked(snapshot.Revision + 1),
            Reason = reason,
        };
    }

    private static SanityTwoAmSpecialDeathAction Action(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathActionKind kind
    )
    {
        return new SanityTwoAmSpecialDeathAction(
            kind,
            snapshot.PlayerKey,
            snapshot.ScreenId,
            snapshot.CorrelationId
        );
    }

    private static SanityTwoAmSpecialDeathMutation Applied(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        string reason,
        params SanityTwoAmSpecialDeathAction[] actions
    )
    {
        return new SanityTwoAmSpecialDeathMutation(
            SanityTwoAmSpecialDeathMutationStatus.Applied,
            reason,
            snapshot,
            actions
        );
    }

    private static SanityTwoAmSpecialDeathMutation Duplicate(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        string reason
    )
    {
        return new SanityTwoAmSpecialDeathMutation(
            SanityTwoAmSpecialDeathMutationStatus.Duplicate,
            reason,
            snapshot
        );
    }

    private static SanityTwoAmSpecialDeathMutation NoChange(
        string reason,
        SanityTwoAmSpecialDeathSnapshot? snapshot = null
    )
    {
        return new SanityTwoAmSpecialDeathMutation(
            SanityTwoAmSpecialDeathMutationStatus.NoChange,
            reason,
            snapshot
        );
    }

    private static SanityTwoAmSpecialDeathMutation Rejected(
        string reason,
        SanityTwoAmSpecialDeathSnapshot? snapshot = null
    )
    {
        return new SanityTwoAmSpecialDeathMutation(
            SanityTwoAmSpecialDeathMutationStatus.Rejected,
            reason,
            snapshot
        );
    }

    private sealed class FlowState
    {
        internal FlowState(SanityTwoAmSpecialDeathSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        internal SanityTwoAmSpecialDeathSnapshot Snapshot { get; set; }

        internal HashSet<string> ProcessedSignalIds { get; } =
            new(StringComparer.Ordinal);
    }
}
