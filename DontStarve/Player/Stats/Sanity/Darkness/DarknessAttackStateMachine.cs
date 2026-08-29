#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Host-authoritative, session-scoped darkness countdown. Expiry only creates an intent; this type
/// has no damage or Sanity dependency and cannot settle an attack by itself.
/// </summary>
internal sealed class DarknessAttackStateMachine : IDisposable
{
    private readonly IDarknessAttackClock clock;
    private readonly IDarknessAttackRandom random;
    private readonly IDarknessAttackRequestIdSource requestIds;
    private readonly Dictionary<DarknessAttackOwnerKey, OwnerState> owners = new();
    private readonly IReadOnlyList<DarknessWarningClip> warningClips;
    private bool disposed;

    internal DarknessAttackStateMachine(
        IDarknessAttackClock clock,
        IDarknessAttackRandom random,
        IDarknessAttackRequestIdSource requestIds,
        double warningLeadSeconds
    )
        : this(
            clock,
            random,
            requestIds,
            new[]
            {
                new DarknessWarningClip(
                    "sanity.clip.darkness.warning",
                    warningLeadSeconds
                ),
            }
        )
    {
    }

    internal DarknessAttackStateMachine(
        IDarknessAttackClock clock,
        IDarknessAttackRandom random,
        IDarknessAttackRequestIdSource requestIds,
        IReadOnlyList<DarknessWarningClip> warningClips
    )
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.requestIds = requestIds
            ?? throw new ArgumentNullException(nameof(requestIds));
        if (warningClips is null || warningClips.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(warningClips),
                "At least one warning clip with a positive duration is required."
            );
        }
        var copiedClips = new DarknessWarningClip[warningClips.Count];
        for (var index = 0; index < warningClips.Count; index++)
        {
            var clip = warningClips[index];
            if (
                string.IsNullOrWhiteSpace(clip.ClipId)
                || clip.ClipId.Length > 256
                || !double.IsFinite(clip.DurationSeconds)
                || clip.DurationSeconds <= 0d
            )
            {
                throw new ArgumentException(
                    "Every warning clip must have a bounded ID and positive finite duration.",
                    nameof(warningClips)
                );
            }
            copiedClips[index] = clip;
        }
        this.warningClips = Array.AsReadOnly(copiedClips);
    }

    internal DarknessAttackUpdateResult Observe(DarknessAttackObservation observation)
    {
        if (disposed)
            return Result(DarknessAttackMutationStatus.Disposed, "darkness.state.disposed");

        var validation = ValidateObservation(observation);
        if (validation is not null)
            return Result(DarknessAttackMutationStatus.Invalid, validation);
        if (observation.AuthorityRole != SanityAuthorityRole.Host)
        {
            return Result(
                DarknessAttackMutationStatus.RequiresHostAuthority,
                "darkness.state.requires-host-authority"
            );
        }

        if (owners.TryGetValue(observation.Key, out var state))
        {
            if (observation.Revision < state.Revision)
            {
                return Result(
                    DarknessAttackMutationStatus.IgnoredStale,
                    "darkness.state.observation-stale"
                );
            }
            if (observation.Revision == state.Revision)
            {
                return Result(
                    DarknessAttackMutationStatus.IgnoredDuplicate,
                    "darkness.state.observation-duplicate"
                );
            }
        }
        else
        {
            if (!observation.IsAuthorizedPitchBlack)
                return DarknessAttackUpdateResult.NoChange("darkness.state.inactive");
            if (owners.Count >= DarknessAttackContract.MaximumOwnerStates)
            {
                return Result(
                    DarknessAttackMutationStatus.CapacityExceeded,
                    "darkness.state.capacity-exceeded"
                );
            }
            state = new OwnerState(observation.Key);
            owners.Add(observation.Key, state);
        }

        state.Revision = observation.Revision;
        state.LightLevel = observation.LightLevel;
        state.EvidenceStatus = observation.EvidenceStatus;
        state.LightReason = observation.LightReason;

        if (!observation.GameplaySettleable)
        {
            var reason = string.IsNullOrWhiteSpace(observation.UnsettleableReason)
                ? "darkness.state.gameplay-unsettleable"
                : observation.UnsettleableReason;
            return Cancel(state, reason);
        }
        if (!observation.IsAuthorizedPitchBlack)
            return Cancel(state, "darkness.state.left-authorized-pitch-black");

        if (state.State == DarknessAttackOwnerState.Inactive)
            return StartCycle(state, initial: true, showEntryPrompt: true);
        if (observation.Paused)
            return DarknessAttackUpdateResult.NoChange("darkness.state.paused");
        if (state.State == DarknessAttackOwnerState.ExpiredAwaitingReceipt)
        {
            return DarknessAttackUpdateResult.NoChange(
                "darkness.state.awaiting-receipt"
            );
        }

        var elapsed = clock.ElapsedGameTime;
        if (elapsed < TimeSpan.Zero)
            return Result(DarknessAttackMutationStatus.Invalid, "darkness.clock.negative");
        if (elapsed == TimeSpan.Zero)
            return DarknessAttackUpdateResult.NoChange("darkness.clock.zero");

        state.RemainingSeconds = Math.Max(
            0d,
            state.RemainingSeconds - elapsed.TotalSeconds
        );
        var warningAction = DarknessWarningClaimAction.None;
        var prompt = DarknessAttackPromptKind.None;
        if (
            state.State == DarknessAttackOwnerState.Countdown
            && state.RemainingSeconds <= state.WarningDurationSeconds
        )
        {
            state.State = DarknessAttackOwnerState.Warned;
            state.WarningClaimActive = true;
            warningAction = DarknessWarningClaimAction.Activate;
            prompt = DarknessAttackPromptKind.Warning;
        }

        DarknessAttackExpiryIntent? intent = null;
        if (state.RemainingSeconds <= 0d)
        {
            state.State = DarknessAttackOwnerState.ExpiredAwaitingReceipt;
            intent = new DarknessAttackExpiryIntent(
                state.Key,
                state.RequestId,
                state.Revision,
                state.LightReason,
                DarknessAttackContract.ContractVersion
            );
        }

        if (warningAction == DarknessWarningClaimAction.None && intent is null)
            return DarknessAttackUpdateResult.NoChange("darkness.state.countdown-advanced");
        return new DarknessAttackUpdateResult(
            DarknessAttackMutationStatus.Applied,
            intent is null ? "darkness.state.warning-emitted" : "darkness.state.expired",
            prompt,
            warningAction,
            warningAction == DarknessWarningClaimAction.Activate
                ? state.RequestId
                : string.Empty,
            intent,
            warningAction == DarknessWarningClaimAction.Activate
                ? state.WarningClipId
                : string.Empty,
            warningAction == DarknessWarningClaimAction.Activate
                ? state.WarningDurationSeconds
                : 0d
        );
    }

    internal DarknessAttackUpdateResult CompleteReceipt(
        DarknessAttackObservation observation,
        DarknessAttackReceipt receipt
    )
    {
        if (disposed)
            return Result(DarknessAttackMutationStatus.Disposed, "darkness.state.disposed");
        var validation = ValidateObservation(observation);
        if (validation is not null)
            return Result(DarknessAttackMutationStatus.Invalid, validation);
        if (observation.AuthorityRole != SanityAuthorityRole.Host)
        {
            return Result(
                DarknessAttackMutationStatus.RequiresHostAuthority,
                "darkness.receipt.requires-host-authority"
            );
        }
        if (!receipt.Key.Equals(observation.Key) || !IsValidRequestId(receipt.RequestId))
            return Result(DarknessAttackMutationStatus.Invalid, "darkness.receipt.invalid");
        if (
            receipt.Disposition == DarknessAttackReceiptDisposition.Rejected
            && string.IsNullOrWhiteSpace(receipt.Reason)
        )
        {
            return Result(
                DarknessAttackMutationStatus.Invalid,
                "darkness.receipt.rejection-reason-required"
            );
        }
        if (!owners.TryGetValue(observation.Key, out var state))
            return DarknessAttackUpdateResult.NoChange("darkness.receipt.owner-not-found");
        if (observation.Revision < state.Revision)
        {
            return Result(
                DarknessAttackMutationStatus.IgnoredStale,
                "darkness.receipt.observation-stale"
            );
        }
        if (
            state.State != DarknessAttackOwnerState.ExpiredAwaitingReceipt
            || !string.Equals(state.RequestId, receipt.RequestId, StringComparison.Ordinal)
        )
        {
            return DarknessAttackUpdateResult.NoChange("darkness.receipt.not-awaiting-request");
        }

        state.Revision = observation.Revision;
        state.LightLevel = observation.LightLevel;
        state.EvidenceStatus = observation.EvidenceStatus;
        state.LightReason = observation.LightReason;
        var release = state.WarningClaimActive
            ? DarknessWarningClaimAction.Release
            : DarknessWarningClaimAction.None;
        var warningRequestId = release == DarknessWarningClaimAction.Release
            ? state.RequestId
            : string.Empty;
        state.WarningClaimActive = false;

        if (!observation.GameplaySettleable || !observation.IsAuthorizedPitchBlack)
        {
            var cancelled = Cancel(
                state,
                observation.GameplaySettleable
                    ? "darkness.state.left-authorized-pitch-black"
                    : observation.UnsettleableReason
            );
            return cancelled with
            {
                WarningClaimAction = release,
                WarningRequestId = warningRequestId,
            };
        }

        var started = StartCycle(state, initial: false, showEntryPrompt: false);
        return started with
        {
            WarningClaimAction = release,
            WarningRequestId = warningRequestId,
            Reason = receipt.Disposition == DarknessAttackReceiptDisposition.Applied
                ? "darkness.receipt.applied-repeat-started"
                : "darkness.receipt.rejected-repeat-started",
        };
    }

    internal DarknessAttackUpdateResult Cancel(
        DarknessAttackOwnerKey key,
        string reason
    )
    {
        if (disposed)
            return Result(DarknessAttackMutationStatus.Disposed, "darkness.state.disposed");
        if (!owners.TryGetValue(key, out var state))
            return DarknessAttackUpdateResult.NoChange("darkness.state.owner-not-found");
        return Cancel(state, reason);
    }

    internal bool Remove(DarknessAttackOwnerKey key)
    {
        return !disposed && owners.Remove(key);
    }

    internal int RemoveOwner(string playerKey)
    {
        if (disposed || !SanityPlayerKey.IsCanonical(playerKey))
            return 0;

        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in owners.Keys)
        {
            if (!string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is null)
            return 0;
        foreach (var key in removals)
            owners.Remove(key);
        return removals.Count;
    }

    internal bool TryGetSnapshot(
        DarknessAttackOwnerKey key,
        out DarknessAttackStateSnapshot snapshot
    )
    {
        if (!disposed && owners.TryGetValue(key, out var state))
        {
            snapshot = state.CreateSnapshot();
            return true;
        }
        snapshot = null!;
        return false;
    }

    internal void Clear()
    {
        if (!disposed)
            owners.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        owners.Clear();
        disposed = true;
    }

    private DarknessAttackUpdateResult StartCycle(
        OwnerState state,
        bool initial,
        bool showEntryPrompt
    )
    {
        var minimum = initial
            ? DarknessAttackContract.InitialMinimumSeconds
            : DarknessAttackContract.RepeatMinimumSeconds;
        var maximum = initial
            ? DarknessAttackContract.InitialMaximumSeconds
            : DarknessAttackContract.RepeatMaximumSeconds;
        int sampled;
        try
        {
            sampled = random.NextInclusive(minimum, maximum);
        }
        catch (Exception)
        {
            return Result(DarknessAttackMutationStatus.Invalid, "darkness.rng.failed");
        }
        if (sampled < minimum || sampled > maximum)
            return Result(DarknessAttackMutationStatus.Invalid, "darkness.rng.out-of-range");

        DarknessWarningClip warningClip;
        try
        {
            var clipIndex = warningClips.Count == 1
                ? 0
                : random.NextInclusive(0, warningClips.Count - 1);
            if (clipIndex < 0 || clipIndex >= warningClips.Count)
            {
                return Result(
                    DarknessAttackMutationStatus.Invalid,
                    "darkness.warning-rng.out-of-range"
                );
            }
            warningClip = warningClips[clipIndex];
        }
        catch (Exception)
        {
            return Result(DarknessAttackMutationStatus.Invalid, "darkness.warning-rng.failed");
        }

        string requestId;
        try
        {
            requestId = requestIds.NextRequestId(state.Key);
        }
        catch (Exception)
        {
            return Result(
                DarknessAttackMutationStatus.Invalid,
                "darkness.request-id.failed"
            );
        }
        if (!IsValidRequestId(requestId))
        {
            return Result(
                DarknessAttackMutationStatus.Invalid,
                "darkness.request-id.invalid"
            );
        }

        state.State = DarknessAttackOwnerState.Countdown;
        state.RemainingSeconds = sampled;
        state.SampledSeconds = sampled;
        state.RngBranch = initial ? "initial-inclusive-5-10" : "repeat-inclusive-5-11";
        state.WarningClipId = warningClip.ClipId;
        state.WarningDurationSeconds = warningClip.DurationSeconds;
        state.RequestId = requestId;
        state.WarningClaimActive = false;
        state.CancelReason = string.Empty;
        return new DarknessAttackUpdateResult(
            DarknessAttackMutationStatus.Applied,
            initial ? "darkness.state.initial-cycle-started" : "darkness.state.repeat-cycle-started",
            showEntryPrompt
                ? DarknessAttackPromptKind.EnteredDarkness
                : DarknessAttackPromptKind.None,
            DarknessWarningClaimAction.None,
            string.Empty,
            null
        );
    }

    private static DarknessAttackUpdateResult Cancel(OwnerState state, string reason)
    {
        if (state.State == DarknessAttackOwnerState.Inactive)
            return DarknessAttackUpdateResult.NoChange("darkness.state.already-inactive");

        var release = state.WarningClaimActive
            ? DarknessWarningClaimAction.Release
            : DarknessWarningClaimAction.None;
        var warningRequestId = release == DarknessWarningClaimAction.Release
            ? state.RequestId
            : string.Empty;
        state.State = DarknessAttackOwnerState.Inactive;
        state.RemainingSeconds = 0d;
        state.WarningClaimActive = false;
        state.WarningClipId = string.Empty;
        state.WarningDurationSeconds = 0d;
        state.RequestId = string.Empty;
        state.CancelReason = string.IsNullOrWhiteSpace(reason)
            ? "darkness.state.cancelled"
            : reason;
        return new DarknessAttackUpdateResult(
            DarknessAttackMutationStatus.Applied,
            state.CancelReason,
            DarknessAttackPromptKind.EscapedDarkness,
            release,
            warningRequestId,
            null
        );
    }

    private static string? ValidateObservation(DarknessAttackObservation observation)
    {
        if (!SanityPlayerKey.IsCanonical(observation.Key.PlayerKey))
            return "darkness.owner.player-key-invalid";
        if (observation.Key.ScreenId < 0)
            return "darkness.owner.screen-id-invalid";
        if (!SanityProtocol.IsValidSessionId(observation.Key.SessionId))
            return "darkness.owner.session-id-invalid";
        if (observation.Revision < 0)
            return "darkness.observation.revision-invalid";
        if (string.IsNullOrWhiteSpace(observation.LightReason))
            return "darkness.observation.light-reason-required";
        if (
            observation.PitchBlackAuthorized
            && (
                observation.LightLevel != EnvironmentLightLevel.PitchBlack
                || observation.EvidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
            )
        )
        {
            return "darkness.observation.authorization-invalid";
        }
        return null;
    }

    private static bool IsValidRequestId(string? requestId)
    {
        return !string.IsNullOrWhiteSpace(requestId)
            && requestId.Length <= DarknessAttackContract.MaximumRequestIdLength;
    }

    private static DarknessAttackUpdateResult Result(
        DarknessAttackMutationStatus status,
        string reason
    )
    {
        return new DarknessAttackUpdateResult(
            status,
            reason,
            DarknessAttackPromptKind.None,
            DarknessWarningClaimAction.None,
            string.Empty,
            null
        );
    }

    private sealed class OwnerState
    {
        internal OwnerState(DarknessAttackOwnerKey key)
        {
            Key = key;
        }

        internal DarknessAttackOwnerKey Key { get; }

        internal DarknessAttackOwnerState State { get; set; }

        internal long Revision { get; set; }

        internal EnvironmentLightLevel LightLevel { get; set; } = EnvironmentLightLevel.Dim;

        internal EnvironmentLightEvidenceStatus EvidenceStatus { get; set; } =
            EnvironmentLightEvidenceStatus.Fallback;

        internal string LightReason { get; set; } = "darkness.light.not-observed";

        internal double RemainingSeconds { get; set; }

        internal bool WarningClaimActive { get; set; }

        internal string WarningClipId { get; set; } = string.Empty;

        internal double WarningDurationSeconds { get; set; }

        internal string RequestId { get; set; } = string.Empty;

        internal int SampledSeconds { get; set; }

        internal string RngBranch { get; set; } = string.Empty;

        internal string CancelReason { get; set; } = string.Empty;

        internal DarknessAttackStateSnapshot CreateSnapshot()
        {
            return new DarknessAttackStateSnapshot(
                Key,
                State,
                Revision,
                LightLevel,
                EvidenceStatus,
                LightReason,
                RemainingSeconds,
                WarningDurationSeconds,
                WarningClaimActive,
                RequestId,
                SampledSeconds,
                RngBranch,
                CancelReason,
                WarningClipId,
                WarningDurationSeconds
            );
        }
    }
}
