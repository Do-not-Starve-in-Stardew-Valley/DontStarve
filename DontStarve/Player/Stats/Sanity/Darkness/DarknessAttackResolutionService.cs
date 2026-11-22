#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Host-only, session-scoped settlement authority for one stage-06 expiry request. It owns the
/// 95/5 draw and the receipt window; physical health and Sanity remain behind injected authorities.
/// </summary>
internal sealed class DarknessAttackResolutionService
{
    internal const int MaximumReceipts = 256;
    internal const int MaximumDiagnosticOwners = DarknessAttackContract.MaximumOwnerStates;
    internal const double SanityDelta = -20d;

    private readonly IDarknessAttackResolutionRandom random;
    private readonly IDarknessAttackDamageAuthority damageAuthority;
    private readonly IDarknessAttackSanityAuthority sanityAuthority;
    // 「饥荒风格生命值」小数账本门控（2026-11-13 决议）：null = 永远骰子模式（既有测试与
    // 调用方零改动，行为退回改动前）；非 null 时每次 Resolve 在结算窗口内读取一次。
    private readonly Func<bool>? decimalHealthLedgerGate;
    private readonly Dictionary<string, DarknessAttackResolutionReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DarknessAttackDamageOperation> requestOperations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<DarknessAttackOwnerKey, DarknessAttackResolutionReceipt> latest =
        new();
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private string activeSessionId = string.Empty;

    internal DarknessAttackResolutionService(
        IDarknessAttackResolutionRandom random,
        IDarknessAttackDamageAuthority damageAuthority,
        IDarknessAttackSanityAuthority sanityAuthority,
        Func<bool>? decimalHealthLedgerGate = null
    )
    {
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.damageAuthority = damageAuthority
            ?? throw new ArgumentNullException(nameof(damageAuthority));
        this.sanityAuthority = sanityAuthority
            ?? throw new ArgumentNullException(nameof(sanityAuthority));
        this.decimalHealthLedgerGate = decimalHealthLedgerGate;
    }

    internal int ReceiptCount => receipts.Count;

    internal string ActiveSessionId => activeSessionId;

    internal DarknessAttackResolutionSessionResult BeginSession(string sessionId)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            return new DarknessAttackResolutionSessionResult(
                false,
                "darkness.resolution.session-invalid"
            );
        }
        if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            return new DarknessAttackResolutionSessionResult(
                true,
                "darkness.resolution.session-already-active"
            );
        }

        ClearSession();
        activeSessionId = sessionId;
        return new DarknessAttackResolutionSessionResult(
            true,
            "darkness.resolution.session-started"
        );
    }

    internal void ClearSession()
    {
        activeSessionId = string.Empty;
        receipts.Clear();
        requestOperations.Clear();
        latest.Clear();
        inFlight.Clear();
    }

    internal void ForgetDiagnosticOwner(string playerKey)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return;

        List<DarknessAttackOwnerKey>? removals = null;
        foreach (var key in latest.Keys)
        {
            if (!string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<DarknessAttackOwnerKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
            latest.Remove(key);
    }

    internal bool TryGetLatest(
        string playerKey,
        int screenId,
        out DarknessAttackResolutionReceipt receipt
    )
    {
        foreach (var pair in latest)
        {
            if (
                pair.Key.ScreenId == screenId
                && string.Equals(pair.Key.PlayerKey, playerKey, StringComparison.Ordinal)
            )
            {
                receipt = pair.Value;
                return true;
            }
        }
        receipt = null!;
        return false;
    }

    internal DarknessAttackResolutionResult Resolve(
        DarknessAttackResolutionRequest request
    )
    {
        var validation = Validate(request);
        if (validation is not null)
            return new DarknessAttackResolutionResult(validation.Value.Status, validation.Value.Reason, null);

        var operation = OperationFor(request.Mode);
        var requestKey = CreateRequestKey(request.Intent.Key, request.Intent.RequestId);
        if (requestOperations.TryGetValue(requestKey, out var originalOperation))
        {
            var originalReceipt = receipts[CreateReceiptKey(requestKey, originalOperation)];
            if (originalOperation != operation)
            {
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.CorrelationConflict,
                    "darkness.resolution.request-operation-conflict",
                    originalReceipt
                );
            }
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.Duplicate,
                "darkness.resolution.duplicate",
                originalReceipt
            );
        }
        if (inFlight.Contains(requestKey))
        {
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.Rejected,
                "darkness.resolution.request-in-flight",
                null
            );
        }
        if (receipts.Count >= MaximumReceipts)
        {
            return new DarknessAttackResolutionResult(
                DarknessAttackResolutionStatus.CapacityExceeded,
                "darkness.resolution.receipt-capacity-exceeded",
                null
            );
        }

        inFlight.Add(requestKey);
        try
        {
            if (operation == DarknessAttackDamageOperation.Disabled)
            {
                var disabledReceipt = new DarknessAttackResolutionReceipt(
                    DarknessAttackResolutionReceiptStatus.Disabled,
                    request.Intent.Key,
                    request.Intent.RequestId,
                    request.Mode,
                    operation,
                    DarknessAttackResolutionCorrelation.Create(
                        request.Intent.Key,
                        request.Intent.RequestId,
                        operation
                    ),
                    -1,
                    0,
                    null,
                    null,
                    "darkness.resolution.mode-off"
                );
                Store(requestKey, disabledReceipt);
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.Disabled,
                    disabledReceipt.Reason,
                    disabledReceipt
                );
            }

            // 【2026-11-13 用户决议】「饥荒风格生命值」小数账本开启时不掷骰：固定期望值
            // 100.05 的整数部分 100 进整数结算链；RngRoll 记 -1，含义"未掷骰"。门控 null /
            // 返回 false = 既有骰子路径，逐字不变。门控只在本结算窗口读取一次，保证同一次
            // Resolve 内掷骰口径与零头判定口径一致。
            var decimalMode = decimalHealthLedgerGate?.Invoke() ?? false;
            int roll;
            int baseDamage;
            if (decimalMode)
            {
                roll = -1;
                baseDamage = DarknessAttackExpectedDamage.ExpectedBaseDamageIntegerPart;
            }
            else
            {
                try
                {
                    roll = random.NextRoll100();
                }
                catch (Exception exception)
                {
                    return StoreRejectedBeforeDamage(
                        request,
                        requestKey,
                        operation,
                        -1,
                        0,
                        $"darkness.resolution.rng-threw:{exception.GetType().Name}"
                    );
                }
                if (roll < 0 || roll > 99)
                {
                    return StoreRejectedBeforeDamage(
                        request,
                        requestKey,
                        operation,
                        roll,
                        0,
                        "darkness.resolution.rng-out-of-range"
                    );
                }

                baseDamage = roll < 95 ? 100 : 101;
            }
            var receiptId = DarknessAttackResolutionCorrelation.Create(
                request.Intent.Key,
                request.Intent.RequestId,
                operation
            );
            var damageRequest = new DarknessAttackDamageRequest(
                request.Intent.Key,
                request.Intent.RequestId,
                request.Mode,
                operation,
                receiptId,
                roll,
                baseDamage,
                request.SanityAuthorityRevision
            );
            DarknessAttackDamageReceipt damageReceipt;
            try
            {
                damageReceipt = damageAuthority.Settle(damageRequest);
            }
            catch (Exception exception)
            {
                damageReceipt = DarknessAttackDamageReceipt.Rejected(
                    damageRequest,
                    $"darkness.resolution.damage-authority-threw:{exception.GetType().Name}"
                );
            }
            if (!IsValidDamageReceipt(damageRequest, damageReceipt, out var damageReason))
            {
                damageReceipt = DarknessAttackDamageReceipt.Rejected(
                    damageRequest,
                    damageReason
                );
            }
            if (!damageReceipt.IsSettled)
            {
                var rejected = new DarknessAttackResolutionReceipt(
                    DarknessAttackResolutionReceiptStatus.Rejected,
                    request.Intent.Key,
                    request.Intent.RequestId,
                    request.Mode,
                    operation,
                    receiptId,
                    roll,
                    baseDamage,
                    damageReceipt,
                    null,
                    damageReceipt.Reason
                );
                Store(requestKey, rejected);
                return new DarknessAttackResolutionResult(
                    DarknessAttackResolutionStatus.Rejected,
                    rejected.Reason,
                    rejected
                );
            }

            // 【2026-11-13 用户决议】零头交付条件（纯规则层，damage 收据已过校验且 IsSettled
            // 在此已成立，条件保留是为与冻结口径逐字对齐）：收据已结算 && ActualDamage > 0 &&
            // 非地板钳制。Default 模式 actual==0（护甲全吸收/iframe/无血量差）天然为 0；
            // NonLethal 的 AfterHealth == floorHealth 视为被钳，零头不交付。理由：减法类削减
            // （防御）作用于完整 100.05，零头应穿透；地板/全吸收把总量钳死，零头必须归零。
            // 保守偏差 ≤ 0.05/次。ReduceToFloor / Off 路径不进本窗口；2AM 特殊昏迷链不经过
            // 本服务，不在范围。扣理智失败时伤害已真实发生，零头仍随 damage 收据交付。
            var decimalLedgerFraction =
                decimalMode
                && damageReceipt.IsSettled
                && damageReceipt.ActualDamage > 0
                && !IsFloorClamped(operation, damageReceipt)
                    ? DarknessAttackExpectedDamage.ExpectedBaseDamageFraction
                    : 0d;

            var sanityRequest = new DarknessAttackSanityRequest(
                request.Intent.Key,
                request.Intent.RequestId,
                receiptId,
                SanityDelta,
                SanityChangeSource.DarknessAttack,
                request.SanityAuthorityRevision
            );
            DarknessAttackSanityReceipt sanityReceipt;
            try
            {
                sanityReceipt = sanityAuthority.Apply(sanityRequest);
            }
            catch (Exception exception)
            {
                sanityReceipt = RejectedSanity(
                    sanityRequest,
                    $"darkness.resolution.sanity-authority-threw:{exception.GetType().Name}"
                );
            }
            if (!IsValidSanityReceipt(sanityRequest, sanityReceipt, out var sanityReason))
                sanityReceipt = RejectedSanity(sanityRequest, sanityReason);

            var settled = damageReceipt.IsSettled && sanityReceipt.IsAccepted;
            var receipt = new DarknessAttackResolutionReceipt(
                settled
                    ? DarknessAttackResolutionReceiptStatus.Settled
                    : DarknessAttackResolutionReceiptStatus.Rejected,
                request.Intent.Key,
                request.Intent.RequestId,
                request.Mode,
                operation,
                receiptId,
                roll,
                baseDamage,
                damageReceipt,
                sanityReceipt,
                settled
                    ? "darkness.resolution.settled"
                    : sanityReceipt.Reason
            );
            Store(requestKey, receipt);
            return new DarknessAttackResolutionResult(
                settled
                    ? DarknessAttackResolutionStatus.Settled
                    : DarknessAttackResolutionStatus.Rejected,
                receipt.Reason,
                receipt,
                decimalLedgerFraction
            );
        }
        finally
        {
            inFlight.Remove(requestKey);
        }
    }

    private (DarknessAttackResolutionStatus Status, string Reason)? Validate(
        DarknessAttackResolutionRequest request
    )
    {
        var intent = request.Intent;
        if (
            !SanityPlayerKey.IsCanonical(intent.Key.PlayerKey)
            || intent.Key.ScreenId < 0
            || !SanityProtocol.IsValidSessionId(intent.Key.SessionId)
            || string.IsNullOrWhiteSpace(intent.RequestId)
            || intent.RequestId.Length > DarknessAttackContract.MaximumRequestIdLength
            || intent.Revision < 0
            || string.IsNullOrWhiteSpace(intent.LightReason)
            || !string.Equals(
                intent.ContractVersion,
                DarknessAttackContract.ContractVersion,
                StringComparison.Ordinal
            )
            || !Enum.IsDefined(typeof(DarknessDamageMode), request.Mode)
            || request.SanityAuthorityRevision < 0
        )
        {
            return (
                DarknessAttackResolutionStatus.Invalid,
                "darkness.resolution.request-invalid"
            );
        }
        if (request.Authority != SanityAuthorityRole.Host)
        {
            return (
                DarknessAttackResolutionStatus.RequiresHostAuthority,
                "darkness.resolution.requires-host-authority"
            );
        }
        if (
            !SanityProtocol.IsValidSessionId(activeSessionId)
            || !string.Equals(activeSessionId, intent.Key.SessionId, StringComparison.Ordinal)
        )
        {
            return (
                DarknessAttackResolutionStatus.SessionMismatch,
                "darkness.resolution.session-mismatch"
            );
        }
        return null;
    }

    private DarknessAttackResolutionResult StoreRejectedBeforeDamage(
        DarknessAttackResolutionRequest request,
        string requestKey,
        DarknessAttackDamageOperation operation,
        int roll,
        int baseDamage,
        string reason
    )
    {
        var receipt = new DarknessAttackResolutionReceipt(
            DarknessAttackResolutionReceiptStatus.Rejected,
            request.Intent.Key,
            request.Intent.RequestId,
            request.Mode,
            operation,
            DarknessAttackResolutionCorrelation.Create(
                request.Intent.Key,
                request.Intent.RequestId,
                operation
            ),
            roll,
            baseDamage,
            null,
            null,
            reason
        );
        Store(requestKey, receipt);
        return new DarknessAttackResolutionResult(
            DarknessAttackResolutionStatus.Rejected,
            reason,
            receipt
        );
    }

    private void Store(string requestKey, DarknessAttackResolutionReceipt receipt)
    {
        requestOperations.Add(requestKey, receipt.Operation);
        receipts.Add(CreateReceiptKey(requestKey, receipt.Operation), receipt);
        if (latest.ContainsKey(receipt.Key) || latest.Count < MaximumDiagnosticOwners)
            latest[receipt.Key] = receipt;
    }

    /// <summary>
    /// NonLethal 地板钳制判定：结算后血量正好落在地板上，说明总量被 ceil(max×0.20) 钳死，
    /// 零头必须归零。只有 ApplyDamageUpToFloor 带地板语义；地板值唯一实现委托
    /// DarknessAttackFloorRules.FloorHealthFor（内部走 NonLethalDamageCalculator），与伤害
    /// 结算 authority 从同一处取式，两处不会分叉。
    /// </summary>
    private static bool IsFloorClamped(
        DarknessAttackDamageOperation operation,
        DarknessAttackDamageReceipt receipt
    )
    {
        return operation == DarknessAttackDamageOperation.ApplyDamageUpToFloor
            && receipt.AfterHealth == DarknessAttackFloorRules.FloorHealthFor(receipt.MaximumHealth);
    }

    private static bool IsValidDamageReceipt(
        DarknessAttackDamageRequest request,
        DarknessAttackDamageReceipt? receipt,
        out string reason
    )
    {
        if (receipt is null)
        {
            reason = "darkness.resolution.damage-receipt-missing";
            return false;
        }
        if (
            !Enum.IsDefined(typeof(DarknessAttackDamageReceiptStatus), receipt.Status)
            || receipt.Operation != request.Operation
            || !receipt.Key.Equals(request.Key)
            || !string.Equals(receipt.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(receipt.ReceiptId, request.ReceiptId, StringComparison.Ordinal)
            || receipt.BaseDamage != request.BaseDamage
            || string.IsNullOrWhiteSpace(receipt.Reason)
        )
        {
            reason = "darkness.resolution.damage-receipt-mismatch";
            return false;
        }
        if (receipt.Status == DarknessAttackDamageReceiptStatus.Rejected)
        {
            reason = "darkness.resolution.damage-receipt-valid";
            return true;
        }
        if (
            receipt.MaximumHealth <= 0
            || receipt.BeforeHealth < 0
            || receipt.BeforeHealth > receipt.MaximumHealth
            || receipt.AfterHealth < 0
            || receipt.AfterHealth > receipt.MaximumHealth
            // 【2026-11-11 原版口径对齐】nonlethal 在地板及以下结算为 0 伤害（血量不动），
            // 袭击循环、扣理智与反馈照常；与 Default 的 0 伤害结算（护甲全吸收/iframe）
            // 同等合法，不再视为无效 receipt。
            || receipt.ActualDamage < 0
        )
        {
            reason = "darkness.resolution.damage-receipt-health-invalid";
            return false;
        }
        reason = "darkness.resolution.damage-receipt-valid";
        return true;
    }

    private static bool IsValidSanityReceipt(
        DarknessAttackSanityRequest request,
        DarknessAttackSanityReceipt? receipt,
        out string reason
    )
    {
        if (receipt is null)
        {
            reason = "darkness.resolution.sanity-receipt-missing";
            return false;
        }
        if (
            !Enum.IsDefined(typeof(DarknessAttackSanityReceiptStatus), receipt.Status)
            || !receipt.Key.Equals(request.Key)
            || !string.Equals(receipt.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.DamageReceiptId,
                request.DamageReceiptId,
                StringComparison.Ordinal
            )
            || receipt.Delta != request.Delta
            || receipt.Source != SanityChangeSource.DarknessAttack
            || receipt.BeforeRevision != request.ExpectedRevision
            || !double.IsFinite(receipt.BeforeSanity)
            || !double.IsFinite(receipt.AfterSanity)
            || string.IsNullOrWhiteSpace(receipt.Reason)
        )
        {
            reason = "darkness.resolution.sanity-receipt-mismatch";
            return false;
        }
        if (receipt.Status == DarknessAttackSanityReceiptStatus.Applied)
        {
            if (
                receipt.BeforeRevision == long.MaxValue
                || receipt.AfterRevision != receipt.BeforeRevision + 1
                || receipt.AfterSanity > receipt.BeforeSanity
                || receipt.BeforeSanity - receipt.AfterSanity > -request.Delta
            )
            {
                reason = "darkness.resolution.sanity-applied-receipt-invalid";
                return false;
            }
        }
        else if (receipt.Status == DarknessAttackSanityReceiptStatus.NoChange)
        {
            if (
                receipt.AfterRevision != receipt.BeforeRevision
                || receipt.AfterSanity != receipt.BeforeSanity
            )
            {
                reason = "darkness.resolution.sanity-no-change-receipt-invalid";
                return false;
            }
        }
        reason = "darkness.resolution.sanity-receipt-valid";
        return true;
    }

    private static DarknessAttackSanityReceipt RejectedSanity(
        DarknessAttackSanityRequest request,
        string reason
    )
    {
        return new DarknessAttackSanityReceipt(
            DarknessAttackSanityReceiptStatus.Rejected,
            request.Key,
            request.RequestId,
            request.DamageReceiptId,
            request.Delta,
            request.Source,
            request.ExpectedRevision,
            request.ExpectedRevision,
            0d,
            0d,
            reason
        );
    }

    private static DarknessAttackDamageOperation OperationFor(DarknessDamageMode mode)
    {
        return mode switch
        {
            DarknessDamageMode.Off => DarknessAttackDamageOperation.Disabled,
            DarknessDamageMode.NonLethal =>
                DarknessAttackDamageOperation.ApplyDamageUpToFloor,
            DarknessDamageMode.Default => DarknessAttackDamageOperation.DefaultPhysical,
            _ => DarknessAttackDamageOperation.Disabled,
        };
    }

    private static string CreateRequestKey(DarknessAttackOwnerKey key, string requestId)
    {
        return string.Concat(key.SessionId, "|", key.PlayerKey, "|", requestId);
    }

    private static string CreateReceiptKey(
        string requestKey,
        DarknessAttackDamageOperation operation
    )
    {
        return string.Concat(requestKey, "|", ((int)operation).ToString());
    }
}
