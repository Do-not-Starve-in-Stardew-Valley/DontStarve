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
    private readonly IHostileShadowRingSnapshotAuthority? ringSnapshotAuthority;
    private readonly IHostileShadowKillEffectAuthority? killEffectAuthority;
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
        IHostileShadowSanityRewardAuthority sanityAuthority,
        IHostileShadowRingSnapshotAuthority? ringSnapshotAuthority = null,
        IHostileShadowKillEffectAuthority? killEffectAuthority = null
    )
    {
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.dropAuthority = dropAuthority
            ?? throw new ArgumentNullException(nameof(dropAuthority));
        this.lastHitterAuthority = lastHitterAuthority
            ?? throw new ArgumentNullException(nameof(lastHitterAuthority));
        this.sanityAuthority = sanityAuthority
            ?? throw new ArgumentNullException(nameof(sanityAuthority));
        this.ringSnapshotAuthority = ringSnapshotAuthority;
        this.killEffectAuthority = killEffectAuthority;
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
            return ringSnapshotAuthority is not null && killEffectAuthority is not null
                ? SettleReservedWithRingEffects(request)
                : SettleReservedLegacy(request);
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

    private HostileShadowSettlementResult SettleReservedWithRingEffects(
        HostileShadowSettlementRequest request
    )
    {
        var key = request.Key;
        var seed = HostileShadowSettlementSeed.Create(key);
        if (!TryNextRoll(seed, out var roll, out var rollReason))
        {
            return RecordFailure(
                request,
                seed,
                roll,
                0,
                HostileShadowDropSpawnReceipt.Rejected(rollReason),
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

        var baseQuantity = CalculateDropQuantity(request, roll);
        var stacks = new List<HostileShadowDropStack>
        {
            new(request.ItemSemanticId, baseQuantity),
        };
        var hitter = HostileShadowLastHitterReceipt.NotEvaluated(
            "hostile-shadow.settlement-last-hitter-not-evaluated"
        );
        var ringSnapshot = HostileShadowRingSnapshotReceipt.NotEvaluated(
            "hostile-shadow.settlement-rings-not-evaluated"
        );

        if (request.LifecycleReceipt.RewardEligible)
        {
            hitter = ResolveLastHitter(request);
            if (hitter.IsValid)
            {
                ringSnapshot = ResolveRingSnapshot(request, hitter.PlayerKey);
            }
        }

        if (
            !request.LifecycleReceipt.RewardEligible
            || !hitter.IsValid
            || !ringSnapshot.IsValid
        )
        {
            var earlyDrop = SpawnDrop(
                request,
                seed,
                roll,
                stacks,
                out var spawnReason
            );
            if (!earlyDrop.Spawned)
            {
                return RecordFailure(
                    request,
                    seed,
                    roll,
                    baseQuantity,
                    earlyDrop,
                    hitter,
                    HostileShadowSanityRewardReceipt.NotAttempted(
                        "hostile-shadow.settlement-sanity-not-attempted-after-drop-failure"
                    ),
                    HostileShadowSettlementReceiptStatus.Rejected,
                    spawnReason,
                    stacks,
                    ringSnapshot
                );
            }

            return RecordSuccess(
                request,
                seed,
                roll,
                baseQuantity,
                earlyDrop,
                hitter,
                HostileShadowSanityRewardReceipt.NotAttempted(
                    hitter.IsValid
                        ? "hostile-shadow.settlement-sanity-skipped-rings-unavailable"
                        : "hostile-shadow.settlement-sanity-skipped-invalid-last-hitter"
                ),
                hitter.IsValid
                    ? "hostile-shadow.settlement-drop-only-rings-unavailable"
                    : "hostile-shadow.settlement-drop-only-last-hitter-invalid",
                stacks,
                ringSnapshot
            );
        }

        var snapshot = ringSnapshot.Snapshot;
        var warriorTriggers = 0;
        var vampireRings = 0;
        var savageRings = 0;
        var soulSapperRings = 0;
        var napalmRings = 0;
        var ringIndex = 0;
        foreach (var ringId in snapshot.EnumerateRingIds())
        {
            switch (ringId)
            {
                case HostileShadowVanillaRingIds.HotJava:
                    if (
                        !TryNextRoll(
                            HostileShadowSettlementSeed.CreateDerived(
                                key,
                                string.Concat("hot-java:", ringIndex, ":coffee")
                            ),
                            out var coffeeRoll,
                            out rollReason
                        )
                    )
                    {
                        return RingRollFailure(
                            request,
                            seed,
                            roll,
                            baseQuantity,
                            stacks,
                            hitter,
                            ringSnapshot,
                            rollReason
                        );
                    }
                    if (coffeeRoll < 2500)
                    {
                        stacks.Add(
                            new HostileShadowDropStack(
                                HostileShadowSettlementItemSemanticIds.Coffee,
                                1
                            )
                        );
                    }
                    else if (
                        !TryNextRoll(
                            HostileShadowSettlementSeed.CreateDerived(
                                key,
                                string.Concat("hot-java:", ringIndex, ":espresso")
                            ),
                            out var espressoRoll,
                            out rollReason
                        )
                    )
                    {
                        return RingRollFailure(
                            request,
                            seed,
                            roll,
                            baseQuantity,
                            stacks,
                            hitter,
                            ringSnapshot,
                            rollReason
                        );
                    }
                    else if (espressoRoll < 1000)
                    {
                        stacks.Add(
                            new HostileShadowDropStack(
                                HostileShadowSettlementItemSemanticIds.TripleShotEspresso,
                                1
                            )
                        );
                    }
                    break;

                case HostileShadowVanillaRingIds.Warrior:
                    if (
                        !TryNextRoll(
                            HostileShadowSettlementSeed.CreateDerived(
                                key,
                                string.Concat("warrior:", ringIndex)
                            ),
                            out var warriorRoll,
                            out rollReason
                        )
                    )
                    {
                        return RingRollFailure(
                            request,
                            seed,
                            roll,
                            baseQuantity,
                            stacks,
                            hitter,
                            ringSnapshot,
                            rollReason
                        );
                    }
                    if (
                        warriorRoll / (double)BonusRollScale
                        < 0.1d + snapshot.LuckLevel / 100d
                    )
                    {
                        warriorTriggers++;
                    }
                    break;

                case HostileShadowVanillaRingIds.Vampire:
                    vampireRings++;
                    break;
                case HostileShadowVanillaRingIds.Savage:
                    savageRings++;
                    break;
                case HostileShadowVanillaRingIds.SoulSapper:
                    soulSapperRings++;
                    break;
                case HostileShadowVanillaRingIds.Napalm:
                    napalmRings++;
                    break;
            }
            ringIndex++;
        }

        if (snapshot.HasBurglarRing)
        {
            if (
                !TryNextRoll(
                    HostileShadowSettlementSeed.CreateDerived(key, "burglar"),
                    out var burglarRoll,
                    out rollReason
                )
            )
            {
                return RingRollFailure(
                    request,
                    seed,
                    roll,
                    baseQuantity,
                    stacks,
                    hitter,
                    ringSnapshot,
                    rollReason
                );
            }
            if ((burglarRoll / (double)BonusRollScale) < request.BonusChance)
            {
                stacks.Add(new HostileShadowDropStack(
                    request.ItemSemanticId,
                    request.GuaranteedQuantity + request.BonusQuantity
                ));
            }
            else
            {
                // Burglar's Ring reruns the complete base table. A failed optional roll does not
                // suppress the table's guaranteed part.
                stacks.Add(new HostileShadowDropStack(
                    request.ItemSemanticId,
                    request.GuaranteedQuantity
                ));
            }
        }

        if (snapshot.HasMonsterBook)
        {
            if (
                !TryNextRoll(
                    HostileShadowSettlementSeed.CreateDerived(key, "monster-book"),
                    out var bookRoll,
                    out rollReason
                )
            )
            {
                return RingRollFailure(
                    request,
                    seed,
                    roll,
                    baseQuantity,
                    stacks,
                    hitter,
                    ringSnapshot,
                    rollReason
                );
            }
            if (bookRoll < 300)
            {
                var copy = stacks.ToArray();
                stacks.AddRange(copy);
            }
        }

        var drop = SpawnDrop(request, seed, roll, stacks, out var dropReason);
        if (!drop.Spawned)
        {
            return RecordFailure(
                request,
                seed,
                roll,
                SumQuantities(stacks),
                drop,
                hitter,
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-not-attempted-after-drop-failure"
                ),
                HostileShadowSettlementReceiptStatus.Rejected,
                dropReason,
                stacks,
                ringSnapshot
            );
        }

        var killEffects = ApplyKillEffects(
            request,
            hitter.PlayerKey,
            vampireRings * 2,
            soulSapperRings * 4,
            warriorTriggers,
            savageRings,
            napalmRings
        );
        if (!IsValidKillEffectReceipt(killEffects, request, hitter.PlayerKey))
        {
            return RecordFailure(
                request,
                seed,
                roll,
                SumQuantities(stacks),
                drop,
                hitter,
                HostileShadowSanityRewardReceipt.NotAttempted(
                    "hostile-shadow.settlement-sanity-not-attempted-after-kill-effects-failure"
                ),
                HostileShadowSettlementReceiptStatus.PartialFailure,
                "hostile-shadow.settlement-kill-effects-failed-after-drop",
                stacks,
                ringSnapshot,
                killEffects
            );
        }

        var sanity = ApplySanityReward(request, hitter);
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
                SumQuantities(stacks),
                drop,
                hitter,
                sanity,
                HostileShadowSettlementReceiptStatus.PartialFailure,
                "hostile-shadow.settlement-sanity-failed-after-drop",
                stacks,
                ringSnapshot,
                killEffects
            );
        }

        return RecordSuccess(
            request,
            seed,
            roll,
            SumQuantities(stacks),
            drop,
            hitter,
            sanity,
            "hostile-shadow.settlement-completed-with-ring-effects",
            stacks,
            ringSnapshot,
            killEffects
        );
    }

    private HostileShadowLastHitterReceipt ResolveLastHitter(
        HostileShadowSettlementRequest request
    )
    {
        HostileShadowLastHitterReceipt hitter;
        try
        {
            hitter = lastHitterAuthority.Resolve(
                new HostileShadowLastHitterRequest(
                    request.Key,
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
            return HostileShadowLastHitterReceipt.Invalid(
                string.IsNullOrWhiteSpace(hitter.Reason)
                    ? "hostile-shadow.settlement-last-hitter-receipt-invalid"
                    : hitter.Reason
            );
        }
        if (
            !string.Equals(
                hitter.PlayerKey,
                request.LifecycleReceipt.AttributedPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return HostileShadowLastHitterReceipt.Invalid(
                "hostile-shadow.settlement-last-hitter-player-mismatch"
            );
        }
        return hitter;
    }

    private HostileShadowRingSnapshotReceipt ResolveRingSnapshot(
        HostileShadowSettlementRequest request,
        string playerKey
    )
    {
        try
        {
            var receipt = ringSnapshotAuthority!.Resolve(
                new HostileShadowRingSnapshotRequest(
                    request.Key,
                    playerKey,
                    request.LocationId
                )
            );
            return receipt.IsValid
                ? receipt
                : HostileShadowRingSnapshotReceipt.Invalid(
                    string.IsNullOrWhiteSpace(receipt.Reason)
                        ? "hostile-shadow.settlement-rings-receipt-invalid"
                        : receipt.Reason
                );
        }
        catch (Exception exception)
        {
            return HostileShadowRingSnapshotReceipt.Invalid(
                string.Concat(
                    "hostile-shadow.settlement-ring-authority-threw-",
                    exception.GetType().Name
                )
            );
        }
    }

    private HostileShadowDropSpawnReceipt SpawnDrop(
        HostileShadowSettlementRequest request,
        int seed,
        int roll,
        IReadOnlyList<HostileShadowDropStack> stacks,
        out string reason
    )
    {
        var dropRequest = new HostileShadowDropSpawnRequest(
            request.Key,
            request.Key.SettlementId,
            request.DropTableId,
            request.ItemSemanticId,
            CalculateDropQuantity(request, roll),
            seed,
            roll,
            request.LocationId,
            request.PositionX,
            request.PositionY
        )
        {
            DropStacks = stacks,
        };
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
            reason = string.IsNullOrWhiteSpace(drop.Reason)
                ? "hostile-shadow.settlement-drop-receipt-invalid"
                : drop.Reason;
            return HostileShadowDropSpawnReceipt.Rejected(reason);
        }
        reason = "hostile-shadow.settlement-drop-spawned";
        return drop;
    }

    private HostileShadowKillEffectReceipt ApplyKillEffects(
        HostileShadowSettlementRequest request,
        string playerKey,
        int vampireHealth,
        int soulSapperEnergy,
        int warriorTriggerCount,
        int savageTriggerCount,
        int napalmExplosionCount
    )
    {
        try
        {
            return killEffectAuthority!.Apply(
                new HostileShadowKillEffectRequest(
                    request.Key,
                    request.Key.SettlementId,
                    playerKey,
                    request.LocationId,
                    request.PositionX,
                    request.PositionY,
                    request.ExplosionTileX,
                    request.ExplosionTileY,
                    vampireHealth,
                    soulSapperEnergy,
                    warriorTriggerCount,
                    savageTriggerCount,
                    napalmExplosionCount
                )
            );
        }
        catch (Exception exception)
        {
            return new HostileShadowKillEffectReceipt(
                HostileShadowKillEffectStatus.Rejected,
                playerKey,
                0,
                0,
                0,
                0,
                0,
                string.Concat(
                    "hostile-shadow.settlement-kill-effect-authority-threw-",
                    exception.GetType().Name
                )
            );
        }
    }

    private HostileShadowSanityRewardReceipt ApplySanityReward(
        HostileShadowSettlementRequest request,
        HostileShadowLastHitterReceipt hitter
    )
    {
        try
        {
            var sanity = sanityAuthority.Apply(
                new HostileShadowSanityRewardRequest(
                    request.Key,
                    request.Key.SettlementId,
                    hitter.PlayerKey,
                    request.LocationId,
                    request.SanityReward,
                    SanityChangeSource.HostileShadowKill
                )
            );
            if (!sanity.IsAccepted && string.IsNullOrWhiteSpace(sanity.Reason))
            {
                return new HostileShadowSanityRewardReceipt(
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
            return sanity;
        }
        catch (Exception exception)
        {
            return new HostileShadowSanityRewardReceipt(
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
    }

    private HostileShadowSettlementResult RingRollFailure(
        HostileShadowSettlementRequest request,
        int seed,
        int roll,
        int baseQuantity,
        IReadOnlyList<HostileShadowDropStack> stacks,
        HostileShadowLastHitterReceipt hitter,
        HostileShadowRingSnapshotReceipt ringSnapshot,
        string reason
    )
    {
        return RecordFailure(
            request,
            seed,
            roll,
            SumQuantities(stacks),
            HostileShadowDropSpawnReceipt.Rejected(reason),
            hitter,
            HostileShadowSanityRewardReceipt.NotAttempted(
                "hostile-shadow.settlement-sanity-not-attempted-after-rng-failure"
            ),
            HostileShadowSettlementReceiptStatus.Rejected,
            "hostile-shadow.settlement-rng-failed",
            stacks,
            ringSnapshot
        );
    }

    private bool TryNextRoll(int seed, out int roll, out string reason)
    {
        try
        {
            roll = random.NextBonusRoll10000(seed);
        }
        catch (Exception exception)
        {
            roll = -1;
            reason = string.Concat(
                "hostile-shadow.settlement-rng-threw-",
                exception.GetType().Name
            );
            return false;
        }
        if (roll < 0 || roll >= BonusRollScale)
        {
            reason = "hostile-shadow.settlement-rng-roll-invalid";
            return false;
        }
        reason = "hostile-shadow.settlement-rng-roll-valid";
        return true;
    }

    private static int CalculateDropQuantity(
        HostileShadowSettlementRequest request,
        int roll
    )
    {
        return request.GuaranteedQuantity
            + (
                roll / (double)BonusRollScale < request.BonusChance
                    ? request.BonusQuantity
                    : 0
            );
    }

    private static int SumQuantities(IReadOnlyList<HostileShadowDropStack> stacks)
    {
        var total = 0;
        foreach (var stack in stacks)
            total += stack.Quantity;
        return total;
    }

    private static bool IsValidKillEffectReceipt(
        HostileShadowKillEffectReceipt receipt,
        HostileShadowSettlementRequest request,
        string playerKey
    )
    {
        return receipt.IsAccepted
            && string.Equals(receipt.PlayerKey, playerKey, StringComparison.Ordinal)
        ;
    }

    private HostileShadowSettlementResult SettleReservedLegacy(
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
        string reason,
        IReadOnlyList<HostileShadowDropStack>? stacks = null,
        HostileShadowRingSnapshotReceipt? ringSnapshot = null,
        HostileShadowKillEffectReceipt? killEffects = null
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
            reason,
            stacks,
            ringSnapshot,
            killEffects
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
        string reason,
        IReadOnlyList<HostileShadowDropStack>? stacks = null,
        HostileShadowRingSnapshotReceipt? ringSnapshot = null,
        HostileShadowKillEffectReceipt? killEffects = null
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
            reason,
            stacks,
            ringSnapshot,
            killEffects
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
        string reason,
        IReadOnlyList<HostileShadowDropStack>? stacks,
        HostileShadowRingSnapshotReceipt? ringSnapshot,
        HostileShadowKillEffectReceipt? killEffects
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
        )
        {
            PlannedDropStacks = stacks is null
                ? Array.Empty<HostileShadowDropStack>()
                : new List<HostileShadowDropStack>(stacks),
            RingSnapshot = ringSnapshot
                ?? HostileShadowRingSnapshotReceipt.NotEvaluated(
                    "hostile-shadow.settlement-rings-not-evaluated"
                ),
            KillEffects = killEffects
                ?? HostileShadowKillEffectReceipt.NotAttempted(
                    "hostile-shadow.settlement-kill-effects-not-attempted"
                ),
        };
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
