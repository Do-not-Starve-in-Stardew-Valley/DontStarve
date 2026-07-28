#nullable enable

using System;
using System.Globalization;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Bounded SMAPI/Stardew bridge for host collision rechecks and ordinary Farmer damage. Attack
/// state, instance, frame, and ledger remain owned by the pure runtime types.
/// </summary>
internal sealed class SmapiHostileAttackCombatService
{
    private readonly HostileShadowAuthority authority;
    private readonly Action<string, LogLevel> log;

    internal SmapiHostileAttackCombatService(
        HostileShadowAuthority authority,
        Action<string, LogLevel> log
    )
    {
        this.authority = authority
            ?? throw new ArgumentNullException(nameof(authority));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal void ProcessCurrentHits(
        HostileShadowMonster monster,
        ShadowMonsterRuntimeProfile profile,
        HostileAttackRuntimeDefinition definition,
        HostileAttackStateMachine attackState,
        ShadowStateSnapshot state
    )
    {
        var instance = attackState.CurrentInstance;
        if (
            instance is null
            || !HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                monster.Position.X,
                monster.Position.Y,
                instance.Facing,
                out var attackBox
            )
        )
            return;

        var attackerStanding = new HostileAttackPoint(
            monster.StandingPixel.X,
            monster.StandingPixel.Y
        );
        var inspected = 0;
        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (inspected >= HostileShadowTargetingLimits.MaximumPlayers)
                break;
            inspected++;
            if (
                farmer.currentLocation is null
                || !string.Equals(
                    farmer.currentLocation.NameOrUniqueName,
                    state.LocationId,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            var playerId = farmer.UniqueMultiplayerID;
            // Automatic hits have no observable rejected receipt. Keep all allocation-free host
            // prechecks ahead of canonical keys and protocol/damage objects, then allocate once
            // only for the first legal settlement event.
            if (playerId < 0 || instance.HasSettledPlayer(playerId))
                continue;

            var farmerBoundingBox = farmer.GetBoundingBox();
            var targetBox = new HostileAttackRectangle(
                farmerBoundingBox.X,
                farmerBoundingBox.Y,
                farmerBoundingBox.Width,
                farmerBoundingBox.Height
            );
            var targetStanding = new HostileAttackPoint(
                farmer.StandingPixel.X,
                farmer.StandingPixel.Y
            );
            if (
                !attackBox.Intersects(targetBox)
                || !HostileAttackCollisionResolver.IsWithinRange(
                    attackerStanding,
                    targetStanding,
                    profile.AttackRangePixels
                )
            )
            {
                continue;
            }

            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                playerId
            );
            var request = new ShadowAttackHitRequest
            {
                SessionId = authority.SessionId,
                Nonce = string.Concat(
                    instance.InstanceId,
                    ":",
                    playerKey,
                    ":",
                    instance.FrameNumber.ToString(CultureInfo.InvariantCulture)
                ),
                EntityId = state.EntityId,
                TargetPlayerKey = playerKey,
                LocationId = state.LocationId,
                AttackInstanceId = instance.InstanceId,
                ObservedEntityRevision = state.Revision,
                ObservedAttackInstanceRevision = instance.Revision,
                ObservedFrameNumber = instance.FrameNumber,
            };
            if (
                !TryProcessHit(
                    monster,
                    profile,
                    definition,
                    state,
                    farmer,
                    instance,
                    playerKey,
                    request,
                    attackBox,
                    targetBox,
                    attackerStanding,
                    targetStanding,
                    out var receipt
                )
                && receipt.Result.Status
                    == HostileAttackReceiptStatus.PipelineFailed
            )
            {
                log(receipt.Result.Reason, LogLevel.Error);
            }
        }
    }

    internal bool TryProcessHit(
        HostileShadowMonster monster,
        ShadowMonsterRuntimeProfile profile,
        HostileAttackRuntimeDefinition definition,
        HostileAttackStateMachine attackState,
        ShadowStateSnapshot state,
        Farmer farmer,
        ShadowAttackHitRequest request,
        out HostileAttackReceipt receipt
    )
    {
        var instance = attackState.CurrentInstance;
        if (
            instance is null
            || !HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                monster.Position.X,
                monster.Position.Y,
                instance.Facing,
                out var attackBox
            )
        )
        {
            receipt = Rejected(
                request,
                "hostile-shadow.attack-box-unavailable"
            );
            return false;
        }

        var farmerBoundingBox = farmer.GetBoundingBox();
        var targetBox = new HostileAttackRectangle(
            farmerBoundingBox.X,
            farmerBoundingBox.Y,
            farmerBoundingBox.Width,
            farmerBoundingBox.Height
        );
        return TryProcessHit(
            monster,
            profile,
            definition,
            state,
            farmer,
            instance,
            SanityPlayerKey.FromUniqueMultiplayerId(
                farmer.UniqueMultiplayerID
            ),
            request,
            attackBox,
            targetBox,
            new HostileAttackPoint(
                monster.StandingPixel.X,
                monster.StandingPixel.Y
            ),
            new HostileAttackPoint(
                farmer.StandingPixel.X,
                farmer.StandingPixel.Y
            ),
            out receipt
        );
    }

    private bool TryProcessHit(
        HostileShadowMonster monster,
        ShadowMonsterRuntimeProfile profile,
        HostileAttackRuntimeDefinition definition,
        ShadowStateSnapshot state,
        Farmer farmer,
        HostileAttackInstance instance,
        string expectedPlayerKey,
        ShadowAttackHitRequest request,
        HostileAttackRectangle attackBox,
        HostileAttackRectangle targetBox,
        HostileAttackPoint attackerStanding,
        HostileAttackPoint targetStanding,
        out HostileAttackReceipt receipt
    )
    {
        return HostileAttackHitProcessor.TryProcess(
            request,
            expectedPlayerKey,
            new HostileAttackHitContext
            {
                SessionId = authority.SessionId,
                EntityId = state.EntityId,
                CurrentEntityRevision = state.Revision,
                LocationId = state.LocationId,
                StateId = state.StateId,
                CurrentFrameNumber = state.AttackFrameNumber,
                TargetPlayerId = farmer.UniqueMultiplayerID,
                Instance = instance,
                Definition = definition,
                AttackBox = attackBox,
                TargetBox = targetBox,
                AttackerStanding = attackerStanding,
                TargetStanding = targetStanding,
                MaximumRangePixels = profile.AttackRangePixels,
                Damage = profile.BaseDamage,
            },
            new SmapiHostileAttackDamageAdapter(farmer, monster),
            out receipt
        );
    }

    internal static HostileAttackReceipt Rejected(
        ShadowAttackHitRequest? request,
        string reason
    )
    {
        return new HostileAttackReceipt(
            request?.AttackInstanceId ?? string.Empty,
            request?.EntityId ?? 0,
            request?.TargetPlayerKey ?? string.Empty,
            request?.ObservedEntityRevision ?? 0,
            new HostileAttackResult(
                HostileAttackReceiptStatus.Rejected,
                requestedDamage: 0,
                healthBefore: 0,
                healthAfter: 0,
                pipelineInvoked: false,
                reason
            )
        );
    }
}
