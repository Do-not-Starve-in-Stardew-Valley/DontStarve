#nullable enable

using System;
using DontStarve.Player.Combat.InvincibilityFrames;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal enum HostileAttackReceiptStatus
{
    Applied,
    SettledWithoutHealthChange,
    PendingRemote,
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

    void ApplyOrdinaryDamage(
        int damage,
        InvincibilityFramePolicy policy
    );
}

internal sealed class HostileAttackHitContext
{
    internal string SessionId { get; set; } = string.Empty;
    internal long EntityId { get; set; }
    internal long CurrentEntityRevision { get; set; }
    internal string LocationId { get; set; } = string.Empty;
    internal string StateId { get; set; } = string.Empty;
    internal int CurrentFrameNumber { get; set; }
    // Zero is the no-player sentinel; every non-zero signed long is a legal Stardew player ID.
    internal long TargetPlayerId { get; set; }
    internal InvincibilityFramePolicy DamagePolicy { get; set; } =
        InvincibilityFramePolicy.Normal;
    /// <summary>
    /// 命中窗口 ID（冻结决议 5）。默认空串 + 校验强制非空：调用方忘记解析窗口时按
    /// fail-closed 拒绝，绝不静默把命中记进任意窗口。
    /// </summary>
    internal string HitWindowId { get; set; } = string.Empty;
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
    internal static bool TryValidateAndClaim(
        ShadowAttackHitRequest? request,
        string expectedSenderPlayerKey,
        HostileAttackHitContext? context,
        out string reason
    )
    {
        reason = "hostile-shadow.attack-hit-context-missing";
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
            return false;
        }

        var instance = context.Instance!;
        var definition = context.Definition;
        if (
            instance is null
            || definition is null
            || string.IsNullOrWhiteSpace(context.HitWindowId)
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
            reason = "hostile-shadow.attack-hit-host-context-invalid";
            return false;
        }

        return instance.TryClaimHit(
            request.Nonce,
            request.TargetPlayerKey,
            context.TargetPlayerId,
            context.HitWindowId,
            out reason
        );
    }

    internal static bool TryProcess(
        ShadowAttackHitRequest? request,
        string expectedSenderPlayerKey,
        HostileAttackHitContext? context,
        IHostileAttackLethalDamagePipeline? pipeline,
        out HostileAttackReceipt receipt
    )
    {
        if (
            !TryValidateAndClaim(
                request,
                expectedSenderPlayerKey,
                context,
                out var reason
            )
        )
        {
            receipt = Rejected(request, context, reason);
            return false;
        }

        if (context is null || context.Instance is null || pipeline is null)
        {
            receipt = Rejected(request, context, "hostile-shadow.attack-hit-pipeline-invalid");
            return false;
        }

        var instance = context.Instance;

        var healthBefore = pipeline.CurrentHealth;
        try
        {
            pipeline.ApplyOrdinaryDamage(context.Damage, context.DamagePolicy);
        }
        catch (Exception exception)
        {
            receipt = new HostileAttackReceipt(
                instance.InstanceId,
                context.EntityId,
                request!.TargetPlayerKey,
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
            request!.TargetPlayerKey,
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
