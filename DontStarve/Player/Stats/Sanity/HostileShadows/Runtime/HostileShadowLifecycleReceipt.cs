#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal enum HostileShadowLifecycleTransitionKind
{
    HitTeleport,
    Dying,
    Despawn,
}

internal readonly record struct HostileShadowLifecycleReceipt(
    string CorrelationId,
    string SessionId,
    long EntityId,
    long Revision,
    HostileShadowLifecycleTransitionKind Kind,
    string Reason,
    int? TeleportRandomSeed,
    string AttributedPlayerKey
)
{
    // Only the internal true-death receipt may cross into settlement. Network deltas retain their
    // separate false gate, so HitTeleport/Despawn and every client remain side-effect free.
    internal bool SettlementEligible =>
        Kind == HostileShadowLifecycleTransitionKind.Dying;
    internal bool DropEligible =>
        Kind == HostileShadowLifecycleTransitionKind.Dying;
    internal bool RewardEligible =>
        Kind == HostileShadowLifecycleTransitionKind.Dying
        && SanityPlayerKey.IsCanonical(AttributedPlayerKey);
    internal long DeathRevision => Kind == HostileShadowLifecycleTransitionKind.Dying
        ? Revision
        : 0;

    internal static bool TryCreate(
        string sessionId,
        long entityId,
        long revision,
        HostileShadowLifecycleTransitionKind kind,
        string reason,
        int? teleportRandomSeed,
        string attributedPlayerKey,
        out HostileShadowLifecycleReceipt receipt
    )
    {
        receipt = default;
        if (
            !SanityProtocol.IsValidSessionId(sessionId)
            || entityId <= 0
            || revision <= 0
            || kind
                is not HostileShadowLifecycleTransitionKind.HitTeleport
                    and not HostileShadowLifecycleTransitionKind.Dying
                    and not HostileShadowLifecycleTransitionKind.Despawn
            || string.IsNullOrWhiteSpace(reason)
            || attributedPlayerKey is null
            || (
                attributedPlayerKey.Length > 0
                && !SanityPlayerKey.IsCanonical(attributedPlayerKey)
            )
            || (
                kind == HostileShadowLifecycleTransitionKind.HitTeleport
                    ? !teleportRandomSeed.HasValue
                    : teleportRandomSeed.HasValue
            )
        )
        {
            return false;
        }

        var correlationId = CreateCorrelationId(sessionId, entityId, revision, kind);
        receipt = new HostileShadowLifecycleReceipt(
            correlationId,
            sessionId,
            entityId,
            revision,
            kind,
            reason,
            teleportRandomSeed,
            attributedPlayerKey
        );
        return true;
    }

    internal static string CreateCorrelationId(
        string sessionId,
        long entityId,
        long revision,
        HostileShadowLifecycleTransitionKind kind
    )
    {
        if (
            !SanityProtocol.IsValidSessionId(sessionId)
            || entityId <= 0
            || revision <= 0
            || kind
                is not HostileShadowLifecycleTransitionKind.HitTeleport
                    and not HostileShadowLifecycleTransitionKind.Dying
                    and not HostileShadowLifecycleTransitionKind.Despawn
        )
        {
            return string.Empty;
        }
        return string.Concat(
            sessionId,
            ":",
            entityId.ToString(CultureInfo.InvariantCulture),
            ":",
            revision.ToString(CultureInfo.InvariantCulture),
            ":",
            KindId(kind)
        );
    }

    private static string KindId(HostileShadowLifecycleTransitionKind kind)
    {
        return kind switch
        {
            HostileShadowLifecycleTransitionKind.HitTeleport => "hit-teleport",
            HostileShadowLifecycleTransitionKind.Dying => "dying",
            HostileShadowLifecycleTransitionKind.Despawn => "despawn",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}

internal enum HostileShadowLifecycleReceiptRecordStatus
{
    Recorded,
    Duplicate,
    Conflict,
    Rejected,
}

internal readonly record struct HostileShadowLifecycleReceiptRecordResult(
    HostileShadowLifecycleReceiptRecordStatus Status,
    HostileShadowLifecycleReceipt Receipt,
    string Reason
);

/// <summary>
/// Bounded session-only diagnostic receipt store. Exact delta replay is idempotent, while one
/// session/entity/revision cannot be reinterpreted as another lifecycle kind.
/// </summary>
internal sealed class HostileShadowLifecycleReceiptStore
{
    internal const int MaximumReceipts = 256;

    private readonly Dictionary<string, HostileShadowLifecycleReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostileShadowLifecycleTransitionKind> kindsByRevision =
        new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    internal int Count => receipts.Count;

    internal HostileShadowLifecycleReceiptRecordResult Record(
        HostileShadowLifecycleReceipt receipt
    )
    {
        if (
            string.IsNullOrWhiteSpace(receipt.CorrelationId)
            || !HostileShadowLifecycleReceipt.TryCreate(
                receipt.SessionId,
                receipt.EntityId,
                receipt.Revision,
                receipt.Kind,
                receipt.Reason,
                receipt.TeleportRandomSeed,
                receipt.AttributedPlayerKey,
                out var canonical
            )
            || !string.Equals(
                canonical.CorrelationId,
                receipt.CorrelationId,
                StringComparison.Ordinal
            )
        )
        {
            return Result(
                HostileShadowLifecycleReceiptRecordStatus.Rejected,
                receipt,
                "hostile-shadow.lifecycle-receipt-invalid"
            );
        }

        if (receipts.TryGetValue(receipt.CorrelationId, out var existing))
        {
            return existing == receipt
                ? Result(
                    HostileShadowLifecycleReceiptRecordStatus.Duplicate,
                    existing,
                    "hostile-shadow.lifecycle-receipt-duplicate"
                )
                : Result(
                    HostileShadowLifecycleReceiptRecordStatus.Conflict,
                    existing,
                    "hostile-shadow.lifecycle-receipt-correlation-conflict"
                );
        }

        var revisionKey = RevisionKey(receipt);
        if (
            kindsByRevision.TryGetValue(revisionKey, out var existingKind)
            && existingKind != receipt.Kind
        )
        {
            return Result(
                HostileShadowLifecycleReceiptRecordStatus.Conflict,
                receipt,
                "hostile-shadow.lifecycle-receipt-kind-conflict"
            );
        }

        while (receipts.Count >= MaximumReceipts && order.Count > 0)
        {
            var oldestCorrelation = order.Dequeue();
            if (!receipts.Remove(oldestCorrelation, out var oldest))
                continue;
            kindsByRevision.Remove(RevisionKey(oldest));
        }
        receipts.Add(receipt.CorrelationId, receipt);
        kindsByRevision[revisionKey] = receipt.Kind;
        order.Enqueue(receipt.CorrelationId);
        return Result(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            receipt,
            "hostile-shadow.lifecycle-receipt-recorded"
        );
    }

    internal bool TryGet(
        string correlationId,
        out HostileShadowLifecycleReceipt receipt
    )
    {
        return receipts.TryGetValue(correlationId, out receipt);
    }

    internal bool TryGetDying(
        string sessionId,
        long entityId,
        long deathRevision,
        out HostileShadowLifecycleReceipt receipt
    )
    {
        receipt = default;
        var correlationId = HostileShadowLifecycleReceipt.CreateCorrelationId(
            sessionId,
            entityId,
            deathRevision,
            HostileShadowLifecycleTransitionKind.Dying
        );
        return correlationId.Length > 0
            && receipts.TryGetValue(correlationId, out receipt);
    }

    internal void Clear()
    {
        receipts.Clear();
        kindsByRevision.Clear();
        order.Clear();
    }

    private static HostileShadowLifecycleReceiptRecordResult Result(
        HostileShadowLifecycleReceiptRecordStatus status,
        HostileShadowLifecycleReceipt receipt,
        string reason
    )
    {
        return new HostileShadowLifecycleReceiptRecordResult(status, receipt, reason);
    }

    private static string RevisionKey(HostileShadowLifecycleReceipt receipt)
    {
        return string.Concat(
            receipt.SessionId,
            ":",
            receipt.EntityId.ToString(CultureInfo.InvariantCulture),
            ":",
            receipt.Revision.ToString(CultureInfo.InvariantCulture)
        );
    }
}
