#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Events;

internal readonly record struct SanityEventGateCapability(
    string Capability,
    string Status,
    string Reason,
    bool PatchInstalled
)
{
    internal const string CapabilityId = "sanity.friendship-event-gate";

    internal static SanityEventGateCapability Disabled(string reason)
    {
        return new SanityEventGateCapability(
            CapabilityId,
            "Disabled",
            reason,
            false
        );
    }
}

/// <summary>
/// SMAPI/Harmony boundary for the narrow event gate and owner-local effective overlay.
/// Classification and overlay ordering stay in pure seams; this adapter only reads the
/// accepted event key and observes CurrentEvent lifecycle edges.
/// </summary>
internal sealed class SanitySmapiEventService : IDisposable
{
    private const int MaximumLoggedDiagnostics = 64;
    private const string OverrideRelativePath =
        "Asset/Sanity/Data/event-overrides.json";

    private static SanitySmapiEventService? activePatchOwner;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanityEventOverrideCatalog overrides;
    private readonly SanityEffectiveSanityOverlay effectiveOverlay = new();
    private readonly Dictionary<int, ActiveEventBinding> activeEventsByScreen = new();
    private readonly Dictionary<int, EventAdmissionReceipt> admissionByScreen = new();
    private readonly HashSet<string> loggedDiagnostics = new(StringComparer.Ordinal);
    private Harmony? harmony;
    private MethodInfo? patchedMethod;
    private bool enabled;
    private bool disposed;
    private long nextRevision;

    internal SanitySmapiEventService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        bool enabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.enabled = enabled;

        var overrideLoad = LoadOverrides(helper.DirectoryPath);
        overrides = overrideLoad.Catalog;
        if (!overrideLoad.IsAvailable)
        {
            LogOnce(
                string.Concat("override|", overrideLoad.Reason),
                $"Sanity event overrides are unavailable and event classification will use known code-only rules ({overrideLoad.Reason}).",
                LogLevel.Warn
            );
        }

        Capability = enabled
            ? InstallGatePatch()
            : SanityEventGateCapability.Disabled("sanity-system-disabled");
        monitor.Log(
            $"Sanity event gate capability: capability={Capability.Capability}, status={Capability.Status}, reason={Capability.Reason}, patch-installed={Capability.PatchInstalled}, overrides={overrideLoad.Reason}.",
            Capability.PatchInstalled ? LogLevel.Debug : LogLevel.Warn
        );

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.Player.Warped += OnWarped;
        lifecycle.SessionClearing += OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal SanityEventGateCapability Capability { get; private set; }

    internal ISanityEffectiveSanityProvider EffectiveSanityProvider => effectiveOverlay;

    /// <summary>
    /// The 2:00 coordinator owns this overlay explicitly. Ordinary CurrentEvent observation may
    /// see the same screen, but it can neither replace nor end the special owner.
    /// </summary>
    internal SanityEffectiveOverlayMutation BeginSpecialFlow(
        string playerKey,
        int screenId,
        string sessionId,
        string eventId
    )
    {
        var key = new SanityEffectiveOverlayKey(playerKey, screenId, sessionId);
        if (disposed || !enabled)
        {
            return new SanityEffectiveOverlayMutation(
                SanityEffectiveOverlayMutationStatus.Rejected,
                disposed
                    ? "effective-overlay-service-disposed"
                    : "sanity-system-disabled",
                null
            );
        }
        if (!TryNextRevision(out var revision))
        {
            return new SanityEffectiveOverlayMutation(
                SanityEffectiveOverlayMutationStatus.Rejected,
                "effective-overlay-revision-unavailable",
                null
            );
        }

        var mutation = effectiveOverlay.Begin(
            key,
            SanityEffectiveOverlayReason.SanityTwoAmSpecial,
            eventId,
            revision
        );
        if (
            mutation.Status is not (
                SanityEffectiveOverlayMutationStatus.Applied
                or SanityEffectiveOverlayMutationStatus.NoChange
            )
        )
        {
            return mutation;
        }

        var coverageKey = new SanityEventCoverageKey(playerKey, screenId, sessionId);
        if (
            lifecycle.TrySetEventCoverage(coverageKey, true, out var coverageReason)
            || coverageReason == "event-coverage-already-active"
        )
        {
            return mutation;
        }

        if (TryNextRevision(out var rollbackRevision))
            effectiveOverlay.EndSpecial(key, eventId, rollbackRevision);
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Rejected,
            coverageReason,
            mutation.Snapshot
        );
    }

    internal SanityEffectiveOverlayMutation EndSpecialFlow(
        string playerKey,
        int screenId,
        string sessionId,
        string eventId
    )
    {
        if (disposed)
        {
            return new SanityEffectiveOverlayMutation(
                SanityEffectiveOverlayMutationStatus.Rejected,
                "effective-overlay-service-disposed",
                null
            );
        }
        var key = new SanityEffectiveOverlayKey(playerKey, screenId, sessionId);
        if (!TryNextRevision(out var revision))
        {
            return new SanityEffectiveOverlayMutation(
                SanityEffectiveOverlayMutationStatus.Rejected,
                "effective-overlay-revision-unavailable",
                null
            );
        }

        var mutation = effectiveOverlay.EndSpecial(key, eventId, revision);
        if (!effectiveOverlay.TryGetSnapshot(key, out _))
        {
            lifecycle.TrySetEventCoverage(
                new SanityEventCoverageKey(playerKey, screenId, sessionId),
                false,
                out _
            );
        }
        return mutation;
    }

    internal void SetEnabled(bool value)
    {
        if (disposed || enabled == value)
            return;

        enabled = value;
        if (!enabled)
        {
            ClearAllOverlays(notifyLifecycle: true);
            admissionByScreen.Clear();
            UninstallGatePatch("sanity-system-disabled");
            return;
        }

        Capability = InstallGatePatch();
        monitor.Log(
            $"Sanity event gate capability changed: capability={Capability.Capability}, status={Capability.Status}, reason={Capability.Reason}, patch-installed={Capability.PatchInstalled}.",
            Capability.PatchInstalled ? LogLevel.Debug : LogLevel.Warn
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        ClearAllOverlays(notifyLifecycle: true);
        admissionByScreen.Clear();
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.Player.Warped -= OnWarped;
        lifecycle.SessionClearing -= OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        UninstallGatePatch("event-gate-service-disposed");
    }

    private SanityEventGateCapability InstallGatePatch()
    {
        if (string.IsNullOrWhiteSpace(modId))
            return SanityEventGateCapability.Disabled("event-gate-mod-id-is-missing");
        if (activePatchOwner is not null && !ReferenceEquals(activePatchOwner, this))
        {
            return SanityEventGateCapability.Disabled(
                "event-gate-patch-owner-already-exists"
            );
        }

        var target = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.checkEventPrecondition),
            new[] { typeof(string), typeof(bool) }
        );
        var postfix = AccessTools.Method(
            typeof(SanitySmapiEventService),
            nameof(CheckEventPreconditionPostfix)
        );
        if (
            target is null
            || postfix is null
            || target.ReturnType != typeof(string)
            || target.GetParameters().Length != 2
            || target.GetParameters()[0].ParameterType != typeof(string)
            || target.GetParameters()[1].ParameterType != typeof(bool)
        )
        {
            return SanityEventGateCapability.Disabled(
                "event-gate-target-signature-is-unsupported"
            );
        }

        try
        {
            harmony = new Harmony(string.Concat(modId, ".Sanity4.EventGate"));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            patchedMethod = target;
            activePatchOwner = this;
            return new SanityEventGateCapability(
                SanityEventGateCapability.CapabilityId,
                "Available",
                "narrow-check-event-precondition-postfix-installed",
                true
            );
        }
        catch (Exception exception)
        {
            if (target is not null && harmony is not null)
            {
                try
                {
                    harmony.Unpatch(
                        target,
                        HarmonyPatchType.Postfix,
                        harmony.Id
                    );
                }
                catch (Exception cleanupException)
                {
                    LogOnce(
                        string.Concat(
                            "patch-cleanup|",
                            cleanupException.GetType().FullName
                        ),
                        $"Sanity event gate patch rollback also failed ({cleanupException.GetType().Name}); the static owner remains unset so the postfix is fail-open.",
                        LogLevel.Error
                    );
                }
            }
            harmony = null;
            patchedMethod = null;
            LogOnce(
                string.Concat("patch|", exception.GetType().FullName),
                $"Sanity friendship event gate was disabled because its narrow postfix could not be installed ({exception.GetType().Name}). Effective event coverage remains available.",
                LogLevel.Error
            );
            return SanityEventGateCapability.Disabled(
                "event-gate-patch-installation-failed"
            );
        }
    }

    private void UninstallGatePatch(string reason)
    {
        if (patchedMethod is not null && harmony is not null)
        {
            try
            {
                harmony.Unpatch(
                    patchedMethod,
                    HarmonyPatchType.Postfix,
                    harmony.Id
                );
            }
            catch (Exception exception)
            {
                LogOnce(
                    string.Concat("unpatch|", exception.GetType().FullName),
                    $"Sanity event gate exact postfix could not be removed cleanly ({exception.GetType().Name}); its static owner was disabled so the gate remains fail-open.",
                    LogLevel.Error
                );
            }
        }
        if (ReferenceEquals(activePatchOwner, this))
            activePatchOwner = null;
        patchedMethod = null;
        harmony = null;
        Capability = SanityEventGateCapability.Disabled(reason);
    }

    private static void CheckEventPreconditionPostfix(
        GameLocation __instance,
        string precondition,
        bool check_seen,
        ref string __result
    )
    {
        activePatchOwner?.ApplyAcceptedPreconditionGate(
            __instance,
            precondition,
            check_seen,
            ref __result
        );
    }

    private void ApplyAcceptedPreconditionGate(
        GameLocation location,
        string precondition,
        bool checkSeen,
        ref string result
    )
    {
        _ = checkSeen;
        if (
            disposed
            || !enabled
            || !Capability.PatchInstalled
            || string.IsNullOrWhiteSpace(result)
            || string.Equals(result, "-1", StringComparison.Ordinal)
        )
        {
            return;
        }

        if (!TryGetCurrentCoverageKey(out var coverageKey, out var ownerReason))
        {
            LogOnce(
                string.Concat("gate-owner|", ownerReason),
                $"Sanity friendship event gate failed open because owner/screen/session identity was unavailable ({ownerReason}).",
                LogLevel.Warn
            );
            return;
        }

        string[] splitPreconditions;
        try
        {
            // Use Stardew's own quote-aware slash splitter; localized event text is never parsed.
            splitPreconditions = Event.SplitPreconditions(precondition);
        }
        catch (Exception exception)
        {
            LogOnce(
                string.Concat("gate-split|", exception.GetType().FullName),
                $"Sanity friendship event gate failed open because the accepted event key could not be split ({exception.GetType().Name}).",
                LogLevel.Warn
            );
            return;
        }

        var locationName = location.NameOrUniqueName ?? string.Empty;
        SanityEventClassification? configuredOverride = null;
        if (overrides.TryResolve(locationName, result, out var resolvedOverride))
            configuredOverride = resolvedOverride;
        var classification = SanityEventConditionClassifier.Classify(
            splitPreconditions,
            configuredOverride
        );

        if (classification.Classification == SanityEventClassification.Unknown)
        {
            LogOnce(
                string.Concat("gate-unknown|", locationName, "|", result, "|", classification.Reason),
                $"Sanity friendship event gate failed open for an unknown accepted condition (location={locationName}, event={result}, reason={classification.Reason}).",
                LogLevel.Warn
            );
            RecordAdmission(
                coverageKey,
                locationName,
                result,
                classification.Classification
            );
            return;
        }

        var baseCurrent = 0d;
        var baseMaximum = 1d;
        if (classification.Classification == SanityEventClassification.Friendship)
        {
            if (
                !lifecycle.TryGetBaseSnapshot(
                    coverageKey.PlayerKey,
                    out var baseSnapshot
                )
                || !double.IsFinite(baseSnapshot.Current)
                || !double.IsFinite(baseSnapshot.Maximum)
                || baseSnapshot.Maximum <= 0
            )
            {
                LogOnce(
                    string.Concat("gate-base|", coverageKey.PlayerKey),
                    $"Sanity friendship event gate failed open because true base Sanity was unavailable (owner={coverageKey.PlayerKey}).",
                    LogLevel.Warn
                );
                RecordAdmission(
                    coverageKey,
                    locationName,
                    result,
                    classification.Classification
                );
                return;
            }
            baseCurrent = baseSnapshot.Current;
            baseMaximum = baseSnapshot.Maximum;
        }

        var gate = SanityFriendshipEventGate.Evaluate(
            classification.Classification,
            baseCurrent,
            baseMaximum
        );
        if (gate.Decision == SanityFriendshipEventGateDecision.Block)
        {
            LogOnce(
                string.Concat("gate-block|", locationName, "|", result),
                $"Sanity friendship event gate blocked an accepted relationship event below 50% base Sanity (location={locationName}, event={result}, owner={coverageKey.PlayerKey}).",
                LogLevel.Debug
            );
            result = "-1";
            return;
        }

        RecordAdmission(
            coverageKey,
            locationName,
            result,
            classification.Classification
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(30))
            ClearInvalidScreens();

        var screenId = Context.ScreenId;
        if (!enabled || !Context.IsWorldReady)
        {
            ClearScreen(screenId, notifyLifecycle: true);
            admissionByScreen.Remove(screenId);
            return;
        }

        var currentEvent = Game1.CurrentEvent;
        if (currentEvent is null)
        {
            EndCurrentEvent(screenId);
            admissionByScreen.Remove(screenId);
            return;
        }

        if (
            activeEventsByScreen.TryGetValue(screenId, out var active)
            && ReferenceEquals(active.Event, currentEvent)
        )
        {
            return;
        }

        EndCurrentEvent(screenId);
        BeginCurrentEvent(currentEvent);
    }

    private void BeginCurrentEvent(Event currentEvent)
    {
        if (!TryGetCurrentCoverageKey(out var coverageKey, out var ownerReason))
        {
            LogOnce(
                string.Concat("overlay-owner|", ownerReason),
                $"Sanity effective event overlay was not started because owner/screen/session identity was unavailable ({ownerReason}).",
                LogLevel.Warn
            );
            return;
        }
        if (!TryNextRevision(out var revision))
            return;

        var eventId = string.IsNullOrWhiteSpace(currentEvent.id)
            ? "-1"
            : currentEvent.id;
        var reason = currentEvent.isFestival
            ? SanityEffectiveOverlayReason.Festival
            : SanityEffectiveOverlayReason.RegularEvent;
        var mutation = effectiveOverlay.Begin(
            ToOverlayKey(coverageKey),
            reason,
            eventId,
            revision
        );
        if (mutation.Status == SanityEffectiveOverlayMutationStatus.Rejected)
        {
            LogOnce(
                string.Concat("overlay-start|", mutation.Reason),
                $"Sanity effective event overlay failed closed for its effects layer ({mutation.Reason}); the already-started game event remains fail-open.",
                LogLevel.Warn
            );
            return;
        }
        if (
            mutation.Status == SanityEffectiveOverlayMutationStatus.NoChange
            && mutation.Reason == "effective-overlay-special-flow-preserved"
        )
        {
            // The future 2:00 flow owns this key and ordinary CurrentEvent observation cannot
            // replace or later clear it.
            RecordStartedEvent(
                currentEvent,
                coverageKey,
                eventId,
                ownsOrdinaryOverlay: false
            );
            return;
        }
        if (
            mutation.Status != SanityEffectiveOverlayMutationStatus.Applied
            && mutation.Status != SanityEffectiveOverlayMutationStatus.NoChange
        )
        {
            return;
        }

        if (
            !lifecycle.TrySetEventCoverage(
                coverageKey,
                true,
                out var coverageReason
            )
            && coverageReason != "event-coverage-already-active"
        )
        {
            effectiveOverlay.ClearScreen(coverageKey.ScreenId);
            LogOnce(
                string.Concat("coverage-start|", coverageReason),
                $"Sanity effective event coverage was rolled back because lifecycle ownership was unavailable ({coverageReason}).",
                LogLevel.Warn
            );
            return;
        }

        RecordStartedEvent(
            currentEvent,
            coverageKey,
            eventId,
            ownsOrdinaryOverlay: true
        );
    }

    private void RecordStartedEvent(
        Event currentEvent,
        SanityEventCoverageKey coverageKey,
        string eventId,
        bool ownsOrdinaryOverlay
    )
    {
        activeEventsByScreen[coverageKey.ScreenId] = new ActiveEventBinding(
            currentEvent,
            coverageKey,
            eventId,
            ownsOrdinaryOverlay
        );
        if (
            !admissionByScreen.TryGetValue(
                coverageKey.ScreenId,
                out var receipt
            )
            || !receipt.Matches(coverageKey, eventId)
        )
        {
            var bypassKind = currentEvent.isFestival
                ? "festival-chain"
                : currentEvent.isWedding
                    ? "wedding-chain"
                    : "direct-or-mod-start";
            LogOnce(
                string.Concat("event-bypass|", bypassKind, "|", eventId),
                $"Sanity event gate observed an already-started event without a matching accepted-condition receipt and failed open (kind={bypassKind}, event={eventId}, owner={coverageKey.PlayerKey}, screen={coverageKey.ScreenId}).",
                LogLevel.Debug
            );
        }
        admissionByScreen.Remove(coverageKey.ScreenId);
    }

    private void EndCurrentEvent(int screenId)
    {
        if (!activeEventsByScreen.Remove(screenId, out var active))
            return;
        if (!active.OwnsOrdinaryOverlay)
            return;
        if (!TryNextRevision(out var revision))
        {
            ClearScreen(screenId, notifyLifecycle: true);
            return;
        }

        effectiveOverlay.EndOrdinary(
            ToOverlayKey(active.CoverageKey),
            active.EventId,
            revision
        );
        if (
            !effectiveOverlay.TryGetSnapshot(
                ToOverlayKey(active.CoverageKey),
                out _
            )
        )
        {
            lifecycle.TrySetEventCoverage(
                active.CoverageKey,
                false,
                out _
            );
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (disposed || !e.IsLocalPlayer)
            return;
        ClearScreen(Context.ScreenId, notifyLifecycle: true);
        admissionByScreen.Remove(Context.ScreenId);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (disposed)
            return;
        ClearAllOverlays(notifyLifecycle: true);
        admissionByScreen.Clear();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        _ = boundary;
        if (disposed)
            return;

        // The lifecycle coordinator clears its coverage index immediately after this callback.
        ClearAllOverlays(notifyLifecycle: false);
        admissionByScreen.Clear();
        nextRevision = 0;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void ClearInvalidScreens()
    {
        var removed = effectiveOverlay.ClearInvalidScreens(Context.HasScreenId);
        foreach (var snapshot in removed)
        {
            lifecycle.TrySetEventCoverage(
                ToCoverageKey(snapshot.Key),
                false,
                out _
            );
            activeEventsByScreen.Remove(snapshot.Key.ScreenId);
            admissionByScreen.Remove(snapshot.Key.ScreenId);
        }
    }

    private void ClearScreen(int screenId, bool notifyLifecycle)
    {
        var removed = effectiveOverlay.ClearScreen(screenId);
        if (notifyLifecycle)
        {
            foreach (var snapshot in removed)
            {
                lifecycle.TrySetEventCoverage(
                    ToCoverageKey(snapshot.Key),
                    false,
                    out _
                );
            }
        }
        activeEventsByScreen.Remove(screenId);
    }

    private void ClearAllOverlays(bool notifyLifecycle)
    {
        var removed = effectiveOverlay.ClearAll();
        if (notifyLifecycle)
        {
            foreach (var snapshot in removed)
            {
                lifecycle.TrySetEventCoverage(
                    ToCoverageKey(snapshot.Key),
                    false,
                    out _
                );
            }
        }
        activeEventsByScreen.Clear();
    }

    private void RecordAdmission(
        SanityEventCoverageKey key,
        string locationName,
        string eventId,
        SanityEventClassification classification
    )
    {
        admissionByScreen[key.ScreenId] = new EventAdmissionReceipt(
            key,
            locationName,
            eventId,
            classification
        );
    }

    private bool TryGetCurrentCoverageKey(
        out SanityEventCoverageKey key,
        out string reason
    )
    {
        var player = Game1.player;
        var screenId = Context.ScreenId;
        var sessionId = lifecycle.SessionId;
        if (player is null)
        {
            key = default;
            reason = "event-owner-is-missing";
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        key = new SanityEventCoverageKey(playerKey, screenId, sessionId);
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "event-owner-key-is-invalid";
            return false;
        }
        if (screenId < 0 || !Context.HasScreenId(screenId))
        {
            reason = "event-screen-is-invalid";
            return false;
        }
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "event-session-is-invalid";
            return false;
        }

        reason = "event-owner-available";
        return true;
    }

    private bool TryNextRevision(out long revision)
    {
        if (nextRevision == long.MaxValue)
        {
            revision = 0;
            LogOnce(
                "overlay-revision-exhausted",
                "Sanity effective event overlay stopped accepting lifecycle transitions because its session revision was exhausted.",
                LogLevel.Error
            );
            return false;
        }

        revision = ++nextRevision;
        return true;
    }

    private SanityEventOverrideLoadResult LoadOverrides(string modDirectory)
    {
        try
        {
            var path = Path.Combine(
                modDirectory,
                OverrideRelativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            return SanityEventOverrideCatalog.Load(File.ReadAllText(path));
        }
        catch (Exception exception)
        {
            LogOnce(
                string.Concat("override-read|", exception.GetType().FullName),
                $"Sanity event override JSON could not be read and overrides were disabled ({exception.GetType().Name}).",
                LogLevel.Warn
            );
            return new SanityEventOverrideLoadResult(
                false,
                "event-override-file-read-failed",
                SanityEventOverrideCatalog.Empty
            );
        }
    }

    private void LogOnce(string key, string message, LogLevel level)
    {
        if (loggedDiagnostics.Count >= MaximumLoggedDiagnostics)
            return;
        if (!loggedDiagnostics.Add(key))
            return;
        monitor.Log(message, level);
    }

    private static SanityEffectiveOverlayKey ToOverlayKey(
        SanityEventCoverageKey key
    )
    {
        return new SanityEffectiveOverlayKey(
            key.PlayerKey,
            key.ScreenId,
            key.SessionId
        );
    }

    private static SanityEventCoverageKey ToCoverageKey(
        SanityEffectiveOverlayKey key
    )
    {
        return new SanityEventCoverageKey(
            key.PlayerKey,
            key.ScreenId,
            key.SessionId
        );
    }

    private sealed record ActiveEventBinding(
        Event Event,
        SanityEventCoverageKey CoverageKey,
        string EventId,
        bool OwnsOrdinaryOverlay
    );

    private readonly record struct EventAdmissionReceipt(
        SanityEventCoverageKey CoverageKey,
        string LocationName,
        string EventId,
        SanityEventClassification Classification
    )
    {
        internal bool Matches(SanityEventCoverageKey key, string eventId)
        {
            return CoverageKey == key
                && string.Equals(EventId, eventId, StringComparison.Ordinal);
        }
    }
}
