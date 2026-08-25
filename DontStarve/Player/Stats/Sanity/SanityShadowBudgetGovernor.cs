#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

internal enum SanityShadowPoolTier
{
    Inactive,
    Harmless50,
    Hostile15,
    Hostile10,
}

internal enum SanityShadowBudgetEvaluationStatus
{
    PermitGranted,
    Waiting,
    PausedAtCap,
    SpeciesIneligible,
    Inactive,
    SystemDisabled,
    Unavailable,
}

internal readonly record struct SanityShadowSpawnPermit(
    string PlayerKey,
    SanityShadowPoolTier PoolTier,
    string IntensityId,
    int Occupancy,
    int Cap,
    long IssuedAtMinute,
    long NextDueMinute,
    string Reason
)
{
    internal SanityShadowEligibleSpecies EligibleSpecies =>
        SanityShadowPoolEligibilityPolicy.GetEligibleSpecies(PoolTier);
}

internal sealed class SanityShadowBudgetEvaluationResult
{
    internal SanityShadowBudgetEvaluationResult(
        SanityShadowBudgetEvaluationStatus status,
        string reason,
        string playerKey,
        SanityShadowPoolTier poolTier,
        string intensityId,
        int occupancy,
        int cap,
        long intervalMinutes,
        long? nextDueMinute,
        SanityShadowSpawnPermit? permit = null
    )
    {
        Status = status;
        Reason = reason;
        PlayerKey = playerKey;
        PoolTier = poolTier;
        IntensityId = intensityId;
        Occupancy = occupancy;
        Cap = cap;
        IntervalMinutes = intervalMinutes;
        NextDueMinute = nextDueMinute;
        Permit = permit;
    }

    internal SanityShadowBudgetEvaluationStatus Status { get; }

    internal string Reason { get; }

    internal string PlayerKey { get; }

    internal SanityShadowPoolTier PoolTier { get; }

    internal string IntensityId { get; }

    internal int Occupancy { get; }

    internal int Cap { get; }

    internal long IntervalMinutes { get; }

    internal long? NextDueMinute { get; }

    internal SanityShadowSpawnPermit? Permit { get; }

    internal SanityShadowEligibleSpecies EligibleSpecies =>
        SanityShadowPoolEligibilityPolicy.GetEligibleSpecies(PoolTier);
}

internal sealed class SanityShadowBudgetOwnerSnapshot
{
    internal SanityShadowBudgetOwnerSnapshot(
        string playerKey,
        SanityShadowPoolTier poolTier,
        int occupancy,
        string intensityId,
        int cap,
        long intervalMinutes,
        long? nextDueMinute,
        bool isPausedAtCap
    )
    {
        PlayerKey = playerKey;
        PoolTier = poolTier;
        Occupancy = occupancy;
        IntensityId = intensityId;
        Cap = cap;
        IntervalMinutes = intervalMinutes;
        NextDueMinute = nextDueMinute;
        IsPausedAtCap = isPausedAtCap;
    }

    internal string PlayerKey { get; }

    internal SanityShadowPoolTier PoolTier { get; }

    internal int Occupancy { get; }

    internal string IntensityId { get; }

    internal int Cap { get; }

    internal long IntervalMinutes { get; }

    internal long? NextDueMinute { get; }

    internal bool IsPausedAtCap { get; }

    internal SanityShadowEligibleSpecies EligibleSpecies =>
        SanityShadowPoolEligibilityPolicy.GetEligibleSpecies(PoolTier);
}

/// <summary>
/// 两种影怪共享的 owner 预算治理器。它只接收 tier 状态、缓存配置、内部游戏分钟
/// 与调用方提交的 occupancy；没有 location、实体集合、仇恨、AI 或随机数入口。
/// </summary>
internal sealed class SanityShadowBudgetGovernor
{
    private sealed class OwnerState
    {
        internal OwnerState(string playerKey)
        {
            PlayerKey = playerKey;
        }

        internal string PlayerKey { get; }

        internal bool ShadowCreaturesActive { get; set; }

        internal bool DangerActive { get; set; }

        internal bool TerrorbeakActive { get; set; }

        internal int Occupancy { get; set; }

        internal bool HasPolicy { get; set; }

        internal SanityShadowPoolTier LastPoolTier { get; set; }

        internal string LastIntensityId { get; set; } = string.Empty;

        internal int LastCap { get; set; }

        internal long LastIntervalMinutes { get; set; }

        internal long? LastGameMinute { get; set; }

        internal long? NextDueMinute { get; set; }

        internal double RealElapsedMilliseconds { get; set; }

        internal bool RealTimerStarted { get; set; }

        internal long RealIntervalMilliseconds { get; set; }

        internal bool WasAtCap { get; set; }

        internal SanityShadowPoolTier CurrentPoolTier =>
            !ShadowCreaturesActive
                ? SanityShadowPoolTier.Inactive
                : TerrorbeakActive
                    ? SanityShadowPoolTier.Hostile10
                    : DangerActive
                        ? SanityShadowPoolTier.Hostile15
                        : SanityShadowPoolTier.Harmless50;
    }

    private readonly Dictionary<string, OwnerState> owners =
        new(StringComparer.Ordinal);
    private readonly ISanityMonsterIntensityProvider intensityProvider;
    private bool? systemEnabled;

    internal SanityShadowBudgetGovernor(
        ISanityMonsterIntensityProvider intensityProvider
    )
    {
        this.intensityProvider = intensityProvider
            ?? throw new ArgumentNullException(nameof(intensityProvider));
    }

    internal bool? IsSystemEnabled => systemEnabled;

    internal bool ApplyStateEvent(SanityStateEvent stateEvent, out string reason)
    {
        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemEnabled:
                if (systemEnabled == true)
                {
                    reason = "budget.system-state-unchanged";
                    return true;
                }

                systemEnabled = true;
                foreach (var owner in owners.Values)
                    ResetPolicy(owner);
                reason = "budget.system-enabled";
                return true;

            case SanityStateEventKind.SystemDisabled:
                if (systemEnabled == false)
                {
                    reason = "budget.system-state-unchanged";
                    return true;
                }

                systemEnabled = false;
                foreach (var owner in owners.Values)
                {
                    owner.ShadowCreaturesActive = false;
                    owner.DangerActive = false;
                    owner.TerrorbeakActive = false;
                    ResetPolicy(owner);
                }
                reason = "budget.system-disabled";
                return true;

            case SanityStateEventKind.WorldCleanup:
                owners.Clear();
                systemEnabled = null;
                reason = "budget.world-cleaned";
                return true;

            case SanityStateEventKind.OwnerInvalidated:
                if (!SanityPlayerKey.IsCanonical(stateEvent.PlayerKey))
                {
                    reason = "budget.player-key-invalid";
                    return false;
                }

                owners.Remove(stateEvent.PlayerKey);
                reason = "budget.owner-invalidated";
                return true;

            case SanityStateEventKind.TierEntered:
            case SanityStateEventKind.TierExited:
                return ApplyTierEvent(stateEvent, out reason);

            default:
                reason = "budget.state-event-unsupported";
                return false;
        }
    }

    internal SanityShadowBudgetEvaluationResult Evaluate(
        string playerKey,
        long gameMinute,
        int occupancy
    )
    {
        return EvaluateCore(playerKey, gameMinute, occupancy, requestedSpecies: null);
    }

    internal SanityShadowBudgetEvaluationResult EvaluateHostileSpawn(
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpecies requestedSpecies
    )
    {
        return EvaluateCore(playerKey, gameMinute, occupancy, requestedSpecies);
    }

    internal SanityShadowBudgetEvaluationResult EvaluateRealTime(
        string playerKey,
        long gameMinute,
        int occupancy,
        int elapsedMilliseconds,
        SanityShadowSpecies? requestedSpecies = null
    )
    {
        if (elapsedMilliseconds < 0)
        {
            return Unavailable(
                playerKey,
                "budget.elapsed-milliseconds-invalid",
                occupancy
            );
        }

        if (!SanityPlayerKey.IsCanonical(playerKey))
            return Unavailable(playerKey, "budget.player-key-invalid", occupancy);
        if (gameMinute < 0)
            return Unavailable(playerKey, "budget.game-minute-invalid", occupancy);
        if (occupancy < 0)
            return Unavailable(playerKey, "budget.occupancy-invalid", occupancy);
        if (!owners.TryGetValue(playerKey, out var owner))
        {
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.owner-untracked",
                playerKey,
                SanityShadowPoolTier.Inactive,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        owner.Occupancy = occupancy;
        if (systemEnabled is null)
        {
            ResetPolicy(owner);
            return Unavailable(playerKey, "budget.system-state-unavailable", occupancy);
        }
        if (systemEnabled == false)
        {
            ResetPolicy(owner);
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.SystemDisabled,
                "budget.system-disabled",
                playerKey,
                owner.CurrentPoolTier,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        var poolTier = owner.CurrentPoolTier;
        if (poolTier == SanityShadowPoolTier.Inactive)
        {
            ResetPolicy(owner);
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.tier-inactive",
                playerKey,
                poolTier,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        SanityMonsterIntensityResolution intensity;
        try
        {
            intensity = intensityProvider.Resolve();
        }
        catch (Exception)
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                "budget.intensity-provider-failed",
                occupancy,
                poolTier
            );
        }
        if (!intensity.HasValue)
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                string.Concat("budget.intensity-unavailable:", intensity.Reason),
                occupancy,
                poolTier
            );
        }
        if (
            !SanityShadowBudgetPolicyCatalog.TryGet(intensity.Value, out var policy)
            || policy is null
        )
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                "budget.intensity-unknown",
                occupancy,
                poolTier,
                intensity.Value
            );
        }

        var cap = policy.GetCap(poolTier);
        if (
            requestedSpecies.HasValue
            && !SanityShadowPoolEligibilityPolicy.TryAuthorize(
                poolTier,
                requestedSpecies.Value,
                out var eligibilityReason
            )
        )
        {
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.SpeciesIneligible,
                eligibilityReason,
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                owner.NextDueMinute
            );
        }

        var hadPolicy = owner.HasPolicy;
        var previousPolicyWasFull = hadPolicy
            && owner.LastCap > 0
            && occupancy >= owner.LastCap;
        var policyChanged = !hadPolicy
            || owner.LastPoolTier != poolTier
            || !string.Equals(owner.LastIntensityId, policy.IntensityId, StringComparison.Ordinal)
            || owner.LastCap != cap
            || owner.RealIntervalMilliseconds != policy.RealIntervalMilliseconds;

        if (cap == 0)
        {
            SetPolicy(owner, poolTier, policy, cap);
            ResetRealTimer(owner);
            owner.NextDueMinute = null;
            owner.WasAtCap = false;
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.intensity-none",
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                null
            );
        }

        if (policyChanged)
        {
            var shouldFillNewVacancy = owner.WasAtCap || previousPolicyWasFull;
            SetPolicy(owner, poolTier, policy, cap);
            ResetRealTimer(owner);
            owner.RealIntervalMilliseconds = policy.RealIntervalMilliseconds;
            if (occupancy >= cap)
                return PauseAtCapRealTime(owner, policy, poolTier);
            if (shouldFillNewVacancy)
                return GrantRealTimePermit(owner, policy, poolTier, gameMinute, "budget.permit.vacancy");
            owner.RealTimerStarted = true;
            owner.RealElapsedMilliseconds = elapsedMilliseconds;
            if (owner.RealElapsedMilliseconds >= policy.RealIntervalMilliseconds)
            {
                return GrantRealTimePermit(
                    owner,
                    policy,
                    poolTier,
                    gameMinute,
                    "budget.permit.real-time-interval-elapsed"
                );
            }
            return WaitingRealTime(owner, policy, poolTier, "budget.timer-started");
        }

        if (occupancy >= cap)
            return PauseAtCapRealTime(owner, policy, poolTier);
        if (owner.WasAtCap)
            return GrantRealTimePermit(owner, policy, poolTier, gameMinute, "budget.permit.vacancy");
        owner.RealTimerStarted = true;
        owner.RealElapsedMilliseconds = Math.Min(
            double.MaxValue - owner.RealElapsedMilliseconds,
            owner.RealElapsedMilliseconds + elapsedMilliseconds
        );
        if (owner.RealElapsedMilliseconds < policy.RealIntervalMilliseconds)
            return WaitingRealTime(owner, policy, poolTier, "budget.waiting");

        return GrantRealTimePermit(
            owner,
            policy,
            poolTier,
            gameMinute,
            "budget.permit.real-time-interval-elapsed"
        );
    }

    private SanityShadowBudgetEvaluationResult EvaluateCore(
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpecies? requestedSpecies
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            return Unavailable(
                playerKey,
                "budget.player-key-invalid",
                occupancy
            );
        }
        if (gameMinute < 0)
        {
            return Unavailable(
                playerKey,
                "budget.game-minute-invalid",
                occupancy
            );
        }
        if (occupancy < 0)
        {
            return Unavailable(
                playerKey,
                "budget.occupancy-invalid",
                occupancy
            );
        }
        if (!owners.TryGetValue(playerKey, out var owner))
        {
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.owner-untracked",
                playerKey,
                SanityShadowPoolTier.Inactive,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        owner.Occupancy = occupancy;
        if (systemEnabled is null)
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                "budget.system-state-unavailable",
                occupancy
            );
        }
        if (systemEnabled == false)
        {
            ResetPolicy(owner);
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.SystemDisabled,
                "budget.system-disabled",
                playerKey,
                owner.CurrentPoolTier,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        var poolTier = owner.CurrentPoolTier;
        if (poolTier == SanityShadowPoolTier.Inactive)
        {
            ResetPolicy(owner);
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.tier-inactive",
                playerKey,
                poolTier,
                string.Empty,
                occupancy,
                0,
                0,
                null
            );
        }

        SanityMonsterIntensityResolution intensity;
        try
        {
            intensity = intensityProvider.Resolve();
        }
        catch (Exception)
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                "budget.intensity-provider-failed",
                occupancy,
                poolTier
            );
        }

        if (!intensity.HasValue)
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                string.Concat("budget.intensity-unavailable:", intensity.Reason),
                occupancy,
                poolTier
            );
        }
        if (
            !SanityShadowBudgetPolicyCatalog.TryGet(
                intensity.Value,
                out var policy
            )
            || policy is null
        )
        {
            ResetPolicy(owner);
            return Unavailable(
                playerKey,
                "budget.intensity-unknown",
                occupancy,
                poolTier,
                intensity.Value
            );
        }

        var cap = policy.GetCap(poolTier);
        if (
            requestedSpecies.HasValue
            && !SanityShadowPoolEligibilityPolicy.TryAuthorize(
                poolTier,
                requestedSpecies.Value,
                out var eligibilityReason
            )
        )
        {
            // Eligibility is checked before SetPolicy/GrantPermit so a rejected species cannot
            // consume a conversion epoch, start a timer, or restart the shared owner budget.
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.SpeciesIneligible,
                eligibilityReason,
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                owner.NextDueMinute
            );
        }
        var hadPolicy = owner.HasPolicy;
        var previousPolicyWasFull = hadPolicy
            && owner.LastCap > 0
            && occupancy >= owner.LastCap;
        var policyChanged = !hadPolicy
            || owner.LastPoolTier != poolTier
            || !string.Equals(
                owner.LastIntensityId,
                policy.IntensityId,
                StringComparison.Ordinal
            )
            || owner.LastCap != cap
            || owner.LastIntervalMinutes != policy.IntervalMinutes;

        if (
            owner.LastGameMinute.HasValue
            && gameMinute < owner.LastGameMinute.Value
        )
        {
            // DIAG-20260812: 时间回溯（CJB 回拨等）后立即恢复刷新——此前重置计时器
            // 后返回 Unavailable，NextDueMinute 被置为“回溯点+间隔”，回溯后玩家要
            // 再等一个完整间隔（如 60 游戏分钟）才看到影怪刷新（用户实测“阈值内
            // 不再刷出来”）。改为回溯时直接授予许可，恢复刷新节奏；超上限仍由
            // 调用方 permit 校验（Cap ≤ occupancy）拦截，不会无限制刷怪。
            SetPolicy(owner, poolTier, policy, cap);
            owner.LastGameMinute = gameMinute;
            owner.WasAtCap = false;
            return GrantPermit(
                owner,
                policy,
                poolTier,
                gameMinute,
                "budget.permit.time-regressed-restarted"
            );
        }

        owner.LastGameMinute = gameMinute;
        if (cap == 0)
        {
            SetPolicy(owner, poolTier, policy, cap);
            owner.NextDueMinute = null;
            owner.WasAtCap = false;
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Inactive,
                "budget.intensity-none",
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                null
            );
        }

        if (policyChanged)
        {
            var shouldFillNewVacancy = owner.WasAtCap || previousPolicyWasFull;
            SetPolicy(owner, poolTier, policy, cap);
            if (occupancy >= cap)
                return PauseAtCap(owner, policy, poolTier);
            if (shouldFillNewVacancy)
            {
                return GrantPermit(
                    owner,
                    policy,
                    poolTier,
                    gameMinute,
                    "budget.permit.vacancy"
                );
            }
            if (!TrySchedule(owner, gameMinute, policy.IntervalMinutes))
            {
                return Unavailable(
                    playerKey,
                    "budget.timer-overflow",
                    occupancy,
                    poolTier,
                    policy.IntensityId,
                    cap,
                    policy.IntervalMinutes
                );
            }

            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Waiting,
                hadPolicy
                    ? "budget.policy-timer-started"
                    : "budget.timer-started",
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                owner.NextDueMinute
            );
        }

        if (occupancy >= cap)
            return PauseAtCap(owner, policy, poolTier);

        if (owner.WasAtCap)
        {
            return GrantPermit(
                owner,
                policy,
                poolTier,
                gameMinute,
                "budget.permit.vacancy"
            );
        }

        if (!owner.NextDueMinute.HasValue)
        {
            if (!TrySchedule(owner, gameMinute, policy.IntervalMinutes))
            {
                return Unavailable(
                    playerKey,
                    "budget.timer-overflow",
                    occupancy,
                    poolTier,
                    policy.IntensityId,
                    cap,
                    policy.IntervalMinutes
                );
            }

            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Waiting,
                "budget.timer-started",
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                owner.NextDueMinute
            );
        }

        if (gameMinute < owner.NextDueMinute.Value)
        {
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Waiting,
                "budget.waiting",
                playerKey,
                poolTier,
                policy.IntensityId,
                occupancy,
                cap,
                policy.IntervalMinutes,
                owner.NextDueMinute
            );
        }

        return GrantPermit(
            owner,
            policy,
            poolTier,
            gameMinute,
            "budget.permit.interval-elapsed"
        );
    }

    internal bool TryGetOwnerState(
        string playerKey,
        out SanityShadowBudgetOwnerSnapshot? snapshot
    )
    {
        snapshot = null;
        if (!owners.TryGetValue(playerKey, out var owner))
            return false;

        snapshot = new SanityShadowBudgetOwnerSnapshot(
            owner.PlayerKey,
            owner.CurrentPoolTier,
            owner.Occupancy,
            owner.LastIntensityId,
            owner.LastCap,
            owner.LastIntervalMinutes,
            owner.NextDueMinute,
            owner.WasAtCap
        );
        return true;
    }

    /// <summary>
    /// DIAG-20260810: 只读查询当前密度档的“统一上限池”容量（BaseCap+TerrorbeakCap，
    /// 四类影怪合计上限）。不推进预算/计时器，仅用于超限清理判定。
    /// </summary>
    internal bool TryGetTotalCap(string playerKey, out int totalCap)
    {
        totalCap = 0;
        if (!SanityPlayerKey.IsCanonical(playerKey) || systemEnabled != true)
            return false;
        if (!owners.TryGetValue(playerKey, out var owner))
            return false;
        if (owner.CurrentPoolTier == SanityShadowPoolTier.Inactive)
            return false;
        SanityMonsterIntensityResolution intensity;
        try
        {
            intensity = intensityProvider.Resolve();
        }
        catch (Exception)
        {
            return false;
        }
        if (
            !intensity.HasValue
            || !SanityShadowBudgetPolicyCatalog.TryGet(
                intensity.Value,
                out var policy
            )
            || policy is null
        )
        {
            return false;
        }
        totalCap = policy.BaseCap + policy.TerrorbeakCap;
        return true;
    }

    private bool ApplyTierEvent(
        SanityStateEvent stateEvent,
        out string reason
    )
    {
        if (!SanityPlayerKey.IsCanonical(stateEvent.PlayerKey))
        {
            reason = "budget.player-key-invalid";
            return false;
        }

        var relevant = stateEvent.TierId is SanityTierIds.ShadowCreatures
            or SanityTierIds.Danger
            or SanityTierIds.Terrorbeak;
        if (!relevant)
        {
            reason = "budget.tier-event-not-relevant";
            return true;
        }

        if (!owners.TryGetValue(stateEvent.PlayerKey, out var owner))
        {
            owner = new OwnerState(stateEvent.PlayerKey);
            owners.Add(stateEvent.PlayerKey, owner);
        }

        var active = stateEvent.Kind == SanityStateEventKind.TierEntered;
        var changed = stateEvent.TierId switch
        {
            SanityTierIds.ShadowCreatures => SetIfChanged(
                owner.ShadowCreaturesActive,
                active,
                value => owner.ShadowCreaturesActive = value
            ),
            SanityTierIds.Danger => SetIfChanged(
                owner.DangerActive,
                active,
                value => owner.DangerActive = value
            ),
            SanityTierIds.Terrorbeak => SetIfChanged(
                owner.TerrorbeakActive,
                active,
                value => owner.TerrorbeakActive = value
            ),
            _ => false,
        };

        if (owner.CurrentPoolTier == SanityShadowPoolTier.Inactive)
            ResetPolicy(owner);
        reason = changed
            ? "budget.tier-state-applied"
            : "budget.tier-state-unchanged";
        return true;
    }

    private static bool SetIfChanged(
        bool current,
        bool next,
        Action<bool> setter
    )
    {
        if (current == next)
            return false;
        setter(next);
        return true;
    }

    private static SanityShadowBudgetEvaluationResult PauseAtCap(
        OwnerState owner,
        SanityShadowBudgetPolicy policy,
        SanityShadowPoolTier poolTier
    )
    {
        owner.NextDueMinute = null;
        owner.WasAtCap = true;
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.PausedAtCap,
            "budget.paused-at-cap",
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            policy.IntervalMinutes,
            null
        );
    }

    private static SanityShadowBudgetEvaluationResult PauseAtCapRealTime(
        OwnerState owner,
        SanityShadowBudgetPolicy policy,
        SanityShadowPoolTier poolTier
    )
    {
        owner.RealElapsedMilliseconds = 0d;
        owner.RealTimerStarted = false;
        owner.NextDueMinute = null;
        owner.WasAtCap = true;
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.PausedAtCap,
            "budget.paused-at-cap",
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            policy.IntervalMinutes,
            null
        );
    }

    private static SanityShadowBudgetEvaluationResult WaitingRealTime(
        OwnerState owner,
        SanityShadowBudgetPolicy policy,
        SanityShadowPoolTier poolTier,
        string reason
    )
    {
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.Waiting,
            reason,
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            policy.IntervalMinutes,
            null
        );
    }

    private static SanityShadowBudgetEvaluationResult GrantRealTimePermit(
        OwnerState owner,
        SanityShadowBudgetPolicy policy,
        SanityShadowPoolTier poolTier,
        long gameMinute,
        string reason
    )
    {
        if (gameMinute == long.MaxValue)
        {
            return Unavailable(
                owner.PlayerKey,
                "budget.real-time-next-minute-overflow",
                owner.Occupancy,
                poolTier,
                policy.IntensityId,
                policy.GetCap(poolTier),
                policy.IntervalMinutes
            );
        }

        owner.RealElapsedMilliseconds = 0d;
        owner.RealTimerStarted = true;
        owner.WasAtCap = false;
        var nextDueMinute = gameMinute + 1;
        var permit = new SanityShadowSpawnPermit(
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            gameMinute,
            nextDueMinute,
            reason
        );
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            reason,
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            policy.IntervalMinutes,
            nextDueMinute,
            permit
        );
    }

    private static SanityShadowBudgetEvaluationResult GrantPermit(
        OwnerState owner,
        SanityShadowBudgetPolicy policy,
        SanityShadowPoolTier poolTier,
        long gameMinute,
        string reason
    )
    {
        owner.WasAtCap = false;
        if (!TrySchedule(owner, gameMinute, policy.IntervalMinutes))
        {
            return Unavailable(
                owner.PlayerKey,
                "budget.timer-overflow",
                owner.Occupancy,
                poolTier,
                policy.IntensityId,
                policy.GetCap(poolTier),
                policy.IntervalMinutes
            );
        }

        var permit = new SanityShadowSpawnPermit(
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            gameMinute,
            owner.NextDueMinute!.Value,
            reason
        );
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            reason,
            owner.PlayerKey,
            poolTier,
            policy.IntensityId,
            owner.Occupancy,
            policy.GetCap(poolTier),
            policy.IntervalMinutes,
            owner.NextDueMinute,
            permit
        );
    }

    private static void SetPolicy(
        OwnerState owner,
        SanityShadowPoolTier poolTier,
        SanityShadowBudgetPolicy policy,
        int cap
    )
    {
        owner.HasPolicy = true;
        owner.LastPoolTier = poolTier;
        owner.LastIntensityId = policy.IntensityId;
        owner.LastCap = cap;
        owner.LastIntervalMinutes = policy.IntervalMinutes;
    }

    private static bool TrySchedule(
        OwnerState owner,
        long gameMinute,
        long intervalMinutes
    )
    {
        if (gameMinute > long.MaxValue - intervalMinutes)
        {
            owner.NextDueMinute = null;
            return false;
        }

        owner.NextDueMinute = gameMinute + intervalMinutes;
        return true;
    }

    private static void ResetPolicy(OwnerState owner)
    {
        owner.HasPolicy = false;
        owner.LastPoolTier = SanityShadowPoolTier.Inactive;
        owner.LastIntensityId = string.Empty;
        owner.LastCap = 0;
        owner.LastIntervalMinutes = 0;
        owner.LastGameMinute = null;
        owner.NextDueMinute = null;
        owner.WasAtCap = false;
        ResetRealTimer(owner);
    }

    private static void ResetRealTimer(OwnerState owner)
    {
        owner.RealElapsedMilliseconds = 0d;
        owner.RealTimerStarted = false;
    }

    private static SanityShadowBudgetEvaluationResult Unavailable(
        string playerKey,
        string reason,
        int occupancy,
        SanityShadowPoolTier poolTier = SanityShadowPoolTier.Inactive,
        string intensityId = "",
        int cap = 0,
        long intervalMinutes = 0
    )
    {
        return new SanityShadowBudgetEvaluationResult(
            SanityShadowBudgetEvaluationStatus.Unavailable,
            reason,
            playerKey,
            poolTier,
            intensityId,
            occupancy,
            cap,
            intervalMinutes,
            null
        );
    }
}
