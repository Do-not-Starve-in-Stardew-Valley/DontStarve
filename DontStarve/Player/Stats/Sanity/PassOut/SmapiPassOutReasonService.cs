#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Narrow Stardew 1.6 evidence adapter for ordinary sleep and protection-aware health death.
/// The 2:00 startToPassOut prefix remains owned by the existing special-flow service, which calls
/// this adapter only to classify and capture the final reason it selected.
/// </summary>
internal sealed class SmapiPassOutReasonService : IDisposable
{
    private const int MaximumLoggedReasons = 64;
    private static SmapiPassOutReasonService? activePatchOwner;

    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly PassOutReasonLedger ledger;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private Harmony? harmony;
    private MethodInfo? doSleepMethod;
    private MethodInfo? updatePauseMethod;
    private bool disposed;

    internal SmapiPassOutReasonService(
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        PassOutReasonLedger ledger
    )
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

        InstallPatches();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal PassOutReasonClassification ClassifyStartToPassOut(Farmer farmer)
    {
        if (farmer is null)
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Unavailable,
                PassOutReason.Unknown,
                Game1.timeOfDay,
                "passout.reason.start-to-pass-out.player-unavailable"
            );
        }
        return PassOutReasonPolicy.ClassifyStartToPassOut(
            Game1.timeOfDay,
            farmer.stamina
        );
    }

    internal PassOutReasonCaptureResult CaptureStartToPassOut(
        Farmer farmer,
        PassOutReasonClassification classification
    )
    {
        return Capture(
            farmer,
            classification.Reason,
            classification.TimeOfDay,
            classification.StableReason
        );
    }

    internal PassOutReasonCaptureResult CaptureSpecialDeath(
        Farmer farmer,
        int timeOfDay,
        string stableReason
    )
    {
        return Capture(
            farmer,
            PassOutReason.SanityDarknessSpecialDeath,
            timeOfDay,
            stableReason
        );
    }

    internal void ForgetPlayer(string playerKey)
    {
        ledger.ForgetPlayer(playerKey);
    }

    internal void ClearSession()
    {
        ledger.Clear();
        loggedReasons.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        ClearSession();
        UninstallPatches();
    }

    private void InstallPatches()
    {
        if (activePatchOwner is not null && !ReferenceEquals(activePatchOwner, this))
        {
            Log(
                "passout.reason.patch-owner-conflict",
                "Ordinary pass-out reason patches were not installed because another runtime owner exists.",
                LogLevel.Error
            );
            return;
        }

        doSleepMethod = AccessTools.Method(
            typeof(GameLocation),
            "doSleep",
            Type.EmptyTypes
        );
        updatePauseMethod = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.updatePause),
            new[] { typeof(GameTime) }
        );
        var doSleepPrefix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(DoSleepPrefix)
        );
        var updatePausePrefix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(UpdatePausePrefix)
        );
        var updatePausePostfix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(UpdatePausePostfix)
        );
        if (
            doSleepMethod is null
            || updatePauseMethod is null
            || doSleepPrefix is null
            || updatePausePrefix is null
            || updatePausePostfix is null
        )
        {
            doSleepMethod = null;
            updatePauseMethod = null;
            Log(
                "passout.reason.patch-signature-unavailable",
                "Ordinary pass-out reason patches failed closed because GameLocation.doSleep() or Game1.updatePause(GameTime) was unavailable.",
                LogLevel.Error
            );
            return;
        }

        try
        {
            harmony = new Harmony(string.Concat(modId, ".Sanity4.PassOutReason"));
            harmony.Patch(doSleepMethod, prefix: new HarmonyMethod(doSleepPrefix));
            harmony.Patch(
                updatePauseMethod,
                prefix: new HarmonyMethod(updatePausePrefix),
                postfix: new HarmonyMethod(updatePausePostfix)
            );
            activePatchOwner = this;
        }
        catch (Exception exception)
        {
            harmony = null;
            doSleepMethod = null;
            updatePauseMethod = null;
            Log(
                "passout.reason.patch-install-failed",
                $"Ordinary pass-out reason patches failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void UninstallPatches()
    {
        if (harmony is not null)
        {
            TryUnpatch(doSleepMethod);
            TryUnpatch(updatePauseMethod);
        }
        if (ReferenceEquals(activePatchOwner, this))
            activePatchOwner = null;
        harmony = null;
        doSleepMethod = null;
        updatePauseMethod = null;
    }

    private void TryUnpatch(MethodInfo? method)
    {
        if (method is null || harmony is null)
            return;
        try
        {
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }
        catch (Exception exception)
        {
            Log(
                "passout.reason.patch-uninstall-failed",
                $"A pass-out reason patch cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private static void DoSleepPrefix()
    {
        var owner = activePatchOwner;
        if (owner is null)
            return;
        try
        {
            owner.CaptureVoluntarySleep();
        }
        catch (Exception exception)
        {
            owner.Log(
                "passout.reason.voluntary-sleep-prefix-threw",
                $"Voluntary sleep evidence failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private static void UpdatePausePrefix(out bool __state)
    {
        __state = Game1.killScreen;
    }

    private static void UpdatePausePostfix(bool __state)
    {
        var owner = activePatchOwner;
        if (owner is null)
            return;
        try
        {
            owner.ApplyHealthDeathRecovery(__state);
        }
        catch (Exception exception)
        {
            owner.Log(
                "passout.reason.health-death-postfix-threw",
                $"Health-death Sanity recovery failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void CaptureVoluntarySleep()
    {
        if (!CanApplyHostPolicy(Game1.player))
            return;

        var classification = PassOutReasonPolicy.ClassifyVoluntarySleep(
            Game1.timeOfDay
        );
        var result = CaptureStartToPassOut(Game1.player, classification);
        ReportCaptureFailure(result);
    }

    private void ApplyHealthDeathRecovery(bool wasKillScreen)
    {
        var farmer = Game1.player;
        if (!CanApplyHostPolicy(farmer))
            return;

        var decision = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            wasKillScreen,
            Game1.killScreen,
            farmer.health,
            farmer.maxHealth
        );
        if (decision.Action != PassOutPolicyAction.SetToMaximumFraction)
            return;
        var target = farmer.GetMaxSanity() * decision.TargetMaximumFraction;
        var result = farmer.SetSanity(target, decision.Source);
        if (result.Status == SanityChangeStatus.Rejected)
        {
            Log(
                result.Reason,
                $"Health-death Sanity recovery was rejected ({result.Reason}).",
                LogLevel.Warn
            );
        }
    }

    private PassOutReasonCaptureResult Capture(
        Farmer farmer,
        PassOutReason reason,
        int timeOfDay,
        string stableReason
    )
    {
        var playerKey = farmer is null
            ? string.Empty
            : SanityPlayerKey.FromUniqueMultiplayerId(farmer.UniqueMultiplayerID);
        var evidence = new PassOutReasonEvidence(
            lifecycle.SessionId,
            Guid.NewGuid().ToString("N"),
            playerKey,
            Math.Max(0, Game1.Date.TotalDays),
            reason,
            timeOfDay,
            stableReason
        );
        if (!CanApplyHostPolicy(farmer))
        {
            return new PassOutReasonCaptureResult(
                PassOutReasonCaptureStatus.Rejected,
                evidence,
                "passout.reason.capture-requires-host-authority"
            );
        }
        return ledger.Capture(evidence);
    }

    private bool CanApplyHostPolicy(Farmer? farmer)
    {
        return !disposed
            && Context.IsWorldReady
            && Context.IsMainPlayer
            && lifecycle.IsEnabled
            && lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && SanityProtocol.IsValidSessionId(lifecycle.SessionId)
            && farmer is not null
            && SanityPlayerKey.IsCanonical(
                SanityPlayerKey.FromUniqueMultiplayerId(
                    farmer.UniqueMultiplayerID
                )
            );
    }

    private void ReportCaptureFailure(PassOutReasonCaptureResult result)
    {
        if (
            result.Status is PassOutReasonCaptureStatus.Rejected
                or PassOutReasonCaptureStatus.Conflict
        )
        {
            Log(
                result.StableReason,
                $"Pass-out reason evidence failed closed ({result.StableReason}).",
                LogLevel.Warn
            );
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void Log(string reason, string message, LogLevel level)
    {
        if (loggedReasons.Count >= MaximumLoggedReasons || !loggedReasons.Add(reason))
            return;
        monitor.Log($"{message} reason={reason}", level);
    }
}
