#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Damage;

namespace DontStarve.Player.Stats.Sanity.Darkness;

internal enum DarknessDamageMode
{
    Off = 0,
    NonLethal = 1,
    Default = 2,
}

internal readonly record struct DarknessDamageModeResolution(
    bool HasValue,
    DarknessDamageMode Mode,
    string Reason
)
{
    internal static DarknessDamageModeResolution Available(
        DarknessDamageMode mode,
        string reason
    )
    {
        return new DarknessDamageModeResolution(true, mode, reason);
    }

    internal static DarknessDamageModeResolution Unavailable(string reason)
    {
        return new DarknessDamageModeResolution(false, DarknessDamageMode.Off, reason);
    }
}

internal interface IDarknessDamageModeResolver
{
    DarknessDamageModeResolution Resolve();
}

internal readonly record struct DarknessDamageModeTransition(
    bool HasValue,
    DarknessDamageMode Mode,
    bool ResetRequired,
    bool CanRun,
    string Reason
);

/// <summary>
/// Tracks the single typed configuration value without owning countdown state. Runtime adapters
/// use ResetRequired to discard a request created under a different settlement operation.
/// </summary>
internal sealed class DarknessDamageModeTracker
{
    private bool initialized;
    private bool hadValue;
    private DarknessDamageMode current;

    internal DarknessDamageModeTransition Observe(DarknessDamageModeResolution resolution)
    {
        if (!resolution.HasValue || !Enum.IsDefined(typeof(DarknessDamageMode), resolution.Mode))
        {
            var reset = initialized && hadValue && current != DarknessDamageMode.Off;
            initialized = true;
            hadValue = false;
            return new DarknessDamageModeTransition(
                false,
                DarknessDamageMode.Off,
                reset,
                false,
                string.IsNullOrWhiteSpace(resolution.Reason)
                    ? "darkness.mode.unavailable"
                    : resolution.Reason
            );
        }

        var changed = initialized && (!hadValue || current != resolution.Mode);
        initialized = true;
        hadValue = true;
        current = resolution.Mode;
        return new DarknessDamageModeTransition(
            true,
            current,
            changed,
            current != DarknessDamageMode.Off,
            string.IsNullOrWhiteSpace(resolution.Reason)
                ? "darkness.mode.available"
                : resolution.Reason
        );
    }
}

internal readonly record struct DarknessAttackModePolicyResult(
    bool CanSettle,
    string Reason,
    int FloorHealth
);

internal static class DarknessAttackModePolicy
{
    internal static DarknessAttackModePolicyResult Evaluate(
        DarknessDamageMode mode,
        int currentHealth,
        int maximumHealth
    )
    {
        if (!Enum.IsDefined(typeof(DarknessDamageMode), mode))
            return new DarknessAttackModePolicyResult(false, "darkness.mode.invalid", 0);
        if (mode == DarknessDamageMode.Off)
            return new DarknessAttackModePolicyResult(false, "darkness.mode.off", 0);
        if (maximumHealth <= 0 || currentHealth < 0 || currentHealth > maximumHealth)
        {
            return new DarknessAttackModePolicyResult(
                false,
                "darkness.mode.health-snapshot-invalid",
                0
            );
        }
        if (currentHealth == 0)
        {
            return new DarknessAttackModePolicyResult(
                false,
                "darkness.mode.player-has-no-health",
                0
            );
        }
        if (mode == DarknessDamageMode.Default)
        {
            return new DarknessAttackModePolicyResult(
                true,
                "darkness.mode.default-settleable",
                0
            );
        }

        // Consume the stage-04 calculator directly; the mode gate must not become a second floor
        // authority merely because it needs to stop new countdowns at the same boundary.
        if (
            !NonLethalDamageCalculator.TryCalculateFloor(
                maximumHealth,
                out var floorHealth,
                out var floorReason
            )
        )
        {
            return new DarknessAttackModePolicyResult(false, floorReason, 0);
        }
        return currentHealth > floorHealth
            ? new DarknessAttackModePolicyResult(
                true,
                "darkness.mode.nonlethal-above-floor",
                floorHealth
            )
            : new DarknessAttackModePolicyResult(
                false,
                "darkness.mode.nonlethal-floor-reached",
                floorHealth
            );
    }
}

internal enum DarknessAttackDamageOperation
{
    Disabled = 0,
    DefaultPhysical = 1,
    ApplyDamageUpToFloor = 2,
}

internal enum DarknessAttackDamageReceiptStatus
{
    Settled,
    Rejected,
}

internal readonly record struct DarknessAttackResolutionRequest(
    DarknessAttackExpiryIntent Intent,
    DarknessDamageMode Mode,
    SanityAuthorityRole Authority,
    long SanityAuthorityRevision
);

internal readonly record struct DarknessAttackDamageRequest(
    DarknessAttackOwnerKey Key,
    string RequestId,
    DarknessDamageMode Mode,
    DarknessAttackDamageOperation Operation,
    string ReceiptId,
    int RngRoll,
    int BaseDamage,
    long SanityAuthorityRevision
);

internal sealed class DarknessAttackDamageReceipt
{
    internal DarknessAttackDamageReceipt(
        DarknessAttackDamageReceiptStatus status,
        DarknessAttackDamageOperation operation,
        DarknessAttackOwnerKey key,
        string requestId,
        string receiptId,
        int baseDamage,
        int beforeHealth,
        int maximumHealth,
        int actualDamage,
        int afterHealth,
        string reason
    )
    {
        Status = status;
        Operation = operation;
        Key = key;
        RequestId = requestId;
        ReceiptId = receiptId;
        BaseDamage = baseDamage;
        BeforeHealth = beforeHealth;
        MaximumHealth = maximumHealth;
        ActualDamage = actualDamage;
        AfterHealth = afterHealth;
        Reason = reason;
    }

    internal DarknessAttackDamageReceiptStatus Status { get; }

    internal DarknessAttackDamageOperation Operation { get; }

    internal DarknessAttackOwnerKey Key { get; }

    internal string RequestId { get; }

    internal string ReceiptId { get; }

    internal int BaseDamage { get; }

    internal int BeforeHealth { get; }

    internal int MaximumHealth { get; }

    internal int ActualDamage { get; }

    internal int AfterHealth { get; }

    internal string Reason { get; }

    internal bool IsSettled => Status == DarknessAttackDamageReceiptStatus.Settled;

    internal static DarknessAttackDamageReceipt Settled(
        DarknessAttackDamageRequest request,
        int beforeHealth,
        int maximumHealth,
        int actualDamage,
        int afterHealth,
        string reason
    )
    {
        return new DarknessAttackDamageReceipt(
            DarknessAttackDamageReceiptStatus.Settled,
            request.Operation,
            request.Key,
            request.RequestId,
            request.ReceiptId,
            request.BaseDamage,
            beforeHealth,
            maximumHealth,
            actualDamage,
            afterHealth,
            reason
        );
    }

    internal static DarknessAttackDamageReceipt Rejected(
        DarknessAttackDamageRequest request,
        string reason,
        int beforeHealth = 0,
        int maximumHealth = 0
    )
    {
        return new DarknessAttackDamageReceipt(
            DarknessAttackDamageReceiptStatus.Rejected,
            request.Operation,
            request.Key,
            request.RequestId,
            request.ReceiptId,
            request.BaseDamage,
            beforeHealth,
            maximumHealth,
            0,
            beforeHealth,
            reason
        );
    }
}

internal enum DarknessAttackSanityReceiptStatus
{
    Applied,
    NoChange,
    Rejected,
}

internal readonly record struct DarknessAttackSanityRequest(
    DarknessAttackOwnerKey Key,
    string RequestId,
    string DamageReceiptId,
    double Delta,
    SanityChangeSource Source,
    long ExpectedRevision
);

internal sealed class DarknessAttackSanityReceipt
{
    internal DarknessAttackSanityReceipt(
        DarknessAttackSanityReceiptStatus status,
        DarknessAttackOwnerKey key,
        string requestId,
        string damageReceiptId,
        double delta,
        SanityChangeSource source,
        long beforeRevision,
        long afterRevision,
        double beforeSanity,
        double afterSanity,
        string reason
    )
    {
        Status = status;
        Key = key;
        RequestId = requestId;
        DamageReceiptId = damageReceiptId;
        Delta = delta;
        Source = source;
        BeforeRevision = beforeRevision;
        AfterRevision = afterRevision;
        BeforeSanity = beforeSanity;
        AfterSanity = afterSanity;
        Reason = reason;
    }

    internal DarknessAttackSanityReceiptStatus Status { get; }

    internal DarknessAttackOwnerKey Key { get; }

    internal string RequestId { get; }

    internal string DamageReceiptId { get; }

    internal double Delta { get; }

    internal SanityChangeSource Source { get; }

    internal long BeforeRevision { get; }

    internal long AfterRevision { get; }

    internal double BeforeSanity { get; }

    internal double AfterSanity { get; }

    internal string Reason { get; }

    internal bool IsAccepted =>
        Status is DarknessAttackSanityReceiptStatus.Applied
            or DarknessAttackSanityReceiptStatus.NoChange;
}

internal enum DarknessAttackResolutionReceiptStatus
{
    Settled,
    Disabled,
    Rejected,
}

internal sealed class DarknessAttackResolutionReceipt
{
    internal DarknessAttackResolutionReceipt(
        DarknessAttackResolutionReceiptStatus status,
        DarknessAttackOwnerKey key,
        string requestId,
        DarknessDamageMode mode,
        DarknessAttackDamageOperation operation,
        string receiptId,
        int rngRoll,
        int baseDamage,
        DarknessAttackDamageReceipt? damageReceipt,
        DarknessAttackSanityReceipt? sanityReceipt,
        string reason
    )
    {
        Status = status;
        Key = key;
        RequestId = requestId;
        Mode = mode;
        Operation = operation;
        ReceiptId = receiptId;
        RngRoll = rngRoll;
        BaseDamage = baseDamage;
        DamageReceipt = damageReceipt;
        SanityReceipt = sanityReceipt;
        Reason = reason;
    }

    internal DarknessAttackResolutionReceiptStatus Status { get; }

    internal DarknessAttackOwnerKey Key { get; }

    internal string RequestId { get; }

    internal DarknessDamageMode Mode { get; }

    internal DarknessAttackDamageOperation Operation { get; }

    internal string ReceiptId { get; }

    internal int RngRoll { get; }

    internal int BaseDamage { get; }

    internal DarknessAttackDamageReceipt? DamageReceipt { get; }

    internal DarknessAttackSanityReceipt? SanityReceipt { get; }

    internal string Reason { get; }

    internal bool IsSettled => Status == DarknessAttackResolutionReceiptStatus.Settled;
}

internal enum DarknessAttackResolutionStatus
{
    Settled,
    Disabled,
    Duplicate,
    CorrelationConflict,
    Invalid,
    RequiresHostAuthority,
    SessionMismatch,
    CapacityExceeded,
    Rejected,
}

internal sealed class DarknessAttackResolutionResult
{
    internal DarknessAttackResolutionResult(
        DarknessAttackResolutionStatus status,
        string reason,
        DarknessAttackResolutionReceipt? receipt
    )
    {
        Status = status;
        Reason = reason;
        Receipt = receipt;
    }

    internal DarknessAttackResolutionStatus Status { get; }

    internal string Reason { get; }

    internal DarknessAttackResolutionReceipt? Receipt { get; }

    internal bool CreatedSettlement => Status == DarknessAttackResolutionStatus.Settled;
}

internal readonly record struct DarknessAttackResolutionSessionResult(
    bool Accepted,
    string Reason
);

internal interface IDarknessAttackResolutionRandom
{
    int NextRoll100();
}

internal interface IDarknessAttackDamageAuthority
{
    DarknessAttackDamageReceipt Settle(DarknessAttackDamageRequest request);
}

internal interface IDarknessAttackSanityAuthority
{
    DarknessAttackSanityReceipt Apply(DarknessAttackSanityRequest request);
}

internal static class DarknessAttackResolutionCorrelation
{
    internal static string Create(
        DarknessAttackOwnerKey key,
        string requestId,
        DarknessAttackDamageOperation operation
    )
    {
        var material = string.Concat(
            key.SessionId,
            "\n",
            key.PlayerKey,
            "\n",
            requestId,
            "\n",
            ((int)operation).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 0, guidBytes, 0, guidBytes.Length);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("N");
    }
}
