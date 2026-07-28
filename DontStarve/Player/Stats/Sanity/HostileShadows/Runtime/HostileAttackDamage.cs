#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal enum HostileAttackReceiptStatus
{
    Applied,
    SettledWithoutHealthChange,
    Rejected,
    PipelineFailed,
}

internal sealed class HostileAttackResult
{
    internal HostileAttackResult(
        HostileAttackReceiptStatus status,
        int requestedDamage,
        int healthBefore,
        int healthAfter,
        bool pipelineInvoked,
        string reason
    )
    {
        Status = status;
        RequestedDamage = requestedDamage;
        HealthBefore = healthBefore;
        HealthAfter = healthAfter;
        PipelineInvoked = pipelineInvoked;
        Reason = reason;
    }

    internal HostileAttackReceiptStatus Status { get; }
    internal int RequestedDamage { get; }
    internal int HealthBefore { get; }
    internal int HealthAfter { get; }
    internal int AppliedDamage => Math.Max(0, HealthBefore - HealthAfter);
    internal bool PipelineInvoked { get; }
    internal string Reason { get; }
}

/// <summary>
/// Ordinary attack idempotency envelope: attack, entity, target, revision, and the settled result.
/// It deliberately carries no threshold-specific damage semantics.
/// </summary>
internal sealed class HostileAttackReceipt
{
    internal HostileAttackReceipt(
        string attackInstanceId,
        long entityId,
        string targetPlayerKey,
        long entityRevision,
        HostileAttackResult result
    )
    {
        AttackInstanceId = attackInstanceId;
        EntityId = entityId;
        TargetPlayerKey = targetPlayerKey;
        EntityRevision = entityRevision;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    internal string AttackInstanceId { get; }
    internal long EntityId { get; }
    internal string TargetPlayerKey { get; }
    internal long EntityRevision { get; }
    internal HostileAttackResult Result { get; }
}

internal interface IHostileAttackLethalDamagePipeline
{
    int CurrentHealth { get; }

    void ApplyOrdinaryDamage(int damage);
}

internal sealed class HostileAttackHitContext
{
    internal string SessionId { get; set; } = string.Empty;
    internal long EntityId { get; set; }
    internal long CurrentEntityRevision { get; set; }
    internal string LocationId { get; set; } = string.Empty;
    internal string StateId { get; set; } = string.Empty;
    internal int CurrentFrameNumber { get; set; }
    internal long TargetPlayerId { get; set; } = -1;
    internal HostileAttackInstance? Instance { get; set; }
    internal HostileAttackRuntimeDefinition? Definition { get; set; }
    internal HostileAttackRectangle AttackBox { get; set; }
    internal HostileAttackRectangle TargetBox { get; set; }
    internal HostileAttackPoint AttackerStanding { get; set; }
    internal HostileAttackPoint TargetStanding { get; set; }
    internal double MaximumRangePixels { get; set; }
    internal int Damage { get; set; }
}

/// <summary>
/// Validates the complete host context before claiming a bounded per-instance/player ledger entry.
/// Vanilla immunity may suppress HP change, but it never substitutes for this ledger.
/// </summary>
internal static class HostileAttackHitProcessor
{
    internal static bool TryProcess(
        ShadowAttackHitRequest? request,
        string expectedSenderPlayerKey,
        HostileAttackHitContext? context,
        IHostileAttackLethalDamagePipeline? pipeline,
        out HostileAttackReceipt receipt
    )
    {
        var reason = "hostile-shadow.attack-hit-context-missing";
        if (
            context is null
            || !HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                expectedSenderPlayerKey,
                context.SessionId,
                out reason
            )
        )
        {
            receipt = Rejected(request, context, reason);
            return false;
        }

        var instance = context.Instance;
        var definition = context.Definition;
        if (
            instance is null
            || definition is null
            || pipeline is null
            || context.EntityId != request!.EntityId
            || context.CurrentEntityRevision != request.ObservedEntityRevision
            || !string.Equals(
                context.LocationId,
                request.LocationId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                context.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            || !string.Equals(
                instance.InstanceId,
                request.AttackInstanceId,
                StringComparison.Ordinal
            )
            || instance.Revision != request.ObservedAttackInstanceRevision
            || instance.EntityId != request.EntityId
            || !string.Equals(
                instance.LocationId,
                request.LocationId,
                StringComparison.Ordinal
            )
            || context.CurrentFrameNumber != request.ObservedFrameNumber
            || instance.FrameNumber != request.ObservedFrameNumber
            || !definition.IsActiveFrame(request.ObservedFrameNumber)
            || context.Damage <= 0
            || !context.AttackBox.Intersects(context.TargetBox)
            || !HostileAttackCollisionResolver.IsWithinRange(
                context.AttackerStanding,
                context.TargetStanding,
                context.MaximumRangePixels
            )
        )
        {
            receipt = Rejected(
                request,
                context,
                "hostile-shadow.attack-hit-host-context-invalid"
            );
            return false;
        }

        if (
            !instance.TryClaimHit(
                request.Nonce,
                request.TargetPlayerKey,
                context.TargetPlayerId,
                out reason
            )
        )
        {
            receipt = Rejected(request, context, reason);
            return false;
        }

        var healthBefore = pipeline.CurrentHealth;
        try
        {
            pipeline.ApplyOrdinaryDamage(context.Damage);
        }
        catch (Exception exception)
        {
            receipt = new HostileAttackReceipt(
                instance.InstanceId,
                context.EntityId,
                request.TargetPlayerKey,
                context.CurrentEntityRevision,
                new HostileAttackResult(
                    HostileAttackReceiptStatus.PipelineFailed,
                    context.Damage,
                    healthBefore,
                    pipeline.CurrentHealth,
                    pipelineInvoked: true,
                    string.Concat(
                        "hostile-shadow.attack-damage-pipeline-threw-",
                        exception.GetType().Name
                    )
                )
            );
            return false;
        }

        var healthAfter = pipeline.CurrentHealth;
        receipt = new HostileAttackReceipt(
            instance.InstanceId,
            context.EntityId,
            request.TargetPlayerKey,
            context.CurrentEntityRevision,
            new HostileAttackResult(
                healthAfter == healthBefore
                    ? HostileAttackReceiptStatus.SettledWithoutHealthChange
                    : HostileAttackReceiptStatus.Applied,
                context.Damage,
                healthBefore,
                healthAfter,
                pipelineInvoked: true,
                healthAfter == healthBefore
                    ? "hostile-shadow.attack-hit-settled-without-health-change"
                    : "hostile-shadow.attack-hit-applied"
            )
        );
        return true;
    }

    private static HostileAttackReceipt Rejected(
        ShadowAttackHitRequest? request,
        HostileAttackHitContext? context,
        string reason
    )
    {
        var health = 0;
        return new HostileAttackReceipt(
            request?.AttackInstanceId ?? string.Empty,
            request?.EntityId ?? 0,
            request?.TargetPlayerKey ?? string.Empty,
            request?.ObservedEntityRevision ?? 0,
            new HostileAttackResult(
                HostileAttackReceiptStatus.Rejected,
                context?.Damage ?? 0,
                health,
                health,
                pipelineInvoked: false,
                string.IsNullOrWhiteSpace(reason)
                    ? "hostile-shadow.attack-hit-rejected"
                    : reason
            )
        );
    }

}
