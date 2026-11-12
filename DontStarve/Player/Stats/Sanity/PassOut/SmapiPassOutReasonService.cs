#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using DontStarve.Player.Stats.Hunger;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Narrow Stardew 1.6 evidence adapter for ordinary sleep and protection-aware health death.
/// The post-animation passOutFromTired seam remains owned by the special-flow service, which calls
/// this adapter only to classify and capture the final reason it selected.
/// </summary>
internal sealed class SmapiPassOutReasonService : IDisposable
{
    private const int MaximumLoggedReasons = 64;
    private static SmapiPassOutReasonService? activePatchOwner;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly PassOutReasonLedger ledger;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private Harmony? harmony;
    private MethodInfo? startSleepMethod;
    private MethodInfo? doSleepMethod;
    private MethodInfo? checkForEventsMethod;
    private bool disposed;
    private bool knownStartSleepAttempt;
    private bool pendingHealthDeathRecovery;
    private string pendingHealthDeathRecoveryPlayerKey = string.Empty;

    internal SmapiPassOutReasonService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        PassOutReasonLedger ledger
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

        InstallPatches();
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// The shared Sleep adapter subscribes here instead of installing another GameLocation
    /// Harmony owner. Returning false suppresses only the vanilla startSleep entry while a
    /// player is answering the custom confirmation dialogue.
    /// </summary>
    internal event Func<Farmer, bool>? StartSleepRequested;

    internal event Action<Farmer>? StartSleepObserved;

    internal event Action<Farmer, PassOutReasonEvidence>? VoluntarySleepCommitted;

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
        knownStartSleepAttempt = false;
        pendingHealthDeathRecovery = false;
        pendingHealthDeathRecoveryPlayerKey = string.Empty;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
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

        startSleepMethod = AccessTools.Method(
            typeof(GameLocation),
            "startSleep",
            Type.EmptyTypes
        );
        doSleepMethod = AccessTools.Method(
            typeof(GameLocation),
            "doSleep",
            Type.EmptyTypes
        );
        checkForEventsMethod = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.checkForEvents),
            Type.EmptyTypes
        );
        var doSleepPrefix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(DoSleepPrefix)
        );
        var startSleepPrefix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(StartSleepPrefix)
        );
        var startSleepPostfix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(StartSleepPostfix)
        );
        var checkForEventsPrefix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(CheckForEventsPrefix)
        );
        var checkForEventsPostfix = AccessTools.Method(
            typeof(SmapiPassOutReasonService),
            nameof(CheckForEventsPostfix)
        );
        if (
            doSleepMethod is null
            || startSleepMethod is null
            || checkForEventsMethod is null
            || doSleepPrefix is null
            || startSleepPrefix is null
            || startSleepPostfix is null
            || checkForEventsPrefix is null
            || checkForEventsPostfix is null
        )
        {
            doSleepMethod = null;
            startSleepMethod = null;
            checkForEventsMethod = null;
            Log(
                "passout.reason.patch-signature-unavailable",
                "Ordinary pass-out reason patches failed closed because GameLocation.doSleep() or GameLocation.checkForEvents() was unavailable.",
                LogLevel.Error
            );
            return;
        }

        try
        {
            harmony = new Harmony(string.Concat(modId, ".Sanity4.PassOutReason"));
            harmony.Patch(
                startSleepMethod,
                prefix: new HarmonyMethod(startSleepPrefix),
                postfix: new HarmonyMethod(startSleepPostfix)
            );
            harmony.Patch(doSleepMethod, prefix: new HarmonyMethod(doSleepPrefix));
            harmony.Patch(
                checkForEventsMethod,
                prefix: new HarmonyMethod(checkForEventsPrefix),
                postfix: new HarmonyMethod(checkForEventsPostfix)
            );
            activePatchOwner = this;
        }
        catch (Exception exception)
        {
            harmony = null;
            startSleepMethod = null;
            doSleepMethod = null;
            checkForEventsMethod = null;
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
            TryUnpatch(startSleepMethod);
            TryUnpatch(doSleepMethod);
            TryUnpatch(checkForEventsMethod);
        }
        if (ReferenceEquals(activePatchOwner, this))
            activePatchOwner = null;
        harmony = null;
        startSleepMethod = null;
        doSleepMethod = null;
        checkForEventsMethod = null;
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
        if (
            !owner.knownStartSleepAttempt
            || Game1.player is null
            || Game1.player.timeWentToBed.Value == 0
        )
        {
            owner.Log(
                "passout.reason.do-sleep-without-start-sleep-evidence",
                "GameLocation.doSleep() had no preceding recognized startSleep() evidence; custom sleep consequences were skipped.",
                LogLevel.Warn
            );
            return;
        }
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
        finally
        {
            owner.knownStartSleepAttempt = false;
        }
    }

    private static bool StartSleepPrefix()
    {
        var owner = activePatchOwner;
        if (owner is null)
            return true;
        try
        {
            var allowed = owner.AllowStartSleep(Game1.player);
            if (allowed)
                owner.knownStartSleepAttempt = true;
            return allowed;
        }
        catch (Exception exception)
        {
            owner.Log(
                "passout.reason.start-sleep-prefix-threw",
                $"Sleep entry guard failed open ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return true;
        }
    }

    private static void StartSleepPostfix()
    {
        var owner = activePatchOwner;
        if (owner is null)
            return;
        try
        {
            owner.ObserveStartSleep(Game1.player);
        }
        catch (Exception exception)
        {
            owner.Log(
                "passout.reason.start-sleep-postfix-threw",
                $"Sleep entry observation failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private static void CheckForEventsPrefix(out bool __state)
    {
        __state = Game1.killScreen;
    }

    private static void CheckForEventsPostfix(bool __state)
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
                "passout.reason.health-death-revive-postfix-threw",
                $"Health-death Sanity recovery after revive failed closed ({exception.GetType().Name}: {exception.Message}).",
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
        if (result.Status != PassOutReasonCaptureStatus.Captured)
            return;

        try
        {
            VoluntarySleepCommitted?.Invoke(Game1.player, result.Evidence);
        }
        catch (Exception exception)
        {
            Log(
                "passout.reason.voluntary-sleep-observer-threw",
                $"Voluntary sleep observer failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private bool AllowStartSleep(Farmer farmer)
    {
        var handlers = StartSleepRequested;
        if (handlers is null)
            return true;

        foreach (Func<Farmer, bool> handler in handlers.GetInvocationList())
        {
            if (!handler(farmer))
                return false;
        }
        return true;
    }

    private void ObserveStartSleep(Farmer farmer)
    {
        var handlers = StartSleepObserved;
        if (handlers is null)
            return;
        foreach (Action<Farmer> handler in handlers.GetInvocationList())
            handler(farmer);
    }

    private void ApplyHealthDeathRecovery(bool wasKillScreen)
    {
        if (Game1.player is not Farmer farmer)
            return;

        var decision = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            wasKillScreen,
            Game1.killScreen,
            farmer.health,
            farmer.maxHealth
        );
        if (decision.Action != PassOutPolicyAction.SetToMaximumFraction)
            return;

        if (!CanApplyHostHealthDeath(farmer))
        {
            if (
                HealthDeathRecoveryRetryPolicy.ShouldQueue(
                    wasKillScreen,
                    Game1.killScreen,
                    farmer.health,
                    Context.IsWorldReady,
                    Context.IsMainPlayer,
                    lifecycle.AuthorityRole == SanityAuthorityRole.Host,
                    SanityProtocol.IsValidSessionId(lifecycle.SessionId),
                    farmerAvailable: true
                )
            )
            {
                pendingHealthDeathRecovery = true;
                pendingHealthDeathRecoveryPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                    farmer.UniqueMultiplayerID
                );
                Log(
                    "passout.reason.health-death-recovery-deferred",
                    "Health-death Hunger/Sanity recovery was deferred until the loaded world became ready.",
                    LogLevel.Debug
                );
            }
            return;
        }

        ClearPendingHealthDeathRecovery();
        ApplyHealthDeathRecoveryValues(farmer, decision);
    }

    private void ApplyHealthDeathRecoveryValues(
        Farmer farmer,
        PassOutPolicyDecision decision
    )
    {
        if (decision.Action != PassOutPolicyAction.SetToMaximumFraction)
            return;

        // The death transition is shared with Hunger, but each stat keeps its own configured
        // consumer gate. Disabling Sanity must not disable Hunger's independent revive rule.
        if (lifecycle.IsEnabled)
        {
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

        // This is the single confirmed true-death boundary. Hunger must consume the same
        // transition so it cannot drift into a second, independently maintained death hook.
        var hungerResult = HungerExtensions.ApplyHealthDeathRecovery(farmer);
        if (HungerExtensions.IsEnabled && !hungerResult.Accepted)
        {
            Log(
                hungerResult.Reason,
                $"Health-death Hunger recovery was rejected ({hungerResult.Reason}).",
                LogLevel.Warn
            );
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!pendingHealthDeathRecovery || Game1.player is not Farmer farmer)
            return;

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(farmer.UniqueMultiplayerID);
        if (!string.Equals(playerKey, pendingHealthDeathRecoveryPlayerKey, StringComparison.Ordinal))
        {
            ClearPendingHealthDeathRecovery();
            return;
        }

        if (Game1.killScreen || farmer.health <= 0)
        {
            ClearPendingHealthDeathRecovery();
            return;
        }

        if (
            !HealthDeathRecoveryRetryPolicy.ShouldAttempt(
                pendingHealthDeathRecovery,
                farmer.health,
                Game1.killScreen,
                Context.IsWorldReady,
                Context.IsMainPlayer,
                lifecycle.AuthorityRole == SanityAuthorityRole.Host,
                SanityProtocol.IsValidSessionId(lifecycle.SessionId)
            )
        )
            return;

        var decision = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            wasKillScreen: true,
            isKillScreen: Game1.killScreen,
            currentHealth: farmer.health,
            maximumHealth: farmer.maxHealth
        );
        ClearPendingHealthDeathRecovery();
        ApplyHealthDeathRecoveryValues(farmer, decision);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e) =>
        ClearPendingHealthDeathRecovery();

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e) =>
        ClearPendingHealthDeathRecovery();

    private void ClearPendingHealthDeathRecovery()
    {
        pendingHealthDeathRecovery = false;
        pendingHealthDeathRecoveryPlayerKey = string.Empty;
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
        return lifecycle.IsEnabled && CanApplyHostHealthDeath(farmer);
    }

    private bool CanApplyHostHealthDeath(Farmer? farmer)
    {
        return !disposed
            && Context.IsWorldReady
            && Context.IsMainPlayer
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
