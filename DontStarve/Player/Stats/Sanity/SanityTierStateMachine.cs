#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity;

internal enum SanityTierEvaluationStatus
{
    Applied,
    NoChange,
    IgnoredDuplicate,
    IgnoredStale,
    SystemDisabled,
    Unavailable,
}

internal enum SanityStateEventKind
{
    TierEntered,
    TierExited,
    SystemEnabled,
    SystemDisabled,
    OwnerInvalidated,
    WorldCleanup,
}

internal readonly record struct SanityStateEvent(
    string EventId,
    SanityStateEventKind Kind,
    string PlayerKey,
    string TierId,
    long Revision,
    double? Ratio
);

internal sealed class SanityTierEvaluationResult
{
    internal SanityTierEvaluationResult(
        SanityTierEvaluationStatus status,
        string reason,
        IReadOnlyCollection<SanityStateEvent>? events = null
    )
    {
        Status = status;
        Reason = reason;
        Events = new ReadOnlyCollection<SanityStateEvent>(
            events is null
                ? Array.Empty<SanityStateEvent>()
                : new List<SanityStateEvent>(events)
        );
    }

    internal SanityTierEvaluationStatus Status { get; }

    internal string Reason { get; }

    internal IReadOnlyList<SanityStateEvent> Events { get; }
}

internal sealed class SanityTierOwnerStateSnapshot
{
    internal SanityTierOwnerStateSnapshot(
        string playerKey,
        double current,
        double maximum,
        long revision,
        bool isAvailable,
        string reason,
        IReadOnlyCollection<string> activeTierIds
    )
    {
        PlayerKey = playerKey;
        Current = current;
        Maximum = maximum;
        Revision = revision;
        IsAvailable = isAvailable;
        Reason = reason;
        ActiveTierIds = new ReadOnlyCollection<string>(
            new List<string>(activeTierIds)
        );
    }

    internal string PlayerKey { get; }

    internal double Current { get; }

    internal double Maximum { get; }

    internal long Revision { get; }

    internal bool IsAvailable { get; }

    internal string Reason { get; }

    internal IReadOnlyList<string> ActiveTierIds { get; }
}

/// <summary>
/// 按 owner 保存的纯逻辑阈值状态机。它只消费权威 Sanity snapshot，不生成实体、
/// 不播放表现，也不执行清理；下游只消费这里发布的稳定状态事件。
/// </summary>
internal sealed class SanityTierStateMachine
{
    private sealed class OwnerState
    {
        internal OwnerState(string playerKey)
        {
            PlayerKey = playerKey;
            ActiveTiers = new bool[SanityTierCatalog.Rules.Count];
        }

        internal string PlayerKey { get; }

        internal double Current { get; set; }

        internal double Maximum { get; set; }

        internal long Revision { get; set; }

        internal bool IsAvailable { get; set; }

        internal string Reason { get; set; } = string.Empty;

        internal bool[] ActiveTiers { get; }

        internal double Ratio => Current / Maximum;
    }

    private readonly Dictionary<string, OwnerState> owners =
        new(StringComparer.Ordinal);
    private bool? systemEnabled;

    internal bool? IsSystemEnabled => systemEnabled;

    /// <summary>
    /// 高频只读调用用这个无分配检查短路；真正变化仍必须经过 <see cref="Observe"/>。
    /// </summary>
    internal bool IsCurrentObservation(SanityPlayerSnapshot snapshot)
    {
        return snapshot is not null
            && owners.TryGetValue(snapshot.PlayerKey, out var owner)
            && owner.IsAvailable
            && owner.Revision == snapshot.Revision
            && SameDouble(owner.Current, snapshot.Current)
            && SameDouble(owner.Maximum, snapshot.Maximum);
    }

    internal SanityTierEvaluationResult Observe(
        SanityPlayerSnapshot snapshot,
        bool isSystemEnabled
    )
    {
        var events = new List<SanityStateEvent>();
        ApplySystemState(isSystemEnabled, events);

        if (snapshot is null)
        {
            return Result(
                SanityTierEvaluationStatus.Unavailable,
                "tier.snapshot-missing",
                events
            );
        }
        if (!SanityPlayerKey.IsCanonical(snapshot.PlayerKey))
        {
            return Result(
                SanityTierEvaluationStatus.Unavailable,
                "tier.player-key-invalid",
                events
            );
        }

        owners.TryGetValue(snapshot.PlayerKey, out var existing);
        if (snapshot.Revision < 0)
        {
            return SetUnavailable(
                snapshot,
                existing,
                "tier.snapshot-revision-invalid",
                events
            );
        }
        if (existing is not null && snapshot.Revision < existing.Revision)
        {
            return Result(
                SanityTierEvaluationStatus.IgnoredStale,
                "tier.snapshot-stale",
                events
            );
        }
        if (existing is not null && snapshot.Revision == existing.Revision)
        {
            if (SameDouble(existing.Current, snapshot.Current)
                && SameDouble(existing.Maximum, snapshot.Maximum))
            {
                var status = existing.IsAvailable
                    ? SanityTierEvaluationStatus.IgnoredDuplicate
                    : SanityTierEvaluationStatus.Unavailable;
                return Result(status, existing.IsAvailable
                    ? "tier.snapshot-duplicate"
                    : existing.Reason, events);
            }

            return SetUnavailable(
                snapshot,
                existing,
                "tier.snapshot-revision-conflict",
                events
            );
        }

        var invalidReason = ValidateSnapshot(snapshot);
        if (invalidReason is not null)
            return SetUnavailable(snapshot, existing, invalidReason, events);

        var owner = existing ?? new OwnerState(snapshot.PlayerKey);
        owner.Current = snapshot.Current;
        owner.Maximum = snapshot.Maximum;
        owner.Revision = snapshot.Revision;
        owner.IsAvailable = true;
        owner.Reason = "tier.snapshot-available";
        owners[snapshot.PlayerKey] = owner;

        if (systemEnabled != true)
        {
            ClearActiveTiers(owner, events);
            return Result(
                SanityTierEvaluationStatus.SystemDisabled,
                "tier.system-disabled",
                events
            );
        }

        EvaluateOwner(owner, events);
        return Result(
            events.Count == 0
                ? SanityTierEvaluationStatus.NoChange
                : SanityTierEvaluationStatus.Applied,
            events.Count == 0
                ? "tier.snapshot-applied-without-transition"
                : "tier.snapshot-applied-with-transitions",
            events
        );
    }

    internal SanityTierEvaluationResult SetSystemEnabled(bool enabled)
    {
        var events = new List<SanityStateEvent>();
        var changed = ApplySystemState(enabled, events);
        return Result(
            changed
                ? SanityTierEvaluationStatus.Applied
                : SanityTierEvaluationStatus.IgnoredDuplicate,
            changed
                ? (enabled ? "tier.system-enabled" : "tier.system-disabled")
                : "tier.system-state-unchanged",
            events
        );
    }

    internal SanityTierEvaluationResult InvalidateOwner(string playerKey)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            return Result(
                SanityTierEvaluationStatus.Unavailable,
                "tier.player-key-invalid"
            );
        }
        if (!owners.Remove(playerKey, out var owner))
        {
            return Result(
                SanityTierEvaluationStatus.NoChange,
                "tier.owner-was-not-tracked"
            );
        }

        var events = new List<SanityStateEvent>();
        ClearActiveTiers(owner, events);
        events.Add(
            LifecycleEvent(
                SanityStateEventIds.OwnerInvalidated,
                SanityStateEventKind.OwnerInvalidated,
                owner.PlayerKey,
                owner.Revision
            )
        );
        return Result(
            SanityTierEvaluationStatus.Applied,
            "tier.owner-invalidated",
            events
        );
    }

    internal SanityTierEvaluationResult CleanupWorld()
    {
        if (owners.Count == 0 && systemEnabled is null)
        {
            return Result(
                SanityTierEvaluationStatus.NoChange,
                "tier.world-already-clean"
            );
        }

        var events = new List<SanityStateEvent>();
        foreach (var owner in OrderedOwners())
            ClearActiveTiers(owner, events);
        events.Add(
            LifecycleEvent(
                SanityStateEventIds.WorldCleanup,
                SanityStateEventKind.WorldCleanup,
                string.Empty,
                -1
            )
        );
        owners.Clear();
        systemEnabled = null;
        return Result(
            SanityTierEvaluationStatus.Applied,
            "tier.world-cleaned",
            events
        );
    }

    internal bool TryGetOwnerState(
        string playerKey,
        out SanityTierOwnerStateSnapshot? snapshot
    )
    {
        snapshot = null;
        if (!owners.TryGetValue(playerKey, out var owner))
            return false;

        var activeTierIds = new List<string>();
        for (var index = 0; index < SanityTierCatalog.Rules.Count; index++)
        {
            if (owner.ActiveTiers[index])
                activeTierIds.Add(SanityTierCatalog.Rules[index].Id);
        }

        snapshot = new SanityTierOwnerStateSnapshot(
            owner.PlayerKey,
            owner.Current,
            owner.Maximum,
            owner.Revision,
            owner.IsAvailable,
            owner.Reason,
            activeTierIds
        );
        return true;
    }

    private bool ApplySystemState(
        bool enabled,
        List<SanityStateEvent> events
    )
    {
        if (systemEnabled == enabled)
            return false;

        if (!enabled)
        {
            foreach (var owner in OrderedOwners())
                ClearActiveTiers(owner, events);
            systemEnabled = false;
            events.Add(
                LifecycleEvent(
                    SanityStateEventIds.SystemDisabled,
                    SanityStateEventKind.SystemDisabled,
                    string.Empty,
                    -1
                )
            );
            return true;
        }

        systemEnabled = true;
        events.Add(
            LifecycleEvent(
                SanityStateEventIds.SystemEnabled,
                SanityStateEventKind.SystemEnabled,
                string.Empty,
                -1
            )
        );
        foreach (var owner in OrderedOwners())
        {
            if (owner.IsAvailable)
                EvaluateOwner(owner, events);
        }
        return true;
    }

    private static string? ValidateSnapshot(SanityPlayerSnapshot snapshot)
    {
        if (!double.IsFinite(snapshot.Current))
            return "tier.current-non-finite";
        if (!double.IsFinite(snapshot.Maximum))
            return "tier.maximum-non-finite";
        if (snapshot.Maximum <= 0)
            return "tier.maximum-not-positive";
        if (snapshot.Current < 0 || snapshot.Current > snapshot.Maximum)
            return "tier.current-out-of-range";
        return null;
    }

    private SanityTierEvaluationResult SetUnavailable(
        SanityPlayerSnapshot snapshot,
        OwnerState? existing,
        string reason,
        List<SanityStateEvent> events
    )
    {
        var owner = existing ?? new OwnerState(snapshot.PlayerKey);
        var wasAvailable = existing?.IsAvailable == true;
        ClearActiveTiers(owner, events);
        owner.Current = snapshot.Current;
        owner.Maximum = snapshot.Maximum;
        owner.Revision = snapshot.Revision;
        owner.IsAvailable = false;
        owner.Reason = reason;
        owners[snapshot.PlayerKey] = owner;

        if (existing is null || wasAvailable)
        {
            events.Add(
                LifecycleEvent(
                    SanityStateEventIds.OwnerInvalidated,
                    SanityStateEventKind.OwnerInvalidated,
                    owner.PlayerKey,
                    owner.Revision
                )
            );
        }

        return Result(SanityTierEvaluationStatus.Unavailable, reason, events);
    }

    private static void EvaluateOwner(
        OwnerState owner,
        List<SanityStateEvent> events
    )
    {
        var ratio = owner.Ratio;
        var next = new bool[SanityTierCatalog.Rules.Count];
        for (var index = 0; index < SanityTierCatalog.Rules.Count; index++)
        {
            var rule = SanityTierCatalog.Rules[index];
            next[index] = owner.ActiveTiers[index]
                ? ratio <= rule.ExitRatio
                : ratio <= rule.EnterRatio;
        }

        // 嵌套 tier 上升退出时按低阈值到高阈值发布，避免消费者先收到外层退出。
        for (var index = SanityTierCatalog.Rules.Count - 1; index >= 0; index--)
        {
            if (!owner.ActiveTiers[index] || next[index])
                continue;
            var rule = SanityTierCatalog.Rules[index];
            events.Add(
                TierEvent(
                    rule.ExitedEventId,
                    SanityStateEventKind.TierExited,
                    owner,
                    rule.Id,
                    ratio
                )
            );
        }

        // 下降进入时按高阈值到低阈值发布，初次低值加载也得到同一确定序列。
        for (var index = 0; index < SanityTierCatalog.Rules.Count; index++)
        {
            if (owner.ActiveTiers[index] || !next[index])
                continue;
            var rule = SanityTierCatalog.Rules[index];
            events.Add(
                TierEvent(
                    rule.EnteredEventId,
                    SanityStateEventKind.TierEntered,
                    owner,
                    rule.Id,
                    ratio
                )
            );
        }

        Array.Copy(next, owner.ActiveTiers, next.Length);
    }

    private static void ClearActiveTiers(
        OwnerState owner,
        List<SanityStateEvent> events
    )
    {
        var ratio = owner.IsAvailable && owner.Maximum > 0
            ? owner.Ratio
            : (double?)null;
        for (var index = SanityTierCatalog.Rules.Count - 1; index >= 0; index--)
        {
            if (!owner.ActiveTiers[index])
                continue;
            var rule = SanityTierCatalog.Rules[index];
            events.Add(
                TierEvent(
                    rule.ExitedEventId,
                    SanityStateEventKind.TierExited,
                    owner,
                    rule.Id,
                    ratio
                )
            );
            owner.ActiveTiers[index] = false;
        }
    }

    private List<OwnerState> OrderedOwners()
    {
        var ordered = new List<OwnerState>(owners.Values);
        ordered.Sort(
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.PlayerKey, right.PlayerKey)
        );
        return ordered;
    }

    private static SanityStateEvent TierEvent(
        string eventId,
        SanityStateEventKind kind,
        OwnerState owner,
        string tierId,
        double? ratio
    )
    {
        return new SanityStateEvent(
            eventId,
            kind,
            owner.PlayerKey,
            tierId,
            owner.Revision,
            ratio
        );
    }

    private static SanityStateEvent LifecycleEvent(
        string eventId,
        SanityStateEventKind kind,
        string playerKey,
        long revision
    )
    {
        return new SanityStateEvent(
            eventId,
            kind,
            playerKey,
            string.Empty,
            revision,
            null
        );
    }

    private static bool SameDouble(double left, double right)
    {
        return BitConverter.DoubleToInt64Bits(left)
            == BitConverter.DoubleToInt64Bits(right);
    }

    private static SanityTierEvaluationResult Result(
        SanityTierEvaluationStatus status,
        string reason,
        IReadOnlyCollection<SanityStateEvent>? events = null
    )
    {
        return new SanityTierEvaluationResult(status, reason, events);
    }
}
