#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal enum HarmlessProjectionSchedulerStatus
{
    Idle,
    Waiting,
    AtCap,
    Attempted,
    Unavailable,
}

internal readonly record struct HarmlessProjectionSchedulerUpdateResult(
    HarmlessProjectionSchedulerStatus Status,
    int ExpiredCount,
    int AttemptCount,
    int SpawnedCount,
    string Reason
);

internal readonly record struct HarmlessProjectionScheduleSnapshot(
    string PlayerKey,
    string SpeciesId,
    bool TierActive,
    bool ImmediateAttemptPending,
    long? NextAttemptMinute,
    long? LastObservedMinute
);

/// <summary>
/// Pure per-owner/per-species scheduler. A real attempt (success or failure) advances only that
/// species' cadence; AtCap is a gate. TTL cleanup runs before same-minute due evaluation.
/// </summary>
internal sealed class HarmlessProjectionScheduler
{
    private sealed class ScheduleState
    {
        internal bool TierActive { get; set; }

        internal bool ImmediateAttemptPending { get; set; }

        internal bool TierExitCleanupPending { get; set; }

        internal long? NextAttemptMinute { get; set; }

        internal long? LastObservedMinute { get; set; }
    }

    private readonly HarmlessProjectionIndex index;
    private readonly Dictionary<string, HarmlessProjectionPolicy> policiesBySpecies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IHarmlessProjectionExitHook> exitHooksBySpecies =
        new(StringComparer.Ordinal);
    private readonly List<HarmlessProjectionPolicy> policies = new();
    private readonly Dictionary<ScheduleKey, ScheduleState> schedules = new();

    internal HarmlessProjectionScheduler(HarmlessProjectionIndex index)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
    }

    internal HarmlessProjectionIndex Index => index;

    internal IReadOnlyList<HarmlessProjectionPolicy> Policies => policies.AsReadOnly();

    internal bool RegisterPolicy(HarmlessProjectionPolicy policy, out string reason)
    {
        return RegisterPolicy(policy, null, out reason);
    }

    internal bool RegisterPolicy(
        HarmlessProjectionPolicy policy,
        IHarmlessProjectionExitHook? exitHook,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policiesBySpecies.ContainsKey(policy.SpeciesId))
        {
            reason = "scheduler.species-policy-duplicate";
            return false;
        }

        policiesBySpecies.Add(policy.SpeciesId, policy);
        policies.Add(policy);
        if (exitHook is not null)
            exitHooksBySpecies.Add(policy.SpeciesId, exitHook);
        reason = "scheduler.species-policy-registered";
        return true;
    }

    internal int SetTierActive(string playerKey, string tierId, bool active)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey) || string.IsNullOrWhiteSpace(tierId))
            return 0;

        var changed = 0;
        foreach (var policy in policies)
        {
            if (!string.Equals(policy.TierId, tierId, StringComparison.Ordinal))
                continue;

            var key = new ScheduleKey(playerKey, policy.SpeciesId);
            if (!schedules.TryGetValue(key, out var state))
            {
                if (!active)
                    continue;
                state = new ScheduleState();
                schedules.Add(key, state);
            }
            if (state.TierActive == active)
                continue;

            changed++;
            state.TierActive = active;
            if (active)
            {
                state.ImmediateAttemptPending = true;
                state.TierExitCleanupPending = false;
                state.NextAttemptMinute = null;
            }
            else
            {
                state.ImmediateAttemptPending = false;
                state.NextAttemptMinute = null;
                if (policy.ClearOnTierExit)
                    state.TierExitCleanupPending = true;
            }
        }
        return changed;
    }

    internal HarmlessProjectionSchedulerUpdateResult UpdateOwner(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        long gameMinute,
        IHarmlessProjectionSpawnFactory spawnFactory
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(spawnFactory);
        if (!ownerStandingWorldPixel.IsFinite)
        {
            return new HarmlessProjectionSchedulerUpdateResult(
                HarmlessProjectionSchedulerStatus.Unavailable,
                0,
                0,
                0,
                "scheduler.owner-standing-pixel-invalid"
            );
        }

        var expired = 0;
        var attempts = 0;
        var spawned = 0;
        var sawWaiting = false;
        var sawCap = false;
        var lastReason = "scheduler.no-active-tier";

        foreach (var policy in policies)
        {
            var key = new ScheduleKey(owner.PlayerKey, policy.SpeciesId);
            if (!schedules.TryGetValue(key, out var state))
                continue;

            // Tier exits are resolved on the next owner update. This lets a same-callback
            // Disabled/warp/day/title boundary apply its more specific cleanup reason first.
            if (state.TierExitCleanupPending)
            {
                if (
                    index.TryGetForOwnerSpecies(
                        owner.PlayerKey,
                        policy.SpeciesId,
                        out var expiringDuringTierExit
                    )
                    && expiringDuringTierExit is not null
                    && gameMinute >= expiringDuringTierExit.ExpiresAtMinute
                    && index.TryRemoveForOwnerSpecies(
                        owner.PlayerKey,
                        policy.SpeciesId,
                        HarmlessProjectionCleanupReason.HardTtlExpired,
                        out _
                    )
                )
                {
                    // A species transition may animate only inside its hard lifetime; it cannot
                    // turn the 20-minute TTL into a soft deadline.
                    expired++;
                    schedules.Remove(key);
                    lastReason = HarmlessProjectionCleanupReasonIds.HardTtlExpired;
                    continue;
                }
                if (
                    exitHooksBySpecies.TryGetValue(policy.SpeciesId, out var exitHook)
                    && index.TryGetForOwnerSpecies(
                        owner.PlayerKey,
                        policy.SpeciesId,
                        out var transitioning
                    )
                    && transitioning is not null
                    && exitHook.Resolve(
                        transitioning,
                        HarmlessProjectionCleanupReason.TierExited
                    ) == HarmlessProjectionExitResolution.RetainForSpeciesTransition
                )
                {
                    // The inactive schedule is no longer eligible to spawn; the mod-private
                    // instance remains only for the species' bounded disappear animation.
                    schedules.Remove(key);
                    lastReason = "cleanup.deferred-to-species-hook";
                    continue;
                }

                index.CleanupOwnerSpecies(
                    owner.PlayerKey,
                    policy.SpeciesId,
                    HarmlessProjectionCleanupReason.TierExited
                );
                schedules.Remove(key);
                lastReason = HarmlessProjectionCleanupReasonIds.TierExited;
                continue;
            }

            if (!RebaseForRollback(key, state, policy, gameMinute, out var rebaseReason))
            {
                lastReason = rebaseReason;
                continue;
            }
            state.LastObservedMinute = gameMinute;

            if (
                index.TryGetForOwnerSpecies(owner.PlayerKey, policy.SpeciesId, out var active)
                && active is not null
                && gameMinute >= active.ExpiresAtMinute
                && index.TryRemoveForOwnerSpecies(
                    owner.PlayerKey,
                    policy.SpeciesId,
                    HarmlessProjectionCleanupReason.HardTtlExpired,
                    out _
                )
            )
            {
                expired++;
                active = null;
            }

            if (!state.TierActive)
            {
                lastReason = "scheduler.tier-inactive";
                continue;
            }

            if (index.CountForOwnerSpecies(owner.PlayerKey, policy.SpeciesId) >= policy.ActiveCap)
            {
                sawCap = true;
                lastReason = "scheduler.at-cap";
                continue;
            }

            var isDue = state.ImmediateAttemptPending
                || (state.NextAttemptMinute.HasValue && gameMinute >= state.NextAttemptMinute.Value);
            if (!isDue)
            {
                sawWaiting = true;
                lastReason = "scheduler.cadence-waiting";
                continue;
            }

            attempts++;
            var request = new HarmlessProjectionSpawnRequest(
                owner,
                policy,
                ownerStandingWorldPixel,
                gameMinute
            );
            HarmlessProjectionSpawnResult result;
            try
            {
                result = spawnFactory.TrySpawn(request)
                    ?? HarmlessProjectionSpawnResult.Failed("spawn.factory-returned-null");
            }
            catch (Exception exception)
            {
                result = HarmlessProjectionSpawnResult.Failed(
                    string.Concat("spawn.factory-threw-", exception.GetType().Name)
                );
            }

            state.ImmediateAttemptPending = false;
            state.NextAttemptMinute = AddDeadline(
                gameMinute,
                policy.AttemptIntervalMinutes
            );
            lastReason = result.Reason;

            if (!result.Success || result.Instance is null)
                continue;
            if (!IsValidSpawnedInstance(request, result.Instance))
            {
                result.Instance.TryMarkCleaned(
                    HarmlessProjectionCleanupReason.OwnerInvalidated
                );
                lastReason = "spawn.instance-contract-invalid";
                continue;
            }
            if (!index.TryAdd(result.Instance, out var addReason))
            {
                result.Instance.TryMarkCleaned(HarmlessProjectionCleanupReason.WorldCleanup);
                lastReason = addReason;
                continue;
            }

            spawned++;
        }

        var status = attempts > 0
            ? HarmlessProjectionSchedulerStatus.Attempted
            : sawCap
                ? HarmlessProjectionSchedulerStatus.AtCap
                : sawWaiting
                    ? HarmlessProjectionSchedulerStatus.Waiting
                    : HarmlessProjectionSchedulerStatus.Idle;
        return new HarmlessProjectionSchedulerUpdateResult(
            status,
            expired,
            attempts,
            spawned,
            lastReason
        );
    }

    internal bool TryGetSchedule(
        string playerKey,
        string speciesId,
        out HarmlessProjectionScheduleSnapshot snapshot
    )
    {
        if (schedules.TryGetValue(new ScheduleKey(playerKey, speciesId), out var state))
        {
            snapshot = new HarmlessProjectionScheduleSnapshot(
                playerKey,
                speciesId,
                state.TierActive,
                state.ImmediateAttemptPending,
                state.NextAttemptMinute,
                state.LastObservedMinute
            );
            return true;
        }

        snapshot = default;
        return false;
    }

    internal bool RequestSoftExit(
        HarmlessProjectionOwnerContext owner,
        string speciesId,
        HarmlessProjectionCleanupReason reason,
        IHarmlessProjectionExitHook? hook,
        out string resultReason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!index.TryGet(owner, speciesId, out var instance) || instance is null)
        {
            resultReason = "cleanup.instance-not-active";
            return false;
        }

        if (
            hook is not null
            && hook.Resolve(instance, reason)
                == HarmlessProjectionExitResolution.RetainForSpeciesTransition
        )
        {
            resultReason = "cleanup.deferred-to-species-hook";
            return false;
        }

        var removed = index.TryRemove(owner, speciesId, reason, out _);
        resultReason = removed
            ? HarmlessProjectionCleanupReasonIds.GetId(reason)
            : "cleanup.instance-not-active";
        return removed;
    }

    internal int CleanupOwner(string playerKey, HarmlessProjectionCleanupReason reason)
    {
        var removed = index.CleanupOwner(playerKey, reason);
        var keys = new List<ScheduleKey>();
        foreach (var key in schedules.Keys)
        {
            if (string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                keys.Add(key);
        }
        foreach (var key in keys)
            schedules.Remove(key);
        return removed;
    }

    internal int CleanupAll(HarmlessProjectionCleanupReason reason)
    {
        var removed = index.CleanupAll(reason);
        schedules.Clear();
        return removed;
    }

    internal int InvalidateResources()
    {
        var removed = index.CleanupAll(
            HarmlessProjectionCleanupReason.ResourceInvalidated
        );
        foreach (var state in schedules.Values)
        {
            if (!state.TierActive)
                continue;
            state.ImmediateAttemptPending = true;
            state.NextAttemptMinute = null;
        }
        return removed;
    }

    internal void RetryActivePolicies(string playerKey)
    {
        foreach (var pair in schedules)
        {
            if (
                !string.Equals(
                    pair.Key.PlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
                || !pair.Value.TierActive
            )
            {
                continue;
            }

            pair.Value.ImmediateAttemptPending = true;
            pair.Value.NextAttemptMinute = null;
        }
    }

    private bool RebaseForRollback(
        ScheduleKey key,
        ScheduleState state,
        HarmlessProjectionPolicy policy,
        long gameMinute,
        out string reason
    )
    {
        if (!state.LastObservedMinute.HasValue || gameMinute >= state.LastObservedMinute.Value)
        {
            reason = "scheduler.time-monotonic";
            return true;
        }

        long delta;
        try
        {
            delta = checked(gameMinute - state.LastObservedMinute.Value);
        }
        catch (OverflowException)
        {
            index.CleanupOwnerSpecies(
                key.PlayerKey,
                policy.SpeciesId,
                HarmlessProjectionCleanupReason.OwnerInvalidated
            );
            schedules.Remove(key);
            reason = "scheduler.rollback-delta-overflow";
            return false;
        }
        if (state.NextAttemptMinute.HasValue)
        {
            try
            {
                state.NextAttemptMinute = checked(state.NextAttemptMinute.Value + delta);
            }
            catch (OverflowException)
            {
                index.CleanupOwnerSpecies(
                    key.PlayerKey,
                    policy.SpeciesId,
                    HarmlessProjectionCleanupReason.OwnerInvalidated
                );
                schedules.Remove(key);
                reason = "scheduler.rollback-deadline-overflow";
                return false;
            }
        }

        if (
            index.TryGetForOwnerSpecies(key.PlayerKey, policy.SpeciesId, out var active)
            && active is not null
            && !active.TryRebaseTime(delta)
        )
        {
            index.CleanupOwnerSpecies(
                key.PlayerKey,
                policy.SpeciesId,
                HarmlessProjectionCleanupReason.OwnerInvalidated
            );
            schedules.Remove(key);
            reason = "scheduler.rollback-ttl-overflow";
            return false;
        }

        reason = "scheduler.rollback-rebased";
        return true;
    }

    private static long AddDeadline(long gameMinute, int minutes)
    {
        try
        {
            return checked(gameMinute + minutes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static bool IsValidSpawnedInstance(
        HarmlessProjectionSpawnRequest request,
        HarmlessProjectionInstance instance
    )
    {
        return instance.Owner.Matches(request.Owner)
            && string.Equals(
                instance.SpeciesId,
                request.Policy.SpeciesId,
                StringComparison.Ordinal
            )
            && string.Equals(
                instance.VisualSlotId,
                request.Policy.VisualSlotId,
                StringComparison.Ordinal
            )
            && instance.SpawnedAtMinute == request.GameMinute
            && instance.ExpiresAtMinute
                == AddDeadline(request.GameMinute, request.Policy.HardTtlMinutes)
            && !instance.IsCleanedUp;
    }

    private readonly struct ScheduleKey : IEquatable<ScheduleKey>
    {
        internal ScheduleKey(string playerKey, string speciesId)
        {
            PlayerKey = playerKey;
            SpeciesId = speciesId;
        }

        internal string PlayerKey { get; }

        private string SpeciesId { get; }

        public bool Equals(ScheduleKey other)
        {
            return string.Equals(PlayerKey, other.PlayerKey, StringComparison.Ordinal)
                && string.Equals(SpeciesId, other.SpeciesId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is ScheduleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(PlayerKey),
                StringComparer.Ordinal.GetHashCode(SpeciesId)
            );
        }
    }
}
