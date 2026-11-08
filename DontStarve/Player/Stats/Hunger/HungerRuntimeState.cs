#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Hunger;

internal sealed class HungerPlayerRuntimeState
{
    internal HungerPlayerRuntimeState(
        string playerKey,
        double current,
        double maximum,
        long revision
    )
    {
        PlayerKey = playerKey;
        Current = current;
        Maximum = maximum;
        Revision = revision;
    }

    internal string PlayerKey { get; }

    internal double Current { get; set; }

    internal double Maximum { get; }

    internal long Revision { get; set; }

    internal HungerPlayerSnapshot Snapshot()
    {
        return new HungerPlayerSnapshot
        {
            PlayerKey = PlayerKey,
            Current = Current,
            Maximum = Maximum,
            Revision = Revision,
        };
    }
}

/// <summary>
/// Hunger 会话内唯一的按玩家状态表。主机持有可写状态，客机只持有主机快照的只读副本。
/// </summary>
internal sealed class HungerRuntimeStateStore
{
    private readonly Dictionary<string, HungerPlayerRuntimeState> players =
        new(StringComparer.Ordinal);
    private HungerPersistenceResult? persistenceBase;
    private string masterPlayerKey = string.Empty;
    private string localPlayerKey = string.Empty;

    internal HungerAuthorityRole Role { get; private set; }

    internal string SessionId { get; private set; } = string.Empty;

    internal string MasterPlayerKey => masterPlayerKey;

    internal bool HasActiveSession => Role != HungerAuthorityRole.None;

    internal bool CanSavePersistence => Role == HungerAuthorityRole.Host && persistenceBase?.CanSave == true;

    internal bool BeginHostSession(
        string sessionId,
        HungerPersistenceResult persistence,
        string masterKey,
        out string reason
    )
    {
        if (!HungerProtocol.IsValidSessionId(sessionId))
        {
            reason = "host-hunger-session-id-is-invalid";
            return false;
        }
        if (!HungerProtocol.IsValidPlayerKey(masterKey))
        {
            reason = "master-hunger-player-key-is-invalid";
            return false;
        }

        Clear();
        Role = HungerAuthorityRole.Host;
        SessionId = sessionId;
        masterPlayerKey = masterKey;
        persistenceBase = persistence;

        if (!TryEnsureHostPlayer(masterKey, out _, out reason))
        {
            Clear();
            return false;
        }

        reason = "host-hunger-session-started";
        return true;
    }

    internal bool BeginClientSession(string playerKey, out string reason)
    {
        if (!HungerProtocol.IsValidPlayerKey(playerKey))
        {
            reason = "client-hunger-player-key-is-invalid";
            return false;
        }

        Clear();
        Role = HungerAuthorityRole.Client;
        localPlayerKey = playerKey;
        players[playerKey] = new HungerPlayerRuntimeState(
            playerKey,
            HungerRules.DefaultMaximum,
            HungerRules.DefaultMaximum,
            0
        );
        reason = "client-hunger-session-awaiting-host-snapshot";
        return true;
    }

    internal void Clear()
    {
        players.Clear();
        persistenceBase = null;
        masterPlayerKey = string.Empty;
        localPlayerKey = string.Empty;
        SessionId = string.Empty;
        Role = HungerAuthorityRole.None;
    }

    internal void UpdatePersistenceBase(HungerPersistenceResult persistence)
    {
        if (Role == HungerAuthorityRole.Host)
            persistenceBase = persistence;
    }

    internal void ForgetPeer(string playerKey)
    {
        // Keep the player's runtime value for reconnect and for the next host save.
        // Only request sequencing belongs to the peer session and is cleared by the service.
    }

    internal bool TryEnsureHostPlayer(
        string playerKey,
        out HungerPlayerSnapshot snapshot,
        out string reason
    )
    {
        snapshot = new HungerPlayerSnapshot();
        if (Role != HungerAuthorityRole.Host)
        {
            reason = "host-hunger-authority-session-is-not-active";
            return false;
        }
        if (!HungerProtocol.IsValidPlayerKey(playerKey))
        {
            reason = "hunger-player-key-is-not-canonical";
            return false;
        }
        if (players.TryGetValue(playerKey, out var existing))
        {
            snapshot = existing.Snapshot();
            reason = "runtime-hunger-player-already-exists";
            return true;
        }

        var current = HungerRules.DefaultMaximum;
        if (
            persistenceBase?.Data?.Players is not null
            && persistenceBase.Data.Players.TryGetValue(playerKey, out var saved)
            && HungerSaveDataCodec.TryResolvePlayerCurrent(
                saved,
                HungerRules.DefaultMaximum,
                out var migratedCurrent
            )
        )
        {
            current = migratedCurrent;
        }

        var state = new HungerPlayerRuntimeState(
            playerKey,
            current,
            HungerRules.DefaultMaximum,
            0
        );
        players.Add(playerKey, state);
        snapshot = state.Snapshot();
        reason = "runtime-hunger-player-created";
        return true;
    }

    internal double GetCurrentOrDefault(string playerKey)
    {
        return players.TryGetValue(playerKey, out var state)
            ? state.Current
            : HungerRules.DefaultMaximum;
    }

    internal double GetMaximumOrDefault(string playerKey)
    {
        return players.TryGetValue(playerKey, out var state)
            ? state.Maximum
            : HungerRules.DefaultMaximum;
    }

    internal bool TryGetSnapshot(string playerKey, out HungerPlayerSnapshot snapshot)
    {
        if (players.TryGetValue(playerKey, out var state))
        {
            snapshot = state.Snapshot();
            return true;
        }

        snapshot = new HungerPlayerSnapshot();
        return false;
    }

    internal bool TrySetHostValue(
        string playerKey,
        double requestedValue,
        out HungerPlayerSnapshot snapshot,
        out bool changed,
        out string reason
    )
    {
        snapshot = new HungerPlayerSnapshot();
        changed = false;
        if (Role != HungerAuthorityRole.Host)
        {
            reason = "hunger-set-is-host-only";
            return false;
        }
        if (!double.IsFinite(requestedValue))
        {
            reason = "requested-hunger-value-must-be-finite";
            return false;
        }
        if (!TryEnsureHostPlayer(playerKey, out snapshot, out reason))
            return false;

        var state = players[playerKey];
            var clamped = HungerRules.ClampForBoundary(requestedValue, state.Maximum);
        if (clamped.Equals(state.Current))
        {
            reason = "hunger-value-is-unchanged";
            return true;
        }
        if (state.Revision == long.MaxValue)
        {
            reason = "hunger-revision-is-exhausted";
            return false;
        }

        state.Current = clamped;
        state.Revision++;
        changed = true;
        snapshot = state.Snapshot();
        reason = "hunger-value-changed";
        return true;
    }

    internal HungerSnapshotMessage CreateFullSnapshot()
    {
        var message = new HungerSnapshotMessage
        {
            SessionId = SessionId,
            IsFull = true,
        };
        var keys = new List<string>(players.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
            message.Players.Add(players[key].Snapshot());
        return message;
    }

    internal HungerSnapshotMessage CreateDeltaSnapshot(HungerPlayerSnapshot snapshot)
    {
        return new HungerSnapshotMessage
        {
            SessionId = SessionId,
            IsFull = false,
            Players = new List<HungerPlayerSnapshot> { snapshot.Clone() },
        };
    }

    internal HungerSnapshotApplyResult ApplyClientSnapshot(HungerSnapshotMessage message)
    {
        if (Role != HungerAuthorityRole.Client)
            return new(HungerSnapshotApplyStatus.Rejected, "client-hunger-session-is-not-active");
        if (message is null || !HungerProtocol.IsValidSessionId(message.SessionId))
            return new(HungerSnapshotApplyStatus.Rejected, "hunger-snapshot-session-id-is-invalid");
        if (
            message.Players is null
            || message.Players.Count == 0
            || !HungerProtocol.HasUniquePlayerKeys(message.Players)
        )
            return new(HungerSnapshotApplyStatus.Rejected, "hunger-snapshot-player-set-is-invalid");
        foreach (var snapshot in message.Players)
        {
            if (!HungerProtocol.IsValidSnapshot(snapshot))
                return new(HungerSnapshotApplyStatus.Rejected, "hunger-snapshot-state-is-invalid");
        }

        if (message.IsFull)
            return ApplyFullSnapshot(message);

        if (!string.Equals(SessionId, message.SessionId, StringComparison.Ordinal))
            return new(HungerSnapshotApplyStatus.NeedsFullSnapshot, "hunger-delta-session-does-not-match");
        if (message.Players.Count != 1)
            return new(HungerSnapshotApplyStatus.Rejected, "hunger-delta-must-contain-one-player");

        var incoming = message.Players[0];
        if (!players.TryGetValue(incoming.PlayerKey, out var current))
            return new(HungerSnapshotApplyStatus.NeedsFullSnapshot, "hunger-delta-player-is-unknown");
        if (incoming.Revision < current.Revision)
            return new(HungerSnapshotApplyStatus.IgnoredStaleOrDuplicate, "hunger-delta-is-stale");
        if (incoming.Revision == current.Revision)
        {
            var identical = incoming.Current.Equals(current.Current)
                && incoming.Maximum.Equals(current.Maximum);
            return new(
                identical
                    ? HungerSnapshotApplyStatus.IgnoredStaleOrDuplicate
                    : HungerSnapshotApplyStatus.NeedsFullSnapshot,
                identical
                    ? "hunger-delta-is-an-identical-duplicate"
                    : "hunger-delta-revision-conflicts-with-current-state"
            );
        }
        if (current.Revision == long.MaxValue || incoming.Revision != current.Revision + 1)
            return new(HungerSnapshotApplyStatus.NeedsFullSnapshot, "hunger-delta-revision-has-a-gap");

        players[incoming.PlayerKey] = FromSnapshot(incoming);
        return new(HungerSnapshotApplyStatus.AppliedDelta, "hunger-delta-applied");
    }

    internal HungerSaveData CaptureSaveData()
    {
        var data = new HungerSaveData();
        if (Role != HungerAuthorityRole.Host)
            return data;

        foreach (var state in players.Values)
        {
            data.Players[state.PlayerKey] = new HungerPlayerSaveData
            {
                Current = state.Current,
                MaxAtSave = state.Maximum,
            };
        }
        return data;
    }

    private HungerSnapshotApplyResult ApplyFullSnapshot(HungerSnapshotMessage message)
    {
        if (
            !string.IsNullOrEmpty(SessionId)
            && !string.Equals(SessionId, message.SessionId, StringComparison.Ordinal)
        )
            return new(HungerSnapshotApplyStatus.Rejected, "hunger-full-snapshot-session-does-not-match");

        var containsLocalPlayer = false;
        foreach (var snapshot in message.Players)
        {
            if (string.Equals(snapshot.PlayerKey, localPlayerKey, StringComparison.Ordinal))
            {
                containsLocalPlayer = true;
                break;
            }
        }
        if (!containsLocalPlayer)
            return new(HungerSnapshotApplyStatus.NeedsFullSnapshot, "hunger-full-snapshot-does-not-contain-local-player");

        if (!string.IsNullOrEmpty(SessionId))
        {
            foreach (var snapshot in message.Players)
            {
                if (!players.TryGetValue(snapshot.PlayerKey, out var current))
                    continue;
                if (snapshot.Revision < current.Revision)
                    return new(HungerSnapshotApplyStatus.IgnoredStaleOrDuplicate, "hunger-full-snapshot-is-stale");
                if (
                    snapshot.Revision == current.Revision
                    && (
                        !snapshot.Current.Equals(current.Current)
                        || !snapshot.Maximum.Equals(current.Maximum)
                    )
                )
                    return new(HungerSnapshotApplyStatus.NeedsFullSnapshot, "hunger-full-snapshot-revision-conflicts-with-current-state");
            }
        }

        players.Clear();
        foreach (var snapshot in message.Players)
            players.Add(snapshot.PlayerKey, FromSnapshot(snapshot));
        SessionId = message.SessionId;
        return new(HungerSnapshotApplyStatus.AppliedFull, "hunger-full-snapshot-applied");
    }

    private static HungerPlayerRuntimeState FromSnapshot(HungerPlayerSnapshot snapshot)
    {
        return new HungerPlayerRuntimeState(
            snapshot.PlayerKey,
            snapshot.Current,
            snapshot.Maximum,
            snapshot.Revision
        );
    }

}

internal sealed class HungerChangeService : IHungerDebugBackend
{
    private readonly HungerRuntimeStateStore state = new();
    private readonly Dictionary<string, long> highestSeenNonce =
        new(StringComparer.Ordinal);
    private long nextClientNonce;
    private bool hostFoodCompletionPatchInstalled;

    internal event Action<HungerPlayerSnapshot>? HostStateChanged;

    internal event Action<HungerFoodChangeRequest>? ClientFoodRequestCreated;

    internal HungerAuthorityRole Role => state.Role;

    internal string SessionId => state.SessionId;

    internal string MasterPlayerKey => state.MasterPlayerKey;

    internal bool IsDebugLocked { get; private set; }

    internal bool HostFoodCompletionSupported { get; private set; }

    internal bool HostFoodCompletionPatchInstalled => hostFoodCompletionPatchInstalled;

    internal bool UsesHostFoodCompletion => HungerFoodCompletionPolicy.UsesHostSettlement(
        Role,
        hostFoodCompletionPatchInstalled,
        HostFoodCompletionSupported
    );

    internal bool CanSavePersistence => state.CanSavePersistence;

    internal bool BeginHostSession(
        string sessionId,
        HungerPersistenceResult persistence,
        string masterPlayerKey,
        out string reason
    )
    {
        nextClientNonce = 0;
        highestSeenNonce.Clear();
        IsDebugLocked = false;
        HostFoodCompletionSupported = false;
        return state.BeginHostSession(sessionId, persistence, masterPlayerKey, out reason);
    }

    internal bool BeginClientSession(string localPlayerKey, out string reason)
    {
        nextClientNonce = 0;
        highestSeenNonce.Clear();
        IsDebugLocked = false;
        HostFoodCompletionSupported = false;
        return state.BeginClientSession(localPlayerKey, out reason);
    }

    internal void ClearSession()
    {
        nextClientNonce = 0;
        highestSeenNonce.Clear();
        IsDebugLocked = false;
        HostFoodCompletionSupported = false;
        state.Clear();
    }

    internal void SetFoodCompletionPatchInstalled(bool installed)
    {
        hostFoodCompletionPatchInstalled = installed;
    }

    internal void SetHostFoodCompletionSupported(bool supported)
    {
        if (state.Role == HungerAuthorityRole.Host)
            HostFoodCompletionSupported = supported;
    }

    internal void SetDebugLocked(bool locked)
    {
        if (state.Role == HungerAuthorityRole.Host)
            IsDebugLocked = locked;
    }

    internal void UpdatePersistenceBase(HungerPersistenceResult persistence) =>
        state.UpdatePersistenceBase(persistence);

    internal void ForgetPeer(string playerKey)
    {
        highestSeenNonce.Remove(playerKey);
        state.ForgetPeer(playerKey);
    }

    internal bool TryEnsureHostPlayer(
        string playerKey,
        out HungerPlayerSnapshot snapshot,
        out string reason
    ) => state.TryEnsureHostPlayer(playerKey, out snapshot, out reason);

    internal double GetCurrent(string playerKey) => state.GetCurrentOrDefault(playerKey);

    internal double GetMaximum(string playerKey) => state.GetMaximumOrDefault(playerKey);

    internal bool TryGetSnapshot(string playerKey, out HungerPlayerSnapshot snapshot) =>
        state.TryGetSnapshot(playerKey, out snapshot);

    internal HungerChangeResult SetHostValue(string playerKey, double value)
    {
        if (IsDebugLocked)
            return HungerChangeResult.Rejected("hunger-change-locked-by-debug-command");

        if (
            !state.TrySetHostValue(
                playerKey,
                value,
                out var snapshot,
                out var changed,
                out var reason
            )
        )
            return HungerChangeResult.Rejected(reason);

        PublishIfChanged(snapshot, changed);
        return HungerChangeResult.Applied(snapshot, changed, reason);
    }

    internal HungerChangeResult SetDebugValue(string playerKey, double value)
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
            return HungerChangeResult.Rejected(reason);

        PublishIfChanged(snapshot, changed);
        return HungerChangeResult.Applied(snapshot, changed, reason);
    }

    HungerDebugMutationResult IHungerDebugBackend.SetDebugValue(
        string playerKey,
        double value
    )
    {
        var before = TryGetSnapshot(playerKey, out var current)
            ? current
            : null;
        var result = SetDebugValue(playerKey, value);
        var snapshot = result.Snapshot ?? before;
        return new HungerDebugMutationResult(
            result.Accepted,
            snapshot is not null && before is not null && !snapshot.Current.Equals(before.Current),
            result.Reason,
            snapshot
        );
    }

    HungerAuthorityRole IHungerDebugBackend.Role => Role;

    bool IHungerDebugBackend.IsDebugLocked => IsDebugLocked;

    bool IHungerDebugBackend.TryGetSnapshot(
        string playerKey,
        out HungerPlayerSnapshot snapshot
    ) => TryGetSnapshot(playerKey, out snapshot);

    bool IHungerDebugBackend.TryEnsureHostPlayer(
        string playerKey,
        out HungerPlayerSnapshot snapshot,
        out string reason
    ) => TryEnsureHostPlayer(playerKey, out snapshot, out reason);

    void IHungerDebugBackend.SetDebugLocked(bool locked) => SetDebugLocked(locked);

    internal HungerChangeResult ApplyFood(string playerKey, double delta)
    {
        if (state.Role != HungerAuthorityRole.Host)
            return HungerChangeResult.Rejected("food-change-is-host-only");
        if (IsDebugLocked)
            return HungerChangeResult.Rejected("hunger-change-locked-by-debug-command");
        if (!double.IsFinite(delta))
            return HungerChangeResult.Rejected("food-hunger-delta-must-be-finite");
        if (!state.TryEnsureHostPlayer(playerKey, out var current, out var reason))
            return HungerChangeResult.Rejected(reason);

        if (
            !state.TrySetHostValue(
                playerKey,
                current.Current + delta,
                out var snapshot,
                out var changed,
                out reason
            )
        )
            return HungerChangeResult.Rejected(reason);

        PublishIfChanged(snapshot, changed);
        return HungerChangeResult.Applied(snapshot, changed, reason);
    }

    internal HungerChangeResult QueueFood(string playerKey, string interactionId)
    {
        if (state.Role != HungerAuthorityRole.Client)
            return HungerChangeResult.Rejected("food-request-is-client-only");
        if (!HungerProtocol.IsValidSessionId(state.SessionId))
            return HungerChangeResult.Rejected("client-authority-session-is-not-ready");
        if (!state.TryGetSnapshot(playerKey, out var current))
            return HungerChangeResult.Rejected("client-hunger-player-is-unknown");
        if (!HungerProtocol.IsValidInteractionId(interactionId))
            return HungerChangeResult.Rejected("food-interaction-id-is-invalid");

        var request = new HungerFoodChangeRequest
        {
            SessionId = state.SessionId,
            PlayerKey = playerKey,
            InteractionId = interactionId,
            Nonce = ++nextClientNonce,
            ExpectedRevision = current.Revision,
        };
        ClientFoodRequestCreated?.Invoke(request);
        return HungerChangeResult.Queued(request);
    }

    internal HungerHostRequestResult HandleHostFoodRequest(
        HungerFoodChangeRequest request,
        long senderPlayerId,
        double delta
    )
    {
        if (state.Role != HungerAuthorityRole.Host)
            return new(false, true, "host-hunger-session-is-not-active", null);
        if (request is null)
            return new(false, false, "hunger-food-request-is-missing", null);
        if (!state.TryEnsureHostPlayer(request.PlayerKey, out var current, out var reason))
            return new(false, false, reason, null);
        var highestNonce = highestSeenNonce.TryGetValue(request.PlayerKey, out var seenNonce)
            ? seenNonce
            : 0L;
        var validation = HungerProtocol.ValidateFoodRequest(
            request,
            senderPlayerId,
            state.SessionId,
            StardewValley.Game1.GetPlayer(senderPlayerId, onlyOnline: true) is not null,
            highestNonce,
            current.Revision
        );
        if (!validation.Accepted)
        {
            if (validation.CanConsumeNonce)
                highestSeenNonce[request.PlayerKey] = request.Nonce;
            return new(false, validation.NeedsSnapshot, validation.Reason, current);
        }
        highestSeenNonce[request.PlayerKey] = request.Nonce;
        if (!double.IsFinite(delta))
            return new(false, false, "host-recomputed-hunger-delta-is-invalid", current);

        var applied = ApplyFood(request.PlayerKey, delta);
        if (applied.Snapshot is null)
            return new(false, true, applied.Reason, current);
        return new(
            true,
            false,
            applied.Reason,
            applied.Snapshot
        );
    }

    internal HungerSnapshotMessage CreateFullSnapshot()
    {
        var message = state.CreateFullSnapshot();
        message.HostFoodCompletionSupported = HostFoodCompletionSupported;
        return message;
    }

    internal HungerSnapshotMessage CreateDeltaSnapshot(HungerPlayerSnapshot snapshot)
    {
        var message = state.CreateDeltaSnapshot(snapshot);
        message.HostFoodCompletionSupported = HostFoodCompletionSupported;
        return message;
    }

    internal HungerSnapshotApplyResult ApplyClientSnapshot(HungerSnapshotMessage message)
    {
        var result = state.ApplyClientSnapshot(message);
        if (
            state.Role == HungerAuthorityRole.Client
            && result.Status is HungerSnapshotApplyStatus.AppliedFull
                or HungerSnapshotApplyStatus.AppliedDelta
                or HungerSnapshotApplyStatus.IgnoredStaleOrDuplicate
        )
        {
            HostFoodCompletionSupported = message.HostFoodCompletionSupported;
        }

        return result;
    }

    internal HungerSaveData CaptureSaveData() => state.CaptureSaveData();

    private void PublishIfChanged(HungerPlayerSnapshot snapshot, bool changed)
    {
        if (changed)
            HostStateChanged?.Invoke(snapshot.Clone());
    }
}

internal sealed class HungerChangeResult
{
    private HungerChangeResult(
        bool accepted,
        bool queued,
        string reason,
        HungerPlayerSnapshot? snapshot,
        HungerFoodChangeRequest? request
    )
    {
        Accepted = accepted;
        IsQueued = queued;
        Reason = reason;
        Snapshot = snapshot;
        Request = request;
    }

    internal bool Accepted { get; }

    internal bool IsQueued { get; }

    internal string Reason { get; }

    internal HungerPlayerSnapshot? Snapshot { get; }

    internal HungerFoodChangeRequest? Request { get; }

    internal static HungerChangeResult Applied(
        HungerPlayerSnapshot snapshot,
        bool changed,
        string reason
    ) => new(true, false, changed ? "hunger-value-applied" : reason, snapshot, null);

    internal static HungerChangeResult Queued(HungerFoodChangeRequest request) =>
        new(false, true, "client-food-request-queued", null, request);

    internal static HungerChangeResult Rejected(string reason) =>
        new(false, false, reason, null, null);
}

internal sealed class HungerHostRequestResult
{
    internal HungerHostRequestResult(
        bool accepted,
        bool needsSnapshot,
        string reason,
        HungerPlayerSnapshot? snapshot
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

    internal HungerPlayerSnapshot? Snapshot { get; }
}
