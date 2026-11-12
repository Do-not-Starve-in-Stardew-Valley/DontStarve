#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Audio.StatusFeedback;

namespace DontStarve.Player.Stats.Hunger;

internal enum HungerAuthorityRole
{
    None,
    Host,
    Client,
}

internal sealed class HungerPlayerSnapshot
{
    public string PlayerKey { get; set; } = string.Empty;

    public double Current { get; set; }

    public double Maximum { get; set; }

    public long Revision { get; set; }

    internal HungerPlayerSnapshot Clone()
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

internal sealed class HungerSnapshotMessage
{
    public string SessionId { get; set; } = string.Empty;

    public bool IsFull { get; set; }

    // Optional for older hosts/clients: false preserves the request-based food fallback.
    public bool HostFoodCompletionSupported { get; set; }

    /// <summary>
    /// 主机对该次变化的播音分类（仅增量快照有意义；完整快照恒为 Unknown）。
    /// wire 契约：Unknown=0 表示没有可播音事实，接收端必须静音，缺字段同样反序列化为 0。
    /// </summary>
    public StatusFeedbackTriggerKind FeedbackHint { get; set; }

    /// <summary>
    /// 主机对该次增量变化标注的结算种类（仅增量快照有意义；完整快照恒为 Unknown）。
    /// 进食恢复弹窗据此识别 Food 结算；wire 契约：Unknown=0 表示没有种类事实
    /// （完整快照、旧主机消息缺字段），接收端必须按「不可弹」处理——缺字段反序列化为 0，
    /// 旧端忽略本附加字段，对旧协议是安全默认（fail-safe）。
    /// </summary>
    public HungerChangeKind DeltaKind { get; set; }

    public List<HungerPlayerSnapshot> Players { get; set; } = new();
}

internal sealed class HungerFoodChangeRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    /// <summary>
    /// 客户端只提交完成食物动作的物品 key，不提交饥饿变化量。
    /// </summary>
    public string InteractionId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public long ExpectedRevision { get; set; }
}

internal sealed class HungerSnapshotRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public long KnownRevision { get; set; }
}

internal enum HungerSnapshotApplyStatus
{
    AppliedFull,
    AppliedDelta,
    IgnoredStaleOrDuplicate,
    NeedsFullSnapshot,
    Rejected,
}

internal readonly record struct HungerSnapshotApplyResult(
    HungerSnapshotApplyStatus Status,
    string Reason
)
{
    internal bool NeedsFullSnapshot =>
        Status == HungerSnapshotApplyStatus.NeedsFullSnapshot;
}

internal readonly record struct HungerFoodRequestValidationResult(
    bool Accepted,
    bool CanConsumeNonce,
    bool NeedsSnapshot,
    string Reason
);

internal static class HungerProtocol
{
    internal static bool IsValidSessionId(string sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && Guid.TryParseExact(sessionId, "N", out _);
    }

    internal static bool IsValidPlayerKey(string playerKey)
    {
        return TryParsePlayerKey(playerKey, out _);
    }

    internal static bool TryParsePlayerKey(string playerKey, out long playerId)
    {
        playerId = 0;
        return long.TryParse(
                playerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out playerId
            )
            && string.Equals(
                playerKey,
                playerId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            );
    }

    internal static bool IsValidInteractionId(string interactionId) =>
        !string.IsNullOrWhiteSpace(interactionId) && interactionId.Length <= 128;

    internal static bool IsValidNonce(long nonce) => nonce > 0;

    internal static HungerFoodRequestValidationResult ValidateFoodRequest(
        HungerFoodChangeRequest? request,
        long senderPlayerId,
        string currentSessionId,
        bool playerIsOnline,
        long highestSeenNonce,
        long currentRevision
    )
    {
        if (request is null)
            return new(false, false, false, "hunger-food-request-is-missing");
        if (
            !IsValidSessionId(request.SessionId)
            || !string.Equals(request.SessionId, currentSessionId, StringComparison.Ordinal)
        )
            return new(false, false, true, "hunger-request-session-does-not-match");
        if (!TryParsePlayerKey(request.PlayerKey, out var requestedPlayerId) || requestedPlayerId != senderPlayerId)
            return new(false, false, false, "hunger-request-sender-does-not-own-player");
        if (!playerIsOnline)
            return new(false, false, false, "hunger-request-player-is-not-online");
        if (!IsValidInteractionId(request.InteractionId))
            return new(false, false, false, "hunger-request-interaction-id-is-invalid");
        if (!IsValidNonce(request.Nonce) || request.Nonce <= highestSeenNonce)
            return new(false, false, false, "hunger-request-nonce-is-replayed-or-invalid");
        if (request.ExpectedRevision != currentRevision)
            return new(false, true, true, "hunger-request-expected-revision-does-not-match");

        return new(true, true, false, "hunger-food-request-is-valid");
    }

    internal static bool IsValidSnapshot(HungerPlayerSnapshot snapshot)
    {
        return snapshot is not null
            && IsValidPlayerKey(snapshot.PlayerKey)
            && double.IsFinite(snapshot.Current)
            && double.IsFinite(snapshot.Maximum)
            && snapshot.Maximum > 0
            && snapshot.Current >= 0
            && snapshot.Current <= snapshot.Maximum
            && snapshot.Revision >= 0;
    }

    internal static bool HasUniquePlayerKeys(
        IReadOnlyCollection<HungerPlayerSnapshot> snapshots
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
}
