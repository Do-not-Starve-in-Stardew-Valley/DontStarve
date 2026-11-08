#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Sleep;

/// <summary>
/// 纯协议状态机：host 持有可写睡眠快照，client 只保留连续 revision 的镜像。
/// </summary>
internal sealed class SleepLifecycleState
{
    private readonly Dictionary<string, SleepPlayerSnapshot> snapshots =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> highestSeenNonces =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> confirmationTokens = new(StringComparer.Ordinal);

    internal SleepAuthorityRole Role { get; private set; }

    internal string SessionId { get; private set; } = string.Empty;

    internal string LocalPlayerKey { get; private set; } = string.Empty;

    internal bool BeginHostSession(string sessionId, out string reason)
    {
        if (!SleepLifecycleProtocol.IsValidSessionId(sessionId))
        {
            reason = "sleep-host-session-id-is-invalid";
            return false;
        }

        ClearTransport();
        Role = SleepAuthorityRole.Host;
        SessionId = sessionId;
        LocalPlayerKey = string.Empty;
        reason = "sleep-host-session-started";
        return true;
    }

    internal bool BeginClientSession(
        string sessionId,
        string localPlayerKey,
        out string reason
    )
    {
        if (!SleepLifecycleProtocol.IsValidSessionId(sessionId))
        {
            reason = "sleep-client-session-id-is-invalid";
            return false;
        }
        if (!SleepLifecycleProtocol.IsValidPlayerKey(localPlayerKey))
        {
            reason = "sleep-client-player-key-is-invalid";
            return false;
        }

        ClearTransport();
        Role = SleepAuthorityRole.Client;
        SessionId = sessionId;
        LocalPlayerKey = localPlayerKey;
        reason = "sleep-client-session-started";
        return true;
    }

    internal bool IssueConfirmationToken(
        string playerKey,
        string sessionId,
        string attemptId,
        out string reason
    )
    {
        if (!MatchesPlayerSession(playerKey, sessionId, out reason))
            return false;
        if (!SleepLifecycleProtocol.IsValidText(attemptId))
        {
            reason = "sleep-attempt-id-is-invalid";
            return false;
        }

        confirmationTokens.Add(TokenKey(playerKey, attemptId));
        reason = "sleep-confirmation-token-issued";
        return true;
    }

    internal long GetHighestNonce(string playerKey)
    {
        return highestSeenNonces.TryGetValue(playerKey, out var nonce) ? nonce : 0;
    }

    internal bool AcceptNonce(string playerKey, long nonce, out string reason)
    {
        if (!SleepLifecycleProtocol.IsValidPlayerKey(playerKey))
        {
            reason = "sleep-player-key-is-invalid";
            return false;
        }
        if (nonce <= 0 || nonce <= GetHighestNonce(playerKey))
        {
            reason = "sleep-request-nonce-is-replayed-or-invalid";
            return false;
        }
        highestSeenNonces[playerKey] = nonce;
        reason = "sleep-request-nonce-accepted";
        return true;
    }

    internal bool ConsumeConfirmationToken(
        string playerKey,
        string sessionId,
        string attemptId,
        out string reason
    )
    {
        if (!MatchesPlayerSession(playerKey, sessionId, out reason))
            return false;
        if (!confirmationTokens.Remove(TokenKey(playerKey, attemptId)))
        {
            reason = "sleep-confirmation-token-is-missing-or-consumed";
            return false;
        }

        reason = "sleep-confirmation-token-consumed";
        return true;
    }

    internal bool CancelAttempt(
        string playerKey,
        string sessionId,
        string attemptId,
        out string reason
    )
    {
        if (!MatchesPlayerSession(playerKey, sessionId, out reason))
            return false;
        confirmationTokens.Remove(TokenKey(playerKey, attemptId));
        snapshots.Remove(playerKey);
        reason = "sleep-attempt-cancelled";
        return true;
    }

    internal bool UpsertHostSnapshot(
        SleepPlayerSnapshot snapshot,
        out SleepPlayerSnapshot applied,
        out string reason
    )
    {
        applied = new SleepPlayerSnapshot();
        if (Role != SleepAuthorityRole.Host)
        {
            reason = "sleep-host-authority-session-is-not-active";
            return false;
        }
        if (
            !SleepLifecycleProtocol.IsValidSnapshot(snapshot)
            || !string.Equals(snapshot.SessionId, SessionId, StringComparison.Ordinal)
        )
        {
            reason = "sleep-host-snapshot-is-invalid";
            return false;
        }

        var revision = snapshots.TryGetValue(snapshot.PlayerKey, out var current)
            ? current.Revision + 1
            : 1;
        applied = snapshot.Clone();
        applied.SessionId = SessionId;
        applied.Revision = revision;
        snapshots[snapshot.PlayerKey] = applied.Clone();
        reason = "sleep-host-snapshot-upserted";
        return true;
    }

    internal SleepSnapshotMessage CreateFullSnapshot()
    {
        var message = new SleepSnapshotMessage
        {
            SessionId = SessionId,
            IsFull = true,
        };
        foreach (var snapshot in snapshots.Values)
            message.Players.Add(snapshot.Clone());
        return message;
    }

    internal SleepSnapshotMessage CreateDeltaSnapshot(SleepPlayerSnapshot snapshot)
    {
        return new SleepSnapshotMessage
        {
            SessionId = SessionId,
            IsFull = false,
            Players = new List<SleepPlayerSnapshot> { snapshot.Clone() },
        };
    }

    internal SleepSnapshotApplyResult ApplyClientSnapshot(SleepSnapshotMessage? message)
    {
        if (Role != SleepAuthorityRole.Client)
            return Rejected("sleep-client-session-is-not-active");
        if (
            message is null
            || !SleepLifecycleProtocol.IsValidSessionId(message.SessionId)
            || !string.Equals(message.SessionId, SessionId, StringComparison.Ordinal)
        )
            return Rejected("sleep-snapshot-session-does-not-match");
        if (
            message.Players is null
            || !SleepLifecycleProtocol.HasUniquePlayerKeys(message.Players)
        )
            return Rejected("sleep-snapshot-player-set-is-invalid");
        foreach (var snapshot in message.Players)
        {
            if (
                !SleepLifecycleProtocol.IsValidSnapshot(snapshot)
                || !string.Equals(snapshot.SessionId, SessionId, StringComparison.Ordinal)
            )
                return Rejected("sleep-snapshot-state-is-invalid");
        }

        if (message.IsFull)
        {
            snapshots.Clear();
            foreach (var snapshot in message.Players)
                snapshots[snapshot.PlayerKey] = snapshot.Clone();
            return new SleepSnapshotApplyResult(
                SleepSnapshotApplyStatus.AppliedFull,
                "sleep-full-snapshot-applied"
            );
        }

        if (message.Players.Count != 1)
            return new SleepSnapshotApplyResult(
                SleepSnapshotApplyStatus.Rejected,
                "sleep-delta-must-contain-one-player"
            );

        var incoming = message.Players[0];
        if (!snapshots.TryGetValue(incoming.PlayerKey, out var current))
            return new SleepSnapshotApplyResult(
                SleepSnapshotApplyStatus.NeedsFullSnapshot,
                "sleep-delta-player-is-unknown"
            );
        if (incoming.Revision <= current.Revision)
            return new SleepSnapshotApplyResult(
                SleepSnapshotApplyStatus.IgnoredStaleOrDuplicate,
                "sleep-delta-is-stale"
            );
        if (incoming.Revision != current.Revision + 1)
            return new SleepSnapshotApplyResult(
                SleepSnapshotApplyStatus.NeedsFullSnapshot,
                "sleep-delta-revision-has-a-gap"
            );

        snapshots[incoming.PlayerKey] = incoming.Clone();
        return new SleepSnapshotApplyResult(
            SleepSnapshotApplyStatus.AppliedDelta,
            "sleep-delta-applied"
        );
    }

    internal bool TryGetSnapshot(string playerKey, out SleepPlayerSnapshot snapshot)
    {
        if (snapshots.TryGetValue(playerKey, out var current))
        {
            snapshot = current.Clone();
            return true;
        }

        snapshot = new SleepPlayerSnapshot();
        return false;
    }

    internal void ForgetPeer(string playerKey)
    {
        snapshots.Remove(playerKey);
        highestSeenNonces.Remove(playerKey);
        confirmationTokens.RemoveWhere(
            token => token.StartsWith(playerKey + "|", StringComparison.Ordinal)
        );
    }

    internal void ClearSession()
    {
        ClearTransport();
        Role = SleepAuthorityRole.None;
        SessionId = string.Empty;
        LocalPlayerKey = string.Empty;
    }

    private bool MatchesPlayerSession(
        string playerKey,
        string sessionId,
        out string reason
    )
    {
        if (!SleepLifecycleProtocol.IsValidPlayerKey(playerKey))
        {
            reason = "sleep-player-key-is-invalid";
            return false;
        }
        if (!SleepLifecycleProtocol.IsValidSessionId(sessionId)
            || !string.Equals(sessionId, SessionId, StringComparison.Ordinal))
        {
            reason = "sleep-request-session-does-not-match";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void ClearTransport()
    {
        snapshots.Clear();
        highestSeenNonces.Clear();
        confirmationTokens.Clear();
    }

    private static string TokenKey(string playerKey, string attemptId) =>
        string.Concat(playerKey, "|", attemptId);

    private static SleepSnapshotApplyResult Rejected(string reason) =>
        new(SleepSnapshotApplyStatus.Rejected, reason);
}
