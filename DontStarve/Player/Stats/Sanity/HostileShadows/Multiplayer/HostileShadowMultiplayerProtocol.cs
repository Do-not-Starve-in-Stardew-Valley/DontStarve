#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

internal enum ShadowStateDeltaKind
{
    Spawned,
    Updated,
    Removed,
}

internal enum ShadowSnapshotTrigger
{
    Join,
    Warp,
    Resync,
}

/// <summary>
/// Shared, transport-safe hostile-shadow state. Owner-local projection details, screen identity,
/// conversion correlation, configuration values, and game CLR objects are intentionally absent.
/// </summary>
internal sealed class ShadowStateSnapshot
{
    public long EntityId { get; set; }

    public string OwnerPlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public string DifficultyProfileId { get; set; } = string.Empty;

    public string AssetBindingId { get; set; } = string.Empty;

    public string StateId { get; set; } = string.Empty;

    public string TargetPlayerKey { get; set; } = string.Empty;

    public double PositionX { get; set; }

    public double PositionY { get; set; }

    public int Health { get; set; }

    public int MaxHealth { get; set; }

    /// <summary>Host-created correlation for the currently active attack, otherwise empty.</summary>
    public string AttackInstanceId { get; set; } = string.Empty;

    /// <summary>The host table revision which created the current attack instance.</summary>
    public long AttackInstanceRevision { get; set; }

    /// <summary>One-based attack animation frame, or zero outside Attack.</summary>
    public int AttackFrameNumber { get; set; }

    /// <summary>DIAG-20260809: 绑定隐藏态——危险实体被无害投影外观取代（不渲染/无敌/行为禁用）。</summary>
    public bool IsBindingHidden { get; set; }

    /// <summary>DIAG-20260809: 绑定的无害投影 correlation id（空=未绑定）。</summary>
    public string BindingCorrelationId { get; set; } = string.Empty;

    /// <summary>DIAG-20260809: 当前朝向（隐藏/恢复/动画行选择用）。</summary>
    public string FacingId { get; set; } = string.Empty;

    /// <summary>The host table revision at which this entity last changed.</summary>
    public long Revision { get; set; }

    internal ShadowStateSnapshot Clone()
    {
        return new ShadowStateSnapshot
        {
            EntityId = EntityId,
            OwnerPlayerKey = OwnerPlayerKey,
            LocationId = LocationId,
            DifficultyProfileId = DifficultyProfileId,
            AssetBindingId = AssetBindingId,
            StateId = StateId,
            TargetPlayerKey = TargetPlayerKey,
            PositionX = PositionX,
            PositionY = PositionY,
            Health = Health,
            MaxHealth = MaxHealth,
            AttackInstanceId = AttackInstanceId,
            AttackInstanceRevision = AttackInstanceRevision,
            AttackFrameNumber = AttackFrameNumber,
            IsBindingHidden = IsBindingHidden,
            BindingCorrelationId = BindingCorrelationId,
            FacingId = FacingId,
            Revision = Revision,
        };
    }
}

internal sealed class ShadowStateSnapshotMessage
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    /// <summary>The single location represented by this full replacement.</summary>
    public string LocationId { get; set; } = string.Empty;

    public ShadowSnapshotTrigger Trigger { get; set; }

    public long Revision { get; set; }

    public List<ShadowStateSnapshot> Entities { get; set; } = new();
}

internal sealed class ShadowStateDelta
{
    public ShadowStateDeltaKind Kind { get; set; }

    public long EntityId { get; set; }

    public ShadowStateSnapshot? State { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>Lifecycle cleanup is never a death/drop/reward settlement.</summary>
    public bool SettlementEligible { get; set; }

    internal ShadowStateDelta Clone()
    {
        return new ShadowStateDelta
        {
            Kind = Kind,
            EntityId = EntityId,
            State = State?.Clone(),
            Reason = Reason,
            SettlementEligible = SettlementEligible,
        };
    }
}

internal sealed class ShadowStateDeltaMessage
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public long BaseRevision { get; set; }

    public long Revision { get; set; }

    public ShadowStateDelta Change { get; set; } = new();

    internal ShadowStateDeltaMessage Clone()
    {
        return new ShadowStateDeltaMessage
        {
            ProtocolVersion = ProtocolVersion,
            SchemaVersion = SchemaVersion,
            SessionId = SessionId,
            BaseRevision = BaseRevision,
            Revision = Revision,
            Change = Change.Clone(),
        };
    }
}

internal sealed class ShadowStateSnapshotRequest
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public ShadowSnapshotTrigger Trigger { get; set; }

    public long KnownRevision { get; set; }
}

/// <summary>
/// A client may report that its private projection was removed. It never supplies location,
/// position, profile data, HP, entity ID, or a requested mutation; the host derives all of them.
/// </summary>
internal sealed class ShadowProjectionConversionRequest
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public string SpeciesId { get; set; } = string.Empty;

    public long RequestedAtMinute { get; set; }

    /// <summary>The Sanity revision which opened the current host-confirmed danger epoch.</summary>
    public long DangerRevision { get; set; }

    /// <summary>DIAG-20260809: 是否携带投影当前位置（原地转化用）。</summary>
    public bool HasPosition { get; set; }

    public double PositionX { get; set; }

    public double PositionY { get; set; }
}

/// <summary>
/// A client can only identify itself as the recent attacker. Location and revision are clues;
/// the host resolves the sender again and recomputes current location/range before accepting it.
/// </summary>
internal sealed class ShadowAggroHintRequest
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public long EntityId { get; set; }

    public string AttackerPlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public long KnownEntityRevision { get; set; }
}

/// <summary>
/// A peer may only report its own overlap with a shared attack. The host re-resolves every field,
/// recomputes the live player box/range, and owns the per-instance hit ledger before damage runs.
/// </summary>
internal sealed class ShadowAttackHitRequest
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string Nonce { get; set; } = string.Empty;

    public long EntityId { get; set; }

    public string TargetPlayerKey { get; set; } = string.Empty;

    public string LocationId { get; set; } = string.Empty;

    public string AttackInstanceId { get; set; } = string.Empty;

    public long ObservedEntityRevision { get; set; }

    public long ObservedAttackInstanceRevision { get; set; }

    public int ObservedFrameNumber { get; set; }
}

/// <summary>
/// Session capability only; it carries no config or fingerprint. The host must not place a custom
/// network Monster while a same-version peer cannot borrow the shared (non-owner-local) visuals.
/// </summary>
internal sealed class ShadowPhysicalCapabilityReport
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public bool Available { get; set; }

    public string Reason { get; set; } = string.Empty;
}

internal static class HostileShadowProtocol
{
    internal const int CurrentProtocolVersion = 1;
    // Stage 08 scopes full replacement snapshots to one subscribed location and one explicit
    // join/warp/resync trigger. Older all-world snapshots must fail closed.
    internal const int CurrentSchemaVersion = 4;
    internal const int MaximumEntitiesPerSnapshot = 256;
    internal const int MaximumIdentifierLength = 256;
    internal const int MaximumStateIdLength = 128;
    internal const int MaximumAttackFrameNumber = 64;

    internal static bool IsValidEnvelope(
        int protocolVersion,
        int schemaVersion,
        string sessionId
    )
    {
        return protocolVersion == CurrentProtocolVersion
            && schemaVersion == CurrentSchemaVersion
            && SanityProtocol.IsValidSessionId(sessionId);
    }

    /// <summary>
    /// Converts the trusted host table copy into a single-location transport replacement. The
    /// source remains an internal authority snapshot; only this validated clone may cross peers.
    /// </summary>
    internal static bool TryCreateScopedSnapshot(
        ShadowStateSnapshotMessage? source,
        string locationId,
        ShadowSnapshotTrigger trigger,
        out ShadowStateSnapshotMessage? scoped,
        out string reason
    )
    {
        scoped = null;
        if (
            source is null
            || !IsValidEnvelope(
                source.ProtocolVersion,
                source.SchemaVersion,
                source.SessionId
            )
            || source.Revision < 0
            || source.Entities is null
            || source.Entities.Count > MaximumEntitiesPerSnapshot
            || !IsValidLocationId(locationId)
            || !IsValidSnapshotTrigger(trigger)
        )
        {
            reason = "hostile-shadow.snapshot-scope-source-invalid";
            return false;
        }

        var entityIds = new HashSet<long>();
        var result = new ShadowStateSnapshotMessage
        {
            ProtocolVersion = source.ProtocolVersion,
            SchemaVersion = source.SchemaVersion,
            SessionId = source.SessionId,
            LocationId = locationId,
            Trigger = trigger,
            Revision = source.Revision,
        };
        foreach (var state in source.Entities)
        {
            if (
                !IsValidState(state, source.Revision, out reason)
                || !entityIds.Add(state.EntityId)
            )
            {
                reason = "hostile-shadow.snapshot-scope-state-invalid";
                return false;
            }
            if (string.Equals(state.LocationId, locationId, StringComparison.Ordinal))
                result.Entities.Add(state.Clone());
        }

        scoped = result;
        reason = "hostile-shadow.snapshot-scoped";
        return true;
    }

    internal static bool IsValidSnapshotMessage(
        ShadowStateSnapshotMessage? message,
        out string reason
    )
    {
        if (
            message is null
            || !IsValidEnvelope(
                message.ProtocolVersion,
                message.SchemaVersion,
                message.SessionId
            )
        )
        {
            reason = "hostile-shadow.snapshot-envelope-invalid";
            return false;
        }
        if (
            message.Revision < 0
            || !IsValidLocationId(message.LocationId)
            || !IsValidSnapshotTrigger(message.Trigger)
            || message.Entities is null
            || message.Entities.Count > MaximumEntitiesPerSnapshot
        )
        {
            reason = "hostile-shadow.snapshot-revision-or-count-invalid";
            return false;
        }

        var entityIds = new HashSet<long>();
        foreach (var entity in message.Entities)
        {
            if (!IsValidState(entity, message.Revision, out reason))
                return false;
            if (!string.Equals(
                entity.LocationId,
                message.LocationId,
                StringComparison.Ordinal
            ))
            {
                reason = "hostile-shadow.snapshot-location-scope-conflict";
                return false;
            }
            if (!entityIds.Add(entity.EntityId))
            {
                reason = "hostile-shadow.snapshot-entity-id-duplicate";
                return false;
            }
        }

        reason = "hostile-shadow.snapshot-valid";
        return true;
    }

    internal static bool IsValidDeltaMessage(
        ShadowStateDeltaMessage? message,
        out string reason
    )
    {
        if (
            message is null
            || !IsValidEnvelope(
                message.ProtocolVersion,
                message.SchemaVersion,
                message.SessionId
            )
        )
        {
            reason = "hostile-shadow.delta-envelope-invalid";
            return false;
        }
        if (
            message.BaseRevision < 0
            || message.BaseRevision == long.MaxValue
            || message.Revision != message.BaseRevision + 1
            || message.Change is null
            || message.Change.EntityId <= 0
            || string.IsNullOrWhiteSpace(message.Change.Reason)
            || message.Change.Reason.Length > MaximumIdentifierLength
        )
        {
            reason = "hostile-shadow.delta-revision-or-change-invalid";
            return false;
        }
        if (message.Change.SettlementEligible)
        {
            reason = "hostile-shadow.delta-settlement-forbidden";
            return false;
        }

        switch (message.Change.Kind)
        {
            case ShadowStateDeltaKind.Spawned:
            case ShadowStateDeltaKind.Updated:
                if (
                    message.Change.State is null
                    || message.Change.State.EntityId != message.Change.EntityId
                    || message.Change.State.Revision != message.Revision
                    || !IsValidState(
                        message.Change.State,
                        message.Revision,
                        out reason
                    )
                )
                {
                    reason = "hostile-shadow.delta-state-invalid";
                    return false;
                }
                break;
            case ShadowStateDeltaKind.Removed:
                if (message.Change.State is not null)
                {
                    reason = "hostile-shadow.delta-remove-state-must-be-empty";
                    return false;
                }
                break;
            default:
                reason = "hostile-shadow.delta-kind-invalid";
                return false;
        }

        reason = "hostile-shadow.delta-valid";
        return true;
    }

    internal static bool IsValidSnapshotRequest(
        ShadowStateSnapshotRequest? request,
        string expectedPlayerKey,
        string expectedLocationId,
        string hostSessionId,
        out string reason
    )
    {
        if (
            request is null
            || request.ProtocolVersion != CurrentProtocolVersion
            || request.SchemaVersion != CurrentSchemaVersion
            || request.KnownRevision < 0
            || !IsValidLocationId(expectedLocationId)
            || !string.Equals(
                request.LocationId,
                expectedLocationId,
                StringComparison.Ordinal
            )
            || !IsValidSnapshotTrigger(request.Trigger)
            || !SanityProtocol.IsValidSessionId(hostSessionId)
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(
                request.PlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || (
                !string.IsNullOrEmpty(request.SessionId)
                && !string.Equals(
                    request.SessionId,
                    hostSessionId,
                    StringComparison.Ordinal
                )
            )
        )
        {
            reason = "hostile-shadow.snapshot-request-invalid";
            return false;
        }

        reason = "hostile-shadow.snapshot-request-valid";
        return true;
    }

    internal static bool IsValidLocationId(string locationId)
    {
        return IsBoundedIdentifier(locationId, MaximumIdentifierLength);
    }

    internal static bool IsValidSnapshotTrigger(ShadowSnapshotTrigger trigger)
    {
        return trigger is ShadowSnapshotTrigger.Join
            or ShadowSnapshotTrigger.Warp
            or ShadowSnapshotTrigger.Resync;
    }

    internal static bool IsValidConversionRequest(
        ShadowProjectionConversionRequest? request,
        string expectedPlayerKey,
        string hostSessionId,
        long hostGameMinute,
        long expectedDangerRevision,
        out string reason
    )
    {
        if (
            request is null
            || !IsValidEnvelope(
                request.ProtocolVersion,
                request.SchemaVersion,
                request.SessionId
            )
            || !SanityProtocol.IsValidSessionId(hostSessionId)
            || !string.Equals(
                request.SessionId,
                hostSessionId,
                StringComparison.Ordinal
            )
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(
                request.PlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || !IsBoundedIdentifier(request.CorrelationId, MaximumIdentifierLength)
            || !IsBoundedIdentifier(request.SpeciesId, MaximumIdentifierLength)
            || request.RequestedAtMinute < 0
            || hostGameMinute < 0
            || request.RequestedAtMinute > hostGameMinute
            || expectedDangerRevision < 0
            || request.DangerRevision != expectedDangerRevision
        )
        {
            reason = "hostile-shadow.conversion-request-invalid";
            return false;
        }

        reason = "hostile-shadow.conversion-request-valid";
        return true;
    }

    internal static bool IsValidAggroHintRequest(
        ShadowAggroHintRequest? request,
        string expectedPlayerKey,
        string hostSessionId,
        out string reason
    )
    {
        if (
            request is null
            || !IsValidEnvelope(
                request.ProtocolVersion,
                request.SchemaVersion,
                request.SessionId
            )
            || !SanityProtocol.IsValidSessionId(hostSessionId)
            || !string.Equals(
                request.SessionId,
                hostSessionId,
                StringComparison.Ordinal
            )
            || request.EntityId <= 0
            || request.KnownEntityRevision <= 0
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(
                request.AttackerPlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || !IsBoundedIdentifier(request.LocationId, MaximumIdentifierLength)
        )
        {
            reason = "hostile-shadow.aggro-hint-request-invalid";
            return false;
        }

        reason = "hostile-shadow.aggro-hint-request-valid";
        return true;
    }

    internal static bool IsValidAttackHitRequest(
        ShadowAttackHitRequest? request,
        string expectedPlayerKey,
        string hostSessionId,
        out string reason
    )
    {
        if (
            request is null
            || !IsValidEnvelope(
                request.ProtocolVersion,
                request.SchemaVersion,
                request.SessionId
            )
            || !SanityProtocol.IsValidSessionId(hostSessionId)
            || !string.Equals(
                request.SessionId,
                hostSessionId,
                StringComparison.Ordinal
            )
            || request.EntityId <= 0
            || request.ObservedEntityRevision <= 0
            || request.ObservedAttackInstanceRevision <= 0
            || request.ObservedFrameNumber <= 0
            || request.ObservedFrameNumber > MaximumAttackFrameNumber
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(
                request.TargetPlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || !IsBoundedIdentifier(request.Nonce, MaximumIdentifierLength)
            || !IsBoundedIdentifier(request.LocationId, MaximumIdentifierLength)
            || !IsBoundedIdentifier(
                request.AttackInstanceId,
                MaximumIdentifierLength
            )
        )
        {
            reason = "hostile-shadow.attack-hit-request-invalid";
            return false;
        }

        reason = "hostile-shadow.attack-hit-request-valid";
        return true;
    }

    internal static bool IsValidPhysicalCapabilityReport(
        ShadowPhysicalCapabilityReport? report,
        string expectedPlayerKey,
        string hostSessionId,
        out string reason
    )
    {
        if (
            report is null
            || !IsValidEnvelope(
                report.ProtocolVersion,
                report.SchemaVersion,
                report.SessionId
            )
            || !SanityProtocol.IsValidSessionId(hostSessionId)
            || !string.Equals(
                report.SessionId,
                hostSessionId,
                StringComparison.Ordinal
            )
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(
                report.PlayerKey,
                expectedPlayerKey,
                StringComparison.Ordinal
            )
            || !IsBoundedIdentifier(report.Reason, MaximumIdentifierLength)
        )
        {
            reason = "hostile-shadow.physical-capability-report-invalid";
            return false;
        }

        reason = "hostile-shadow.physical-capability-report-valid";
        return true;
    }

    internal static bool StateSetsEqual(
        IReadOnlyDictionary<long, ShadowStateSnapshot> current,
        IReadOnlyCollection<ShadowStateSnapshot> incoming
    )
    {
        if (current.Count != incoming.Count)
            return false;
        foreach (var state in incoming)
        {
            if (
                !current.TryGetValue(state.EntityId, out var existing)
                || !StatesEqual(existing, state)
            )
            {
                return false;
            }
        }
        return true;
    }

    internal static bool StatesEqual(
        ShadowStateSnapshot left,
        ShadowStateSnapshot right
    )
    {
        return left.EntityId == right.EntityId
            && string.Equals(
                left.OwnerPlayerKey,
                right.OwnerPlayerKey,
                StringComparison.Ordinal
            )
            && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
            && string.Equals(
                left.DifficultyProfileId,
                right.DifficultyProfileId,
                StringComparison.Ordinal
            )
            && string.Equals(
                left.AssetBindingId,
                right.AssetBindingId,
                StringComparison.Ordinal
            )
            && string.Equals(left.StateId, right.StateId, StringComparison.Ordinal)
            && string.Equals(
                left.TargetPlayerKey,
                right.TargetPlayerKey,
                StringComparison.Ordinal
            )
            && SameDouble(left.PositionX, right.PositionX)
            && SameDouble(left.PositionY, right.PositionY)
            && left.Health == right.Health
            && left.MaxHealth == right.MaxHealth
            && string.Equals(
                left.AttackInstanceId,
                right.AttackInstanceId,
                StringComparison.Ordinal
            )
            && left.AttackInstanceRevision == right.AttackInstanceRevision
            && left.AttackFrameNumber == right.AttackFrameNumber
            && left.Revision == right.Revision;
    }

    private static bool IsValidState(
        ShadowStateSnapshot? state,
        long envelopeRevision,
        out string reason
    )
    {
        if (
            state is null
            || state.EntityId <= 0
            || !SanityPlayerKey.IsCanonical(state.OwnerPlayerKey)
            || !IsBoundedIdentifier(state.LocationId, MaximumIdentifierLength)
            || !IsBoundedIdentifier(
                state.DifficultyProfileId,
                MaximumIdentifierLength
            )
            || !IsBoundedIdentifier(state.AssetBindingId, MaximumIdentifierLength)
            || !IsBoundedIdentifier(state.StateId, MaximumStateIdLength)
            || !Runtime.HostileShadowStateIds.IsKnown(state.StateId)
            || state.TargetPlayerKey is null
            || state.AttackInstanceId is null
            || (
                !string.IsNullOrEmpty(state.TargetPlayerKey)
                && !SanityPlayerKey.IsCanonical(state.TargetPlayerKey)
            )
            || !double.IsFinite(state.PositionX)
            || !double.IsFinite(state.PositionY)
            || state.MaxHealth <= 0
            || state.Health < 0
            || state.Health > state.MaxHealth
            || !Runtime.HostileShadowStateIds.IsHealthValid(
                state.StateId,
                state.Health
            )
            || !IsValidAttackState(state)
            || state.Revision <= 0
            || state.Revision > envelopeRevision
        )
        {
            reason = "hostile-shadow.state-invalid";
            return false;
        }

        reason = "hostile-shadow.state-valid";
        return true;
    }

    private static bool IsValidAttackState(ShadowStateSnapshot state)
    {
        var attacking = string.Equals(
            state.StateId,
            Runtime.HostileShadowStateIds.Attack,
            StringComparison.Ordinal
        );
        if (!attacking)
        {
            return state.AttackInstanceId.Length == 0
                && state.AttackInstanceRevision == 0
                && state.AttackFrameNumber == 0;
        }

        return SanityPlayerKey.IsCanonical(state.TargetPlayerKey)
            && IsBoundedIdentifier(
                state.AttackInstanceId,
                MaximumIdentifierLength
            )
            && state.AttackInstanceRevision > 0
            && state.AttackInstanceRevision <= state.Revision
            && state.AttackFrameNumber > 0
            && state.AttackFrameNumber <= MaximumAttackFrameNumber;
    }

    private static bool IsBoundedIdentifier(string value, int maximumLength)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
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
}
