#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Host-only, session-only terminal settlement window. Records are never evicted inside a live
/// session: losing an old death key would make a reconnect or delayed replay duplicate loot.
/// </summary>
internal sealed class HostileShadowSettlementService
{
    internal const int MaximumReceipts = 256;
    internal const int BonusRollScale = 10_000;

    private readonly IHostileShadowSettlementRandom random;
    private readonly IHostileShadowDropSpawnAuthority dropAuthority;
    private readonly IHostileShadowLastHitterAuthority lastHitterAuthority;
    private readonly IHostileShadowSanityRewardAuthority sanityAuthority;
    private readonly Dictionary<HostileShadowSettlementKey, HostileShadowSettlementRequest>
        intents = new();
    private readonly Dictionary<HostileShadowSettlementKey, HostileShadowSettlementReceipt>
        receipts = new();
    private readonly HashSet<HostileShadowSettlementKey> inFlight = new();
    private string activeSessionId = string.Empty;

    internal HostileShadowSettlementService(
        IHostileShadowSettlementRandom random,
        IHostileShadowDropSpawnAuthority dropAuthority,
        IHostileShadowLastHitterAuthority lastHitterAuthority,
        IHostileShadowSanityRewardAuthority sanityAuthority
    )
    {
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.dropAuthority = dropAuthority
            ?? throw new ArgumentNullException(nameof(dropAuthority));
        this.lastHitterAuthority = lastHitterAuthority
            ?? throw new ArgumentNullException(nameof(lastHitterAuthority));
        this.sanityAuthority = sanityAuthority
            ?? throw new ArgumentNullException(nameof(sanityAuthority));
    }

    internal int ReceiptCount => receipts.Count;

    internal bool BeginSession(string sessionId, out string reason)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "hostile-shadow.settlement-session-invalid";
            return false;
        }
        if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            reason = "hostile-shadow.settlement-session-already-active";
            return true;
        }

        ClearSession();
        activeSessionId = sessionId;
        reason = "hostile-shadow.settlement-session-started";
        return true;
    }

    internal HostileShadowSettlementResult Resolve(
        HostileShadowSettlementRequest request
    )
    {
        if (request is null)
            return Transient(HostileShadowSettlementStatus.InvalidIntent, "request-missing");
        if (request.Authority != SanityAuthorityRole.Host)
        {
            return Transient(
                HostileShadowSettlementStatus.RequiresHostAuthority,
                "host-authority-required"
            );
        }
        if (
            activeSessionId.Length == 0
            || !string.Equals(
                request.LifecycleReceipt.SessionId,
                activeSessionId,
                StringComparison.Ordinal
            )
        )
        {
            return Transient(
                HostileShadowSettlementStatus.SessionMismatch,
                "session-does-not-match"
            );
        }

        var key = request.Key;
        if (!key.IsValid)
            return Transient(HostileShadowSettlementStatus.InvalidIntent, "key-invalid");
        if (receipts.TryGetValue(key, out var priorReceipt))
        {
            if (intents.TryGetValue(key, out var prior) && prior == request)
            {
                return new HostileShadowSettlementResult(
                    HostileShadowSettlementStatus.Duplicate,
                    priorReceipt,
                    priorReceipt.RetryDisposition,
                    "hostile-shadow.settlement-duplicate"
                );
            }
            return new HostileShadowSettlementResult(
                HostileShadowSettlementStatus.CorrelationConflict,
                priorReceipt,
                HostileShadowSettlementRetryDisposition.TerminalDoNotRetrySameDeath,
                "hostile-shadow.settlement-correlation-conflict"
            );
        }
        if (inFlight.Contains(key))
        {
            return Transient(
                HostileShadowSettlementStatus.InProgress,
                "settlement-already-in-progress"
            );
        }
        if (!TryValidate(request, out var validationReason))
        {
            return Transient(
                HostileShadowSettlementStatus.InvalidIntent,
                validationReason
            );
        }
        if (receipts.Count >= MaximumReceipts)
        {
            return Transient(
                HostileShadowSettlementStatus.CapacityExceeded,
                "receipt-capacity-exceeded"
            );
        }

        intents.Add(key, request);
        inFlight.Add(key);
        try
        {
            return SettleReserved(request);
        }
        finally
        {
            inFlight.Remove(key);
        }
    }

    internal bool TryGetReceipt(
        HostileShadowSettlementKey key,
        out HostileShadowSettlementReceipt? receipt
    )
    {
        if (receipts.TryGetValue(key, out var found))
        {
            receipt = found;
            return true;
        }
        receipt = null;
        return false;
    }

    internal void ClearSession()
    {
        activeSessionId = string.Empty;
        inFlight.Clear();
        intents.Clear();
        receipts.Clear();
    }

    private HostileShadowSettlementResult SettleReserved(
        HostileShadowSettlementRequest request
    )
    {
        var key = request.Key;
        var seed = HostileShadowSettlementSeed.Create(key);
        int roll;
        try
        {
            roll = random.NextBonusRoll10000(seed);
        }
        catch (Exception exception)
        {
            return RecordFailure(
                request,
                seed,
                -1,
                0,
                HostileShadowDropSpawnReceipt.Rejected(
                    string.Concat(
                        "hostile-shadow.settlement-rng-threw-",
                        exception.GetType().Name
                    )
                ),
                HostileShadowLastHitterReceipt.NotEvaluated(
                    "hostile-shadow.settlement-last-hitter-not-evaluated"
                ),
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-not-attempted"
                ),
                HostileShadowSettlementReceiptStatus.Rejected,
                "hostile-shadow.settlement-rng-failed"
            );
        }
        if (roll < 0 || roll >= BonusRollScale)
        {
            return RecordFailure(
                request,
                seed,
                roll,
                0,
                HostileShadowDropSpawnReceipt.Rejected(
                    "hostile-shadow.settlement-rng-roll-invalid"
                ),
                HostileShadowLastHitterReceipt.NotEvaluated(
                    "hostile-shadow.settlement-last-hitter-not-evaluated"
                ),
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-not-attempted"
                ),
                HostileShadowSettlementReceiptStatus.Rejected,
                "hostile-shadow.settlement-rng-roll-invalid"
            );
        }

        var quantity = request.GuaranteedQuantity;
        if ((roll / (double)BonusRollScale) < request.BonusChance)
            quantity += request.BonusQuantity;
        var dropRequest = new HostileShadowDropSpawnRequest(
            key,
            key.SettlementId,
            request.DropTableId,
            request.ItemSemanticId,
            quantity,
            seed,
            roll,
            request.LocationId,
            request.PositionX,
            request.PositionY
        );
        HostileShadowDropSpawnReceipt drop;
        try
        {
            drop = dropAuthority.Spawn(dropRequest);
        }
        catch (Exception exception)
        {
            drop = HostileShadowDropSpawnReceipt.Rejected(
                string.Concat(
                    "hostile-shadow.settlement-drop-authority-threw-",
                    exception.GetType().Name
                )
            );
        }
        if (!drop.Spawned)
        {
            if (string.IsNullOrWhiteSpace(drop.Reason))
            {
                drop = HostileShadowDropSpawnReceipt.Rejected(
                    "hostile-shadow.settlement-drop-receipt-invalid"
                );
            }
            return RecordFailure(
                request,
                seed,
                roll,
                quantity,
                drop,
                HostileShadowLastHitterReceipt.NotEvaluated(
                    "hostile-shadow.settlement-last-hitter-not-evaluated-after-drop-failure"
                ),
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-not-attempted-after-drop-failure"
                ),
                HostileShadowSettlementReceiptStatus.Rejected,
                "hostile-shadow.settlement-drop-failed"
            );
        }

        if (!request.LifecycleReceipt.RewardEligible)
        {
            return RecordSuccess(
                request,
                seed,
                roll,
                quantity,
                drop,
                HostileShadowLastHitterReceipt.NotEvaluated(
                    "hostile-shadow.settlement-no-attributed-last-hitter"
                ),
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-skipped-no-attributed-last-hitter"
                ),
                "hostile-shadow.settlement-drop-only-no-attributed-last-hitter"
            );
        }

        HostileShadowLastHitterReceipt hitter;
        try
        {
            hitter = lastHitterAuthority.Resolve(
                new HostileShadowLastHitterRequest(
                    key,
                    request.LifecycleReceipt.AttributedPlayerKey,
                    request.LocationId
                )
            );
        }
        catch (Exception exception)
        {
            hitter = HostileShadowLastHitterReceipt.Invalid(
                string.Concat(
                    "hostile-shadow.settlement-last-hitter-authority-threw-",
                    exception.GetType().Name
                )
            );
        }
        if (!hitter.IsValid)
        {
            hitter = HostileShadowLastHitterReceipt.Invalid(
                string.IsNullOrWhiteSpace(hitter.Reason)
                    ? "hostile-shadow.settlement-last-hitter-receipt-invalid"
                    : hitter.Reason
            );
        }
        else if (
            !string.Equals(
                hitter.PlayerKey,
                request.LifecycleReceipt.AttributedPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            hitter = HostileShadowLastHitterReceipt.Invalid(
                "hostile-shadow.settlement-last-hitter-player-mismatch"
            );
        }
        if (!hitter.IsValid)
        {
            return RecordSuccess(
                request,
                seed,
                roll,
                quantity,
                drop,
                hitter,
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-skipped-invalid-last-hitter"
                ),
                "hostile-shadow.settlement-drop-only-last-hitter-invalid"
            );
        }

        HostileShadowSanityRewardReceipt sanity;
        try
        {
            sanity = sanityAuthority.Apply(
                new HostileShadowSanityRewardRequest(
                    key,
                    key.SettlementId,
                    hitter.PlayerKey,
                    request.LocationId,
                    request.SanityReward,
                    SanityChangeSource.HostileShadowKill
                )
            );
        }
        catch (Exception exception)
        {
            sanity = new HostileShadowSanityRewardReceipt(
                HostileShadowSanityRewardStatus.Rejected,
                hitter.PlayerKey,
                request.SanityReward,
                SanityChangeSource.HostileShadowKill,
                0,
                0,
                0d,
                0d,
                string.Concat(
                    "hostile-shadow.settlement-sanity-authority-threw-",
                    exception.GetType().Name
                )
            );
        }
        if (!sanity.IsAccepted && string.IsNullOrWhiteSpace(sanity.Reason))
        {
            sanity = new HostileShadowSanityRewardReceipt(
                HostileShadowSanityRewardStatus.Rejected,
                hitter.PlayerKey,
                request.SanityReward,
                SanityChangeSource.HostileShadowKill,
                sanity.BeforeRevision,
                sanity.AfterRevision,
                sanity.BeforeSanity,
                sanity.AfterSanity,
                "hostile-shadow.settlement-sanity-receipt-invalid"
            );
        }
        if (
            !sanity.IsAccepted
            || !string.Equals(sanity.PlayerKey, hitter.PlayerKey, StringComparison.Ordinal)
            || sanity.Source != SanityChangeSource.HostileShadowKill
            || Math.Abs(sanity.Delta - request.SanityReward) > 0.0000001d
        )
        {
            return RecordFailure(
                request,
                seed,
                roll,
                quantity,
                drop,
                hitter,
                sanity,
                HostileShadowSettlementReceiptStatus.PartialFailure,
                "hostile-shadow.settlement-sanity-failed-after-drop"
            );
        }
        return RecordSuccess(
            request,
            seed,
            roll,
            quantity,
            drop,
            hitter,
            sanity,
            "hostile-shadow.settlement-completed"
        );
    }

    private HostileShadowSettlementResult RecordSuccess(
        HostileShadowSettlementRequest request,
        int seed,
        int roll,
        int quantity,
        HostileShadowDropSpawnReceipt drop,
        HostileShadowLastHitterReceipt hitter,
        HostileShadowSanityRewardReceipt sanity,
        string reason
    )
    {
        var receipt = CreateReceipt(
            HostileShadowSettlementReceiptStatus.Settled,
            request,
            seed,
            roll,
            quantity,
            drop,
            hitter,
            sanity,
            reason
        );
        receipts.Add(request.Key, receipt);
        return new HostileShadowSettlementResult(
            HostileShadowSettlementStatus.Settled,
            receipt,
            receipt.RetryDisposition,
            reason
        );
    }

    private HostileShadowSettlementResult RecordFailure(
        HostileShadowSettlementRequest request,
        int seed,
        int roll,
        int quantity,
        HostileShadowDropSpawnReceipt drop,
        HostileShadowLastHitterReceipt hitter,
        HostileShadowSanityRewardReceipt sanity,
        HostileShadowSettlementReceiptStatus status,
        string reason
    )
    {
        var receipt = CreateReceipt(
            status,
            request,
            seed,
            roll,
            quantity,
            drop,
            hitter,
            sanity,
            reason
        );
        receipts.Add(request.Key, receipt);
        return new HostileShadowSettlementResult(
            HostileShadowSettlementStatus.Rejected,
            receipt,
            receipt.RetryDisposition,
            reason
        );
    }

    private static HostileShadowSettlementReceipt CreateReceipt(
        HostileShadowSettlementReceiptStatus status,
        HostileShadowSettlementRequest request,
        int seed,
        int roll,
        int quantity,
        HostileShadowDropSpawnReceipt drop,
        HostileShadowLastHitterReceipt hitter,
        HostileShadowSanityRewardReceipt sanity,
        string reason
    )
    {
        return new HostileShadowSettlementReceipt(
            status,
            request.Key,
            request.LifecycleReceipt.CorrelationId,
            request.Key.SettlementId,
            seed,
            roll,
            quantity,
            drop,
            hitter,
            sanity,
            HostileShadowSettlementRetryDisposition.TerminalDoNotRetrySameDeath,
            reason
        );
    }

    private static bool TryValidate(
        HostileShadowSettlementRequest request,
        out string reason
    )
    {
        var receipt = request.LifecycleReceipt;
        var key = request.Key;
        if (
            receipt.Kind != HostileShadowLifecycleTransitionKind.Dying
            || !receipt.SettlementEligible
            || !receipt.DropEligible
            || receipt.DeathRevision != receipt.Revision
            || !string.Equals(
                receipt.CorrelationId,
                key.LifecycleCorrelationId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                receipt.Reason,
                HostileShadowSettlementReasonIds.DyingHealthZero,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.settlement-dying-receipt-invalid";
            return false;
        }
        if (
            !string.Equals(
                request.StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
            || request.Health != 0
        )
        {
            reason = "hostile-shadow.settlement-state-is-not-true-dying";
            return false;
        }
        if (
            string.IsNullOrWhiteSpace(request.LocationId)
            || request.LocationId.Length > 512
            || !double.IsFinite(request.PositionX)
            || !double.IsFinite(request.PositionY)
        )
        {
            reason = "hostile-shadow.settlement-location-invalid";
            return false;
        }
        if (
            request.DropTableSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(request.DropTableId)
            || string.IsNullOrWhiteSpace(request.ItemSemanticId)
            || request.GuaranteedQuantity <= 0
            || request.BonusQuantity < 0
            || !double.IsFinite(request.BonusChance)
            || request.BonusChance < 0d
            || request.BonusChance > 1d
            || request.GuaranteedQuantity > 999
            || request.BonusQuantity > 999
            || request.GuaranteedQuantity + request.BonusQuantity > 999
        )
        {
            reason = "hostile-shadow.settlement-drop-contract-invalid";
            return false;
        }
        if (
            request.SanityReward < 0
            || request.ExperienceValue != 0
            || !string.IsNullOrEmpty(request.KillCounterId)
        )
        {
            reason = "hostile-shadow.settlement-unsupported-reward-contract";
            return false;
        }

        reason = "hostile-shadow.settlement-intent-valid";
        return true;
    }

    private static HostileShadowSettlementResult Transient(
        HostileShadowSettlementStatus status,
        string suffix
    )
    {
        return new HostileShadowSettlementResult(
            status,
            null,
            HostileShadowSettlementRetryDisposition.CorrectedIntentMayRetry,
            string.Concat("hostile-shadow.settlement-", suffix)
        );
    }
}
