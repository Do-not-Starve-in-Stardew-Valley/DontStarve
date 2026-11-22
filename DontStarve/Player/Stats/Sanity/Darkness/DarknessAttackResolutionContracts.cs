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
                DarknessAttackContract.PlayerHasNoHealthReason,
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

        // 【2026-11-11 原版口径对齐】DST grue.lua 的 nonlethal 分支不因血量触地板停止袭击：
        // 倒计时与袭击循环照常继续，伤害被钳制到地板（地板及以下结算为 0 伤害），扣理智与
        // 袭击反馈照常发生。因此模式门只拦血量为 0 / 无效健康快照，地板判断完全交给伤害
        // 结算 authority（ApplyDamageUpToFloor）；这里不得成为第二套地板 authority，否则会
        // 退回“到地板即取消倒计时”的旧行为。FloorHealth 只作诊断保留。
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
        return new DarknessAttackModePolicyResult(
            true,
            "darkness.mode.nonlethal-settleable",
            floorHealth
        );
    }
}

/// <summary>
/// 【2026-11-13 用户决议】「饥荒风格生命值」（Health 小数账本，EnableDontStarveHealth）开启时，
/// 黑暗袭击基础伤害不再掷骰，固定取期望值：0.95×100 + 0.05×101 = 100.05。整数结算链
/// （request / 收据 / 执行器，全 int 链路）照旧喂整数部分 floor(100.05)=100；零头 -0.05 由
/// 结算结果的 DecimalLedgerFraction 交付给 Health 余数账本，交付条件（真实发生且未被地板
/// 钳制）见 DarknessAttackResolutionService 的零头判定。关闭（gate null/false）时骰子路径
/// 一字不变。
/// </summary>
internal static class DarknessAttackExpectedDamage
{
    internal const double ExpectedBaseDamage = 100.05d;

    internal const int ExpectedBaseDamageIntegerPart = 100;

    internal const double ExpectedBaseDamageFraction = -0.05d;
}

/// <summary>
/// 黑暗袭击零头判定专用的地板取值助手。唯一实现是委托 NonLethalDamageCalculator.TryCalculateFloor
/// 的 ceil(max×0.20) 整数算术，从结构上保证与伤害结算 authority 的地板永不分叉——两处各自
/// 手写"同式"会随时间漂移。
/// </summary>
internal static class DarknessAttackFloorRules
{
    internal static int FloorHealthFor(int maximumHealth)
    {
        // maximumHealth <= 0 时返回 0：零头判定只在伤害收据校验通过（MaximumHealth > 0，
        // 见 DarknessAttackResolutionService.IsValidDamageReceipt）之后运行，该分支实际不可达，
        // 仅保持纯函数对任意输入的确定性。
        return NonLethalDamageCalculator.TryCalculateFloor(maximumHealth, out var floorHealth, out _)
            ? floorHealth
            : 0;
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
        DarknessAttackResolutionReceipt? receipt,
        double decimalLedgerFraction = 0d
    )
    {
        Status = status;
        Reason = reason;
        Receipt = receipt;
        DecimalLedgerFraction = decimalLedgerFraction;
    }

    internal DarknessAttackResolutionStatus Status { get; }

    internal string Reason { get; }

    internal DarknessAttackResolutionReceipt? Receipt { get; }

    /// <summary>
    /// 本次结算应交给 Health 余数账本的「饥荒风格生命值」零头（ExpectedBaseDamageFraction
    /// 或 0）。0 = 不交付：整数模式、Off / 拒绝 / 重复（Duplicate 复用原收据走默认 0，天然
    /// 防重复交付）、伤害未真实发生或被地板钳制。纯规则层只产出数值；是否落账由 SMAPI
    /// 包装层的 sink 决定。
    /// </summary>
    internal double DecimalLedgerFraction { get; }

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
