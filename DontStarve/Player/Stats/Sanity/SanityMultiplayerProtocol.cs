#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>主机发布的单玩家权威状态；revision 只在真实数值变化时递增。</summary>
internal sealed class SanityPlayerSnapshot
{
    public string PlayerKey { get; set; } = string.Empty;

    public double Current { get; set; }

    public double Maximum { get; set; }

    public long Revision { get; set; }

    internal SanityPlayerSnapshot Clone()
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = PlayerKey,
            Current = Current,
            Maximum = Maximum,
            Revision = Revision,
        };
    }
}

/// <summary>
/// Sanity 快照信封。完整快照可建立/重建会话；增量快照只允许连续 revision。
/// </summary>
internal sealed class SanitySnapshotMessage
{
    public string SessionId { get; set; } = string.Empty;

    public bool IsFull { get; set; }

    public List<SanityPlayerSnapshot> Players { get; set; } = new();
}

/// <summary>
/// 客户端只能提交交互事实，不携带 Set/Change 数值；delta 必须由主机 truth source 重算。
/// </summary>
internal sealed class SanityChangeRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public SanityChangeSource Source { get; set; }

    public string InteractionId { get; set; } = string.Empty;

    public long Nonce { get; set; }

    public long ExpectedRevision { get; set; }
}

internal sealed class SanitySnapshotRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public long KnownRevision { get; set; }
}

internal enum SanitySnapshotApplyStatus
{
    AppliedFull,
    AppliedDelta,
    IgnoredStaleOrDuplicate,
    NeedsFullSnapshot,
    Rejected,
}

internal readonly record struct SanitySnapshotApplyResult(
    SanitySnapshotApplyStatus Status,
    string Reason
)
{
    internal bool NeedsFullSnapshot =>
        Status == SanitySnapshotApplyStatus.NeedsFullSnapshot;
}

internal static class SanityProtocol
{
    internal static bool IsValidSessionId(string sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && Guid.TryParseExact(sessionId, "N", out _);
    }

    internal static bool IsValidSnapshot(SanityPlayerSnapshot snapshot)
    {
        return snapshot is not null
            && SanityPlayerKey.IsCanonical(snapshot.PlayerKey)
            && double.IsFinite(snapshot.Current)
            && double.IsFinite(snapshot.Maximum)
            && snapshot.Maximum > 0
            && snapshot.Current >= 0
            && snapshot.Current <= snapshot.Maximum
            && snapshot.Revision >= 0;
    }

    internal static bool HasUniquePlayerKeys(
        IReadOnlyCollection<SanityPlayerSnapshot> snapshots
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
