#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Stats.Sanity.Damage;
using DontStarve.Player.Stats.Sanity.PassOut.Damage;
using DontStarve.Resource.Sanity;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Runtime bridge from the stage-06 expiry event to the pure settlement authority. The bridge is
/// synchronous on the owning SMAPI screen so split-screen Game1.player identity remains explicit.
/// </summary>
internal sealed class SmapiDarknessAttackResolutionService : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SmapiDarknessAttackService darknessAttack;
    private readonly IDarknessDamageModeResolver modeResolver;
    private readonly SanitySmapiResourceService resources;
    private readonly NonLethalDamageService nonLethalDamage;
    private readonly ISanityDarknessSpecialDeathNonLethalPolicy
        specialDeathNonLethal;
    private readonly DarknessAttackResolutionService resolution;
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private bool disposed;

    internal SmapiDarknessAttackResolutionService(
        IModHelper helper,
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle,
        SmapiDarknessAttackService darknessAttack,
        IDarknessDamageModeResolver modeResolver,
        SanitySmapiResourceService resources
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.darknessAttack = darknessAttack
            ?? throw new ArgumentNullException(nameof(darknessAttack));
        this.modeResolver = modeResolver
            ?? throw new ArgumentNullException(nameof(modeResolver));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));

        Func<string, Farmer?> resolvePlayer = ResolveCurrentScreenPlayer;
        nonLethalDamage = new NonLethalDamageService(
            new SmapiApplyDamageUpToFloorExecutor(resolvePlayer),
            new SmapiReduceToFloorExecutor(resolvePlayer)
        );
        specialDeathNonLethal = new SanityDarknessSpecialDeathNonLethalPolicy(
            nonLethalDamage
        );
        var damageAuthority = new SmapiDarknessAttackDamageAuthority(
            lifecycle,
            nonLethalDamage,
            new SmapiDefaultDarknessDamageExecutor(resolvePlayer)
        );
        resolution = new DarknessAttackResolutionService(
            new SystemDarknessAttackResolutionRandom(),
            damageAuthority,
            new SmapiDarknessAttackSanityAuthority(lifecycle)
        );

        darknessAttack.ExpiryIntentCreated += OnExpiryIntentCreated;
        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
    }

    /// <summary>
    /// Narrow future pass-out integration seam. It consumes the existing lifecycle authority and
    /// starts the same task-family-06 receipt window before delegating to the fixed-purpose policy;
    /// it does not classify a pass-out reason or play presentation effects.
    /// </summary>
    internal SanityDarknessSpecialDeathNonLethalResult
        ReduceSanityDarknessSpecialDeathToFloor(
            SanityDarknessSpecialDeathNonLethalRequest request
        )
    {
        if (disposed)
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.RuntimeDisposed
            );
        }
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || request.Authority != lifecycle.AuthorityRole
        )
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.HostAuthorityRequired
            );
        }
        if (
            !string.Equals(
                lifecycle.SessionId,
                request.SessionId,
                StringComparison.Ordinal
            )
        )
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                SanityDarknessSpecialDeathNonLethalReasonIds.RuntimeSessionMismatch
            );
        }

        var session = nonLethalDamage.BeginSession(request.SessionId);
        if (!session.Accepted)
        {
            return SanityDarknessSpecialDeathNonLethalResult.Rejected(
                session.Reason
            );
        }

        return specialDeathNonLethal.ReduceToFloor(request);
    }

    internal bool TryGetSnapshot(
        string playerKey,
        int screenId,
        out DarknessAttackResolutionReceipt receipt
    )
    {
        if (!disposed && resolution.TryGetLatest(playerKey, screenId, out receipt))
            return true;
        receipt = null!;
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        darknessAttack.ExpiryIntentCreated -= OnExpiryIntentCreated;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        ClearSession();
        loggedFailures.Clear();
    }

    private void OnExpiryIntentCreated(DarknessAttackExpiryIntent intent)
    {
        try
        {
            HandleExpiryIntentCreated(intent);
        }
        catch (Exception exception)
        {
            var reason = $"darkness.resolution.runtime-threw:{exception.GetType().Name}";
            LogFailureOnce(
                reason,
                $"Darkness settlement runtime failed closed ({exception.GetType().Name}: {exception.Message})."
            );
            try
            {
                RejectIntent(intent, reason);
            }
            catch (Exception receiptException)
            {
                LogFailureOnce(
                    $"{reason}:receipt",
                    $"Darkness settlement rejection receipt failed ({receiptException.GetType().Name}: {receiptException.Message})."
                );
            }
        }
    }

    private void HandleExpiryIntentCreated(DarknessAttackExpiryIntent intent)
    {
        if (disposed)
            return;

        var mode = modeResolver.Resolve();
        if (!mode.HasValue)
        {
            RejectIntent(intent, mode.Reason);
            LogFailureOnce(
                mode.Reason,
                $"Darkness settlement config failed closed (reason={mode.Reason})."
            );
            return;
        }
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !string.Equals(
                lifecycle.SessionId,
                intent.Key.SessionId,
                StringComparison.Ordinal
            )
            || !lifecycle.TryGetBaseSnapshot(intent.Key.PlayerKey, out var sanitySnapshot)
        )
        {
            RejectIntent(intent, "darkness.resolution.authority-snapshot-unavailable");
            LogFailureOnce(
                "darkness.resolution.authority-snapshot-unavailable",
                "Darkness settlement failed closed because the host Sanity snapshot was unavailable."
            );
            return;
        }

        var resolutionSession = resolution.BeginSession(intent.Key.SessionId);
        var damageSession = nonLethalDamage.BeginSession(intent.Key.SessionId);
        if (!resolutionSession.Accepted || !damageSession.Accepted)
        {
            var reason = !resolutionSession.Accepted
                ? resolutionSession.Reason
                : damageSession.Reason;
            RejectIntent(intent, reason);
            LogFailureOnce(
                reason,
                $"Darkness settlement session failed closed (reason={reason})."
            );
            return;
        }

        var result = resolution.Resolve(
            new DarknessAttackResolutionRequest(
                intent,
                mode.Mode,
                lifecycle.AuthorityRole,
                sanitySnapshot.Revision
            )
        );
        var originalSettled = result.Status == DarknessAttackResolutionStatus.Settled;
        if (originalSettled)
            darknessAttack.ShowResolvedPrompt(intent.Key);

        var disposition = result.Receipt?.IsSettled == true
            ? DarknessAttackReceiptDisposition.Applied
            : DarknessAttackReceiptDisposition.Rejected;
        darknessAttack.CompleteReceipt(
            new DarknessAttackReceipt(
                intent.Key,
                intent.RequestId,
                disposition,
                result.Reason
            )
        );

        var receipt = result.Receipt;
        if (receipt is not null && result.Status != DarknessAttackResolutionStatus.Duplicate)
        {
            LogDiagnosticOnce(
                originalSettled
                    ? $"settled:{receipt.Mode}:{receipt.BaseDamage}"
                    : result.Reason,
                $"Darkness settlement (request={receipt.RequestId}, mode={receipt.Mode}, roll={receipt.RngRoll}, base={receipt.BaseDamage}, operation={receipt.Operation}, receipt={receipt.ReceiptId}, status={result.Status}, sanity-revision={receipt.SanityReceipt?.AfterRevision.ToString() ?? "none"}, reason={result.Reason}).",
                originalSettled ? LogLevel.Debug : LogLevel.Warn
            );
        }
        else if (result.Status != DarknessAttackResolutionStatus.Duplicate)
        {
            LogFailureOnce(
                result.Reason,
                $"Darkness settlement failed closed (status={result.Status}, reason={result.Reason})."
            );
        }
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;
        if (stateEvent.Kind == SanityStateEventKind.SystemDisabled)
        {
            ClearSession();
            return;
        }
        if (stateEvent.Kind == SanityStateEventKind.OwnerInvalidated)
            resolution.ForgetDiagnosticOwner(stateEvent.PlayerKey);
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (!disposed && boundary == SanityWorldBoundary.DayStarted)
            ClearSession();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        _ = boundary;
        if (!disposed)
            ClearSession();
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (disposed)
            return;
        if (reason == SanityResourceReleaseReason.Dispose)
        {
            Dispose();
            return;
        }
        ClearSession();
    }

    private void ClearSession()
    {
        resolution.ClearSession();
        nonLethalDamage.ClearSession();
    }

    private void RejectIntent(DarknessAttackExpiryIntent intent, string reason)
    {
        darknessAttack.CompleteReceipt(
            new DarknessAttackReceipt(
                intent.Key,
                intent.RequestId,
                DarknessAttackReceiptDisposition.Rejected,
                string.IsNullOrWhiteSpace(reason)
                    ? "darkness.resolution.rejected"
                    : reason
            )
        );
    }

    private void LogFailureOnce(string code, string message)
    {
        LogDiagnosticOnce(code, message, LogLevel.Error);
    }

    private void LogDiagnosticOnce(string code, string message, LogLevel level)
    {
        if (loggedFailures.Count >= 32 || !loggedFailures.Add(code))
            return;
        monitor.Log(message, level);
    }

    private static Farmer? ResolveCurrentScreenPlayer(string playerKey)
    {
        var player = Game1.player;
        if (
            player is not null
            && string.Equals(
            SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
            playerKey,
            StringComparison.Ordinal
            )
        )
        {
            return player;
        }

        // The same host-owned service may receive a 2:00 request from an online farmhand. This
        // is still one resolver and one receipt registry; ordinary local-screen darkness calls
        // continue to resolve their existing Game1.player first.
        if (
            !Game1.IsMasterGame
            || !long.TryParse(
                playerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var multiplayerId
            )
        )
        {
            return null;
        }
        return Game1.GetPlayer(multiplayerId, true);
    }
}

internal sealed class SystemDarknessAttackResolutionRandom
    : IDarknessAttackResolutionRandom
{
    public int NextRoll100()
    {
        return Random.Shared.Next(100);
    }
}

internal sealed class SmapiDefaultDarknessDamageExecutor
{
    private readonly Func<string, Farmer?> resolvePlayer;

    internal SmapiDefaultDarknessDamageExecutor(Func<string, Farmer?> resolvePlayer)
    {
        this.resolvePlayer = resolvePlayer ?? throw new ArgumentNullException(nameof(resolvePlayer));
    }

    internal DarknessAttackDamageReceipt Execute(DarknessAttackDamageRequest request)
    {
        Farmer? player;
        try
        {
            player = resolvePlayer(request.Key.PlayerKey);
        }
        catch (Exception exception)
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                $"darkness.default.player-resolver-threw:{exception.GetType().Name}"
            );
        }
        if (player is null)
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.default.player-unavailable"
            );
        }
        if (
            !string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                request.Key.PlayerKey,
                StringComparison.Ordinal
            )
            || player.maxHealth <= 0
            || player.health <= 0
            || player.health > player.maxHealth
        )
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.default.player-health-invalid"
            );
        }

        var beforeHealth = player.health;
        var maximumHealth = player.maxHealth;
        // Default mode deliberately enters the verified vanilla physical pipeline. A normal return
        // is a settled attack even when CanBeDamaged/Yoba/iframes leave actual HP unchanged.
        player.takeDamage(request.BaseDamage, overrideParry: false, damager: null!);
        if (
            player.maxHealth != maximumHealth
            || player.health < 0
            || player.health > maximumHealth
        )
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.default.post-damage-health-invalid",
                beforeHealth,
                maximumHealth
            );
        }
        var actualDamage = Math.Max(0, beforeHealth - player.health);
        return DarknessAttackDamageReceipt.Settled(
            request,
            beforeHealth,
            maximumHealth,
            actualDamage,
            player.health,
            actualDamage == 0
                ? "darkness.default.physical-pipeline-settled-no-hp-delta"
                : "darkness.default.physical-pipeline-settled"
        );
    }
}

internal sealed class SmapiDarknessAttackDamageAuthority
    : IDarknessAttackDamageAuthority
{
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly INonLethalDamageService nonLethalDamage;
    private readonly SmapiDefaultDarknessDamageExecutor defaultExecutor;

    internal SmapiDarknessAttackDamageAuthority(
        SanitySystemLifecycleCoordinator lifecycle,
        INonLethalDamageService nonLethalDamage,
        SmapiDefaultDarknessDamageExecutor defaultExecutor
    )
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.nonLethalDamage = nonLethalDamage
            ?? throw new ArgumentNullException(nameof(nonLethalDamage));
        this.defaultExecutor = defaultExecutor
            ?? throw new ArgumentNullException(nameof(defaultExecutor));
    }

    public DarknessAttackDamageReceipt Settle(DarknessAttackDamageRequest request)
    {
        if (
            !lifecycle.IsEnabled
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !string.Equals(
                lifecycle.SessionId,
                request.Key.SessionId,
                StringComparison.Ordinal
            )
            || !lifecycle.TryGetBaseSnapshot(request.Key.PlayerKey, out var sanitySnapshot)
            || sanitySnapshot.Revision != request.SanityAuthorityRevision
            || (sanitySnapshot.Current > 0d && sanitySnapshot.Revision == long.MaxValue)
        )
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.resolution.damage-authority-preflight-failed"
            );
        }
        if (request.Operation == DarknessAttackDamageOperation.DefaultPhysical)
            return defaultExecutor.Execute(request);
        if (request.Operation != DarknessAttackDamageOperation.ApplyDamageUpToFloor)
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.resolution.damage-operation-invalid"
            );
        }

        var player = ResolveCurrentPlayer(request.Key.PlayerKey);
        if (player is null || player.maxHealth <= 0 || player.health < 0)
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                "darkness.nonlethal.player-unavailable"
            );
        }
        var result = nonLethalDamage.ApplyDamageUpToFloor(
            new ApplyDamageUpToFloorRequest(
                new NonLethalDamageContext(
                    request.Key.SessionId,
                    request.ReceiptId,
                    request.Key.PlayerKey,
                    NonLethalDamagePurpose.DarknessAttack,
                    SanityAuthorityRole.Host,
                    request.SanityAuthorityRevision
                ),
                player.health,
                player.maxHealth,
                request.BaseDamage
            )
        );
        var receipt = result.Receipt;
        if (
            receipt is null
            || receipt.Operation != NonLethalDamageOperation.ApplyDamageUpToFloor
            || !receipt.IsInvariantSatisfied(out _)
        )
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                $"darkness.nonlethal.receipt-unavailable:{result.Reason}"
            );
        }
        if (
            result.Status is not NonLethalDamageResultStatus.Applied
                and not NonLethalDamageResultStatus.Duplicate
            || receipt.Outcome != NonLethalDamageReceiptOutcome.Applied
            || receipt.AppliedDamage <= 0
        )
        {
            return DarknessAttackDamageReceipt.Rejected(
                request,
                $"darkness.nonlethal.floor-reached-or-rejected:{result.Reason}",
                receipt.BeforeHealth,
                receipt.MaximumHealth
            );
        }
        return DarknessAttackDamageReceipt.Settled(
            request,
            receipt.BeforeHealth,
            receipt.MaximumHealth,
            receipt.AppliedDamage,
            receipt.AfterHealth,
            receipt.Reason
        );
    }

    private static Farmer? ResolveCurrentPlayer(string playerKey)
    {
        var player = Game1.player;
        if (
            player is not null
            && string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                playerKey,
                StringComparison.Ordinal
            )
        )
        {
            return player;
        }
        if (
            !Game1.IsMasterGame
            || !long.TryParse(
                playerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var multiplayerId
            )
        )
        {
            return null;
        }
        return Game1.GetPlayer(multiplayerId, onlyOnline: true);
    }
}

internal sealed class SmapiDarknessAttackSanityAuthority
    : IDarknessAttackSanityAuthority
{
    private readonly SanitySystemLifecycleCoordinator lifecycle;

    internal SmapiDarknessAttackSanityAuthority(
        SanitySystemLifecycleCoordinator lifecycle
    )
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public DarknessAttackSanityReceipt Apply(DarknessAttackSanityRequest request)
    {
        if (
            request.Source != SanityChangeSource.DarknessAttack
            || request.Delta != DarknessAttackResolutionService.SanityDelta
            || !lifecycle.IsEnabled
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !string.Equals(
                lifecycle.SessionId,
                request.Key.SessionId,
                StringComparison.Ordinal
            )
            || !lifecycle.TryGetBaseSnapshot(request.Key.PlayerKey, out var before)
            || before.Revision != request.ExpectedRevision
        )
        {
            return Rejected(request, "darkness.sanity.preflight-failed");
        }

        var result = lifecycle.ApplySanityChange(
            request.Key.PlayerKey,
            request.Delta,
            SanityChangeSource.DarknessAttack
        );
        if (
            result.Source != SanityChangeSource.DarknessAttack
            || result.Snapshot is null
        )
        {
            return Rejected(request, result.Reason);
        }

        var status = result.Status switch
        {
            SanityChangeStatus.Applied => DarknessAttackSanityReceiptStatus.Applied,
            SanityChangeStatus.NoChange
                when string.Equals(
                    result.Reason,
                    "sanity-value-is-unchanged",
                    StringComparison.Ordinal
                ) => DarknessAttackSanityReceiptStatus.NoChange,
            _ => DarknessAttackSanityReceiptStatus.Rejected,
        };
        return new DarknessAttackSanityReceipt(
            status,
            request.Key,
            request.RequestId,
            request.DamageReceiptId,
            request.Delta,
            request.Source,
            before.Revision,
            result.Snapshot.Revision,
            before.Current,
            result.Snapshot.Current,
            result.Reason
        );
    }

    private static DarknessAttackSanityReceipt Rejected(
        DarknessAttackSanityRequest request,
        string reason
    )
    {
        return new DarknessAttackSanityReceipt(
            DarknessAttackSanityReceiptStatus.Rejected,
            request.Key,
            request.RequestId,
            request.DamageReceiptId,
            request.Delta,
            request.Source,
            request.ExpectedRevision,
            request.ExpectedRevision,
            0d,
            0d,
            string.IsNullOrWhiteSpace(reason)
                ? "darkness.sanity.rejected"
                : reason
        );
    }
}
