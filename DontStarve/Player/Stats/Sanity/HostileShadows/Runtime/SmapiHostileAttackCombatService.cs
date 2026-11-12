#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Combat.InvincibilityFrames;
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
    private const int MaximumDiagnosticEntries = 256;
    private const int MaximumPendingRemoteDamageCommands = 256;

    // Attack diagnostics are deliberately bounded because this service runs on the fixed update
    // path. Re-enabling the command starts a fresh bounded sample for the next reproduction.
    private sealed class BoundedDiagnosticSet
    {
        private readonly HashSet<string> values = new(StringComparer.Ordinal);

        internal bool Add(string value)
        {
            if (values.Count >= MaximumDiagnosticEntries)
                return false;
            return values.Add(value);
        }

        internal void Clear() => values.Clear();
    }

    private readonly HostileShadowAuthority authority;
    private readonly SmapiInvincibilityFrameDamageService damageService;
    private readonly Action<string, LogLevel> log;
    private IHostileShadowDamageCommandSender? remoteDamageTransport;
    private readonly Dictionary<string, ShadowAttackDamageCommand>
        pendingRemoteDamageCommands = new(StringComparer.Ordinal);
    // DIAG-20260806: 攻击命中但结算被拒时记录一次原因（去重，不刷屏），
    // 用于定位“受击传送后无法对玩家造成伤害”。
    private readonly BoundedDiagnosticSet loggedRejectedReasons = new();
    // DIAG-20260806: 攻击框未命中玩家的静默分支也记录（去重）——否则“影怪打不到人”无任何日志。
    private readonly BoundedDiagnosticSet loggedMissReasons = new();
    private readonly BoundedDiagnosticSet loggedDecisionKeys = new();
    // DIAG-20260809: 攻击 miss / 位置不匹配诊断总开关。默认关闭；通过 ds_attacklog on
    // 开启一次有界样本，不让正常游玩固定产生攻击诊断日志。
    internal static bool MissDiagnosticsEnabled = false;
    private static int missDiagnosticsEpoch;
    internal static int MissDiagnosticsEpoch => missDiagnosticsEpoch;

    internal static void SetMissDiagnosticsEnabled(bool enabled)
    {
        if (MissDiagnosticsEnabled == enabled)
            return;
        MissDiagnosticsEnabled = enabled;
        missDiagnosticsEpoch++;
    }

    private int observedMissDiagnosticsEpoch = -1;

    internal SmapiHostileAttackCombatService(
        HostileShadowAuthority authority,
        SmapiInvincibilityFrameDamageService damageService,
        Action<string, LogLevel> log
    )
    {
        this.authority = authority
            ?? throw new ArgumentNullException(nameof(authority));
        this.damageService = damageService
            ?? throw new ArgumentNullException(nameof(damageService));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool BindRemoteDamageTransport(
        IHostileShadowDamageCommandSender transport,
        out string reason
    )
    {
        if (transport is null)
        {
            reason = "hostile-shadow.damage-transport-null";
            return false;
        }

        if (remoteDamageTransport is not null)
        {
            reason = "hostile-shadow.damage-transport-already-bound";
            return false;
        }

        remoteDamageTransport = transport;
        reason = "hostile-shadow.damage-transport-bound";
        return true;
    }

    internal void ClearPendingRemoteDamage()
    {
        pendingRemoteDamageCommands.Clear();
    }

    internal void ProcessCurrentHits(
        HostileShadowMonster monster,
        ShadowMonsterRuntimeProfile profile,
        HostileAttackRuntimeDefinition definition,
        HostileAttackStateMachine attackState,
        ShadowStateSnapshot state
    )
    {
        SynchronizeDiagnosticState();
        var instance = attackState.CurrentInstance;
        if (instance is null)
        {
            LogAttackPreflightDecision(
                "attack-instance-missing",
                state.EntityId,
                state.StateId,
                string.Empty,
                0,
                string.Empty,
                string.Empty,
                LogLevel.Warn
            );
            return;
        }

        if (!definition.IsActiveFrame(instance.FrameNumber))
        {
            LogAttackPreflightDecision(
                "inactive-frame",
                state.EntityId,
                state.StateId,
                instance.InstanceId,
                instance.FrameNumber,
                string.Empty,
                string.Empty,
                LogLevel.Info
            );
            return;
        }

        // 激活帧必须能解析出唯一命中窗口（20261113 冻结决议 5）；解析失败按不变量违例
        // fail-closed 跳过本帧结算，绝不退化成无窗口命中。
        if (
            !definition.TryGetWindowForFrame(
                instance.FrameNumber,
                out var preflightHitWindow
            )
            || preflightHitWindow is null
        )
        {
            LogAttackPreflightDecision(
                "hit-window-missing",
                state.EntityId,
                state.StateId,
                instance.InstanceId,
                instance.FrameNumber,
                string.Empty,
                string.Empty,
                LogLevel.Warn
            );
            return;
        }

        if (
            !HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                monster.Position.X,
                monster.Position.Y,
                instance.Facing,
                out var attackBox
            )
        )
        {
            LogAttackPreflightDecision(
                "attack-box-unavailable",
                state.EntityId,
                state.StateId,
                instance.InstanceId,
                instance.FrameNumber,
                string.Empty,
                string.Empty,
                LogLevel.Warn
            );
            return;
        }

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
                        FormatPlayerLocationMismatch(farmer, state),
                        LogLevel.Warn
                    );
                }
                continue;
            }

            var playerId = farmer.UniqueMultiplayerID;
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(playerId);
            if (!string.Equals(playerKey, state.TargetPlayerKey, StringComparison.Ordinal))
            {
                LogAttackPreflightDecision(
                    "target-mismatch",
                    state.EntityId,
                    state.StateId,
                    instance.InstanceId,
                    instance.FrameNumber,
                    playerKey,
                    state.TargetPlayerKey,
                    LogLevel.Info
                );
                continue;
            }
            // Automatic hits have no observable rejected receipt. Keep all allocation-free host
            // prechecks ahead of canonical keys and protocol/damage objects, then allocate once
            // only for the first legal settlement event.
            if (playerId == 0)
            {
                LogAttackPreflightDecision(
                    "invalid-player-id",
                    state.EntityId,
                    state.StateId,
                    instance.InstanceId,
                    instance.FrameNumber,
                    playerKey,
                    state.TargetPlayerKey,
                    LogLevel.Warn
                );
                continue;
            }
            if (
                instance.HasSettledPlayerInWindow(
                    preflightHitWindow.WindowId,
                    playerId
                )
            )
            {
                LogAttackPreflightDecision(
                    "player-already-settled",
                    state.EntityId,
                    state.StateId,
                    instance.InstanceId,
                    instance.FrameNumber,
                    playerKey,
                    state.TargetPlayerKey,
                    LogLevel.Info
                );
                continue;
            }

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
                    state.EntityId.ToString(CultureInfo.InvariantCulture),
                    "@",
                    instance.InstanceId,
                    "@",
                    instance.FrameNumber.ToString(CultureInfo.InvariantCulture),
                    "@",
                    playerKey,
                    "@",
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
                if (MissDiagnosticsEnabled)
                {
                    LogAttackDecision(
                        string.Concat(
                            "attack-decision:rejected:",
                            state.EntityId.ToString(CultureInfo.InvariantCulture),
                            ":",
                            instance.InstanceId,
                            ":",
                            instance.FrameNumber.ToString(CultureInfo.InvariantCulture),
                            ":",
                            playerKey,
                            ":",
                            receipt.Result.Reason
                        ),
                        FormatAttackDecision(
                            "rejected",
                            state,
                            instance,
                            playerKey,
                            receipt
                        ),
                        receipt.Result.Status == HostileAttackReceiptStatus.PipelineFailed
                            ? LogLevel.Error
                            : LogLevel.Warn
                    );
                }
            }
            else if (MissDiagnosticsEnabled)
            {
                LogAttackDecision(
                    string.Concat(
                        "attack-decision:accepted:",
                        state.EntityId.ToString(CultureInfo.InvariantCulture),
                        ":",
                        instance.InstanceId,
                        ":",
                        instance.FrameNumber.ToString(CultureInfo.InvariantCulture),
                        ":",
                        playerKey
                    ),
                    FormatAttackDecision(
                        receipt.Result.Status == HostileAttackReceiptStatus.Applied
                            ? "applied"
                            : "settled-without-health-change",
                        state,
                        instance,
                        playerKey,
                        receipt
                    ),
                    LogLevel.Info
                );
            }
        }
    }

    private void LogAttackPreflightDecision(
        string reason,
        long entityId,
        string stateId,
        string attackInstanceId,
        int frameNumber,
        string playerKey,
        string lockedTargetPlayerKey,
        LogLevel level
    )
    {
        if (!MissDiagnosticsEnabled)
            return;

        var instancePart = string.IsNullOrEmpty(attackInstanceId)
            ? "<none>"
            : attackInstanceId;
        var playerPart = string.IsNullOrEmpty(playerKey) ? "<none>" : playerKey;
        var targetPart = string.IsNullOrEmpty(lockedTargetPlayerKey)
            ? "<none>"
            : lockedTargetPlayerKey;
        LogAttackDecision(
            string.Concat(
                "attack-decision:",
                reason,
                ":",
                entityId.ToString(CultureInfo.InvariantCulture),
                ":",
                instancePart,
                ":",
                frameNumber.ToString(CultureInfo.InvariantCulture),
                ":",
                playerPart
            ),
            string.Concat(
                "hostile-shadow.attack-decision (result=skip, reason=",
                reason,
                ", entity=",
                entityId.ToString(CultureInfo.InvariantCulture),
                ", state=",
                stateId,
                ", attackInstance=",
                instancePart,
                ", frame=",
                frameNumber.ToString(CultureInfo.InvariantCulture),
                ", candidatePlayer=",
                playerPart,
                ", lockedTarget=",
                targetPart,
                ")"
            ),
            level
        );
    }

    private static string FormatPlayerLocationMismatch(
        Farmer farmer,
        ShadowStateSnapshot state
    )
    {
        return string.Concat(
            "hostile-shadow.attack-hit.player-location-mismatch (player=",
            farmer.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture),
            ", currentLocation=",
            farmer.currentLocation?.NameOrUniqueName ?? "<null>",
            ", stateId=",
            state.StateId,
            ", location=",
            state.LocationId,
            ")"
        );
    }

    private void SynchronizeDiagnosticState()
    {
        if (observedMissDiagnosticsEpoch == MissDiagnosticsEpoch)
            return;
        observedMissDiagnosticsEpoch = MissDiagnosticsEpoch;
        loggedRejectedReasons.Clear();
        loggedMissReasons.Clear();
        loggedDecisionKeys.Clear();
    }

    private void LogAttackDecision(string key, string message, LogLevel level)
    {
        if (!MissDiagnosticsEnabled || !loggedDecisionKeys.Add(key))
            return;
        log(message, level);
    }

    private static string FormatAttackDecision(
        string result,
        ShadowStateSnapshot state,
        HostileAttackInstance instance,
        string playerKey,
        HostileAttackReceipt receipt
    )
    {
        var attackResult = receipt.Result;
        return string.Concat(
            "hostile-shadow.attack-decision (result=",
            result,
            ", entity=",
            state.EntityId.ToString(CultureInfo.InvariantCulture),
            ", attackInstance=",
            instance.InstanceId,
            ", frame=",
            instance.FrameNumber.ToString(CultureInfo.InvariantCulture),
            ", player=",
            playerKey,
            ", reason=",
            attackResult.Reason,
            ", status=",
            attackResult.Status.ToString(),
            ", requestedDamage=",
            attackResult.RequestedDamage.ToString(CultureInfo.InvariantCulture),
            ", appliedDamage=",
            attackResult.AppliedDamage.ToString(CultureInfo.InvariantCulture),
            ", health=",
            attackResult.HealthBefore.ToString(CultureInfo.InvariantCulture),
            "->",
            attackResult.HealthAfter.ToString(CultureInfo.InvariantCulture),
            ", pipelineInvoked=",
            attackResult.PipelineInvoked.ToString(),
            ")"
        );
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
        // 命中窗口是策略的唯一来源：DamagePolicy 必须来自当前帧所属窗口的定义，
        // 不允许任何调用方硬编码 Normal/Strong/Weak 绕过资源声明（冻结决议 6）。
        if (
            !definition.TryGetWindowForFrame(
                instance.FrameNumber,
                out var hitWindow
            )
            || hitWindow is null
        )
        {
            receipt = Rejected(
                request,
                "hostile-shadow.attack-hit-window-missing"
            );
            return false;
        }

        var context = new HostileAttackHitContext
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
            DamagePolicy = hitWindow.Policy,
            HitWindowId = hitWindow.WindowId,
        };

        if (!farmer.IsLocalPlayer)
        {
            return TryDispatchRemoteHit(
                monster,
                farmer,
                request,
                expectedPlayerKey,
                context,
                out receipt
            );
        }

        return HostileAttackHitProcessor.TryProcess(
            request,
            expectedPlayerKey,
            context,
            new SmapiHostileAttackDamageAdapter(
                farmer,
                monster,
                damageService
            ),
            out receipt
        );
    }

    private bool TryDispatchRemoteHit(
        HostileShadowMonster monster,
        Farmer farmer,
        ShadowAttackHitRequest request,
        string expectedPlayerKey,
        HostileAttackHitContext context,
        out HostileAttackReceipt receipt
    )
    {
        if (remoteDamageTransport is null)
        {
            receipt = Rejected(
                request,
                "hostile-shadow.damage-transport-unavailable"
            );
            return false;
        }

        if (pendingRemoteDamageCommands.Count >= MaximumPendingRemoteDamageCommands)
        {
            receipt = Rejected(
                request,
                "hostile-shadow.damage-transport-pending-capacity"
            );
            return false;
        }

        if (
            !HostileAttackHitProcessor.TryValidateAndClaim(
                request,
                expectedPlayerKey,
                context,
                out var validationReason
            )
        )
        {
            receipt = Rejected(request, validationReason);
            return false;
        }

        var command = new ShadowAttackDamageCommand
        {
            SessionId = context.SessionId,
            Nonce = request.Nonce,
            EntityId = context.EntityId,
            TargetPlayerKey = request.TargetPlayerKey,
            LocationId = request.LocationId,
            AttackInstanceId = request.AttackInstanceId,
            ObservedEntityRevision = request.ObservedEntityRevision,
            ObservedAttackInstanceRevision = request.ObservedAttackInstanceRevision,
            ObservedFrameNumber = request.ObservedFrameNumber,
            Damage = context.Damage,
            PolicyMode = (int)context.DamagePolicy.Mode,
            PolicyDurationMilliseconds = context.DamagePolicy.DurationMilliseconds,
            HitWindowId = context.HitWindowId,
        };
        pendingRemoteDamageCommands[command.Nonce] = command;
        if (
            !remoteDamageTransport.SendAttackDamageCommand(
                command,
                farmer.UniqueMultiplayerID,
                out var sendReason
            )
        )
        {
            pendingRemoteDamageCommands.Remove(command.Nonce);
            receipt = Rejected(request, sendReason);
            return false;
        }

        receipt = new HostileAttackReceipt(
            request.AttackInstanceId,
            request.EntityId,
            request.TargetPlayerKey,
            request.ObservedEntityRevision,
            new HostileAttackResult(
                HostileAttackReceiptStatus.PendingRemote,
                context.Damage,
                farmer.health,
                farmer.health,
                pipelineInvoked: false,
                "hostile-shadow.attack-damage-command-pending"
            )
        );
        return true;
    }

    internal bool TryHandleRemoteDamageResult(
        ShadowAttackDamageResult result,
        long senderPlayerId,
        out string reason
    )
    {
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            senderPlayerId
        );
        if (
            !HostileShadowProtocol.IsValidAttackDamageResult(
                result,
                expectedPlayerKey,
                authority.SessionId,
                out reason
            )
        )
        {
            return false;
        }

        if (!pendingRemoteDamageCommands.TryGetValue(result.Nonce, out var command))
        {
            reason = "hostile-shadow.damage-result-pending-command-missing";
            return false;
        }

        if (
            command.EntityId != result.EntityId
            || command.ObservedEntityRevision != result.ObservedEntityRevision
            || command.ObservedAttackInstanceRevision
                != result.ObservedAttackInstanceRevision
            || command.ObservedFrameNumber != result.ObservedFrameNumber
            || !string.Equals(
                command.LocationId,
                result.LocationId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                command.TargetPlayerKey,
                result.TargetPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.damage-result-command-mismatch";
            return false;
        }

        pendingRemoteDamageCommands.Remove(result.Nonce);
        reason = "hostile-shadow.damage-result-accepted";
        return true;
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
