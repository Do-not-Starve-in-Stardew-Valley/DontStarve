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
    private readonly HashSet<string> hitPlayerKeys = new(StringComparer.Ordinal);
    private readonly HashSet<long> hitPlayerIds = new();

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
    internal long Revision { get; }
    internal string LocationId { get; }
    internal string TargetPlayerKey { get; }
    internal double OriginPositionX { get; }
    internal double OriginPositionY { get; }
    internal double DirectionX { get; }
    internal double DirectionY { get; }
    internal HostileAttackFacing Facing { get; }
    internal int FrameNumber { get; set; }
    internal int HitPlayerCount => hitPlayerKeys.Count;

    internal bool HasSettledPlayer(long playerId)
    {
        return playerId >= 0 && hitPlayerIds.Contains(playerId);
    }

    internal bool TryClaimHit(string nonce, string playerKey, out string reason)
    {
        return TryClaimHit(nonce, playerKey, playerId: -1, out reason);
    }

    internal bool TryClaimHit(
        string nonce,
        string playerKey,
        long playerId,
        out string reason
    )
    {
        if (
            string.IsNullOrWhiteSpace(nonce)
            || string.IsNullOrWhiteSpace(playerKey)
            || nonce.Length > HostileShadowProtocol.MaximumIdentifierLength
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
            hitPlayerKeys.Contains(playerKey)
            || HasSettledPlayer(playerId)
        )
        {
            reason = "hostile-shadow.attack-hit-player-already-settled";
            return false;
        }
        if (
            nonces.Count >= MaximumLedgerEntries
            || hitPlayerKeys.Count >= MaximumLedgerEntries
            || (playerId >= 0 && hitPlayerIds.Count >= MaximumLedgerEntries)
        )
        {
            reason = "hostile-shadow.attack-hit-ledger-capacity";
            return false;
        }

        nonces.Add(nonce);
        hitPlayerKeys.Add(playerKey);
        if (playerId >= 0)
            hitPlayerIds.Add(playerId);
        reason = "hostile-shadow.attack-hit-ledger-claimed";
        return true;
    }
}
