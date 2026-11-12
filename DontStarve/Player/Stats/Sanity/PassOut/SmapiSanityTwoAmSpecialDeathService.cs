#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Player.Death;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Events;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.PassOut.Compatibility;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Host-authoritative boundary for the 2:00 darkness chain. The Harmony seam is deliberately the
/// frame-5 <c>passOutFromTired</c> callback, after vanilla's collapse animation has completed;
/// the earlier animation trigger is never suppressed, so the original collapse cannot be swallowed.
/// </summary>
internal sealed class SmapiSanityTwoAmSpecialDeathService : IDisposable
{
    private const int PresentationCadenceTicks = 60;
    private const int MaximumLoggedReasons = 64;
    private const string EnteredPromptKey = "darkness-attack.prompt.entered";
    private const string WarningPromptKey = "darkness-attack.prompt.warning";
    private const string ResolvedPromptKey = "darkness-attack.prompt.resolved";

    private static SmapiSanityTwoAmSpecialDeathService? activePatchOwner;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SmapiPassOutReasonService passOutReasons;
    private readonly IDarknessDamageModeResolver modeResolver;
    private readonly IJunimoBlessingResolver junimoBlessingResolver;
    private readonly EnvironmentLightService environmentLight;
    private readonly EnvironmentLightLocationRuleCatalog locationRules;
    private readonly SanitySmapiEventService events;
    private readonly SanitySmapiAudioService audio;
    private readonly PlayerDeathCauseLedger deathCauses;
    private readonly SpecialPassOutSanitySettlement specialSanitySettlement;
    private readonly Func<Farmer, bool> registerHiddenHospitalDeath;
    private readonly LateNightBehaviorEvidence lateNightBehaviorEvidence = new();
    private readonly SanityTwoAmSpecialDeathStateMachine stateMachine = new();
    private readonly Dictionary<string, long> nextPresentationTickByPlayer = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private readonly SanityTwoAmSpecialDeathHostRequestReceipts hostRequestReceipts = new();
    private readonly SanityTwoAmSpecialDeathClientReceipts clientReceipts = new();
    private NightOwlPlusCompatibilityResult nightOwlPlusCompatibility = NightOwlPlusCompatibility.Unresolved();
    private Harmony? harmony;
    private MethodInfo? patchedMethod;
    private bool persistenceCanWrite = true;
    private bool beginningOfficialPassOut;
    private bool clientRunOriginalOnce;
    private bool clientSpecialFlowActive;
    private bool clientSnapshotRequestPending;
    private string pendingClientCorrelationId = string.Empty;
    private long pendingClientNonce;
    private long pendingClientAuthorityRevision = -1;
    private long nextClientNonce = 1;
    private long pendingClientDeadlineTick;
    private SanityTwoAmSpecialDeathSnapshot? clientSnapshot;
    private Event? localPresentationEvent;
    private SanityTwoAmSpecialDeathSnapshot? localPresentationSnapshot;
    private bool disposed;

    internal SmapiSanityTwoAmSpecialDeathService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        SmapiPassOutReasonService passOutReasons,
        IDarknessDamageModeResolver modeResolver,
        IJunimoBlessingResolver junimoBlessingResolver,
        EnvironmentLightService environmentLight,
        EnvironmentLightLocationRuleCatalog locationRules,
        SanitySmapiEventService events,
        SanitySmapiAudioService audio,
        PlayerDeathCauseLedger deathCauses,
        SpecialPassOutSanitySettlement specialSanitySettlement,
        Func<Farmer, bool> registerHiddenHospitalDeath
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.passOutReasons = passOutReasons ?? throw new ArgumentNullException(nameof(passOutReasons));
        this.modeResolver = modeResolver ?? throw new ArgumentNullException(nameof(modeResolver));
        this.junimoBlessingResolver = junimoBlessingResolver ?? throw new ArgumentNullException(nameof(junimoBlessingResolver));
        this.environmentLight = environmentLight ?? throw new ArgumentNullException(nameof(environmentLight));
        this.locationRules = locationRules ?? throw new ArgumentNullException(nameof(locationRules));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.deathCauses = deathCauses ?? throw new ArgumentNullException(nameof(deathCauses));
        this.specialSanitySettlement = specialSanitySettlement ?? throw new ArgumentNullException(nameof(specialSanitySettlement));
        this.registerHiddenHospitalDeath = registerHiddenHospitalDeath ?? throw new ArgumentNullException(nameof(registerHiddenHospitalDeath));

        InstallPassOutPatch();
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        lifecycle.StateEventPublished += OnStateEventPublished;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal int ActiveFlowCount => stateMachine.Count;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.GameLoop.GameLaunched -= OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged -= OnTimeChanged;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.Saving -= OnSaving;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        helper.Events.Player.Warped -= OnWarped;
        helper.Events.Multiplayer.PeerConnected -= OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        ClearSession("passout.two-am.runtime-disposed");
        UninstallPassOutPatch();
    }

    private void InstallPassOutPatch()
    {
        if (activePatchOwner is not null && !ReferenceEquals(activePatchOwner, this))
        {
            LogOnce("passout.two-am.patch-owner-conflict", "The post-animation pass-out patch was not installed because another runtime owner exists.", LogLevel.Error);
            return;
        }

        patchedMethod = AccessTools.Method(typeof(Farmer), "passOutFromTired", new[] { typeof(Farmer) });
        var prefix = AccessTools.Method(typeof(SmapiSanityTwoAmSpecialDeathService), nameof(PassOutFromTiredPrefix));
        if (patchedMethod is null || prefix is null)
        {
            LogOnce("passout.two-am.patch-signature-unavailable", "Farmer.passOutFromTired(Farmer) was unavailable; the special chain failed open.", LogLevel.Error);
            patchedMethod = null;
            return;
        }

        try
        {
            harmony = new Harmony(string.Concat(modId, ".Sanity4.TwoAmPassOut"));
            harmony.Patch(patchedMethod, prefix: new HarmonyMethod(prefix));
            activePatchOwner = this;
        }
        catch (Exception exception)
        {
            harmony = null;
            patchedMethod = null;
            LogOnce("passout.two-am.patch-install-failed", $"The post-animation pass-out patch failed open ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void UninstallPassOutPatch()
    {
        if (harmony is not null && patchedMethod is not null)
        {
            try
            {
                harmony.Unpatch(patchedMethod, HarmonyPatchType.Prefix, harmony.Id);
            }
            catch (Exception exception)
            {
                LogOnce("passout.two-am.patch-uninstall-failed", $"The post-animation pass-out patch cleanup failed ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
            }
        }
        if (ReferenceEquals(activePatchOwner, this))
            activePatchOwner = null;
        harmony = null;
        patchedMethod = null;
    }

    private static bool PassOutFromTiredPrefix(Farmer who)
    {
        var owner = activePatchOwner;
        if (owner is null)
            return true;
        try
        {
            return owner.HandlePassOutFromTired(who);
        }
        catch (Exception exception)
        {
            owner.LogOnce("passout.two-am.prefix-threw", $"The post-animation pass-out prefix failed open ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
            return true;
        }
    }

    private bool HandlePassOutFromTired(Farmer farmer)
    {
        if (
            disposed
            || !Context.IsWorldReady
            || farmer is null
            || !farmer.IsLocalPlayer
            || !ReferenceEquals(farmer, Game1.player)
        )
            return true;

        if (clientRunOriginalOnce)
        {
            clientRunOriginalOnce = false;
            return true;
        }

        var classification = passOutReasons.ClassifyStartToPassOut(farmer);
        if (!classification.IsAvailable || classification.Reason is not (PassOutReason.TimeLimitPassOut or PassOutReason.ExhaustionPassOut))
            return true;

        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client)
            return BeginClientPassOutRequest(farmer, classification);
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !Context.IsMainPlayer
            || !lifecycle.IsEnabled
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
            || !lifecycle.TryGetBaseSnapshot(PlayerKey(farmer), out var sanitySnapshot)
        )
            return true;

        var route = ResolveRoute(farmer, classification.Reason, Context.ScreenId);
        if (route.Action != PassOutPolicyAction.DelegateToTwoAmSpecialDeath)
        {
            CaptureAndReport(farmer, classification);
            return true;
        }

        var request = CreateStartRequest(
            farmer,
            classification,
            route,
            sanitySnapshot.Revision,
            CreateCorrelation(lifecycle.SessionId, PlayerKey(farmer), Game1.Date.TotalDays),
            Context.ScreenId
        );
        var mutation = stateMachine.Begin(request);
        if (mutation.Snapshot is null || mutation.Status is not (SanityTwoAmSpecialDeathMutationStatus.Applied or SanityTwoAmSpecialDeathMutationStatus.Duplicate))
        {
            CaptureAndReport(farmer, classification);
            return true;
        }

        CaptureSpecialAndReport(farmer);
        ApplyActions(mutation);
        return false;
    }

    private bool BeginClientPassOutRequest(Farmer farmer, PassOutReasonClassification classification)
    {
        if (
            lateNightBehaviorEvidence.IsObserved
            || !nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain
        )
            return true;
        if (!SanityProtocol.IsValidSessionId(lifecycle.SessionId) || !lifecycle.TryGetBaseSnapshot(PlayerKey(farmer), out var sanitySnapshot))
            return true;

        var currentTick = Math.Max(0L, Game1.ticks);
        if (!string.IsNullOrEmpty(pendingClientCorrelationId))
        {
            if (currentTick <= pendingClientDeadlineTick)
                return false;
            pendingClientCorrelationId = string.Empty;
            pendingClientNonce = 0;
            pendingClientAuthorityRevision = -1;
            LogOnce("passout.two-am.client-request-timeout", "The host pass-out decision timed out; the original pass-out resumed.", LogLevel.Warn);
            return true;
        }

        var playerKey = PlayerKey(farmer);
        var correlation = CreateCorrelation(lifecycle.SessionId, playerKey, Game1.Date.TotalDays);
        if (nextClientNonce <= 0 || nextClientNonce == long.MaxValue)
            return true;
        var nonce = nextClientNonce++;
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathRequestMessage
                {
                    SessionId = lifecycle.SessionId,
                    CorrelationId = correlation,
                    PlayerKey = playerKey,
                    ScreenId = Context.ScreenId,
                    Nonce = nonce,
                    ExpectedAuthorityRevision = sanitySnapshot.Revision,
                },
                SanityTwoAmSpecialDeathContract.RequestMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
            );
            pendingClientCorrelationId = correlation;
            pendingClientNonce = nonce;
            pendingClientAuthorityRevision = sanitySnapshot.Revision;
            pendingClientDeadlineTick = currentTick + 180;
            return false;
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.client-request-send-failed", $"The client pass-out request failed open ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
            return true;
        }
    }

    private PassOutPolicyDecision ResolveRoute(Farmer farmer, PassOutReason reason, int screenId)
    {
        var mode = modeResolver.Resolve();
        var junimo = junimoBlessingResolver.Resolve();
        var locationSafe = false;
        var junimoEligible = false;
        var locationReason = TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe;
        var location = farmer.currentLocation;
        if (location is not null)
        {
            _ = environmentLight.Evaluate(farmer, screenId, location, Game1.timeOfDay);
            var playerKey = PlayerKey(farmer);
            if (
                environmentLight.TryGetDiagnostic(playerKey, screenId, out var diagnostic)
                && string.Equals(diagnostic.LocationNameOrUniqueName, location.NameOrUniqueName, StringComparison.Ordinal)
            )
            {
                locationSafe = diagnostic.TwoAmSpecialDeathSafe;
                junimoEligible = diagnostic.JunimoBlessingEligible;
                locationReason = diagnostic.TwoAmSpecialDeathReason;
            }
        }

        return SpecialPassOutRoutePolicy.Resolve(
            reason,
            new SpecialPassOutRouteContext(
                mode.HasValue,
                mode.Mode,
                junimo.HasValue,
                junimo.Enabled,
                locationSafe,
                junimoEligible,
                nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain,
                nightOwlPlusCompatibility.StableReason,
                Game1.IsMultiplayer,
                Game1.timeOfDay,
                lateNightBehaviorEvidence.IsObserved
            )
        );
    }

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (disposed)
            return;

        if (
            lateNightBehaviorEvidence.ObserveTimeChanged(
                e.NewTime,
                Game1.timeOfDay,
                Context.IsWorldReady,
                Game1.newDay
            )
        )
        {
            LogOnce(
                LateNightBehaviorEvidence.ClockRollbackReason,
                "An external late-night clock owner was observed; the conflicting special pass-out chain is disabled for this session/day.",
                LogLevel.Debug
            );
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!disposed)
            lateNightBehaviorEvidence.Reset(Game1.timeOfDay);
    }

    private SanityTwoAmSpecialDeathStartRequest CreateStartRequest(
        Farmer farmer,
        PassOutReasonClassification classification,
        PassOutPolicyDecision route,
        long authorityRevision,
        string correlationId,
        int screenId
    )
    {
        var mode = modeResolver.Resolve();
        var locationSafe = false;
        var locationReason = TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe;
        if (
            farmer.currentLocation is not null
            && environmentLight.TryGetDiagnostic(
                PlayerKey(farmer),
                screenId,
                out var diagnostic
            )
            && string.Equals(
                diagnostic.LocationNameOrUniqueName,
                farmer.currentLocation.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            locationSafe = diagnostic.TwoAmSpecialDeathSafe;
            locationReason = diagnostic.TwoAmSpecialDeathReason;
        }

        return new SanityTwoAmSpecialDeathStartRequest(
            lifecycle.SessionId,
            correlationId,
            PlayerKey(farmer),
            screenId,
            SanityAuthorityRole.Host,
            authorityRevision,
            mode.Mode,
            locationSafe,
            string.IsNullOrWhiteSpace(locationReason) ? route.StableReason : locationReason,
            farmer.health,
            farmer.maxHealth
        );
    }

    private void CaptureAndReport(Farmer farmer, PassOutReasonClassification classification)
    {
        var result = passOutReasons.CaptureStartToPassOut(farmer, classification);
        if (result.Status is PassOutReasonCaptureStatus.Rejected or PassOutReasonCaptureStatus.Conflict)
            LogOnce(result.StableReason, $"Ordinary pass-out reason capture failed ({result.StableReason}).", LogLevel.Warn);
    }

    private void CaptureSpecialAndReport(Farmer farmer)
    {
        var result = passOutReasons.CaptureSpecialDeath(
            farmer,
            Game1.timeOfDay,
            "passout.reason.special-death.two-am-policy"
        );
        if (result.Status is PassOutReasonCaptureStatus.Rejected or PassOutReasonCaptureStatus.Conflict)
            LogOnce(result.StableReason, $"Special pass-out reason capture failed ({result.StableReason}).", LogLevel.Warn);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        nightOwlPlusCompatibility = NightOwlPlusCompatibility.Resolve(
            uniqueId =>
            {
                var mod = helper.ModRegistry.Get(uniqueId);
                return mod is null
                    ? null
                    : new NightOwlPlusManifestEvidence(
                        mod.Manifest.UniqueID ?? string.Empty,
                        mod.Manifest.Name ?? string.Empty,
                        mod.Manifest.Version?.ToString() ?? string.Empty,
                        mod.Manifest.UpdateKeys ?? Array.Empty<string>()
                    );
            },
            () =>
            {
                var result = new List<NightOwlPlusManifestEvidence>();
                foreach (var mod in helper.ModRegistry.GetAll())
                {
                    if (result.Count >= NightOwlPlusCompatibility.MaximumDiagnosticManifests)
                        throw new InvalidOperationException("Night Owl Plus manifest diagnostic bound exceeded.");
                    result.Add(
                        new NightOwlPlusManifestEvidence(
                            mod.Manifest.UniqueID ?? string.Empty,
                            mod.Manifest.Name ?? string.Empty,
                            mod.Manifest.Version?.ToString() ?? string.Empty,
                            mod.Manifest.UpdateKeys ?? Array.Empty<string>()
                        )
                    );
                }
                return result;
            }
        );
        LogOnce(
            nightOwlPlusCompatibility.StableReason,
            $"Late-night mod compatibility: status={nightOwlPlusCompatibility.Status}; reason={nightOwlPlusCompatibility.StableReason}; uids={NightOwlPlusCompatibility.ExactUniqueId},{NightOwlPlusCompatibility.OriginalNightOwlUniqueId},{NightOwlPlusCompatibility.StayUpUniqueId}; version={nightOwlPlusCompatibility.DetectedVersion}.",
            nightOwlPlusCompatibility.Status == NightOwlPlusCompatibilityStatus.NotInstalled
                ? LogLevel.Debug
                : LogLevel.Warn
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed || !Context.IsWorldReady)
            return;

        if (
            lateNightBehaviorEvidence.Observe(
                Game1.timeOfDay,
                worldReady: true,
                Game1.newDay
            )
        )
        {
            LogOnce(
                LateNightBehaviorEvidence.ClockRollbackReason,
                "An external late-night clock owner was observed; the conflicting special pass-out chain is disabled for this session/day.",
                LogLevel.Debug
            );
        }

        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client)
        {
            if (clientSnapshotRequestPending && e.IsMultipleOf(30))
                TrySendClientSnapshotRequest();
            return;
        }
        // The owner-local generated Event drives its three step callbacks. The host only
        // mutates the state machine when that owner reports a step, so a farmhand cannot be
        // advanced by an unrelated host frame cadence.
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (disposed || !string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;
        try
        {
            if (
                e.Type == SanityTwoAmSpecialDeathContract.RequestMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Host
                && Context.IsMainPlayer
            )
            {
                HandleHostRequest(e.ReadAs<SanityTwoAmSpecialDeathRequestMessage>(), e.FromPlayerID);
                return;
            }
            if (
                e.Type == SanityTwoAmSpecialDeathContract.SnapshotRequestMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Host
                && Context.IsMainPlayer
            )
            {
                HandleHostSnapshotRequest(e.ReadAs<SanityTwoAmSpecialDeathSnapshotRequestMessage>(), e.FromPlayerID);
                return;
            }
            if (
                e.Type == SanityTwoAmSpecialDeathContract.StepMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Host
                && Context.IsMainPlayer
            )
            {
                HandleHostPresentationStep(e.ReadAs<SanityTwoAmSpecialDeathStepMessage>(), e.FromPlayerID);
                return;
            }
            if (
                e.Type == SanityTwoAmSpecialDeathContract.DecisionMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Client
                && e.FromPlayerID == Game1.MasterPlayer.UniqueMultiplayerID
            )
            {
                HandleClientDecision(e.ReadAs<SanityTwoAmSpecialDeathDecisionMessage>());
                return;
            }
            if (
                e.Type == SanityTwoAmSpecialDeathContract.ActionMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Client
                && e.FromPlayerID == Game1.MasterPlayer.UniqueMultiplayerID
            )
            {
                HandleClientAction(e.ReadAs<SanityTwoAmSpecialDeathActionMessage>());
                return;
            }
            if (
                e.Type == SanityTwoAmSpecialDeathContract.SnapshotMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Client
                && e.FromPlayerID == Game1.MasterPlayer.UniqueMultiplayerID
            )
                HandleClientSnapshot(e.ReadAs<SanityTwoAmSpecialDeathSnapshotMessage>());
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.message-handler-failed", $"The special pass-out message failed closed ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void HandleHostRequest(
        SanityTwoAmSpecialDeathRequestMessage? message,
        long fromPlayerId
    )
    {
        if (message is null)
            return;
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(fromPlayerId);
        if (!lifecycle.TryGetBaseSnapshot(expectedPlayerKey, out var sanitySnapshot))
        {
            SendDecision(fromPlayerId, message, true, false, "passout.two-am.host-sanity-snapshot-unavailable");
            return;
        }

        var admission = hostRequestReceipts.Admit(
            message,
            lifecycle.SessionId,
            expectedPlayerKey,
            sanitySnapshot.Revision
        );
        if (admission.Status == SanityTwoAmSpecialDeathProtocolStatus.Duplicate)
        {
            SendDecisionMessage(fromPlayerId, admission.CachedDecision!);
            return;
        }
        if (admission.Status == SanityTwoAmSpecialDeathProtocolStatus.Rejected)
        {
            SendDecision(fromPlayerId, message, true, false, admission.Reason);
            return;
        }

        var farmer = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (farmer is null)
        {
            SendDecision(fromPlayerId, message, true, false, "passout.two-am.host-player-unavailable");
            return;
        }
        var classification = passOutReasons.ClassifyStartToPassOut(farmer);
        if (!classification.IsAvailable)
        {
            SendDecision(fromPlayerId, message, true, false, classification.StableReason);
            return;
        }
        var route = ResolveRoute(farmer, classification.Reason, message.ScreenId);
        if (route.Action != PassOutPolicyAction.DelegateToTwoAmSpecialDeath)
        {
            CaptureAndReport(farmer, classification);
            SendDecision(fromPlayerId, message, true, false, route.StableReason);
            return;
        }

        var request = CreateStartRequest(
            farmer,
            classification,
            route,
            sanitySnapshot.Revision,
            message.CorrelationId,
            message.ScreenId
        );
        var mutation = stateMachine.Begin(request);
        var special = mutation.Snapshot is not null
            && mutation.Status is SanityTwoAmSpecialDeathMutationStatus.Applied or SanityTwoAmSpecialDeathMutationStatus.Duplicate;
        if (!special)
        {
            CaptureAndReport(farmer, classification);
            SendDecision(fromPlayerId, message, true, false, mutation.Reason);
            return;
        }

        CaptureSpecialAndReport(farmer);
        SendDecision(fromPlayerId, message, false, true, mutation.Reason);
        ApplyActions(mutation);
    }

    private void SendDecision(
        long playerId,
        SanityTwoAmSpecialDeathRequestMessage request,
        bool runOriginal,
        bool specialActive,
        string reason
    )
    {
        var decision = new SanityTwoAmSpecialDeathDecisionMessage
        {
            SessionId = lifecycle.SessionId,
            CorrelationId = request.CorrelationId,
            PlayerKey = request.PlayerKey,
            ScreenId = request.ScreenId,
            RequestNonce = request.Nonce,
            AuthorityRevision = request.ExpectedAuthorityRevision,
            RunOriginalPassOut = runOriginal,
            SpecialFlowActive = specialActive,
            Reason = reason,
        };
        hostRequestReceipts.RecordDecision(request, decision);
        SendDecisionMessage(playerId, decision);
    }

    private void SendDecisionMessage(long playerId, SanityTwoAmSpecialDeathDecisionMessage decision)
    {
        helper.Multiplayer.SendMessage(
            decision,
            SanityTwoAmSpecialDeathContract.DecisionMessageType,
            modIDs: new[] { modId },
            playerIDs: new[] { playerId }
        );
    }

    private void HandleClientDecision(SanityTwoAmSpecialDeathDecisionMessage? message)
    {
        var result = clientReceipts.AcceptDecision(
            message,
            pendingClientCorrelationId,
            pendingClientNonce,
            pendingClientAuthorityRevision
        );
        if (result.Status != SanityTwoAmSpecialDeathProtocolStatus.Accepted || message is null)
            return;

        pendingClientCorrelationId = string.Empty;
        pendingClientNonce = 0;
        pendingClientAuthorityRevision = -1;
        clientSpecialFlowActive = message.SpecialFlowActive;
        if (!message.RunOriginalPassOut)
            return;

        clientSpecialFlowActive = false;
        clientRunOriginalOnce = true;
        if (Game1.player is Farmer farmer)
            Farmer.passOutFromTired(farmer);
    }

    private void HandleClientAction(SanityTwoAmSpecialDeathActionMessage? message)
    {
        var result = clientReceipts.AcceptAction(message);
        if (result.Status != SanityTwoAmSpecialDeathProtocolStatus.Accepted || message is null)
            return;

        var action = new SanityTwoAmSpecialDeathAction(
            message.Kind,
            message.PlayerKey,
            message.ScreenId,
            message.CorrelationId
        );
        clientSnapshot ??= new SanityTwoAmSpecialDeathSnapshot(
            message.SessionId,
            message.CorrelationId,
            message.PlayerKey,
            message.ScreenId,
            SanityAuthorityRole.Host,
            message.AuthorityRevision,
            SanityTwoAmSpecialDeathFlowKind.DefaultDeath,
            SanityTwoAmSpecialDeathPhase.PromptEntered,
            Game1.player?.health ?? 0,
            Game1.player?.maxHealth ?? 1,
            message.Revision,
            "passout.two-am.client-action-context"
        );
        if (
            message.Kind is SanityTwoAmSpecialDeathActionKind.BeginHiddenHospitalPassOut
                or SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut
        )
            clientSpecialFlowActive = false;
        ApplyLocalAction(action, null);
    }

    private void HandleClientSnapshot(SanityTwoAmSpecialDeathSnapshotMessage? message)
    {
        if (message is null || Game1.player is null)
            return;
        if (!EnsureClientReceiptContext(message.PlayerKey))
            return;
        var result = clientReceipts.AcceptSnapshot(message);
        if (result.Status is not (SanityTwoAmSpecialDeathProtocolStatus.Accepted or SanityTwoAmSpecialDeathProtocolStatus.Duplicate))
            return;

        clientSnapshot = new SanityTwoAmSpecialDeathSnapshot(
            message.SessionId,
            message.CorrelationId,
            message.PlayerKey,
            message.ScreenId,
            SanityAuthorityRole.Host,
            message.AuthorityRevision,
            message.Kind,
            message.Phase,
            message.CurrentHealth,
            message.MaximumHealth,
            message.Revision,
            message.Reason
        );
        if (localPresentationEvent is null && message.Phase == SanityTwoAmSpecialDeathPhase.PromptEntered)
        {
            ApplyLocalAction(
                new SanityTwoAmSpecialDeathAction(
                    SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                    message.PlayerKey,
                    message.ScreenId,
                    message.CorrelationId
                ),
                clientSnapshot
            );
            ApplyLocalAction(
                new SanityTwoAmSpecialDeathAction(
                    SanityTwoAmSpecialDeathActionKind.ShowPromptEntered,
                    message.PlayerKey,
                    message.ScreenId,
                    message.CorrelationId
                ),
                clientSnapshot
            );
        }
    }

    private void HandleHostSnapshotRequest(
        SanityTwoAmSpecialDeathSnapshotRequestMessage? request,
        long fromPlayerId
    )
    {
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(fromPlayerId);
        if (
            request is null
            || request.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !string.Equals(request.SessionId, lifecycle.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.PlayerKey, playerKey, StringComparison.Ordinal)
            || !stateMachine.TryGetSnapshot(playerKey, out var snapshot)
            || snapshot.ScreenId != request.ScreenId
        )
            return;
        SendSnapshotToClient(snapshot, fromPlayerId);
    }

    private void HandleHostPresentationStep(
        SanityTwoAmSpecialDeathStepMessage? message,
        long fromPlayerId
    )
    {
        if (message is null)
            return;
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(fromPlayerId);
        if (
            message.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !string.Equals(message.SessionId, lifecycle.SessionId, StringComparison.Ordinal)
            || !string.Equals(message.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
            || message.ScreenId < 0
            || message.AuthorityRevision < 0
            || message.Step is not ("warning" or "hit" or "outcome" or "failed")
            || !stateMachine.TryGetSnapshot(expectedPlayerKey, out var snapshot)
            || !string.Equals(snapshot.CorrelationId, message.CorrelationId, StringComparison.Ordinal)
            || snapshot.AuthorityRevision != message.AuthorityRevision
        )
            return;

        if (message.Step == "failed")
        {
            ApplyActions(stateMachine.CancelPlayer(expectedPlayerKey, message.CorrelationId, "passout.two-am.presentation-failed-open"));
            return;
        }

        HandleHostPresentationStep(expectedPlayerKey, message.CorrelationId, message.Step);
    }

    private void HandleHostPresentationStep(
        string playerKey,
        string correlationId,
        string step
    )
    {
        if (!stateMachine.TryGetSnapshot(playerKey, out var snapshot))
            return;
        if (step == "failed")
        {
            ApplyActions(stateMachine.CancelPlayer(playerKey, correlationId, "passout.two-am.presentation-failed-open"));
            return;
        }

        var mutation = stateMachine.Signal(
            playerKey,
            correlationId,
            SanityTwoAmSpecialDeathSignal.AdvancePresentation,
            string.Concat("event-", step)
        );
        if (mutation.Status == SanityTwoAmSpecialDeathMutationStatus.Applied)
            ApplyActions(mutation);
    }

    private void SendActionToClient(
        SanityTwoAmSpecialDeathAction action,
        SanityTwoAmSpecialDeathSnapshot snapshot,
        long playerId
    )
    {
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathActionMessage
                {
                    SessionId = snapshot.SessionId,
                    CorrelationId = action.CorrelationId,
                    PlayerKey = action.PlayerKey,
                    ScreenId = action.ScreenId,
                    AuthorityRevision = snapshot.AuthorityRevision,
                    Revision = snapshot.Revision,
                    Kind = action.Kind,
                },
                SanityTwoAmSpecialDeathContract.ActionMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { playerId }
            );
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.action-send-failed", $"The owner-local action could not be sent ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void SendSnapshotToClient(SanityTwoAmSpecialDeathSnapshot snapshot, long playerId)
    {
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathSnapshotMessage
                {
                    SessionId = snapshot.SessionId,
                    CorrelationId = snapshot.CorrelationId,
                    PlayerKey = snapshot.PlayerKey,
                    ScreenId = snapshot.ScreenId,
                    AuthorityRevision = snapshot.AuthorityRevision,
                    Revision = snapshot.Revision,
                    Kind = snapshot.Kind,
                    Phase = snapshot.Phase,
                    CurrentHealth = snapshot.CurrentHealth,
                    MaximumHealth = snapshot.MaximumHealth,
                    Reason = snapshot.Reason,
                },
                SanityTwoAmSpecialDeathContract.SnapshotMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { playerId }
            );
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.snapshot-send-failed", $"The special pass-out snapshot could not be sent ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private bool EnsureClientReceiptContext(string playerKey)
    {
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Client
            || Game1.player is null
            || !string.Equals(playerKey, PlayerKey(Game1.player), StringComparison.Ordinal)
        )
            return false;
        if (clientReceipts.MatchesContext(lifecycle.SessionId, playerKey, Context.ScreenId))
            return true;
        return clientReceipts.Reset(lifecycle.SessionId, playerKey, Context.ScreenId);
    }

    private void TrySendClientSnapshotRequest()
    {
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Client
            || Game1.player is null
            || !EnsureClientReceiptContext(PlayerKey(Game1.player))
        )
            return;
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathSnapshotRequestMessage
                {
                    SessionId = lifecycle.SessionId,
                    PlayerKey = PlayerKey(Game1.player),
                    ScreenId = Context.ScreenId,
                },
                SanityTwoAmSpecialDeathContract.SnapshotRequestMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
            );
            clientSnapshotRequestPending = false;
        }
        catch (Exception exception)
        {
            clientSnapshotRequestPending = false;
            LogOnce("passout.two-am.snapshot-request-send-failed", $"The special pass-out snapshot request failed ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void ApplyActions(SanityTwoAmSpecialDeathMutation mutation)
    {
        if (mutation.Snapshot is not { } snapshot)
            return;

        if (
            lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && !IsCurrentLocalPlayer(snapshot.PlayerKey)
            && SanityPlayerKey.TryParseCanonicalPlayerId(snapshot.PlayerKey, out var playerId)
        )
            SendSnapshotToClient(snapshot, playerId);

        foreach (var action in mutation.Actions)
        {
            if (
                lifecycle.AuthorityRole == SanityAuthorityRole.Host
                && !IsCurrentLocalPlayer(action.PlayerKey)
            )
            {
                if (action.Kind is SanityTwoAmSpecialDeathActionKind.BeginHiddenHospitalPassOut or SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut)
                    PrepareRemoteOutcome(snapshot, action.Kind);
                if (SanityPlayerKey.TryParseCanonicalPlayerId(action.PlayerKey, out var remotePlayerId))
                    SendActionToClient(action, snapshot, remotePlayerId);
                continue;
            }
            ApplyLocalAction(action, snapshot);
        }
    }

    private void ApplyLocalAction(
        SanityTwoAmSpecialDeathAction action,
        SanityTwoAmSpecialDeathSnapshot? providedSnapshot
    )
    {
        var snapshot = providedSnapshot;
        if (snapshot is null && clientSnapshot is not null)
            snapshot = clientSnapshot;
        switch (action.Kind)
        {
            case SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay:
                audio.SetSpecialEventAudioAllowed(true);
                var begin = events.BeginSpecialFlow(
                    action.PlayerKey,
                    action.ScreenId,
                    lifecycle.SessionId,
                    SanityTwoAmSpecialDeathContract.SpecialEventId
                );
                if (begin.Status == SanityEffectiveOverlayMutationStatus.Rejected)
                {
                    LogOnce(begin.Reason, $"The special event overlay failed ({begin.Reason}).", LogLevel.Error);
                    return;
                }
                if (Game1.player is Farmer localFarmer)
                    BeginLocalPresentation(localFarmer);
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptEntered:
                break;
            case SanityTwoAmSpecialDeathActionKind.PlayWarningCue:
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptWarning:
                break;
            case SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack:
                break;
            case SanityTwoAmSpecialDeathActionKind.PlayDeathSound:
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptResolved:
                break;
            case SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay:
                audio.RemoveDarknessWarningClaim(
                    action.PlayerKey,
                    action.ScreenId,
                    lifecycle.SessionId,
                    action.CorrelationId
                );
                EndLocalPresentation();
                events.EndSpecialFlow(
                    action.PlayerKey,
                    action.ScreenId,
                    lifecycle.SessionId,
                    SanityTwoAmSpecialDeathContract.SpecialEventId
                );
                audio.SetSpecialEventAudioAllowed(false);
                break;
            case SanityTwoAmSpecialDeathActionKind.BeginHiddenHospitalPassOut:
                if (snapshot is not null && Game1.player is Farmer deathFarmer)
                {
                    deathCauses.MarkDarkness(deathFarmer);
                    BeginHiddenHospitalPassOut(deathFarmer);
                }
                break;
            case SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut:
                if (snapshot is not null && Game1.player is Farmer homeFarmer)
                    BeginNonLethalPassOut(homeFarmer, snapshot);
                break;
        }
    }

    private void BeginLocalPresentation(Farmer farmer)
    {
        if (localPresentationEvent is not null || Game1.currentLocation is null)
            return;
        if (!stateMachine.TryGetSnapshot(PlayerKey(farmer), out var snapshot))
            snapshot = clientSnapshot;
        if (snapshot is null)
        {
            RunOriginalPassOutAfterPresentationFailure(farmer, null);
            return;
        }
        try
        {
            if (Game1.activeClickableMenu is not null)
            {
                Game1.activeClickableMenu.emergencyShutDown();
                Game1.activeClickableMenu = null;
            }
            farmer.completelyStopAnimatingOrDoingAction();
            farmer.CanMove = false;
            farmer.FarmerSprite.setCurrentSingleFrame(5, 3000);
            farmer.FarmerSprite.PauseForSingleAnimation = true;
            var eventInstance = new SanityTwoAmDarknessEvent(
                farmer,
                helper.Translation.Get(EnteredPromptKey).ToString(),
                helper.Translation.Get(WarningPromptKey).ToString(),
                helper.Translation.Get(ResolvedPromptKey).ToString(),
                command => OnLocalPresentationCommand(snapshot, command)
            );
            eventInstance.onEventFinished = () => OnLocalPresentationFinished(snapshot);
            localPresentationEvent = eventInstance;
            Game1.currentLocation.startEvent(eventInstance);
            if (!ReferenceEquals(Game1.currentLocation.currentEvent, eventInstance))
                throw new InvalidOperationException("The game rejected the generated darkness event.");
            localPresentationSnapshot = snapshot;
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.presentation-start-failed", $"The owner-local darkness event could not start ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
            EndLocalPresentation();
            RunOriginalPassOutAfterPresentationFailure(farmer, snapshot);
        }
    }

    private void RunOriginalPassOutAfterPresentationFailure(
        Farmer farmer,
        SanityTwoAmSpecialDeathSnapshot? snapshot
    )
    {
        if (snapshot is not null)
            SendOrHandlePresentationStep(snapshot, "failed");
        clientRunOriginalOnce = true;
        Farmer.passOutFromTired(farmer);
    }

    private void OnLocalPresentationCommand(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmDarknessEventCommand command
    )
    {
        if (command == SanityTwoAmDarknessEventCommand.Warning)
        {
            audio.SubmitDarknessWarningClaim(
                new SanityDarknessWarningClaim(
                    snapshot.PlayerKey,
                    snapshot.ScreenId,
                    snapshot.SessionId,
                    snapshot.CorrelationId,
                    long.MaxValue
                )
            );
            SendOrHandlePresentationStep(snapshot, "warning");
            return;
        }
        if (command == SanityTwoAmDarknessEventCommand.Hit)
        {
            audio.RemoveDarknessWarningClaim(
                snapshot.PlayerKey,
                snapshot.ScreenId,
                snapshot.SessionId,
                snapshot.CorrelationId
            );
            Game1.player?.playNearbySoundAll("ow");
            audio.TriggerDarknessAttack();
            if (snapshot.Kind == SanityTwoAmSpecialDeathFlowKind.DefaultDeath)
                Game1.player?.playNearbySoundAll("death");
            SendOrHandlePresentationStep(snapshot, "hit");
            return;
        }
    }

    private void OnLocalPresentationFinished(SanityTwoAmSpecialDeathSnapshot snapshot)
    {
        localPresentationEvent = null;
        localPresentationSnapshot = null;
        SendOrHandlePresentationStep(snapshot, "outcome");
    }

    private void SendOrHandlePresentationStep(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        string step
    )
    {
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host && Context.IsMainPlayer)
        {
            HandleHostPresentationStep(snapshot.PlayerKey, snapshot.CorrelationId, step);
            return;
        }
        if (lifecycle.AuthorityRole != SanityAuthorityRole.Client)
            return;
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathStepMessage
                {
                    SessionId = snapshot.SessionId,
                    CorrelationId = snapshot.CorrelationId,
                    PlayerKey = snapshot.PlayerKey,
                    ScreenId = snapshot.ScreenId,
                    AuthorityRevision = snapshot.AuthorityRevision,
                    Step = step,
                },
                SanityTwoAmSpecialDeathContract.StepMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
            );
        }
        catch (Exception exception)
        {
            LogOnce("passout.two-am.presentation-step-send-failed", $"The owner-local presentation step failed ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void EndLocalPresentation()
    {
        if (localPresentationEvent is null)
            return;
        if (Game1.activeClickableMenu is not null)
        {
            Game1.activeClickableMenu.emergencyShutDown();
            Game1.activeClickableMenu = null;
        }
        Game1.dialogueUp = false;
        Game1.dialogueTyping = false;
        if (Game1.currentLocation is not null && ReferenceEquals(Game1.currentLocation.currentEvent, localPresentationEvent))
            Game1.currentLocation.currentEvent = null;
        localPresentationEvent = null;
        Game1.eventUp = false;
        Game1.eventOver = false;
        Game1.displayHUD = true;
        if (Game1.player is Farmer farmer)
        {
            farmer.canOnlyWalk = false;
            farmer.CanMove = true;
        }
        Game1.fadeScreenToBlack();
        Game1.fadeToBlack = true;
        Game1.fadeToBlackAlpha = 1f;
    }

    private void ShowPrompt(string key)
    {
        var text = helper.Translation.Get(key).ToString();
        Game1.drawDialogueNoTyping(text);
    }

    private void BeginHiddenHospitalPassOut(Farmer farmer)
    {
        if (!registerHiddenHospitalDeath(farmer))
        {
            LogOnce(
                "passout.two-am.hidden-wake-death-registration-failed",
                "The hidden Hospital wake-death marker could not be registered; the special pass-out is falling back to the immediate vanilla death handoff.",
                LogLevel.Error
            );
            BeginHiddenHospitalHandoff(farmer);
            return;
        }

        farmer.CanMove = false;
        farmer.FarmerSprite.setCurrentSingleFrame(5, 3000);
        farmer.FarmerSprite.PauseForSingleAnimation = true;
        beginningOfficialPassOut = true;
        SanityTwoAmVanillaPassOutBridge.BeginNoFeePassOut(farmer);
    }

    internal void BeginHiddenHospitalHandoff(Farmer farmer)
    {
        if (
            disposed
            || farmer is null
            || !farmer.IsLocalPlayer
            || !ReferenceEquals(farmer, Game1.player)
        )
            return;

        deathCauses.MarkDarkness(farmer);
        farmer.health = 0;
        farmer.CanMove = false;
        farmer.freezePause = 0;
        Game1.screenGlow = false;
        Game1.messageAfterPause = string.Empty;
        Game1.dialogueUp = false;
        Game1.killScreen = true;
        Game1.pauseTime = 1f;
        Game1.stats.TimesUnconscious++;
        Game1.changeMusicTrack("silence");
    }

    private void BeginNonLethalPassOut(
        Farmer farmer,
        SanityTwoAmSpecialDeathSnapshot snapshot
    )
    {
        var health = SanityTwoAmVanillaPassOutPolicy.ResolveNonLethalWakeHealth(
            snapshot.CurrentHealth,
            snapshot.MaximumHealth
        );
        farmer.health = health;
        if (
            !specialSanitySettlement.RegisterNonLethal(
                lifecycle.SessionId,
                PlayerKey(farmer),
                Math.Max(0, Game1.Date.TotalDays),
                out var reason
            )
            && reason != "passout.sanity-settlement.duplicate"
        )
            LogOnce(reason, $"NonLethal Sanity settlement could not be registered ({reason}).", LogLevel.Error);
        beginningOfficialPassOut = true;
        SanityTwoAmVanillaPassOutBridge.BeginNoFeePassOut(farmer);
    }

    private void PrepareRemoteOutcome(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        SanityTwoAmSpecialDeathActionKind kind
    )
    {
        var farmer = ResolveFarmer(snapshot.PlayerKey);
        if (farmer is null)
            return;
        if (kind == SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut)
        {
            farmer.health = SanityTwoAmVanillaPassOutPolicy.ResolveNonLethalWakeHealth(
                snapshot.CurrentHealth,
                snapshot.MaximumHealth
            );
            _ = specialSanitySettlement.RegisterNonLethal(
                lifecycle.SessionId,
                snapshot.PlayerKey,
                Math.Max(0, Game1.Date.TotalDays),
                out _
            );
        }
        else
        {
            registerHiddenHospitalDeath(farmer);
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        ClearSession("passout.two-am.save-loaded-reset");
        lateNightBehaviorEvidence.Reset(Game1.timeOfDay);
        persistenceCanWrite = true;
        if (lifecycle.AuthorityRole != SanityAuthorityRole.Host)
        {
            if (Game1.player is not null)
                clientSnapshotRequestPending = clientReceipts.Reset(lifecycle.SessionId, PlayerKey(Game1.player), Context.ScreenId);
            return;
        }
        try
        {
            var data = helper.Data.ReadSaveData<SanityTwoAmSpecialDeathSaveData>(SanityTwoAmSpecialDeathContract.SaveKey);
            var validation = SanityTwoAmSpecialDeathPersistence.Validate(data);
            if (validation.Status == SanityTwoAmSpecialDeathPersistenceStatus.Invalid)
            {
                persistenceCanWrite = false;
                LogOnce(validation.Reason, $"The legacy special pass-out save payload was not overwritten ({validation.Reason}).", LogLevel.Error);
            }
            else if (validation.Data is not null && validation.Data.Flows.Count > 0)
                LogOnce(validation.Reason, "Legacy special pass-out recovery data was safely discarded; no mail or clinic recovery was resumed.", LogLevel.Debug);
        }
        catch (Exception exception)
        {
            persistenceCanWrite = false;
            LogOnce("passout.two-am.save-read-failed", $"The special pass-out save payload became read-only ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (disposed || !persistenceCanWrite || lifecycle.AuthorityRole != SanityAuthorityRole.Host || !Context.IsMainPlayer)
            return;
        try
        {
            helper.Data.WriteSaveData(SanityTwoAmSpecialDeathContract.SaveKey, SanityTwoAmSpecialDeathPersistence.Capture(stateMachine.SnapshotAll()));
        }
        catch (Exception exception)
        {
            persistenceCanWrite = false;
            LogOnce("passout.two-am.save-write-failed", $"The special pass-out save payload became read-only ({exception.GetType().Name}: {exception.Message}).", LogLevel.Error);
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (disposed)
            return;
        var key = PlayerKey(e.Player);
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client)
        {
            if (e.IsLocalPlayer && clientSpecialFlowActive && !beginningOfficialPassOut)
                ClearSession("passout.two-am.client-unexpected-warp");
            return;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host && Context.IsMainPlayer && stateMachine.TryGetSnapshot(key, out var snapshot) && snapshot.Phase != SanityTwoAmSpecialDeathPhase.Completed && !beginningOfficialPassOut)
            ApplyActions(stateMachine.CancelPlayer(key, snapshot.CorrelationId, "passout.two-am.unexpected-warp"));
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (disposed)
            return;
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host && Context.IsMainPlayer)
        {
            var key = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
            hostRequestReceipts.ForgetPlayer(key);
            if (stateMachine.TryGetSnapshot(key, out var snapshot))
                SendSnapshotToClient(snapshot, e.Peer.PlayerID);
        }
        else if (lifecycle.AuthorityRole == SanityAuthorityRole.Client && e.Peer.IsHost)
            clientSnapshotRequestPending = true;
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (disposed)
            return;
        var key = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Host && Context.IsMainPlayer)
        {
            hostRequestReceipts.ForgetPlayer(key);
            passOutReasons.ForgetPlayer(key);
            return;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client && e.Peer.IsHost)
            ClearSession("passout.two-am.host-peer-disconnected");
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (stateEvent.Kind == SanityStateEventKind.SystemDisabled)
            ClearSession("passout.two-am.sanity-system-disabled");
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearSession("passout.two-am.returned-to-title");
        persistenceCanWrite = true;
    }

    private void ClearSession(string reason)
    {
        EndLocalPresentation();
        audio.SetSpecialEventAudioAllowed(false);
        if (Game1.player is Farmer farmer)
            audio.RemoveDarknessWarningOwner(PlayerKey(farmer));
        stateMachine.ClearSession();
        hostRequestReceipts.Clear();
        clientReceipts.Clear();
        specialSanitySettlement.ClearSession(lifecycle.SessionId);
        nextPresentationTickByPlayer.Clear();
        pendingClientCorrelationId = string.Empty;
        pendingClientNonce = 0;
        pendingClientAuthorityRevision = -1;
        pendingClientDeadlineTick = 0;
        clientRunOriginalOnce = false;
        clientSpecialFlowActive = false;
        clientSnapshotRequestPending = false;
        clientSnapshot = null;
        beginningOfficialPassOut = false;
        lateNightBehaviorEvidence.Clear();
        if (!string.IsNullOrWhiteSpace(reason))
            LogOnce(reason, $"The special pass-out session was cleared ({reason}).", LogLevel.Debug);
    }

    private static string PlayerKey(Farmer farmer) => SanityPlayerKey.FromUniqueMultiplayerId(farmer.UniqueMultiplayerID);

    private static Farmer? ResolveFarmer(string playerKey)
    {
        if (Game1.player is Farmer local && string.Equals(PlayerKey(local), playerKey, StringComparison.Ordinal))
            return local;
        return SanityPlayerKey.TryParseCanonicalPlayerId(playerKey, out var id) ? Game1.GetPlayer(id, onlyOnline: true) : null;
    }

    private static bool IsCurrentLocalPlayer(string playerKey) => Game1.player is Farmer farmer && string.Equals(PlayerKey(farmer), playerKey, StringComparison.Ordinal);

    private static string CreateCorrelation(string sessionId, string playerKey, int totalDays)
    {
        var material = string.Concat(sessionId, "\n", playerKey, "\n", totalDays.ToString(CultureInfo.InvariantCulture), "\nSanityDarknessSpecialDeath");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var bytes = new byte[16];
        Buffer.BlockCopy(hash, 0, bytes, 0, bytes.Length);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes).ToString("N");
    }

    private void OnProcessExit(object? sender, EventArgs e) { }

    private void LogOnce(string reason, string message, LogLevel level)
    {
        if (loggedReasons.Count >= MaximumLoggedReasons || !loggedReasons.Add(reason))
            return;
        monitor.Log(message, level);
    }
}
