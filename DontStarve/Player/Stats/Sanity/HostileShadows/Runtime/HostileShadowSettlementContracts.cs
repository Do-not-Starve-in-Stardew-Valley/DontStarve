#nullable enable

using System;
using System.Text;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal static class HostileShadowSettlementReasonIds
{
    internal const string DyingHealthZero = "hostile-shadow.dying-health-zero";
}

internal readonly record struct HostileShadowSettlementKey(
    string SessionId,
    long EntityId,
    long DeathRevision
)
{
    internal bool IsValid =>
        SanityProtocol.IsValidSessionId(SessionId)
        && EntityId > 0
        && DeathRevision > 0;

    internal string LifecycleCorrelationId => IsValid
        ? HostileShadowLifecycleReceipt.CreateCorrelationId(
            SessionId,
            EntityId,
            DeathRevision,
            HostileShadowLifecycleTransitionKind.Dying
        )
        : string.Empty;

    internal string SettlementId => IsValid
        ? string.Concat(LifecycleCorrelationId, ":settlement")
        : string.Empty;
}

/// <summary>
/// Immutable scalar capture of one host-confirmed death. It deliberately does not retain the
/// mutable runtime profile or a Stardew object, so replay comparison cannot drift after death.
/// </summary>
internal sealed record HostileShadowSettlementRequest(
    HostileShadowLifecycleReceipt LifecycleReceipt,
    SanityAuthorityRole Authority,
    string StateId,
    int Health,
    string LocationId,
    double PositionX,
    double PositionY,
    int DropTableSchemaVersion,
    string DropTableId,
    string ItemSemanticId,
    int GuaranteedQuantity,
    int BonusQuantity,
    double BonusChance,
    int SanityReward,
    int ExperienceValue,
    string KillCounterId
)
{
    internal HostileShadowSettlementKey Key => new(
        LifecycleReceipt.SessionId,
        LifecycleReceipt.EntityId,
        LifecycleReceipt.DeathRevision
    );

    internal static HostileShadowSettlementRequest Capture(
        HostileShadowLifecycleReceipt lifecycleReceipt,
        SanityAuthorityRole authority,
        string stateId,
        int health,
        string locationId,
        double positionX,
        double positionY,
        ShadowMonsterRuntimeProfile profile
    )
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));
        var drop = profile.DropTable
            ?? throw new ArgumentException(
                "Hostile shadow runtime profile has no drop table.",
                nameof(profile)
            );
        return new HostileShadowSettlementRequest(
            lifecycleReceipt,
            authority,
            stateId,
            health,
            locationId,
            positionX,
            positionY,
            drop.SchemaVersion,
            drop.DropTableId,
            drop.ItemSemanticId,
            drop.GuaranteedQuantity,
            drop.BonusQuantity,
            drop.BonusChance,
            profile.SanityReward,
            profile.ExperienceValue,
            profile.KillCounterId ?? string.Empty
        );
    }
}

internal interface IHostileShadowSettlementRandom
{
    int NextBonusRoll10000(int seed);
}

/// <summary>
/// A one-shot, version-stable xorshift roll. The seed is supplied by the settlement key, so
/// retries never depend on global game RNG state.
/// </summary>
internal sealed class StableHostileShadowSettlementRandom
    : IHostileShadowSettlementRandom
{
    public int NextBonusRoll10000(int seed)
    {
        var value = unchecked((uint)seed);
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return (int)(value % HostileShadowSettlementService.BonusRollScale);
    }
}

internal static class HostileShadowSettlementSeed
{
    internal static int Create(HostileShadowSettlementKey key)
    {
        if (!key.IsValid)
            return 0;

        // FNV-1a is used only as a stable correlation seed, not for security.
        var bytes = Encoding.UTF8.GetBytes(key.SettlementId);
        var hash = 2166136261u;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 16777619u;
        }
        var seed = (int)(hash & 0x7fffffff);
        return seed == 0 ? 1 : seed;
    }
}

internal readonly record struct HostileShadowDropSpawnRequest(
    HostileShadowSettlementKey Key,
    string SettlementId,
    string DropTableId,
    string ItemSemanticId,
    int Quantity,
    int RandomSeed,
    int BonusRoll10000,
    string LocationId,
    double PositionX,
    double PositionY
);

internal enum HostileShadowDropSpawnStatus
{
    Rejected = 0,
    Spawned = 1,
}

internal readonly record struct HostileShadowDropSpawnReceipt(
    HostileShadowDropSpawnStatus Status,
    string Reason
)
{
    internal bool Spawned =>
        Status == HostileShadowDropSpawnStatus.Spawned
        && !string.IsNullOrWhiteSpace(Reason);

    internal static HostileShadowDropSpawnReceipt Success(string reason)
    {
        return new HostileShadowDropSpawnReceipt(
            HostileShadowDropSpawnStatus.Spawned,
            reason
        );
    }

    internal static HostileShadowDropSpawnReceipt Rejected(string reason)
    {
        return new HostileShadowDropSpawnReceipt(
            HostileShadowDropSpawnStatus.Rejected,
            reason
        );
    }
}

internal interface IHostileShadowDropSpawnAuthority
{
    HostileShadowDropSpawnReceipt Spawn(HostileShadowDropSpawnRequest request);
}

internal readonly record struct HostileShadowLastHitterRequest(
    HostileShadowSettlementKey Key,
    string AttributedPlayerKey,
    string LocationId
);

internal enum HostileShadowLastHitterStatus
{
    NotEvaluated,
    Valid,
    Invalid,
}

internal readonly record struct HostileShadowLastHitterReceipt(
    HostileShadowLastHitterStatus Status,
    string PlayerKey,
    string Reason
)
{
    internal bool IsValid =>
        Status == HostileShadowLastHitterStatus.Valid
        && SanityPlayerKey.IsCanonical(PlayerKey);

    internal static HostileShadowLastHitterReceipt NotEvaluated(string reason)
    {
        return new HostileShadowLastHitterReceipt(
            HostileShadowLastHitterStatus.NotEvaluated,
            string.Empty,
            reason
        );
    }

    internal static HostileShadowLastHitterReceipt Valid(
        string playerKey,
        string reason
    )
    {
        return new HostileShadowLastHitterReceipt(
            HostileShadowLastHitterStatus.Valid,
            playerKey,
            reason
        );
    }

    internal static HostileShadowLastHitterReceipt Invalid(string reason)
    {
        return new HostileShadowLastHitterReceipt(
            HostileShadowLastHitterStatus.Invalid,
            string.Empty,
            reason
        );
    }
}

internal interface IHostileShadowLastHitterAuthority
{
    HostileShadowLastHitterReceipt Resolve(HostileShadowLastHitterRequest request);
}

internal readonly record struct HostileShadowSanityRewardRequest(
    HostileShadowSettlementKey Key,
    string SettlementId,
    string PlayerKey,
    string LocationId,
    double Delta,
    SanityChangeSource Source
);

internal enum HostileShadowSanityRewardStatus
{
    NotAttempted,
    Applied,
    NoChange,
    Rejected,
}

internal readonly record struct HostileShadowSanityRewardReceipt(
    HostileShadowSanityRewardStatus Status,
    string PlayerKey,
    double Delta,
    SanityChangeSource Source,
    long BeforeRevision,
    long AfterRevision,
    double BeforeSanity,
    double AfterSanity,
    string Reason
)
{
    internal bool IsAccepted =>
        Status
            is HostileShadowSanityRewardStatus.Applied
                or HostileShadowSanityRewardStatus.NoChange;

    internal static HostileShadowSanityRewardReceipt NotAttempted(string reason)
    {
        return new HostileShadowSanityRewardReceipt(
            HostileShadowSanityRewardStatus.NotAttempted,
            string.Empty,
            0d,
            SanityChangeSource.HostileShadowKill,
            0,
            0,
            0d,
            0d,
            reason
        );
    }
}

internal interface IHostileShadowSanityRewardAuthority
{
    HostileShadowSanityRewardReceipt Apply(HostileShadowSanityRewardRequest request);
}

internal enum HostileShadowSettlementReceiptStatus
{
    Settled,
    Rejected,
    PartialFailure,
}

internal enum HostileShadowSettlementRetryDisposition
{
    CorrectedIntentMayRetry,
    TerminalDoNotRetrySameDeath,
}

internal sealed record HostileShadowSettlementReceipt(
    HostileShadowSettlementReceiptStatus Status,
    HostileShadowSettlementKey Key,
    string LifecycleCorrelationId,
    string SettlementId,
    int RandomSeed,
    int BonusRoll10000,
    int PlannedDropQuantity,
    HostileShadowDropSpawnReceipt Drop,
    HostileShadowLastHitterReceipt LastHitter,
    HostileShadowSanityRewardReceipt SanityReward,
    HostileShadowSettlementRetryDisposition RetryDisposition,
    string Reason
);

internal enum HostileShadowSettlementStatus
{
    Settled,
    Duplicate,
    RequiresHostAuthority,
    SessionMismatch,
    InvalidIntent,
    CorrelationConflict,
    InProgress,
    CapacityExceeded,
    Rejected,
}

internal readonly record struct HostileShadowSettlementResult(
    HostileShadowSettlementStatus Status,
    HostileShadowSettlementReceipt? Receipt,
    HostileShadowSettlementRetryDisposition RetryDisposition,
    string Reason
)
{
    internal bool Completed =>
        Status
            is HostileShadowSettlementStatus.Settled
                or HostileShadowSettlementStatus.Duplicate;
}
