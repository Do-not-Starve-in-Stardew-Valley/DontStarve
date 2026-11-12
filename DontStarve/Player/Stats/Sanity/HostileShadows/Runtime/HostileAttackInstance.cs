#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal readonly record struct HostileAttackTransitionContext(
    long EntityId,
    string TargetPlayerKey,
    long AttackInstanceRevision
);

internal readonly record struct HostileAttackPostAttackTransition(
    bool EnterTaunt,
    double NextAttackDelaySeconds
);

internal interface IHostileAttackTransitionPolicy
{
    bool ShouldTauntBeforeFirstChase(HostileAttackTransitionContext context);

    bool TryResolvePostAttackTransition(
        HostileAttackTransitionContext context,
        double configuredProfileIntervalSeconds,
        out HostileAttackPostAttackTransition transition
    );
}

/// <summary>
/// The common layer deliberately carries no species probability. An injected species policy may
/// elect Taunt; the canonical profile interval remains the default post-attack delay input.
/// </summary>
internal sealed class HostileAttackDeferredTransitionPolicy
    : IHostileAttackTransitionPolicy
{
    internal static HostileAttackDeferredTransitionPolicy Instance { get; } = new();

    private HostileAttackDeferredTransitionPolicy() { }

    public bool ShouldTauntBeforeFirstChase(HostileAttackTransitionContext context)
    {
        return false;
    }

    public bool TryResolvePostAttackTransition(
        HostileAttackTransitionContext context,
        double configuredProfileIntervalSeconds,
        out HostileAttackPostAttackTransition transition
    )
    {
        transition = new HostileAttackPostAttackTransition(
            false,
            configuredProfileIntervalSeconds
        );
        return double.IsFinite(configuredProfileIntervalSeconds)
            && configuredProfileIntervalSeconds >= 0d;
    }
}

internal sealed class HostileAttackInstance
{
    private const int MaximumLedgerEntries = 256;
    private readonly HashSet<string> nonces = new(StringComparer.Ordinal);
    // 命中身份 = (命中窗口, 玩家)（20261113 冻结决议 5）：同一窗口内每个玩家只结算一次，
    // 不同窗口（连击的第二记红框）允许再次命中同一玩家。nonce 仍按整个攻击实例去重。
    private readonly HashSet<(string WindowId, string PlayerKey)> claimedWindowPlayerKeys =
        new();
    private readonly HashSet<(string WindowId, long PlayerId)> claimedWindowPlayerIds =
        new();

    internal HostileAttackInstance(
        string instanceId,
        long entityId,
        long revision,
        string locationId,
        string targetPlayerKey,
        double originPositionX,
        double originPositionY,
        double directionX,
        double directionY,
        HostileAttackFacing facing
    )
    {
        InstanceId = instanceId;
        EntityId = entityId;
        Revision = revision;
        LocationId = locationId;
        TargetPlayerKey = targetPlayerKey;
        OriginPositionX = originPositionX;
        OriginPositionY = originPositionY;
        DirectionX = directionX;
        DirectionY = directionY;
        Facing = facing;
        FrameNumber = 1;
    }

    internal string InstanceId { get; }
    internal long EntityId { get; }
    internal long Revision { get; private set; }
    internal string LocationId { get; }
    internal string TargetPlayerKey { get; }
    internal double OriginPositionX { get; private set; }
    internal double OriginPositionY { get; private set; }
    internal double DirectionX { get; }
    internal double DirectionY { get; }
    internal HostileAttackFacing Facing { get; }
    internal int FrameNumber { get; set; }
    internal int HitPlayerCount => claimedWindowPlayerKeys.Count;

    internal bool TryTranslateOrigin(
        double deltaX,
        double deltaY
    )
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            return false;

        var nextX = OriginPositionX + deltaX;
        var nextY = OriginPositionY + deltaY;
        if (!double.IsFinite(nextX) || !double.IsFinite(nextY))
            return false;

        OriginPositionX = nextX;
        OriginPositionY = nextY;
        return true;
    }

    internal bool TrySetRevision(long revision)
    {
        if (revision <= 0)
            return false;

        Revision = revision;
        return true;
    }

    /// <summary>
    /// Window-scoped settled check: a player blocked for one hit window may still be hit by
    /// another window of the same attack instance. Zero remains the no-player sentinel; every
    /// non-zero signed long is a possible Stardew UniqueMultiplayerID, including negative IDs.
    /// </summary>
    internal bool HasSettledPlayerInWindow(string hitWindowId, long playerId)
    {
        return !string.IsNullOrWhiteSpace(hitWindowId)
            && playerId != 0
            && claimedWindowPlayerIds.Contains((hitWindowId, playerId));
    }

    internal bool TryClaimHit(
        string nonce,
        string playerKey,
        long playerId,
        string hitWindowId,
        out string reason
    )
    {
        if (
            string.IsNullOrWhiteSpace(nonce)
            || string.IsNullOrWhiteSpace(playerKey)
            || string.IsNullOrWhiteSpace(hitWindowId)
            || nonce.Length > HostileShadowProtocol.MaximumIdentifierLength
            || hitWindowId.Length > HostileShadowProtocol.MaximumIdentifierLength
        )
        {
            reason = "hostile-shadow.attack-hit-ledger-input-invalid";
            return false;
        }
        if (nonces.Contains(nonce))
        {
            reason = "hostile-shadow.attack-hit-nonce-replayed";
            return false;
        }
        if (
            claimedWindowPlayerKeys.Contains((hitWindowId, playerKey))
            || (
                playerId != 0
                && claimedWindowPlayerIds.Contains((hitWindowId, playerId))
            )
        )
        {
            reason = "hostile-shadow.attack-hit-player-already-settled";
            return false;
        }
        if (
            nonces.Count >= MaximumLedgerEntries
            || claimedWindowPlayerKeys.Count >= MaximumLedgerEntries
            || (
                playerId != 0
                && claimedWindowPlayerIds.Count >= MaximumLedgerEntries
            )
        )
        {
            reason = "hostile-shadow.attack-hit-ledger-capacity";
            return false;
        }

        nonces.Add(nonce);
        claimedWindowPlayerKeys.Add((hitWindowId, playerKey));
        if (playerId != 0)
            claimedWindowPlayerIds.Add((hitWindowId, playerId));
        reason = "hostile-shadow.attack-hit-ledger-claimed";
        return true;
    }
}
