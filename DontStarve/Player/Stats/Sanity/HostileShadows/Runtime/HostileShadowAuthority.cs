#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Session-only host truth for shared hostile-shadow identity and transport state. It deliberately
/// owns no targeting, attack, reward, persistence, config transport, or owner-local projection.
/// </summary>
internal sealed class HostileShadowAuthority
{
    internal const int MaximumEntities = HostileShadowProtocol.MaximumEntitiesPerSnapshot;
    internal const int MaximumSpawnReceipts = 256;

    private sealed class ConversionEpoch
    {
        internal ConversionEpoch(long tierRevision)
        {
            TierRevision = tierRevision;
        }

        internal long TierRevision { get; }
        internal bool Consumed { get; set; }
    }

    private sealed record SpawnReceipt(
        string OwnerPlayerKey,
        HostileShadowSpawnResult Result
    );

    private readonly IHostileShadowBudgetAuthority budget;
    private readonly IHostileShadowEntityIdSource entityIds;
    private readonly HostileShadowPhysicalEntityCapability physicalEntityCapability;
    private readonly Dictionary<long, ShadowStateSnapshot> entities = new();
    private readonly Dictionary<string, HashSet<long>> entityIdsByOwner =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpawnReceipt> receipts =
        new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    private readonly Dictionary<string, ConversionEpoch> conversionEpochs =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> eventBlockedOwners =
        new(StringComparer.Ordinal);
    private bool hostSessionActive;
    private bool enabled;

    internal HostileShadowAuthority(
        IHostileShadowBudgetAuthority budget,
        IHostileShadowEntityIdSource entityIds,
        HostileShadowPhysicalEntityCapability? physicalEntityCapability = null
    )
    {
        this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        this.entityIds = entityIds ?? throw new ArgumentNullException(nameof(entityIds));
        this.physicalEntityCapability =
            physicalEntityCapability
            ?? HostileShadowPhysicalEntityCapability.Unverified;
    }

    internal HostileShadowPhysicalEntityCapability PhysicalEntityCapability =>
        physicalEntityCapability;

    internal string SessionId { get; private set; } = string.Empty;
    internal long Revision { get; private set; }
    internal int Count => entities.Count;

    /// <summary>DIAG-20260810: 指定 owner 的在册实体数（统一上限池超限清理用）。</summary>
    internal int CountForOwner(string playerKey)
    {
        return entityIdsByOwner.TryGetValue(playerKey, out var ids)
            ? ids.Count
            : 0;
    }
    internal bool IsEnabled => enabled;
    internal bool IsHostSessionActive => hostSessionActive;

    internal event Action<ShadowStateDeltaMessage>? DeltaProduced;

    internal bool BeginHostSession(
        string sessionId,
        bool systemEnabled,
        out string reason
    )
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "hostile-shadow.session-id-invalid";
            return false;
        }
        if (
            hostSessionActive
            && string.Equals(SessionId, sessionId, StringComparison.Ordinal)
        )
        {
            SetEnabled(systemEnabled);
            reason = "hostile-shadow.session-already-active";
            return true;
        }

        ResetSessionState();
        SessionId = sessionId;
        hostSessionActive = true;
        enabled = systemEnabled;
        reason = systemEnabled
            ? "hostile-shadow.host-session-started"
            : "hostile-shadow.host-session-started-disabled";
        return true;
    }

    internal void EndSession(string reason)
    {
        if (hostSessionActive && entities.Count > 0)
            CleanupAll(reason);
        ResetSessionState();
    }

    internal int SetEnabled(bool value)
    {
        enabled = value;
        if (value)
            return 0;
        conversionEpochs.Clear();
        eventBlockedOwners.Clear();
        return CleanupAll(HostileShadowCleanupReasonIds.SystemDisabled);
    }

    internal void ObserveStateEvent(SanityStateEvent stateEvent)
    {
        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemEnabled:
                enabled = true;
                break;
            case SanityStateEventKind.SystemDisabled:
                SetEnabled(false);
                break;
            case SanityStateEventKind.TierEntered
                when string.Equals(
                    stateEvent.TierId,
                    SanityTierIds.Danger,
                    StringComparison.Ordinal
                ):
                if (SanityPlayerKey.IsCanonical(stateEvent.PlayerKey))
                {
                    if (
                        !conversionEpochs.TryGetValue(
                            stateEvent.PlayerKey,
                            out var epoch
                        )
                        || epoch.TierRevision != stateEvent.Revision
                    )
                    {
                        conversionEpochs[stateEvent.PlayerKey] =
                            new ConversionEpoch(stateEvent.Revision);
                    }
                }
                break;
            case SanityStateEventKind.TierExited
                when string.Equals(
                    stateEvent.TierId,
                    SanityTierIds.Danger,
                    StringComparison.Ordinal
                ):
                // DIAG-20260811: 解除危险状态（san>17.5%）不再清空实体——危险影怪继续
                // 攻击（饥荒原版），由宿主脱战系统接管（每 10 游戏分钟 25% roll）。
                // 此前直接 CleanupOwner(DangerExited) 会在快速拉高理智时先于恐吓流程
                // 清掉全部实体，导致“直接消失、无恐吓动画、无绑定投影”。
                conversionEpochs.Remove(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.OwnerInvalidated:
                ForgetOwner(
                    stateEvent.PlayerKey,
                    HostileShadowCleanupReasonIds.OwnerDisconnected
                );
                break;
            case SanityStateEventKind.WorldCleanup:
                CleanupAll(HostileShadowCleanupReasonIds.WorldCleanup);
                conversionEpochs.Clear();
                eventBlockedOwners.Clear();
                break;
        }
    }

    internal int SetEventOverride(string ownerPlayerKey, bool active)
    {
        if (!SanityPlayerKey.IsCanonical(ownerPlayerKey))
            return 0;
        if (!active)
        {
            eventBlockedOwners.Remove(ownerPlayerKey);
            return 0;
        }

        eventBlockedOwners.Add(ownerPlayerKey);
        return CleanupOwner(
            ownerPlayerKey,
            HostileShadowCleanupReasonIds.EventOverride
        );
    }

    internal HostileShadowSpawnResult TrySpawn(
        HostileShadowSpawnCommand? command,
        SanityShadowBudgetEvaluationResult? preEvaluatedBudget = null
    )
    {
        if (!TryValidateCommand(command, out var validationReason))
            return Failure(HostileShadowSpawnStatus.Rejected, validationReason);

        if (receipts.TryGetValue(command!.RequestId, out var receipt))
        {
            return receipt.Result with
            {
                Status = HostileShadowSpawnStatus.Duplicate,
                Reason = "hostile-shadow.spawn-request-duplicate",
            };
        }
        if (!hostSessionActive)
        {
            return Record(
                command,
                Failure(
                    HostileShadowSpawnStatus.Unavailable,
                    "hostile-shadow.host-session-inactive"
                )
            );
        }
        if (!physicalEntityCapability.IsAvailable)
        {
            return Record(
                command,
                Failure(
                    HostileShadowSpawnStatus.Unavailable,
                    physicalEntityCapability.Reason
                )
            );
        }
        if (!enabled)
        {
            return Record(
                command,
                Failure(
                    HostileShadowSpawnStatus.Inactive,
                    "hostile-shadow.system-disabled"
                )
            );
        }
        if (eventBlockedOwners.Contains(command.OwnerPlayerKey))
        {
            return Record(
                command,
                Failure(
                    HostileShadowSpawnStatus.Inactive,
                    "hostile-shadow.owner-event-override-active"
                )
            );
        }
        if (entities.Count >= MaximumEntities)
        {
            return Record(
                command,
                Failure(
                    HostileShadowSpawnStatus.Unavailable,
                    "hostile-shadow.entity-capacity-reached"
                )
            );
        }

        if (
            !HostileShadowSpeciesBindingPolicy.TryResolveSpecies(
                command.Profile.AssetBindingId,
                out var requestedSpecies,
                out var speciesReason
            )
        )
        {
            return Record(
                command,
                Failure(HostileShadowSpawnStatus.Rejected, speciesReason)
            );
        }

        var occupancy = GetOwnerOccupancy(command.OwnerPlayerKey);
        var isDebugCommand = command.Origin == HostileShadowSpawnOrigin.DebugCommand;
        SanityShadowBudgetEvaluationResult evaluation;
        if (isDebugCommand)
        {
            evaluation = new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.PermitGranted,
                "hostile-shadow.debug-command-authorized",
                command.OwnerPlayerKey,
                SanityShadowPoolTier.Hostile15,
                string.Empty,
                occupancy,
                int.MaxValue,
                0,
                command.GameMinute,
                null
            );
        }
        else
        {
            try
            {
                evaluation = preEvaluatedBudget
                    ?? budget.Evaluate(
                        command.OwnerPlayerKey,
                        command.GameMinute,
                        occupancy,
                        requestedSpecies
                    );
            }
            catch (Exception exception)
            {
                return Record(
                    command,
                    Failure(
                        HostileShadowSpawnStatus.Unavailable,
                        string.Concat(
                            "hostile-shadow.budget-threw-",
                            exception.GetType().Name
                        ),
                        occupancy: occupancy
                    )
                );
            }
        }

        if (!isDebugCommand &&
            evaluation.PoolTier is not SanityShadowPoolTier.Hostile15
                and not SanityShadowPoolTier.Hostile10
        )
        {
            return Record(
                command,
                FromBudget(
                    HostileShadowSpawnStatus.Inactive,
                    "hostile-shadow.owner-not-in-danger-tier",
                    evaluation
                )
            );
        }
        if (!isDebugCommand &&
            evaluation.Status
            == SanityShadowBudgetEvaluationStatus.SpeciesIneligible
        )
        {
            return Record(
                command,
                FromBudget(
                    HostileShadowSpawnStatus.Rejected,
                    evaluation.Reason,
                    evaluation
                )
            );
        }
        if (!isDebugCommand && (occupancy >= evaluation.Cap || evaluation.Cap <= 0))
        {
            var status = evaluation.Status == SanityShadowBudgetEvaluationStatus.Unavailable
                ? HostileShadowSpawnStatus.Unavailable
                : evaluation.Cap <= 0
                    ? HostileShadowSpawnStatus.Inactive
                    : HostileShadowSpawnStatus.AtCap;
            return Record(command, FromBudget(status, evaluation.Reason, evaluation));
        }

        if (command.Origin == HostileShadowSpawnOrigin.OwnerProjectionConversion)
        {
            // DIAG-20260812: 转化不再受 epoch 一次性限制——每个 Danger 档期场上存量/新刷
            // 的每只无害投影都应能 1:1 转化为危险影怪，重复提交由协调器 evidence 去重。
            if (!conversionEpochs.TryGetValue(command.OwnerPlayerKey, out var epoch))
            {
                return Record(
                    command,
                    FromBudget(
                        HostileShadowSpawnStatus.Rejected,
                        "hostile-shadow.conversion-epoch-unavailable-or-consumed",
                        evaluation
                    )
                );
            }
            if (
                evaluation.Status is not SanityShadowBudgetEvaluationStatus.PermitGranted
                    and not SanityShadowBudgetEvaluationStatus.Waiting
            )
            {
                return Record(
                    command,
                    FromBudget(MapBudgetStatus(evaluation.Status), evaluation.Reason, evaluation)
                );
            }
        }
        else if (!isDebugCommand && evaluation.Status != SanityShadowBudgetEvaluationStatus.PermitGranted)
        {
            return Record(
                command,
                FromBudget(MapBudgetStatus(evaluation.Status), evaluation.Reason, evaluation)
            );
        }

        long entityId;
        try
        {
            entityId = entityIds.Next();
        }
        catch (Exception exception)
        {
            return Record(
                command,
                FromBudget(
                    HostileShadowSpawnStatus.Unavailable,
                    string.Concat(
                        "hostile-shadow.entity-id-source-threw-",
                        exception.GetType().Name
                    ),
                    evaluation
                )
            );
        }
        if (entityId <= 0 || entities.ContainsKey(entityId))
        {
            return Record(
                command,
                FromBudget(
                    HostileShadowSpawnStatus.Unavailable,
                    "hostile-shadow.entity-id-invalid-or-duplicate",
                    evaluation
                )
            );
        }
        if (!TryNextRevision(out var nextRevision))
        {
            return Record(
                command,
                FromBudget(
                    HostileShadowSpawnStatus.Unavailable,
                    "hostile-shadow.revision-overflow",
                    evaluation
                )
            );
        }

        var state = new ShadowStateSnapshot
        {
            EntityId = entityId,
            OwnerPlayerKey = command.OwnerPlayerKey,
            LocationId = command.LocationId,
            DifficultyProfileId = command.Profile.DifficultyProfileId,
            AssetBindingId = command.Profile.AssetBindingId,
            // Conversion replaces an already-visible harmless projection, so its hostile
            // counterpart must enter the one-time Taunt presentation directly. Standalone
            // interval/debug spawns retain the normal hostile Spawn animation.
            StateId = command.Origin == HostileShadowSpawnOrigin.OwnerProjectionConversion
                ? HostileShadowStateIds.Taunt
                : HostileShadowStateIds.Spawn,
            TargetPlayerKey = string.Empty,
            PositionX = command.PositionX,
            PositionY = command.PositionY,
            Health = command.Profile.MaxHealth,
            MaxHealth = command.Profile.MaxHealth,
            AttackInstanceId = string.Empty,
            AttackInstanceRevision = 0,
            AttackFrameNumber = 0,
            Revision = nextRevision,
        };
        entities.Add(entityId, state);
        GetOrCreateOwnerIndex(command.OwnerPlayerKey).Add(entityId);
        Revision = nextRevision;

        if (command.Origin == HostileShadowSpawnOrigin.OwnerProjectionConversion)
        {
            conversionEpochs[command.OwnerPlayerKey].Consumed = true;
        }

        Emit(
            ShadowStateDeltaKind.Spawned,
            state,
            command.Reason
        );
        return Record(
            command,
            new HostileShadowSpawnResult(
                HostileShadowSpawnStatus.Spawned,
                "hostile-shadow.spawned",
                entityId,
                evaluation.Status,
                occupancy,
                evaluation.Cap
            )
        );
    }

    internal bool TryUpdate(
        HostileShadowStateUpdate? update,
        out string reason
    )
    {
        if (
            update is null
            || !hostSessionActive
            || !enabled
            || update.EntityId <= 0
            || string.IsNullOrWhiteSpace(update.LocationId)
            || string.IsNullOrWhiteSpace(update.StateId)
            || update.TargetPlayerKey is null
            || update.AttackInstanceId is null
            || (
                update.TargetPlayerKey.Length > 0
                && !SanityPlayerKey.IsCanonical(update.TargetPlayerKey)
            )
            || string.IsNullOrWhiteSpace(update.Reason)
            || !double.IsFinite(update.PositionX)
            || !double.IsFinite(update.PositionY)
            || !entities.TryGetValue(update.EntityId, out var current)
            || update.Health < 0
            || update.Health > current.MaxHealth
            || !HostileShadowStateIds.IsKnown(update.StateId)
            || !HostileShadowStateIds.IsHealthValid(update.StateId, update.Health)
            || !IsValidAttackUpdate(update)
        )
        {
            reason = "hostile-shadow.update-invalid-or-unavailable";
            return false;
        }
        if (
            string.Equals(current.LocationId, update.LocationId, StringComparison.Ordinal)
            && string.Equals(current.StateId, update.StateId, StringComparison.Ordinal)
            && string.Equals(
                current.TargetPlayerKey,
                update.TargetPlayerKey,
                StringComparison.Ordinal
            )
            && SameDouble(current.PositionX, update.PositionX)
            && SameDouble(current.PositionY, update.PositionY)
            && current.Health == update.Health
            && string.Equals(
                current.AttackInstanceId,
                update.AttackInstanceId,
                StringComparison.Ordinal
            )
            && current.AttackInstanceRevision == update.AttackInstanceRevision
            && current.AttackFrameNumber == update.AttackFrameNumber
        )
        {
            reason = "hostile-shadow.update-duplicate";
            return true;
        }
        if (!TryNextRevision(out var nextRevision))
        {
            reason = "hostile-shadow.revision-overflow";
            return false;
        }
        if (
            string.Equals(
                update.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && (
                string.Equals(
                    current.StateId,
                    HostileShadowStateIds.Attack,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    current.AttackInstanceId,
                    update.AttackInstanceId,
                    StringComparison.Ordinal
                )
                    ? update.AttackInstanceRevision
                        != current.AttackInstanceRevision
                    : update.AttackInstanceRevision != nextRevision
            )
        )
        {
            reason = "hostile-shadow.attack-instance-revision-invalid";
            return false;
        }

        current.LocationId = update.LocationId;
        current.StateId = update.StateId;
        current.TargetPlayerKey = update.TargetPlayerKey;
        current.PositionX = update.PositionX;
        current.PositionY = update.PositionY;
        current.Health = update.Health;
        current.AttackInstanceId = update.AttackInstanceId;
        current.AttackInstanceRevision = update.AttackInstanceRevision;
        current.AttackFrameNumber = update.AttackFrameNumber;
        current.Revision = nextRevision;
        Revision = nextRevision;
        Emit(ShadowStateDeltaKind.Updated, current, update.Reason);
        reason = "hostile-shadow.updated";
        return true;
    }

    /// <summary>
    /// DIAG-20260809: 设置实体绑定隐藏状态（脱战隐藏/低理智恢复）。事件性变更，走独立
    /// 方法与 TryUpdate 分离（不污染常规状态更新）；delta 同步携带隐藏/绑定/朝向字段。
    /// </summary>
    internal bool SetBindingState(
        long entityId,
        bool isHidden,
        string correlationId,
        string facingId,
        out string reason
    )
    {
        if (
            !hostSessionActive
            || !enabled
            || !entities.TryGetValue(entityId, out var state)
        )
        {
            reason = "hostile-shadow.binding-state-unavailable";
            return false;
        }
        if (
            state.IsBindingHidden == isHidden
            && string.Equals(
                state.BindingCorrelationId,
                correlationId ?? string.Empty,
                StringComparison.Ordinal
            )
            && string.Equals(
                state.FacingId,
                facingId ?? string.Empty,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.binding-state-duplicate";
            return true;
        }
        if (!TryNextRevision(out var nextRevision))
        {
            reason = "hostile-shadow.revision-overflow";
            return false;
        }
        state.IsBindingHidden = isHidden;
        state.BindingCorrelationId = correlationId ?? string.Empty;
        state.FacingId = facingId ?? string.Empty;
        state.Revision = nextRevision;
        Revision = nextRevision;
        Emit(
            ShadowStateDeltaKind.Updated,
            state,
            "hostile-shadow.binding-state-changed"
        );
        reason = "hostile-shadow.binding-state-updated";
        return true;
    }

    internal bool CleanupEntity(long entityId, string reason)
    {
        if (!entities.TryGetValue(entityId, out var state))
            return false;
        return RemoveEntity(state, reason);
    }

    internal HostileShadowSpawnResult FailSpawnMaterialization(
        string requestId,
        long entityId,
        string reason
    )
    {
        if (
            !IsValidIdentifier(requestId)
            || !receipts.TryGetValue(requestId, out var receipt)
            || !entities.TryGetValue(entityId, out var state)
            || !string.Equals(
                receipt.OwnerPlayerKey,
                state.OwnerPlayerKey,
                StringComparison.Ordinal
            )
            || !RemoveEntity(
                state,
                HostileShadowCleanupReasonIds.PhysicalMaterializationFailed
            )
        )
        {
            return Failure(
                HostileShadowSpawnStatus.Unavailable,
                "hostile-shadow.physical-materialization-rollback-failed"
            );
        }

        var failed = receipt.Result with
        {
            Status = HostileShadowSpawnStatus.Unavailable,
            Reason = NormalizeReason(reason),
            EntityId = null,
        };
        receipts[requestId] = new SpawnReceipt(
            receipt.OwnerPlayerKey,
            failed
        );
        return failed;
    }

    internal int CleanupOwner(string ownerPlayerKey, string reason)
    {
        if (
            !SanityPlayerKey.IsCanonical(ownerPlayerKey)
            || !entityIdsByOwner.TryGetValue(ownerPlayerKey, out var ownerIds)
        )
        {
            return 0;
        }

        var ordered = new List<long>(ownerIds);
        ordered.Sort();
        var removed = 0;
        foreach (var entityId in ordered)
        {
            if (entities.TryGetValue(entityId, out var state))
                removed += RemoveEntity(state, reason) ? 1 : 0;
        }
        return removed;
    }

    internal int CleanupAll(string reason)
    {
        if (entities.Count == 0)
            return 0;
        var ordered = new List<long>(entities.Keys);
        ordered.Sort();
        var removed = 0;
        foreach (var entityId in ordered)
        {
            if (entities.TryGetValue(entityId, out var state))
                removed += RemoveEntity(state, reason) ? 1 : 0;
        }
        return removed;
    }

    internal int ForgetOwner(string ownerPlayerKey, string reason)
    {
        var removed = CleanupOwner(ownerPlayerKey, reason);
        conversionEpochs.Remove(ownerPlayerKey);
        eventBlockedOwners.Remove(ownerPlayerKey);

        if (receipts.Count > 0)
        {
            var remove = new List<string>();
            foreach (var pair in receipts)
            {
                if (string.Equals(
                    pair.Value.OwnerPlayerKey,
                    ownerPlayerKey,
                    StringComparison.Ordinal
                ))
                {
                    remove.Add(pair.Key);
                }
            }
            foreach (var requestId in remove)
                receipts.Remove(requestId);
        }
        return removed;
    }

    internal ShadowStateSnapshotMessage CreateFullSnapshot()
    {
        var message = new ShadowStateSnapshotMessage
        {
            SessionId = SessionId,
            Revision = Revision,
        };
        var ordered = new List<long>(entities.Keys);
        ordered.Sort();
        foreach (var entityId in ordered)
            message.Entities.Add(entities[entityId].Clone());
        return message;
    }

    internal bool TryGetEntity(long entityId, out ShadowStateSnapshot? state)
    {
        state = null;
        if (!entities.TryGetValue(entityId, out var current))
            return false;
        state = current.Clone();
        return true;
    }

    internal int GetOwnerOccupancy(string ownerPlayerKey)
    {
        return entityIdsByOwner.TryGetValue(ownerPlayerKey, out var ids)
            ? ids.Count
            : 0;
    }

    internal bool TryGetConversionEpochRevision(
        string ownerPlayerKey,
        out long revision
    )
    {
        if (
            conversionEpochs.TryGetValue(ownerPlayerKey, out var epoch)
        )
        {
            revision = epoch.TierRevision;
            return true;
        }
        revision = -1;
        return false;
    }

    /// <summary>
    /// Rehydrates the current Danger epoch from the Sanity tier snapshot after a session reset.
    /// Sanity publishes its initial tier events before the hostile-shadow SaveLoaded handler can
    /// begin this authority session, so event-only observation would lose the conversion gate.
    /// On a client this revision is only used to build the request envelope; the host remains the
    /// sole authority which accepts the conversion.
    /// </summary>
    internal bool SynchronizeDangerEpoch(
        string ownerPlayerKey,
        bool dangerActive,
        long tierRevision,
        out string reason
    )
    {
        if (!SanityPlayerKey.IsCanonical(ownerPlayerKey))
        {
            reason = "hostile-shadow.conversion-owner-key-invalid";
            return false;
        }
        if (tierRevision < 0)
        {
            reason = "hostile-shadow.conversion-tier-revision-invalid";
            return false;
        }

        if (!dangerActive)
        {
            conversionEpochs.Remove(ownerPlayerKey);
            reason = "hostile-shadow.conversion-danger-epoch-inactive";
            return true;
        }

        // The tier snapshot revision can advance while the player remains inside the same Danger
        // interval. Preserve the original interval revision so host and client envelopes keep the
        // same value; a real TierEntered event is the only operation that starts a new epoch.
        if (!conversionEpochs.ContainsKey(ownerPlayerKey))
        {
            conversionEpochs[ownerPlayerKey] = new ConversionEpoch(tierRevision);
            reason = "hostile-shadow.conversion-danger-epoch-synchronized";
            return true;
        }

        reason = "hostile-shadow.conversion-danger-epoch-already-current";
        return true;
    }

    private bool TryValidateCommand(
        HostileShadowSpawnCommand? command,
        out string reason
    )
    {
        if (
            command is null
            || !IsValidIdentifier(command.RequestId)
            || !SanityPlayerKey.IsCanonical(command.OwnerPlayerKey)
            || !IsValidIdentifier(command.LocationId)
            || !double.IsFinite(command.PositionX)
            || !double.IsFinite(command.PositionY)
            || command.GameMinute < 0
            || command.Profile is null
            || !IsValidIdentifier(command.Profile.DifficultyProfileId)
            || !IsValidIdentifier(command.Profile.AssetBindingId)
            || command.Profile.MaxHealth <= 0
            || !IsValidIdentifier(command.Reason)
        )
        {
            reason = "hostile-shadow.spawn-command-invalid";
            return false;
        }

        reason = "hostile-shadow.spawn-command-valid";
        return true;
    }

    private bool RemoveEntity(ShadowStateSnapshot state, string reason)
    {
        var transitionToDespawn = !string.Equals(
                state.StateId,
                HostileShadowStateIds.Dying,
                StringComparison.Ordinal
            )
            && !string.Equals(
                state.StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            );
        var requiredRevisions = transitionToDespawn ? 2L : 1L;
        if (Revision > long.MaxValue - requiredRevisions)
            return false;

        if (transitionToDespawn)
        {
            var despawnRevision = Revision + 1;
            state.StateId = HostileShadowStateIds.Despawn;
            state.TargetPlayerKey = string.Empty;
            state.AttackInstanceId = string.Empty;
            state.AttackInstanceRevision = 0;
            state.AttackFrameNumber = 0;
            state.Revision = despawnRevision;
            Revision = despawnRevision;
            Emit(ShadowStateDeltaKind.Updated, state, reason);
        }

        var nextRevision = Revision + 1;
        entities.Remove(state.EntityId);
        if (entityIdsByOwner.TryGetValue(state.OwnerPlayerKey, out var ownerIds))
        {
            ownerIds.Remove(state.EntityId);
            if (ownerIds.Count == 0)
                entityIdsByOwner.Remove(state.OwnerPlayerKey);
        }
        var baseRevision = Revision;
        Revision = nextRevision;
        DeltaProduced?.Invoke(
            new ShadowStateDeltaMessage
            {
                SessionId = SessionId,
                BaseRevision = baseRevision,
                Revision = Revision,
                Change = new ShadowStateDelta
                {
                    Kind = ShadowStateDeltaKind.Removed,
                    EntityId = state.EntityId,
                    State = null,
                    Reason = NormalizeReason(reason),
                    SettlementEligible = false,
                },
            }
        );
        return true;
    }

    private void Emit(
        ShadowStateDeltaKind kind,
        ShadowStateSnapshot state,
        string reason
    )
    {
        DeltaProduced?.Invoke(
            new ShadowStateDeltaMessage
            {
                SessionId = SessionId,
                BaseRevision = Revision - 1,
                Revision = Revision,
                Change = new ShadowStateDelta
                {
                    Kind = kind,
                    EntityId = state.EntityId,
                    State = state.Clone(),
                    Reason = NormalizeReason(reason),
                    SettlementEligible = false,
                },
            }
        );
    }

    private HashSet<long> GetOrCreateOwnerIndex(string ownerPlayerKey)
    {
        if (!entityIdsByOwner.TryGetValue(ownerPlayerKey, out var ids))
        {
            ids = new HashSet<long>();
            entityIdsByOwner.Add(ownerPlayerKey, ids);
        }
        return ids;
    }

    private HostileShadowSpawnResult Record(
        HostileShadowSpawnCommand command,
        HostileShadowSpawnResult result
    )
    {
        // A successful spawn needs an idempotent receipt so replay cannot materialize a second
        // entity. Failed/rejected capacity, budget, or capability checks are retryable state
        // observations; caching them would turn a later valid retry with the same conversion
        // correlation into a permanent Duplicate-without-entity rejection.
        if (result.Status != HostileShadowSpawnStatus.Spawned)
            return result;

        while (receipts.Count >= MaximumSpawnReceipts && receiptOrder.Count > 0)
        {
            var oldest = receiptOrder.Dequeue();
            receipts.Remove(oldest);
        }
        receipts[command.RequestId] = new SpawnReceipt(
            command.OwnerPlayerKey,
            result
        );
        receiptOrder.Enqueue(command.RequestId);
        return result;
    }

    private void ResetSessionState()
    {
        entities.Clear();
        entityIdsByOwner.Clear();
        receipts.Clear();
        receiptOrder.Clear();
        conversionEpochs.Clear();
        eventBlockedOwners.Clear();
        SessionId = string.Empty;
        Revision = 0;
        hostSessionActive = false;
        enabled = false;
    }

    private bool TryNextRevision(out long revision)
    {
        if (Revision == long.MaxValue)
        {
            revision = Revision;
            return false;
        }
        revision = Revision + 1;
        return true;
    }

    private static HostileShadowSpawnStatus MapBudgetStatus(
        SanityShadowBudgetEvaluationStatus status
    )
    {
        return status switch
        {
            SanityShadowBudgetEvaluationStatus.Waiting =>
                HostileShadowSpawnStatus.Waiting,
            SanityShadowBudgetEvaluationStatus.PausedAtCap =>
                HostileShadowSpawnStatus.AtCap,
            SanityShadowBudgetEvaluationStatus.SpeciesIneligible =>
                HostileShadowSpawnStatus.Rejected,
            SanityShadowBudgetEvaluationStatus.Inactive or
            SanityShadowBudgetEvaluationStatus.SystemDisabled =>
                HostileShadowSpawnStatus.Inactive,
            _ => HostileShadowSpawnStatus.Unavailable,
        };
    }

    private static HostileShadowSpawnResult FromBudget(
        HostileShadowSpawnStatus status,
        string reason,
        SanityShadowBudgetEvaluationResult evaluation
    )
    {
        return new HostileShadowSpawnResult(
            status,
            reason,
            null,
            evaluation.Status,
            evaluation.Occupancy,
            evaluation.Cap
        );
    }

    private static HostileShadowSpawnResult Failure(
        HostileShadowSpawnStatus status,
        string reason,
        int occupancy = 0
    )
    {
        return new HostileShadowSpawnResult(
            status,
            reason,
            null,
            null,
            occupancy,
            0
        );
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? "hostile-shadow.reason-missing"
            : reason.Length <= HostileShadowProtocol.MaximumIdentifierLength
                ? reason
                : reason[..HostileShadowProtocol.MaximumIdentifierLength];
    }

    private static bool IsValidIdentifier(string value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > HostileShadowProtocol.MaximumIdentifierLength
        )
        {
            return false;
        }
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    private static bool SameDouble(double left, double right)
    {
        return BitConverter.DoubleToInt64Bits(left)
            == BitConverter.DoubleToInt64Bits(right);
    }

    private static bool IsValidAttackUpdate(HostileShadowStateUpdate update)
    {
        var attacking = string.Equals(
            update.StateId,
            HostileShadowStateIds.Attack,
            StringComparison.Ordinal
        );
        if (!attacking)
        {
            return update.AttackInstanceId.Length == 0
                && update.AttackInstanceRevision == 0
                && update.AttackFrameNumber == 0;
        }

        return SanityPlayerKey.IsCanonical(update.TargetPlayerKey)
            && IsValidIdentifier(update.AttackInstanceId)
            && update.AttackInstanceRevision > 0
            && update.AttackFrameNumber > 0
            && update.AttackFrameNumber
                <= HostileShadowProtocol.MaximumAttackFrameNumber;
    }
}
