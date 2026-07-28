#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal static class HostileShadowStateIds
{
    internal const string Spawn = "hostile-shadow.state.spawn";
    internal const string Taunt = "hostile-shadow.state.taunt";
    internal const string Idle = "hostile-shadow.state.idle";
    internal const string Chase = "hostile-shadow.state.chase";
    internal const string Attack = "hostile-shadow.state.attack";
    internal const string HitTeleport = "hostile-shadow.state.hit-teleport";
    internal const string Dying = "hostile-shadow.state.dying";
    internal const string Despawn = "hostile-shadow.state.despawn";

    internal static bool IsKnown(string stateId)
    {
        return string.Equals(stateId, Spawn, StringComparison.Ordinal)
            || string.Equals(stateId, Taunt, StringComparison.Ordinal)
            || string.Equals(stateId, Idle, StringComparison.Ordinal)
            || string.Equals(stateId, Chase, StringComparison.Ordinal)
            || string.Equals(stateId, Attack, StringComparison.Ordinal)
            || string.Equals(stateId, HitTeleport, StringComparison.Ordinal)
            || string.Equals(stateId, Dying, StringComparison.Ordinal)
            || string.Equals(stateId, Despawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dying is the only public live state which proves true zero HP. Despawn is a positive-HP
    /// lifecycle classification and never acquires kill semantics.
    /// </summary>
    internal static bool IsHealthValid(string stateId, int health)
    {
        if (!IsKnown(stateId) || health < 0)
            return false;
        if (string.Equals(stateId, Dying, StringComparison.Ordinal))
            return health == 0;
        return health > 0;
    }
}

internal static class HostileShadowCleanupReasonIds
{
    internal const string DangerExited = "hostile-shadow.cleanup.danger-exited";
    internal const string EventOverride = "hostile-shadow.cleanup.event-override";
    internal const string OwnerDisconnected = "hostile-shadow.cleanup.owner-disconnected";
    internal const string DayEnding = "hostile-shadow.cleanup.day-ending";
    internal const string ReturnedToTitle = "hostile-shadow.cleanup.returned-to-title";
    internal const string SystemDisabled = "hostile-shadow.cleanup.system-disabled";
    internal const string WorldCleanup = "hostile-shadow.cleanup.world-cleanup";
    internal const string Disposed = "hostile-shadow.cleanup.disposed";
    internal const string Natural = "hostile-shadow.cleanup.natural";
    internal const string PhysicalMaterializationFailed =
        "hostile-shadow.cleanup.physical-materialization-failed";
    internal const string PhysicalEntityMissing =
        "hostile-shadow.cleanup.physical-entity-missing";
    internal const string IncompatiblePeer =
        "hostile-shadow.cleanup.incompatible-peer";
    internal const string ResourceInvalidated =
        "hostile-shadow.cleanup.resource-invalidated";
    internal const string PeerCapabilityChanged =
        "hostile-shadow.cleanup.peer-capability-changed";
    internal const string HitTeleportLocationInvalid =
        "hostile-shadow.cleanup.hit-teleport-location-invalid";
    internal const string DyingCompleted =
        "hostile-shadow.cleanup.dying-completed";
    internal const string HitResponseSynchronizationFailed =
        "hostile-shadow.cleanup.hit-response-synchronization-failed";
}

internal enum HostileShadowPhysicalEntityCapabilityStatus
{
    Available,
    Unavailable,
}

internal readonly record struct HostileShadowPhysicalEntityCapability(
    HostileShadowPhysicalEntityCapabilityStatus Status,
    string Reason
)
{
    internal bool IsAvailable =>
        Status == HostileShadowPhysicalEntityCapabilityStatus.Available;

    internal static HostileShadowPhysicalEntityCapability Unverified =>
        new(
            HostileShadowPhysicalEntityCapabilityStatus.Unavailable,
            "hostile-shadow.physical-monster-subclass-net-serialization-unverified"
        );
}

internal enum HostileShadowSpawnOrigin
{
    Interval,
    OwnerProjectionConversion,
}

internal enum HostileShadowSpawnStatus
{
    Spawned,
    Duplicate,
    Waiting,
    AtCap,
    Inactive,
    Rejected,
    Unavailable,
}

internal static class HostileShadowSpeciesBindingPolicy
{
    internal static bool TryResolveSpecies(
        string assetBindingId,
        out SanityShadowSpecies species,
        out string reason
    )
    {
        if (
            string.Equals(
                assetBindingId,
                ShadowMonsterAssetBindingIds.CreeperFear,
                StringComparison.Ordinal
            )
        )
        {
            species = SanityShadowSpecies.CreeperFear;
            reason = "hostile-shadow.asset-binding-creeper-fear";
            return true;
        }
        if (
            string.Equals(
                assetBindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            )
        )
        {
            species = SanityShadowSpecies.Terrorbeak;
            reason = "hostile-shadow.asset-binding-terrorbeak";
            return true;
        }

        species = default;
        reason = "hostile-shadow.asset-binding-not-budget-species";
        return false;
    }

    /// <summary>
    /// At 15% interval refreshes remain Creeper Fear. The 10% tier adds Terrorbeak as the
    /// interval candidate; both bindings still compete inside the same owner budget.
    /// </summary>
    internal static bool TrySelectIntervalBinding(
        SanityShadowPoolTier tier,
        out string assetBindingId,
        out string reason
    )
    {
        if (tier == SanityShadowPoolTier.Hostile15)
        {
            assetBindingId = ShadowMonsterAssetBindingIds.CreeperFear;
            reason = "hostile-shadow.refresh-pool-creeper-fear";
            return true;
        }
        if (tier == SanityShadowPoolTier.Hostile10)
        {
            assetBindingId = ShadowMonsterAssetBindingIds.Terrorbeak;
            reason = "hostile-shadow.refresh-pool-terrorbeak";
            return true;
        }

        assetBindingId = string.Empty;
        reason = "hostile-shadow.refresh-pool-tier-ineligible";
        return false;
    }
}

internal sealed class HostileShadowSpawnCommand
{
    internal HostileShadowSpawnCommand(
        string requestId,
        HostileShadowSpawnOrigin origin,
        string ownerPlayerKey,
        string locationId,
        double positionX,
        double positionY,
        long gameMinute,
        ShadowMonsterRuntimeProfile profile,
        string reason
    )
    {
        RequestId = requestId;
        Origin = origin;
        OwnerPlayerKey = ownerPlayerKey;
        LocationId = locationId;
        PositionX = positionX;
        PositionY = positionY;
        GameMinute = gameMinute;
        Profile = profile;
        Reason = reason;
    }

    internal string RequestId { get; }
    internal HostileShadowSpawnOrigin Origin { get; }
    internal string OwnerPlayerKey { get; }
    internal string LocationId { get; }
    internal double PositionX { get; }
    internal double PositionY { get; }
    internal long GameMinute { get; }
    internal ShadowMonsterRuntimeProfile Profile { get; }
    internal string Reason { get; }
}

internal readonly record struct HostileShadowSpawnResult(
    HostileShadowSpawnStatus Status,
    string Reason,
    long? EntityId,
    SanityShadowBudgetEvaluationStatus? BudgetStatus,
    int Occupancy,
    int Cap
)
{
    internal bool Spawned => Status == HostileShadowSpawnStatus.Spawned;
}

internal sealed class HostileShadowStateUpdate
{
    internal HostileShadowStateUpdate(
        long entityId,
        string locationId,
        string stateId,
        string targetPlayerKey,
        double positionX,
        double positionY,
        int health,
        string reason,
        string attackInstanceId = "",
        long attackInstanceRevision = 0,
        int attackFrameNumber = 0
    )
    {
        EntityId = entityId;
        LocationId = locationId;
        StateId = stateId;
        TargetPlayerKey = targetPlayerKey;
        PositionX = positionX;
        PositionY = positionY;
        Health = health;
        Reason = reason;
        AttackInstanceId = attackInstanceId;
        AttackInstanceRevision = attackInstanceRevision;
        AttackFrameNumber = attackFrameNumber;
    }

    internal long EntityId { get; }
    internal string LocationId { get; }
    internal string StateId { get; }
    internal string TargetPlayerKey { get; }
    internal double PositionX { get; }
    internal double PositionY { get; }
    internal int Health { get; }
    internal string Reason { get; }
    internal string AttackInstanceId { get; }
    internal long AttackInstanceRevision { get; }
    internal int AttackFrameNumber { get; }
}

internal interface IHostileShadowBudgetAuthority
{
    SanityShadowBudgetEvaluationResult Evaluate(
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpecies requestedSpecies
    );
}

internal interface IHostileShadowEntityIdSource
{
    long Next();
}
