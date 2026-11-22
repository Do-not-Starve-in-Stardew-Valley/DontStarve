#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Sleep;

internal enum SleepAuthorityRole
{
    None,
    Host,
    Client,
}

internal sealed class SleepBeginRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string BedId { get; set; } = string.Empty;

    public string AttemptId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public long ExpectedRevision { get; set; }
}

internal sealed class SleepCancelRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string AttemptId { get; set; } = string.Empty;

    public long Nonce { get; set; }
}

internal sealed class SleepSnapshotRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public long KnownRevision { get; set; }
}

internal sealed class SleepPendingDarknessWakeRequest
{
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>主机下发的 pending-darkness 死亡标记（防逃课决议③）。标记日随键携带，客户端
/// 导入时按本机 CurrentDay 重新 Normalize。</summary>
internal sealed class SleepPendingDarknessWakeMessage
{
    public string SessionId { get; set; } = string.Empty;

    public Dictionary<string, int> Markers { get; set; } = new();
}

internal sealed class SleepBeginDecision
{
    public string SessionId { get; set; } = string.Empty;
    public string PlayerKey { get; set; } = string.Empty;
    public string AttemptId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal sealed class SleepPlayerSnapshot
{
    public bool HasHealth { get; set; }
    public int Health { get; set; }
    public int MaximumHealth { get; set; }
    // Null means an older peer omitted cause metadata; preserve its prior zero-HP behavior.
    public bool? HungerDeath { get; set; }
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public bool Active { get; set; }

    public bool Confirmed { get; set; }

    public SleepMode Mode { get; set; }

    public SleepPhase Phase { get; set; }

    public string BedId { get; set; } = string.Empty;

    public string SettlementId { get; set; } = string.Empty;

    public long CurrentMinute { get; set; }

    public long TargetMinute { get; set; }

    public long Revision { get; set; }

    internal SleepPlayerSnapshot Clone()
    {
        return new SleepPlayerSnapshot
        {
            HasHealth = HasHealth,
            Health = Health,
            MaximumHealth = MaximumHealth,
            HungerDeath = HungerDeath,
            SessionId = SessionId,
            PlayerKey = PlayerKey,
            Active = Active,
            Confirmed = Confirmed,
            Mode = Mode,
            Phase = Phase,
            BedId = BedId,
            SettlementId = SettlementId,
            CurrentMinute = CurrentMinute,
            TargetMinute = TargetMinute,
            Revision = Revision,
        };
    }
}

internal sealed class SleepSnapshotMessage
{
    public string SessionId { get; set; } = string.Empty;

    public bool IsFull { get; set; }

    public List<SleepPlayerSnapshot> Players { get; set; } = new();
}

internal enum SleepSnapshotApplyStatus
{
    AppliedFull,
    AppliedDelta,
    IgnoredStaleOrDuplicate,
    NeedsFullSnapshot,
    Rejected,
}

internal readonly record struct SleepSnapshotApplyResult(
    SleepSnapshotApplyStatus Status,
    string Reason
)
{
    internal bool NeedsFullSnapshot =>
        Status == SleepSnapshotApplyStatus.NeedsFullSnapshot;
}

internal readonly record struct SleepRequestValidationResult(
    bool Accepted,
    bool CanConsumeNonce,
    bool NeedsSnapshot,
    string Reason
);

internal static class SleepLifecycleProtocol
{
    internal static bool IsValidSessionId(string sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && Guid.TryParseExact(sessionId, "N", out _);
    }

    internal static bool IsValidPlayerKey(string playerKey)
    {
        return long.TryParse(
                playerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var playerId
            )
            && string.Equals(
                playerKey,
                playerId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            );
    }

    internal static bool IsValidText(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    internal static SleepRequestValidationResult ValidateBeginRequest(
        SleepBeginRequest? request,
        long senderPlayerId,
        string currentSessionId,
        bool playerIsOnline,
        long highestSeenNonce,
        long currentRevision
    )
    {
        if (request is null)
            return Reject("sleep-begin-request-is-missing");
        if (
            !IsValidSessionId(request.SessionId)
            || !string.Equals(request.SessionId, currentSessionId, StringComparison.Ordinal)
        )
            return Reject("sleep-request-session-does-not-match", needsSnapshot: true);
        if (
            !IsValidPlayerKey(request.PlayerKey)
            || !long.TryParse(
                request.PlayerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
            return Reject("sleep-request-sender-does-not-own-player");
        if (!playerIsOnline)
            return Reject("sleep-request-player-is-not-online");
        if (!IsValidText(request.BedId))
            return Reject("sleep-request-bed-id-is-invalid");
        if (!IsValidText(request.AttemptId))
            return Reject("sleep-request-attempt-id-is-invalid");
        if (request.Nonce <= 0 || request.Nonce <= highestSeenNonce)
            return Reject("sleep-request-nonce-is-replayed-or-invalid");
        if (request.ExpectedRevision != currentRevision)
            return new SleepRequestValidationResult(
                false,
                true,
                true,
                "sleep-request-expected-revision-does-not-match"
            );

        return new SleepRequestValidationResult(
            true,
            true,
            false,
            "sleep-begin-request-is-valid"
        );
    }

    internal static SleepRequestValidationResult ValidateCancelRequest(
        SleepCancelRequest? request,
        long senderPlayerId,
        string currentSessionId,
        bool playerIsOnline,
        long highestSeenNonce
    )
    {
        if (request is null)
            return Reject("sleep-cancel-request-is-missing");
        if (
            !IsValidSessionId(request.SessionId)
            || !string.Equals(request.SessionId, currentSessionId, StringComparison.Ordinal)
        )
            return Reject("sleep-request-session-does-not-match", needsSnapshot: true);
        if (
            !IsValidPlayerKey(request.PlayerKey)
            || !long.TryParse(
                request.PlayerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
            return Reject("sleep-request-sender-does-not-own-player");
        if (!playerIsOnline)
            return Reject("sleep-request-player-is-not-online");
        if (!IsValidText(request.AttemptId))
            return Reject("sleep-request-attempt-id-is-invalid");
        if (request.Nonce <= 0 || request.Nonce <= highestSeenNonce)
            return Reject("sleep-request-nonce-is-replayed-or-invalid");

        return new SleepRequestValidationResult(
            true,
            true,
            false,
            "sleep-cancel-request-is-valid"
        );
    }

    internal static bool IsValidSnapshot(SleepPlayerSnapshot? snapshot)
    {
        return snapshot is not null
            && IsValidSessionId(snapshot.SessionId)
            && IsValidPlayerKey(snapshot.PlayerKey)
            && IsValidText(snapshot.BedId)
            && IsValidText(snapshot.SettlementId)
            && Enum.IsDefined(typeof(SleepMode), snapshot.Mode)
            && Enum.IsDefined(typeof(SleepPhase), snapshot.Phase)
            && snapshot.CurrentMinute >= 0
            && snapshot.TargetMinute >= snapshot.CurrentMinute
            && (!snapshot.HasHealth || (snapshot.MaximumHealth > 0
                && snapshot.MaximumHealth <= 1_000_000 && snapshot.Health >= 0
                && snapshot.Health <= snapshot.MaximumHealth))
            && (snapshot.HungerDeath != true
                || (snapshot.HasHealth && snapshot.Health == 0))
            && snapshot.Revision >= 0;
    }

    internal static bool HasUniquePlayerKeys(
        IReadOnlyCollection<SleepPlayerSnapshot> snapshots
    )
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || !keys.Add(snapshot.PlayerKey))
                return false;
        }
        return true;
    }

    private static SleepRequestValidationResult Reject(
        string reason,
        bool needsSnapshot = false
    )
    {
        return new SleepRequestValidationResult(false, false, needsSnapshot, reason);
    }
}
