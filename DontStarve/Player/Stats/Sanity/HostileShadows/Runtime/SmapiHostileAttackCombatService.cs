#nullable enable

using System;
using System.Collections.Generic;
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
    // DIAG-20260806: 攻击命中但结算被拒时记录一次原因（去重，不刷屏），
    // 用于定位“受击传送后无法对玩家造成伤害”。
    private readonly HashSet<string> loggedRejectedReasons =
        new(StringComparer.Ordinal);
    // DIAG-20260806: 攻击框未命中玩家的静默分支也记录（去重）——否则“影怪打不到人”无任何日志。
    private readonly HashSet<string> loggedMissReasons =
        new(StringComparer.Ordinal);
    // DIAG-20260809: 攻击 miss / 位置不匹配诊断黄字总开关。主策划要求暂时停用
    // （定位已收敛、黄字刷屏）；方法完整保留，下次需要排查时置 true 即可（或后续接 ds 命令）。
    internal static bool MissDiagnosticsEnabled = false;

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
                if (MissDiagnosticsEnabled && loggedMissReasons.Add("attack-hit.player-location-mismatch"))
                {
                    log(
                        string.Concat(
                            "hostile-shadow.attack-hit.player-location-mismatch (",
                            "stateId=",
                            state.StateId,
                            ", location=",
                            state.LocationId,
                            ")"
                        ),
                        LogLevel.Warn
                    );
                }
                continue;
            }

            var playerId = farmer.UniqueMultiplayerID;
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(playerId);
            if (!string.Equals(playerKey, state.TargetPlayerKey, StringComparison.Ordinal))
                continue;
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
            if (!attackBox.Intersects(targetBox))
            {
                // DIAG-20260807: 仅按攻击框（红框）判定命中，已移除二次射程检查
                // （AttackRangePixels）。此前红框 320px 而射程仅 128px，导致只有贴近
                // 怪物的受击框（绿框）区域能命中——“玩家站在绿框里受伤”的根因之一。
                // 主策划语义：红框=攻击判定区，玩家站在红框内就应受伤；
                // 绿框=受击判定区，玩家站在绿框内不应受伤（接触伤害另由 DamageToFarmer 控制）。
                var missKey = string.Concat(
                    "attack-hit.box-miss-or-out-of-range@",
                    ((int)monster.Position.X).ToString(CultureInfo.InvariantCulture),
                    ",",
                    ((int)monster.Position.Y).ToString(CultureInfo.InvariantCulture),
                    "@",
                    ((int)farmer.Position.X).ToString(CultureInfo.InvariantCulture),
                    ",",
                    ((int)farmer.Position.Y).ToString(CultureInfo.InvariantCulture)
                );
                if (MissDiagnosticsEnabled && loggedMissReasons.Add(missKey))
                {
                    var distX = targetStanding.X - attackerStanding.X;
                    var distY = targetStanding.Y - attackerStanding.Y;
                    var distance = Math.Sqrt(
                        (distX * distX) + (distY * distY)
                    );
                    log(
                        string.Concat(
                            "hostile-shadow.attack-hit.box-miss-or-out-of-range (",
                            "stateId=",
                            state.StateId,
                            ", frame=",
                            instance.FrameNumber,
                            ", box=",
                            attackBox.X.ToString(CultureInfo.InvariantCulture),
                            ",",
                            attackBox.Y.ToString(CultureInfo.InvariantCulture),
                            ",",
                            attackBox.Width.ToString(CultureInfo.InvariantCulture),
                            "x",
                            attackBox.Height.ToString(CultureInfo.InvariantCulture),
                            ", monster=",
                            ((int)monster.Position.X).ToString(CultureInfo.InvariantCulture),
                            ",",
                            ((int)monster.Position.Y).ToString(CultureInfo.InvariantCulture),
                            ", farmer=",
                            ((int)farmer.Position.X).ToString(CultureInfo.InvariantCulture),
                            ",",
                            ((int)farmer.Position.Y).ToString(CultureInfo.InvariantCulture),
                            ", dist=",
                            distance.ToString(CultureInfo.InvariantCulture),
                            ", range=",
                            profile.AttackRangePixels.ToString(CultureInfo.InvariantCulture),
                            ")"
                        ),
                        LogLevel.Warn
                    );
                }
                continue;
            }

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
            )
            {
                // DIAG-20260806: 攻击框命中玩家但结算被拒——记录原因（去重）供定位。
                if (loggedRejectedReasons.Add(receipt.Result.Reason))
                {
                    log(
                        receipt.Result.Status
                            == HostileAttackReceiptStatus.PipelineFailed
                            ? receipt.Result.Reason
                            : string.Concat(
                                "hostile-shadow.attack-hit-rejected (",
                                receipt.Result.Reason,
                                ", stateId=",
                                state.StateId,
                                ", frame=",
                                state.AttackFrameNumber,
                                ")"
                            ),
                        receipt.Result.Status
                            == HostileAttackReceiptStatus.PipelineFailed
                            ? LogLevel.Error
                            : LogLevel.Warn
                    );
                }
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
                // DIAG-20260807: MaximumRangePixels 改为攻击框覆盖范围（对角/2 + 玩家半宽余量）。
                // 原值 AttackRangeTiles×64=128px，TryProcess 里的 IsWithinRange 会拒绝红框
                // （320×320）偏外侧的命中——用户实测“只有贴着绿框才吃到 50 伤害”即此根因。
                // 主策划裁定：红框区域=想要的实际伤害范围，红框相交即命中。
                MaximumRangePixels = Math.Sqrt(
                    (attackBox.Width * attackBox.Width)
                    + (attackBox.Height * attackBox.Height)
                ) / 2d + 128d,
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
