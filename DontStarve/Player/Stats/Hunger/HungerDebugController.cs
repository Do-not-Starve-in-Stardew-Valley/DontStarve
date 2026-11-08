#nullable enable

using System;

namespace DontStarve.Player.Stats.Hunger;

internal interface IHungerDebugBackend
{
    HungerAuthorityRole Role { get; }

    bool IsDebugLocked { get; }

    bool TryGetSnapshot(string playerKey, out HungerPlayerSnapshot snapshot);

    bool TryEnsureHostPlayer(
        string playerKey,
        out HungerPlayerSnapshot snapshot,
        out string reason
    );

    HungerDebugMutationResult SetDebugValue(string playerKey, double value);

    void SetDebugLocked(bool locked);
}

internal readonly record struct HungerDebugMutationResult(
    bool Accepted,
    bool Changed,
    string Reason,
    HungerPlayerSnapshot? Snapshot
);

internal enum HungerDebugResultStatus
{
    Applied,
    NoChange,
    Disabled,
    HostOnly,
    Rejected,
}

internal sealed class HungerDebugResult
{
    private HungerDebugResult(
        HungerDebugResultStatus status,
        string reason,
        HungerPlayerSnapshot? snapshot
    )
    {
        Status = status;
        Reason = reason;
        Snapshot = snapshot;
    }

    internal HungerDebugResultStatus Status { get; }

    internal string Reason { get; }

    internal HungerPlayerSnapshot? Snapshot { get; }

    internal double Current => Snapshot?.Current ?? 0d;

    internal double Maximum => Snapshot?.Maximum ?? 0d;

    internal long Revision => Snapshot?.Revision ?? 0L;

    internal static HungerDebugResult Applied(
        HungerPlayerSnapshot snapshot,
        string reason = "hunger-debug-value-applied"
    ) => new(HungerDebugResultStatus.Applied, reason, snapshot);

    internal static HungerDebugResult NoChange(
        HungerPlayerSnapshot snapshot,
        string reason = "hunger-debug-value-unchanged"
    ) => new(HungerDebugResultStatus.NoChange, reason, snapshot);

    internal static HungerDebugResult Disabled(
        HungerPlayerSnapshot? snapshot = null,
        string reason = "hunger-system-disabled"
    ) => new(HungerDebugResultStatus.Disabled, reason, snapshot);

    internal static HungerDebugResult HostOnly(string reason = "hunger-debug-host-only") =>
        new(HungerDebugResultStatus.HostOnly, reason, null);

    internal static HungerDebugResult Rejected(string reason) =>
        new(HungerDebugResultStatus.Rejected, reason, null);
}

/// <summary>
/// Host-only debug facade. Console/UI adapters use this instead of writing the Hunger state table.
/// </summary>
internal sealed class HungerDebugController
{
    private readonly IHungerDebugBackend service;
    private readonly Func<bool> isEnabled;

    internal HungerDebugController(
        IHungerDebugBackend service,
        Func<bool> isEnabled
    )
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    internal bool IsLocked => service.IsDebugLocked;

    internal HungerDebugResult Query(string playerKey)
    {
        if (!TryGetHostSnapshot(playerKey, out var snapshot, out var failure))
            return failure!;
        return isEnabled() ? HungerDebugResult.Applied(snapshot, "hunger-debug-state-read") : HungerDebugResult.Disabled(snapshot);
    }

    internal HungerDebugResult SetAbsolute(string playerKey, double value)
    {
        if (!TryGetHostSnapshot(playerKey, out var before, out var failure))
            return failure!;
        if (!isEnabled())
            return HungerDebugResult.Disabled(before);
        if (!double.IsFinite(value) || value < 0d)
            return HungerDebugResult.Rejected("hunger-debug-value-is-invalid");

        var result = service.SetDebugValue(playerKey, value);
        return FromChangeResult(result, before);
    }

    internal HungerDebugResult SetPercentage(string playerKey, double percent)
    {
        if (!double.IsFinite(percent) || percent < 0d || percent > 100d)
            return HungerDebugResult.Rejected("hunger-debug-percentage-is-out-of-range");

        if (!TryGetHostSnapshot(playerKey, out var snapshot, out var failure))
            return failure!;
        if (!isEnabled())
            return HungerDebugResult.Disabled(snapshot);

        return SetAbsolute(playerKey, snapshot.Maximum * percent / 100d);
    }

    internal HungerDebugResult SetMaximum(string playerKey)
    {
        if (!TryGetHostSnapshot(playerKey, out var snapshot, out var failure))
            return failure!;
        return SetAbsolute(playerKey, snapshot.Maximum);
    }

    internal HungerDebugResult SetMinimum(string playerKey) =>
        SetAbsolute(playerKey, 0d);

    internal HungerDebugResult SetLocked(bool locked)
    {
        if (service.Role != HungerAuthorityRole.Host)
            return HungerDebugResult.HostOnly();

        service.SetDebugLocked(locked);
        return HungerDebugResult.Applied(
            new HungerPlayerSnapshot(),
            locked ? "hunger-debug-locked" : "hunger-debug-unlocked"
        );
    }

    private bool TryGetHostSnapshot(
        string playerKey,
        out HungerPlayerSnapshot snapshot,
        out HungerDebugResult? failure
    )
    {
        snapshot = new HungerPlayerSnapshot();
        failure = null;
        if (service.Role != HungerAuthorityRole.Host)
        {
            failure = HungerDebugResult.HostOnly();
            return false;
        }
        if (!service.TryEnsureHostPlayer(playerKey, out snapshot, out var reason))
        {
            failure = HungerDebugResult.Rejected(reason);
            return false;
        }
        return true;
    }

    private static HungerDebugResult FromChangeResult(
        HungerDebugMutationResult result,
        HungerPlayerSnapshot before
    )
    {
        if (!result.Accepted || result.Snapshot is null)
            return HungerDebugResult.Rejected(result.Reason);
        return result.Snapshot.Current.Equals(before.Current)
            ? HungerDebugResult.NoChange(result.Snapshot, result.Reason)
            : HungerDebugResult.Applied(result.Snapshot);
    }
}
