#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// 给显示层暴露的只读总开关。显示层不得借此修改 Sanity 或生命周期状态。
/// </summary>
internal interface ISanitySystemState
{
    bool IsEnabled { get; }
}

internal enum SanityWorldBoundary
{
    Warp,
    DayStarted,
}

internal enum SanitySessionBoundary
{
    WorldCleanup,
    ReturnedToTitle,
}

internal readonly record struct SanityEventCoverageKey(
    string PlayerKey,
    int ScreenId,
    string SessionId
);

internal readonly record struct SanityEventOwnerCoverageChanged(
    SanityEventCoverageKey Key,
    bool Active
);

/// <summary>
/// 把配置开关、事件覆盖和 world 边界收敛到同一个协调器。数值真相仍由
/// <see cref="SanityChangeService"/> 持有；本类只编排冻结与派生状态清理/重建。
/// </summary>
internal sealed class SanitySystemLifecycleCoordinator
    : ISanitySystemState,
        IShadowCreatureProjectionBudgetAuthority,
        ISanityShadowRealTimeBudgetAuthority
{
    private readonly SanityChangeService service;
    private readonly HashSet<SanityEventCoverageKey> eventCoverage = new();
    private readonly Dictionary<string, int> eventCoverageCountByPlayer =
        new(StringComparer.Ordinal);
    private bool initialized;

    internal SanitySystemLifecycleCoordinator(SanityChangeService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public bool IsEnabled => service.IsSystemEnabled;

    internal bool IsEventCoverageActive => eventCoverage.Count > 0;

    internal bool IsEventCoverageActiveForPlayer(string playerKey)
    {
        return eventCoverageCountByPlayer.TryGetValue(playerKey, out var count)
            && count > 0;
    }

    internal string SessionId => service.SessionId;

    internal SanityAuthorityRole AuthorityRole => service.Role;

    internal event Action<SanityStateEvent>? StateEventPublished
    {
        add => service.TierStateEventPublished += value;
        remove => service.TierStateEventPublished -= value;
    }

    internal event Action<SanityTierOwnerStateSnapshot>? TierStateObserved
    {
        add => service.TierStateObserved += value;
        remove => service.TierStateObserved -= value;
    }

    internal event Action<bool>? EventCoverageChanged;

    internal event Action<SanityEventOwnerCoverageChanged>?
        EventOwnerCoverageChanged;

    internal event Action<SanityWorldBoundary>? WorldBoundaryStarting;

    internal event Action<SanitySessionBoundary>? SessionClearing;

    internal bool TryGetTierState(
        string playerKey,
        out SanityTierOwnerStateSnapshot? snapshot
    )
    {
        return service.TryGetTierState(playerKey, out snapshot);
    }

    internal bool TryGetBaseSnapshot(
        string playerKey,
        out SanityPlayerSnapshot snapshot
    )
    {
        return service.TryGetSnapshot(playerKey, out snapshot);
    }

    /// <summary>
    /// Runtime gameplay integrations still pass through the one task-family-01 change service.
    /// This narrow facade prevents darkness settlement from writing legacy static Sanity state.
    /// </summary>
    internal SanityChangeResult ApplySanityChange(
        string playerKey,
        double delta,
        SanityChangeSource source
    )
    {
        return service.Change(playerKey, delta, source);
    }

    // DIAG-20260804: ds_sanity lock/unlock 命令转发。锁定时除 Administration 外全部拒绝。
    internal bool IsDebugSanityLocked => service.IsDebugSanityLocked;

    internal void SetDebugSanityLocked(bool locked)
    {
        service.SetDebugSanityLocked(locked);
    }

    SanityShadowBudgetEvaluationResult
        IShadowCreatureProjectionBudgetAuthority.EvaluateShadowBudget(
            string playerKey,
            long gameMinute,
            int occupancy
        )
    {
        return service.EvaluateShadowBudget(playerKey, gameMinute, occupancy);
    }

    SanityShadowBudgetEvaluationResult
        ISanityShadowRealTimeBudgetAuthority.EvaluateShadowBudgetRealTime(
            string playerKey,
            long gameMinute,
            int occupancy,
            int elapsedMilliseconds,
            SanityShadowSpecies? requestedSpecies
        )
    {
        return requestedSpecies.HasValue
            ? service.EvaluateHostileShadowBudgetRealTime(
                playerKey,
                gameMinute,
                occupancy,
                elapsedMilliseconds,
                requestedSpecies.Value
            )
            : service.EvaluateShadowBudgetRealTime(
                playerKey,
                gameMinute,
                occupancy,
                elapsedMilliseconds
            );
    }

    internal SanityShadowBudgetEvaluationResult EvaluateHostileShadowBudget(
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpecies requestedSpecies
    )
    {
        return service.EvaluateHostileShadowBudget(
            playerKey,
            gameMinute,
            occupancy,
            requestedSpecies
        );
    }

    internal SanityShadowBudgetEvaluationResult EvaluateHostileShadowBudgetRealTime(
        string playerKey,
        long gameMinute,
        int occupancy,
        int elapsedMilliseconds,
        SanityShadowSpecies requestedSpecies
    )
    {
        return service.EvaluateHostileShadowBudgetRealTime(
            playerKey,
            gameMinute,
            occupancy,
            elapsedMilliseconds,
            requestedSpecies
        );
    }

    internal SanityShadowBudgetEvaluationResult EvaluateShadowBudgetRealTime(
        string playerKey,
        long gameMinute,
        int occupancy,
        int elapsedMilliseconds
    )
    {
        return service.EvaluateShadowBudgetRealTime(
            playerKey,
            gameMinute,
            occupancy,
            elapsedMilliseconds
        );
    }

    /// <summary>
    /// DIAG-20260810: 只读查询当前密度档的“统一上限池”容量（超限清理用，无副作用）。
    /// </summary>
    internal bool TryGetShadowBudgetTotalCap(
        string playerKey,
        out int totalCap
    )
    {
        return service.TryGetShadowBudgetTotalCap(playerKey, out totalCap);
    }

    /// <summary>
    /// 先让 tier/budget/cleanup 链进入可用状态；ModEntry 随后才应用实际配置，
    /// 因而启动即 Disabled 也不会跳过注册或初始化。
    /// </summary>
    internal void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        service.SetTierSystemEnabled(true);
    }

    internal SanityTierEvaluationResult ApplyConfiguredState(bool enabled)
    {
        EnsureInitialized();
        if (!enabled)
        {
            // Disable tier/audio consumers before releasing event freezes so an owner cannot
            // briefly reactivate low-Sanity effects during the same config transition.
            var disabled = service.SetTierSystemEnabled(false);
            ClearEventCoverage();
            return disabled;
        }
        return service.SetTierSystemEnabled(true);
    }

    internal void HandleWorldBoundary(SanityWorldBoundary boundary)
    {
        EnsureInitialized();
        // Projection owners need the specific warp/day reason before the tier state machine emits
        // its generic exited/world-cleanup/re-enter sequence.
        WorldBoundaryStarting?.Invoke(boundary);
        // DIAG-20260809: 主策划裁定——玩家切图（Warp）不清空 tier 状态机。
        // 此前 Warp 也走 CleanupAndRebuildDerivedState → tierState.CleanupWorld() →
        // 对激活 tier（含 Danger）发 TierExited + WorldCleanup 事件 → HostileShadowAuthority
        // 清空玩家名下真实影怪（违背设计稿“切图不跟随、冻结在原地”）。切图只发
        // WorldBoundaryStarting 给订阅者（投影/音频自行处理）；DayStarted 跨天仍清理重建
        // （真实影怪已在 DayEnding 清空，tier 新一天重新评估）。
        if (boundary == SanityWorldBoundary.DayStarted)
            service.CleanupAndRebuildDerivedState();
    }

    internal bool TrySetEventCoverage(
        SanityEventCoverageKey key,
        bool active,
        out string reason
    )
    {
        EnsureInitialized();
        if (
            !SanityPlayerKey.IsCanonical(key.PlayerKey)
            || key.ScreenId < 0
            || !SanityProtocol.IsValidSessionId(key.SessionId)
        )
        {
            reason = "event-coverage-key-is-invalid";
            return false;
        }
        if (
            !service.HasActiveSession
            || !string.Equals(key.SessionId, service.SessionId, StringComparison.Ordinal)
        )
        {
            reason = "event-coverage-session-does-not-match";
            return false;
        }

        if (active)
        {
            if (!eventCoverage.Add(key))
            {
                reason = "event-coverage-already-active";
                return false;
            }

            var wasGloballyActive = eventCoverage.Count > 1;
            var playerCount = eventCoverageCountByPlayer.TryGetValue(
                key.PlayerKey,
                out var currentCount
            )
                ? currentCount + 1
                : 1;
            eventCoverageCountByPlayer[key.PlayerKey] = playerCount;
            // Owner observers may synchronously query effective coverage. Publish the edge after
            // its reference count is visible, but before tier invalidation from the service.
            EventOwnerCoverageChanged?.Invoke(
                new SanityEventOwnerCoverageChanged(key, true)
            );
            if (playerCount == 1)
                service.SetEffectiveOverlayOwnerActive(key.PlayerKey, true);
            if (!wasGloballyActive)
                EventCoverageChanged?.Invoke(true);
            reason = "event-coverage-activated";
            return true;
        }

        if (!eventCoverage.Remove(key))
        {
            reason = "event-coverage-already-inactive";
            return false;
        }

        var ownerCoverageEnded = true;
        if (
            eventCoverageCountByPlayer.TryGetValue(key.PlayerKey, out var count)
            && count > 1
        )
        {
            eventCoverageCountByPlayer[key.PlayerKey] = count - 1;
            ownerCoverageEnded = false;
        }
        else
        {
            eventCoverageCountByPlayer.Remove(key.PlayerKey);
        }
        // As on entry, observers see the post-edge coverage fact before the tier service rebuilds.
        EventOwnerCoverageChanged?.Invoke(
            new SanityEventOwnerCoverageChanged(key, false)
        );
        if (ownerCoverageEnded)
            service.SetEffectiveOverlayOwnerActive(key.PlayerKey, false);
        if (eventCoverage.Count == 0)
            EventCoverageChanged?.Invoke(false);
        reason = "event-coverage-deactivated";
        return true;
    }

    internal void ClearSession(
        SanitySessionBoundary boundary = SanitySessionBoundary.WorldCleanup
    )
    {
        EnsureInitialized();
        // Consumers with borrowed world resources must clear before the tier state machine and
        // loader release erase the more specific returned-title/world-cleanup boundary.
        SessionClearing?.Invoke(boundary);
        eventCoverage.Clear();
        eventCoverageCountByPlayer.Clear();
        service.ClearSession();
    }

    private void ClearEventCoverage()
    {
        if (eventCoverage.Count == 0)
            return;

        foreach (var key in eventCoverage)
        {
            EventOwnerCoverageChanged?.Invoke(
                new SanityEventOwnerCoverageChanged(key, false)
            );
        }
        foreach (var playerKey in eventCoverageCountByPlayer.Keys)
            service.SetEffectiveOverlayOwnerActive(playerKey, false);
        eventCoverage.Clear();
        eventCoverageCountByPlayer.Clear();
        EventCoverageChanged?.Invoke(false);
    }

    private void EnsureInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException(
                "Sanity system lifecycle must be initialized before use."
            );
        }
    }
}
