#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Events;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.PassOut.Compatibility;
using DontStarve.Player.Stats.Sanity.PassOut.Damage;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// SMAPI boundary for the original-design 2:00 behavior. The narrow startToPassOut prefix only
/// postpones the vanilla time-limit pass-out while a Default special presentation is active;
/// Off, safe locations, NonLethal and every unsupported/client case keep the original call.
/// </summary>
internal sealed class SmapiSanityTwoAmSpecialDeathService : IDisposable
{
    private const int PresentationCadenceTicks = 60;
    private const int MaximumLoggedReasons = 64;

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
    private readonly SmapiDarknessAttackResolutionService darknessResolution;
    private readonly SanitySmapiEventService events;
    private readonly SanitySmapiAudioService audio;
    private readonly SanityTwoAmSpecialDeathStateMachine stateMachine = new();
    private readonly Dictionary<string, long> nextPresentationTickByPlayer =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private readonly SanityTwoAmSpecialDeathHostRequestReceipts hostRequestReceipts =
        new();
    private readonly SanityTwoAmSpecialDeathClientReceipts clientReceipts = new();
    private NightOwlPlusCompatibilityResult nightOwlPlusCompatibility =
        NightOwlPlusCompatibility.Unresolved();
    private Harmony? harmony;
    private MethodInfo? patchedMethod;
    private bool persistenceCanWrite = true;
    private bool controlsFrozen;
    private bool beginningOfficialNewDay;
    private string pendingClientCorrelationId = string.Empty;
    private long pendingClientNonce;
    private long pendingClientAuthorityRevision = -1;
    private long nextClientNonce = 1;
    private long pendingClientDeadlineTick;
    private bool clientSnapshotRequestPending;
    private bool clientRunOriginalOnce;
    private bool clientSpecialFlowActive;
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
        SmapiDarknessAttackResolutionService darknessResolution,
        SanitySmapiEventService events,
        SanitySmapiAudioService audio
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.passOutReasons = passOutReasons
            ?? throw new ArgumentNullException(nameof(passOutReasons));
        this.modeResolver = modeResolver
            ?? throw new ArgumentNullException(nameof(modeResolver));
        this.junimoBlessingResolver = junimoBlessingResolver
            ?? throw new ArgumentNullException(nameof(junimoBlessingResolver));
        this.environmentLight = environmentLight
            ?? throw new ArgumentNullException(nameof(environmentLight));
        this.locationRules = locationRules
            ?? throw new ArgumentNullException(nameof(locationRules));
        this.darknessResolution = darknessResolution
            ?? throw new ArgumentNullException(nameof(darknessResolution));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

        InstallStartPatch();
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Content.AssetRequested += OnAssetRequested;
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
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.GameLoop.Saving -= OnSaving;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        helper.Events.Player.Warped -= OnWarped;
        helper.Events.Content.AssetRequested -= OnAssetRequested;
        helper.Events.Multiplayer.PeerConnected -= OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        ClearSession("passout.two-am.runtime-disposed");
        UninstallStartPatch();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        nightOwlPlusCompatibility = NightOwlPlusCompatibility.Resolve(
            CaptureExactNightOwlPlusManifest,
            CaptureLoadedManifestDiagnostics
        );

        var level = nightOwlPlusCompatibility.Status switch
        {
            NightOwlPlusCompatibilityStatus.NotInstalled => LogLevel.Debug,
            NightOwlPlusCompatibilityStatus.InstalledSupported => LogLevel.Info,
            _ => LogLevel.Warn,
        };
        var version = string.IsNullOrEmpty(nightOwlPlusCompatibility.DetectedVersion)
            ? "unavailable"
            : nightOwlPlusCompatibility.DetectedVersion;
        var detail = string.IsNullOrEmpty(nightOwlPlusCompatibility.DiagnosticDetail)
            ? "none"
            : nightOwlPlusCompatibility.DiagnosticDetail;
        LogOnce(
            nightOwlPlusCompatibility.StableReason,
            $"Night Owl Plus compatibility: capability={NightOwlPlusCompatibility.CapabilityId}; status={nightOwlPlusCompatibility.Status}; reason={nightOwlPlusCompatibility.StableReason}; detail={detail}; uid={NightOwlPlusCompatibility.ExactUniqueId}; version={version}; supportedVersion={NightOwlPlusCompatibility.ExplicitlySupportedVersion}; nexusDiagnostic={nightOwlPlusCompatibility.DiagnosticUpdateKeyObserved}; conflictingSpecialChainAllowed={nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain}.",
            level
        );
    }

    private NightOwlPlusManifestEvidence? CaptureExactNightOwlPlusManifest(
        string uniqueId
    )
    {
        var mod = helper.ModRegistry.Get(uniqueId);
        return mod is null ? null : CaptureManifestEvidence(mod.Manifest);
    }

    private IReadOnlyList<NightOwlPlusManifestEvidence> CaptureLoadedManifestDiagnostics()
    {
        var manifests = new List<NightOwlPlusManifestEvidence>();
        foreach (var mod in helper.ModRegistry.GetAll())
        {
            if (manifests.Count >= NightOwlPlusCompatibility.MaximumDiagnosticManifests)
            {
                throw new InvalidOperationException(
                    "The loaded manifest diagnostic exceeded its fixed bound."
                );
            }
            manifests.Add(CaptureManifestEvidence(mod.Manifest));
        }
        return manifests;
    }

    private static NightOwlPlusManifestEvidence CaptureManifestEvidence(
        IManifest? manifest
    )
    {
        if (manifest is null)
        {
            return new NightOwlPlusManifestEvidence(
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<string>()
            );
        }

        return new NightOwlPlusManifestEvidence(
            manifest.UniqueID ?? string.Empty,
            manifest.Name ?? string.Empty,
            manifest.Version?.ToString() ?? string.Empty,
            manifest.UpdateKeys ?? Array.Empty<string>()
        );
    }

    private void InstallStartPatch()
    {
        if (activePatchOwner is not null && !ReferenceEquals(activePatchOwner, this))
        {
            LogOnce(
                "passout.two-am.patch-owner-conflict",
                "The 2:00 pass-out prefix was not installed because another runtime owner exists.",
                LogLevel.Error
            );
            return;
        }

        patchedMethod = AccessTools.Method(
            typeof(Farmer),
            "startToPassOut",
            Type.EmptyTypes
        );
        var prefix = AccessTools.Method(
            typeof(SmapiSanityTwoAmSpecialDeathService),
            nameof(StartToPassOutPrefix)
        );
        if (patchedMethod is null || prefix is null)
        {
            LogOnce(
                "passout.two-am.patch-signature-unavailable",
                "The 2:00 pass-out prefix failed closed because Farmer.startToPassOut() was unavailable.",
                LogLevel.Error
            );
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
            LogOnce(
                "passout.two-am.patch-install-failed",
                $"The 2:00 pass-out prefix failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void UninstallStartPatch()
    {
        if (harmony is not null && patchedMethod is not null)
        {
            try
            {
                harmony.Unpatch(patchedMethod, HarmonyPatchType.Prefix, harmony.Id);
            }
            catch (Exception exception)
            {
                LogOnce(
                    "passout.two-am.patch-uninstall-failed",
                    $"The 2:00 pass-out prefix cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Error
                );
            }
        }
        if (ReferenceEquals(activePatchOwner, this))
            activePatchOwner = null;
        harmony = null;
        patchedMethod = null;
    }

    private static bool StartToPassOutPrefix(Farmer __instance)
    {
        var owner = activePatchOwner;
        if (owner is null)
            return true;
        try
        {
            return owner.ShouldRunOriginalStartToPassOut(__instance);
        }
        catch (Exception exception)
        {
            owner.LogOnce(
                "passout.two-am.prefix-threw",
                $"The 2:00 prefix failed open ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return true;
        }
    }

    private bool ShouldRunOriginalStartToPassOut(Farmer farmer)
    {
        if (
            disposed
            || beginningOfficialNewDay
            || !Context.IsWorldReady
            || farmer is null
            || !farmer.IsLocalPlayer
            || !ReferenceEquals(farmer, Game1.player)
        )
        {
            return true;
        }

        var classification = passOutReasons.ClassifyStartToPassOut(farmer);
        if (!classification.IsAvailable)
        {
            if (lifecycle.AuthorityRole == SanityAuthorityRole.Host)
                CaptureAndReport(farmer, classification);
            return true;
        }
        if (classification.Reason == PassOutReason.ExhaustionPassOut)
        {
            if (lifecycle.AuthorityRole == SanityAuthorityRole.Host)
                CaptureAndReport(farmer, classification);
            return true;
        }
        if (classification.Reason != PassOutReason.TimeLimitPassOut)
            return true;

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            farmer.UniqueMultiplayerID
        );
        if (
            lifecycle.AuthorityRole == SanityAuthorityRole.Client
            && !nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain
        )
        {
            return true;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client)
            return HandleClientStartToPassOut(playerKey);
        if (
            stateMachine.TryGetSnapshot(playerKey, out var active)
            && active.Phase
                is not (
                    SanityTwoAmSpecialDeathPhase.Recovered
                    or SanityTwoAmSpecialDeathPhase.Completed
                )
        )
        {
            CaptureSpecialAndReport(farmer);
            return active.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic;
        }

        if (
            !lifecycle.IsEnabled
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !Context.IsMainPlayer
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
            || !SanityPlayerKey.IsCanonical(playerKey)
            || !lifecycle.TryGetBaseSnapshot(playerKey, out var sanitySnapshot)
        )
        {
            return true;
        }

        var mode = modeResolver.Resolve();
        var junimo = junimoBlessingResolver.Resolve();

        var locationReason = TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe;
        var locationSafe = false;
        var junimoEligible = false;
        var screenId = Context.ScreenId;
        var location = Game1.currentLocation;
        if (location is not null)
        {
            _ = environmentLight.Evaluate(farmer, screenId, location, Game1.timeOfDay);
            if (
                environmentLight.TryGetDiagnostic(playerKey, screenId, out var diagnostic)
                && string.Equals(
                    diagnostic.LocationNameOrUniqueName,
                    location.NameOrUniqueName,
                    StringComparison.Ordinal
                )
            )
            {
                locationSafe = diagnostic.TwoAmSpecialDeathSafe;
                locationReason = diagnostic.TwoAmSpecialDeathReason;
                junimoEligible = diagnostic.JunimoBlessingEligible;
            }
        }

        var route = PassOutReasonPolicy.ResolveTimeLimitRoute(
            new PassOutTimeLimitPolicyContext(
                mode.HasValue,
                mode.Mode,
                junimo.HasValue,
                junimo.Enabled,
                locationSafe,
                junimoEligible,
                locationReason,
                nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain,
                nightOwlPlusCompatibility.StableReason
            )
        );
        if (route.Action != PassOutPolicyAction.DelegateToTwoAmSpecialDeath)
        {
            CaptureAndReport(farmer, classification);
            if (!mode.HasValue)
            {
                LogOnce(
                    mode.Reason,
                    $"The 2:00 mode was unavailable; the original pass-out stayed authoritative ({mode.Reason}).",
                    LogLevel.Warn
                );
            }
            if (!junimo.HasValue)
            {
                LogOnce(
                    junimo.StableReason,
                    $"The Junimo setting was unavailable; the original pass-out stayed authoritative ({junimo.StableReason}).",
                    LogLevel.Warn
                );
            }
            return true;
        }

        var correlationId = CreateCorrelation(
            lifecycle.SessionId,
            playerKey,
            Game1.Date.TotalDays
        );
        var result = stateMachine.Begin(
            new SanityTwoAmSpecialDeathStartRequest(
                lifecycle.SessionId,
                correlationId,
                playerKey,
                screenId,
                lifecycle.AuthorityRole,
                sanitySnapshot.Revision,
                mode.Mode,
                locationSafe,
                locationReason,
                farmer.health,
                farmer.maxHealth
            )
        );
        ApplyActions(result);

        if (mode.Mode == DarknessDamageMode.NonLethal && result.Snapshot is { } nonLethal)
        {
            CaptureSpecialAndReport(farmer);
            SettleNonLethal(nonLethal);
            return true;
        }

        var defaultSpecial = result.Snapshot?.Kind
                == SanityTwoAmSpecialDeathFlowKind.DefaultClinic
            && result.Status
                is SanityTwoAmSpecialDeathMutationStatus.Applied
                    or SanityTwoAmSpecialDeathMutationStatus.Duplicate;
        if (defaultSpecial)
        {
            CaptureSpecialAndReport(farmer);
            return false;
        }

        CaptureAndReport(
            farmer,
            new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Unavailable,
                PassOutReason.Unknown,
                Game1.timeOfDay,
                "passout.reason.special-death-start-rejected"
            )
        );
        return true;
    }

    private void CaptureAndReport(
        Farmer farmer,
        PassOutReasonClassification classification
    )
    {
        ReportCapture(passOutReasons.CaptureStartToPassOut(farmer, classification));
    }

    private void CaptureSpecialAndReport(Farmer farmer)
    {
        ReportCapture(
            passOutReasons.CaptureSpecialDeath(
                farmer,
                Game1.timeOfDay,
                "passout.reason.special-death.two-am-policy"
            )
        );
    }

    private void ReportCapture(PassOutReasonCaptureResult result)
    {
        if (
            result.Status
                is PassOutReasonCaptureStatus.Rejected
                    or PassOutReasonCaptureStatus.Conflict
        )
        {
            LogOnce(
                result.StableReason,
                $"The pass-out reason evidence failed closed ({result.StableReason}).",
                LogLevel.Warn
            );
        }
    }

    private bool HandleClientStartToPassOut(string playerKey)
    {
        if (clientRunOriginalOnce)
        {
            clientRunOriginalOnce = false;
            return true;
        }
        if (clientSpecialFlowActive)
            return false;
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
            || !lifecycle.TryGetBaseSnapshot(playerKey, out var snapshot)
        )
        {
            return true;
        }
        if (!EnsureClientReceiptContext(playerKey))
            return true;

        var currentTick = Math.Max(0L, Game1.ticks);
        if (!string.IsNullOrEmpty(pendingClientCorrelationId))
        {
            if (currentTick <= pendingClientDeadlineTick)
                return false;
            pendingClientCorrelationId = string.Empty;
            pendingClientNonce = 0;
            pendingClientAuthorityRevision = -1;
            LogOnce(
                "passout.two-am.client-request-timeout",
                "The 2:00 host decision timed out; the client resumed the original pass-out.",
                LogLevel.Warn
            );
            return true;
        }

        var correlationId = CreateCorrelation(
            lifecycle.SessionId,
            playerKey,
            Game1.Date.TotalDays
        );
        if (nextClientNonce <= 0 || nextClientNonce == long.MaxValue)
        {
            LogOnce(
                "passout.two-am.client-request-nonce-exhausted",
                "The 2:00 client request nonce was exhausted; the original pass-out stayed authoritative.",
                LogLevel.Error
            );
            return true;
        }
        var nonce = nextClientNonce++;
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathRequestMessage
                {
                    SessionId = lifecycle.SessionId,
                    CorrelationId = correlationId,
                    PlayerKey = playerKey,
                    ScreenId = Context.ScreenId,
                    Nonce = nonce,
                    ExpectedAuthorityRevision = snapshot.Revision,
                },
                SanityTwoAmSpecialDeathContract.RequestMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
            );
            pendingClientCorrelationId = correlationId;
            pendingClientNonce = nonce;
            pendingClientAuthorityRevision = snapshot.Revision;
            pendingClientDeadlineTick = currentTick + 180;
            return false;
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.client-request-send-failed",
                $"The 2:00 client request failed open ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return true;
        }
    }

    private void SettleNonLethal(SanityTwoAmSpecialDeathSnapshot snapshot)
    {
        var result = darknessResolution.ReduceSanityDarknessSpecialDeathToFloor(
            new SanityDarknessSpecialDeathNonLethalRequest(
                snapshot.SessionId,
                snapshot.CorrelationId,
                snapshot.PlayerKey,
                snapshot.Authority,
                snapshot.AuthorityRevision,
                snapshot.CurrentHealth,
                snapshot.MaximumHealth
            )
        );
        if (
            result.Status
                is SanityDarknessSpecialDeathNonLethalStatus.Applied
                    or SanityDarknessSpecialDeathNonLethalStatus.NoChange
                    or SanityDarknessSpecialDeathNonLethalStatus.Duplicate
        )
        {
            stateMachine.Signal(
                snapshot.PlayerKey,
                snapshot.CorrelationId,
                SanityTwoAmSpecialDeathSignal.NonLethalSettled,
                "nonlethal-settled"
            );
            if (result.ShouldEmitDamageFeedback)
            {
                Game1.showGlobalMessage(
                    helper.Translation.Get("passout.two-am.nonlethal-feedback")
                );
            }
            return;
        }

        LogOnce(
            result.Reason,
            $"The 2:00 NonLethal floor operation failed closed ({result.Reason}); the original home pass-out continues.",
            LogLevel.Error
        );
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        try
        {
            HandleModMessageReceived(e);
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.message-handler-failed",
                $"The 2:00 multiplayer message failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (disposed)
            return;
        if (
            lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && Context.IsMainPlayer
        )
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
            hostRequestReceipts.ForgetPlayer(playerKey);
            if (stateMachine.TryGetSnapshot(playerKey, out var snapshot))
                SendSnapshotToClient(snapshot, e.Peer.PlayerID);
            return;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client && e.Peer.IsHost)
            clientSnapshotRequestPending = true;
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (disposed)
            return;
        if (
            lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && Context.IsMainPlayer
        )
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID);
            // The host-owned recovery flow remains resumable for late join, while transport and
            // day-ending reason receipts for the disconnected peer cannot be replayed.
            hostRequestReceipts.ForgetPlayer(playerKey);
            passOutReasons.ForgetPlayer(playerKey);
            return;
        }
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client && e.Peer.IsHost)
        {
            clientSnapshotRequestPending = false;
            ClearSession("passout.two-am.host-peer-disconnected");
        }
    }

    private void HandleModMessageReceived(ModMessageReceivedEventArgs e)
    {
        if (disposed || !string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        if (
            e.Type == SanityTwoAmSpecialDeathContract.RequestMessageType
            && lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && Context.IsMainPlayer
        )
        {
            HandleHostRequestMessage(
                e.ReadAs<SanityTwoAmSpecialDeathRequestMessage>(),
                e.FromPlayerID
            );
            return;
        }
        if (
            e.Type == SanityTwoAmSpecialDeathContract.SnapshotRequestMessageType
            && lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && Context.IsMainPlayer
        )
        {
            HandleHostSnapshotRequest(
                e.ReadAs<SanityTwoAmSpecialDeathSnapshotRequestMessage>(),
                e.FromPlayerID
            );
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
        {
            HandleClientSnapshot(e.ReadAs<SanityTwoAmSpecialDeathSnapshotMessage>());
        }
    }

    private void HandleHostRequestMessage(
        SanityTwoAmSpecialDeathRequestMessage? message,
        long fromPlayerId
    )
    {
        if (message is null)
            return;
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(fromPlayerId);
        if (!lifecycle.TryGetBaseSnapshot(expectedPlayerKey, out var sanitySnapshot))
        {
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                "passout.two-am.host-authority-snapshot-unavailable"
            );
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
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                admission.Reason
            );
            return;
        }

        var farmer = Game1.GetPlayer(fromPlayerId, true);
        var mode = modeResolver.Resolve();
        if (
            farmer is null
            || farmer.currentLocation is null
            || farmer.maxHealth <= 0
            || farmer.health < 0
            || farmer.health > farmer.maxHealth
        )
        {
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                "passout.two-am.host-player-snapshot-unavailable"
            );
            return;
        }

        var classification = passOutReasons.ClassifyStartToPassOut(farmer);
        if (
            !classification.IsAvailable
            || classification.Reason != PassOutReason.TimeLimitPassOut
        )
        {
            CaptureAndReport(farmer, classification);
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                classification.StableReason
            );
            return;
        }

        var location = SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(
            farmer.currentLocation,
            locationRules
        );
        var junimo = junimoBlessingResolver.Resolve();
        var route = PassOutReasonPolicy.ResolveTimeLimitRoute(
            new PassOutTimeLimitPolicyContext(
                mode.HasValue,
                mode.Mode,
                junimo.HasValue,
                junimo.Enabled,
                location.TwoAmSpecialDeathSafe,
                location.JunimoBlessingEligible,
                location.TwoAmSpecialDeathReason,
                nightOwlPlusCompatibility.AllowsConflictingSpecialDeathChain,
                nightOwlPlusCompatibility.StableReason
            )
        );
        if (route.Action != PassOutPolicyAction.DelegateToTwoAmSpecialDeath)
        {
            CaptureAndReport(farmer, classification);
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                route.StableReason
            );
            return;
        }

        var result = stateMachine.Begin(
            new SanityTwoAmSpecialDeathStartRequest(
                message.SessionId,
                message.CorrelationId,
                message.PlayerKey,
                message.ScreenId,
                SanityAuthorityRole.Host,
                sanitySnapshot.Revision,
                mode.Mode,
                location.TwoAmSpecialDeathSafe,
                location.TwoAmSpecialDeathReason,
                farmer.health,
                farmer.maxHealth
            )
        );

        if (result.Snapshot?.Kind == SanityTwoAmSpecialDeathFlowKind.NonLethalHome)
        {
            CaptureSpecialAndReport(farmer);
            SettleNonLethal(result.Snapshot);
            SendDecision(
                fromPlayerId,
                message,
                runOriginal: true,
                specialActive: false,
                result.Reason
            );
            return;
        }

        var specialActive = result.Snapshot?.Kind
            == SanityTwoAmSpecialDeathFlowKind.DefaultClinic
            && result.Status
                is SanityTwoAmSpecialDeathMutationStatus.Applied
                    or SanityTwoAmSpecialDeathMutationStatus.Duplicate;
        if (specialActive)
            CaptureSpecialAndReport(farmer);
        else
        {
            CaptureAndReport(
                farmer,
                new PassOutReasonClassification(
                    PassOutReasonClassificationStatus.Unavailable,
                    PassOutReason.Unknown,
                    Game1.timeOfDay,
                    "passout.reason.special-death-host-start-rejected"
                )
            );
        }
        SendDecision(
            fromPlayerId,
            message,
            runOriginal: !specialActive,
            specialActive,
            result.Reason
        );
        // The decision establishes the current correlation/nonce receipt before any owner-local
        // presentation arrives. The host remains the only state-machine and settlement owner.
        ApplyActions(result);
    }

    private void HandleHostSnapshotRequest(
        SanityTwoAmSpecialDeathSnapshotRequestMessage? request,
        long fromPlayerId
    )
    {
        var expectedPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(fromPlayerId);
        if (
            request is null
            || request.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !string.Equals(request.SessionId, lifecycle.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
            || request.ScreenId < 0
            || !stateMachine.TryGetSnapshot(expectedPlayerKey, out var snapshot)
            || snapshot.ScreenId != request.ScreenId
        )
        {
            return;
        }
        SendSnapshotToClient(snapshot, fromPlayerId);
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

    private void SendDecisionMessage(
        long playerId,
        SanityTwoAmSpecialDeathDecisionMessage decision
    )
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
        var receipt = clientReceipts.AcceptDecision(
            message,
            pendingClientCorrelationId,
            pendingClientNonce,
            pendingClientAuthorityRevision
        );
        if (receipt.Status != SanityTwoAmSpecialDeathProtocolStatus.Accepted)
            return;

        var acceptedMessage = message!;
        pendingClientCorrelationId = string.Empty;
        pendingClientNonce = 0;
        pendingClientAuthorityRevision = -1;
        clientRunOriginalOnce = acceptedMessage.RunOriginalPassOut;
        clientSpecialFlowActive = acceptedMessage.SpecialFlowActive;
    }

    private void HandleClientAction(SanityTwoAmSpecialDeathActionMessage? message)
    {
        var receipt = clientReceipts.AcceptAction(message);
        if (receipt.Status != SanityTwoAmSpecialDeathProtocolStatus.Accepted)
            return;

        var acceptedMessage = message!;
        ApplyLocalAction(
            new SanityTwoAmSpecialDeathAction(
                acceptedMessage.Kind,
                acceptedMessage.PlayerKey,
                acceptedMessage.ScreenId,
                acceptedMessage.CorrelationId
            ),
            acceptedMessage.Revision,
            acceptedMessage.SessionId
        );
        if (acceptedMessage.Kind == SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay)
            clientSpecialFlowActive = false;
    }

    private void HandleClientSnapshot(SanityTwoAmSpecialDeathSnapshotMessage? message)
    {
        if (
            message is null
            || !EnsureClientReceiptContext(message.PlayerKey)
        )
        {
            return;
        }
        var receipt = clientReceipts.AcceptSnapshot(message);
        if (receipt.Status != SanityTwoAmSpecialDeathProtocolStatus.Accepted)
            return;

        clientSpecialFlowActive = message!.Phase
            is not (
                SanityTwoAmSpecialDeathPhase.Recovered
                or SanityTwoAmSpecialDeathPhase.Completed
            );
        foreach (var kind in SanityTwoAmSpecialDeathClientProjection.ForPhase(message.Phase))
        {
            var action = new SanityTwoAmSpecialDeathActionMessage
            {
                SessionId = message.SessionId,
                CorrelationId = message.CorrelationId,
                PlayerKey = message.PlayerKey,
                ScreenId = message.ScreenId,
                AuthorityRevision = message.AuthorityRevision,
                Revision = message.Revision,
                Kind = kind,
            };
            HandleClientAction(action);
        }
    }

    private bool EnsureClientReceiptContext(string playerKey)
    {
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Client
            || Game1.player is null
            || !string.Equals(
                playerKey,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    Game1.player.UniqueMultiplayerID
                ),
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }
        if (
            clientReceipts.MatchesContext(
                lifecycle.SessionId,
                playerKey,
                Context.ScreenId
            )
        )
        {
            return true;
        }
        return clientReceipts.Reset(
            lifecycle.SessionId,
            playerKey,
            Context.ScreenId
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            !disposed
            && Context.IsWorldReady
            && lifecycle.AuthorityRole == SanityAuthorityRole.Client
        )
        {
            TrySendClientSnapshotRequest();
            return;
        }
        if (
            disposed
            || !Context.IsWorldReady
            || !Context.IsMainPlayer
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
        )
            return;

        var snapshots = stateMachine.SnapshotAll();
        foreach (var snapshot in snapshots)
        {
            if (
                snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic
                || snapshot.Phase
                    is not (
                        SanityTwoAmSpecialDeathPhase.PromptA
                        or SanityTwoAmSpecialDeathPhase.WarningCue
                        or SanityTwoAmSpecialDeathPhase.PromptB
                        or SanityTwoAmSpecialDeathPhase.PromptC
                    )
            )
            {
                continue;
            }

            var currentTick = Math.Max(0L, Game1.ticks);
            if (
                nextPresentationTickByPlayer.TryGetValue(
                    snapshot.PlayerKey,
                    out var nextTick
                )
                && currentTick < nextTick
            )
            {
                continue;
            }
            nextPresentationTickByPlayer[snapshot.PlayerKey] =
                currentTick + PresentationCadenceTicks;
            var mutation = stateMachine.Signal(
                snapshot.PlayerKey,
                snapshot.CorrelationId,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation,
                string.Concat("presentation-", snapshot.Phase.ToString())
            );
            ApplyActions(mutation);
        }
    }

    private void TrySendClientSnapshotRequest()
    {
        if (
            !clientSnapshotRequestPending
            || Game1.player is null
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
        )
        {
            return;
        }
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            Game1.player.UniqueMultiplayerID
        );
        if (!EnsureClientReceiptContext(playerKey))
            return;
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathSnapshotRequestMessage
                {
                    SessionId = lifecycle.SessionId,
                    PlayerKey = playerKey,
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
            LogOnce(
                "passout.two-am.snapshot-request-send-failed",
                $"The 2:00 client snapshot request failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (
            disposed
            || !Context.IsMainPlayer
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
        )
        {
            return;
        }
        foreach (var snapshot in stateMachine.SnapshotAll())
        {
            if (snapshot.Phase != SanityTwoAmSpecialDeathPhase.AwaitingDayEnding)
                continue;
            TryQueueMail(snapshot);
            ApplyActions(
                stateMachine.Signal(
                    snapshot.PlayerKey,
                    snapshot.CorrelationId,
                    SanityTwoAmSpecialDeathSignal.DayEnding,
                    "day-ending"
                )
            );
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (
            disposed
            || !Context.IsMainPlayer
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
        )
        {
            return;
        }
        var beforeSave = stateMachine.SnapshotAll();
        foreach (var snapshot in beforeSave)
        {
            if (snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic)
                continue;
            TryQueueMail(snapshot);
            var signal = snapshot.Phase switch
            {
                SanityTwoAmSpecialDeathPhase.AwaitingSaving =>
                    SanityTwoAmSpecialDeathSignal.Saving,
                SanityTwoAmSpecialDeathPhase.Recovered =>
                    SanityTwoAmSpecialDeathSignal.LaterSaving,
                _ => (SanityTwoAmSpecialDeathSignal?)null,
            };
            if (signal.HasValue)
            {
                ApplyActions(
                    stateMachine.Signal(
                        snapshot.PlayerKey,
                        snapshot.CorrelationId,
                        signal.Value,
                        signal == SanityTwoAmSpecialDeathSignal.Saving
                            ? "saving"
                            : "later-saving"
                    )
                );
            }
        }

        if (!persistenceCanWrite)
            return;
        try
        {
            helper.Data.WriteSaveData(
                SanityTwoAmSpecialDeathContract.SaveKey,
                SanityTwoAmSpecialDeathPersistence.Capture(
                    stateMachine.SnapshotAll()
                )
            );
        }
        catch (Exception exception)
        {
            persistenceCanWrite = false;
            LogOnce(
                "passout.two-am.save-write-failed",
                $"The 2:00 recovery state became read-only after a save write failure ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        ClearSession("passout.two-am.save-loaded-reset");
        persistenceCanWrite = true;
        if (
            lifecycle.AuthorityRole == SanityAuthorityRole.Client
            && Game1.player is not null
        )
        {
            clientSnapshotRequestPending = clientReceipts.Reset(
                lifecycle.SessionId,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    Game1.player.UniqueMultiplayerID
                ),
                Context.ScreenId
            );
        }
        if (lifecycle.AuthorityRole != SanityAuthorityRole.Host)
            return;

        SanityTwoAmSpecialDeathSaveData? data;
        try
        {
            data = helper.Data.ReadSaveData<SanityTwoAmSpecialDeathSaveData>(
                SanityTwoAmSpecialDeathContract.SaveKey
            );
        }
        catch (Exception exception)
        {
            persistenceCanWrite = false;
            LogOnce(
                "passout.two-am.save-read-failed",
                $"The 2:00 recovery state is read-only because save data could not be read ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return;
        }

        var validation = SanityTwoAmSpecialDeathPersistence.Validate(data);
        if (validation.Status == SanityTwoAmSpecialDeathPersistenceStatus.Invalid)
        {
            persistenceCanWrite = false;
            LogOnce(
                validation.Reason,
                $"The 2:00 recovery state is read-only and was not overwritten ({validation.Reason}).",
                LogLevel.Error
            );
            return;
        }
        if (validation.Data is null)
            return;

        foreach (var flow in validation.Data.Flows)
        {
            var imported = stateMachine.ImportRecovery(flow, lifecycle.SessionId);
            if (imported.Status == SanityTwoAmSpecialDeathMutationStatus.Rejected)
            {
                persistenceCanWrite = false;
                LogOnce(
                    imported.Reason,
                    $"The 2:00 recovery state import failed closed ({imported.Reason}).",
                    LogLevel.Error
                );
            }
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        beginningOfficialNewDay = false;
        if (
            disposed
            || !Context.IsMainPlayer
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
        )
        {
            RestoreControls();
            return;
        }
        foreach (var snapshot in stateMachine.SnapshotAll())
        {
            if (snapshot.Phase != SanityTwoAmSpecialDeathPhase.AwaitingRecovery)
                continue;
            if (!snapshot.MailQueued)
                TryDeliverRecoveryMail(snapshot);
            ApplyActions(
                stateMachine.Signal(
                    snapshot.PlayerKey,
                    snapshot.CorrelationId,
                    SanityTwoAmSpecialDeathSignal.DayStarted,
                    "day-started"
                )
            );
        }
        RestoreControls();
    }

    private void TryDeliverRecoveryMail(SanityTwoAmSpecialDeathSnapshot snapshot)
    {
        try
        {
            var farmer = ResolveFarmer(snapshot.PlayerKey);
            if (farmer is null)
                throw new InvalidOperationException("The target farmer is unavailable.");
            if (!farmer.hasOrWillReceiveMail(SanityTwoAmSpecialDeathContract.MailId))
                farmer.mailbox.Add(SanityTwoAmSpecialDeathContract.MailId);
            stateMachine.Signal(
                snapshot.PlayerKey,
                snapshot.CorrelationId,
                SanityTwoAmSpecialDeathSignal.MailQueued,
                "recovery-mail-delivered"
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.recovery-mail-failed",
                $"The 2:00 recovery mail could not be delivered ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (disposed)
            return;

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            e.Player.UniqueMultiplayerID
        );
        if (lifecycle.AuthorityRole == SanityAuthorityRole.Client)
        {
            if (e.IsLocalPlayer && clientSpecialFlowActive)
                ClearClientPresentation("passout.two-am.client-warp-cleared");
            return;
        }
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !Context.IsMainPlayer
            || !stateMachine.TryGetSnapshot(playerKey, out var snapshot)
            || snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic
            || snapshot.Phase
                is SanityTwoAmSpecialDeathPhase.AwaitingRecovery
                    or SanityTwoAmSpecialDeathPhase.Recovered
                    or SanityTwoAmSpecialDeathPhase.Completed
        )
        {
            return;
        }

        passOutReasons.ForgetPlayer(playerKey);
        hostRequestReceipts.ForgetPlayer(playerKey);
        nextPresentationTickByPlayer.Remove(playerKey);
        ApplyActions(
            stateMachine.CancelPlayer(
                playerKey,
                snapshot.CorrelationId,
                "passout.two-am.unexpected-warp-cleared"
            )
        );
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearSession("passout.two-am.returned-to-title");
        persistenceCanWrite = true;
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (stateEvent.Kind == SanityStateEventKind.SystemDisabled)
            ClearSession("passout.two-am.sanity-system-disabled");
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
            return;

        e.Edit(asset =>
        {
            asset.AsDictionary<string, string>().Data[
                SanityTwoAmSpecialDeathContract.MailId
            ] = helper.Translation.Get("passout.two-am.mail.body");
        });
    }

    private void ApplyActions(SanityTwoAmSpecialDeathMutation mutation)
    {
        if (
            mutation.Snapshot is { } remoteSnapshot
            && lifecycle.AuthorityRole == SanityAuthorityRole.Host
            && !IsCurrentLocalPlayer(remoteSnapshot.PlayerKey)
            && long.TryParse(
                remoteSnapshot.PlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var remotePlayerId
            )
        )
        {
            SendSnapshotToClient(remoteSnapshot, remotePlayerId);
        }
        foreach (var action in mutation.Actions)
        {
            if (action.Kind == SanityTwoAmSpecialDeathActionKind.QueueMail)
            {
                if (mutation.Snapshot is { } mailSnapshot)
                    TryQueueMail(mailSnapshot);
                continue;
            }
            if (action.Kind == SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay)
            {
                if (!beginningOfficialNewDay)
                {
                    RestoreControls();
                    beginningOfficialNewDay = true;
                    Game1.PassOutNewDay();
                }
                continue;
            }
            if (action.Kind == SanityTwoAmSpecialDeathActionKind.ReduceToFloor)
                continue;

            var sessionId = mutation.Snapshot?.SessionId ?? lifecycle.SessionId;
            var revision = mutation.Snapshot?.Revision ?? 0;
            if (
                lifecycle.AuthorityRole == SanityAuthorityRole.Host
                && !IsCurrentLocalPlayer(action.PlayerKey)
            )
            {
                SendActionToClient(
                    action,
                    sessionId,
                    mutation.Snapshot?.AuthorityRevision ?? -1,
                    revision
                );
                continue;
            }
            ApplyLocalAction(action, revision, sessionId);
        }
    }

    private void ApplyLocalAction(
        SanityTwoAmSpecialDeathAction action,
        long revision,
        string sessionId
    )
    {
        switch (action.Kind)
        {
            case SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay:
                FreezeControls();
                audio.SetSpecialEventAudioAllowed(true);
                var begin = events.BeginSpecialFlow(
                    action.PlayerKey,
                    action.ScreenId,
                    sessionId,
                    SanityTwoAmSpecialDeathContract.SpecialEventId
                );
                if (begin.Status == SanityEffectiveOverlayMutationStatus.Rejected)
                {
                    LogOnce(
                        begin.Reason,
                        $"The 2:00 effective overlay failed closed ({begin.Reason}).",
                        LogLevel.Error
                    );
                }
                break;
            case SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay:
                events.EndSpecialFlow(
                    action.PlayerKey,
                    action.ScreenId,
                    sessionId,
                    SanityTwoAmSpecialDeathContract.SpecialEventId
                );
                audio.SetSpecialEventAudioAllowed(false);
                RestoreControls();
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptA:
                ShowTranslation("passout.two-am.prompt-a");
                break;
            case SanityTwoAmSpecialDeathActionKind.PlayWarningCue:
                audio.SubmitDarknessWarningClaim(
                    new SanityDarknessWarningClaim(
                        action.PlayerKey,
                        action.ScreenId,
                        sessionId,
                        action.CorrelationId,
                        // This terminal day claim must supersede any earlier darkness countdown
                        // on the same owner/screen. DayStarted clears the receipt before a new
                        // ordinary countdown can exist.
                        long.MaxValue
                    )
                );
                break;
            case SanityTwoAmSpecialDeathActionKind.StopWarningCue:
                audio.RemoveDarknessWarningClaim(
                    action.PlayerKey,
                    action.ScreenId,
                    sessionId,
                    action.CorrelationId
                );
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptB:
                ShowTranslation("passout.two-am.prompt-b");
                break;
            case SanityTwoAmSpecialDeathActionKind.ShowPromptC:
                ShowTranslation("passout.two-am.prompt-c");
                break;
            case SanityTwoAmSpecialDeathActionKind.ReportMissingDeathCue:
                LogOnce(
                    SanityTwoAmSpecialDeathContract.MissingDeathCueReason,
                    "The 2:00 death cue is an explicit missing placeholder; the save/recovery flow continues without inventing a cue.",
                    LogLevel.Warn
                );
                break;
            case SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic:
                Game1.warpFarmer("Hospital", 20, 12, false);
                ShowTranslation("passout.two-am.clinic-recovery");
                break;
        }
    }

    private void SendActionToClient(
        SanityTwoAmSpecialDeathAction action,
        string sessionId,
        long authorityRevision,
        long revision
    )
    {
        if (
            !long.TryParse(
                action.PlayerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var playerId
            )
        )
        {
            return;
        }
        try
        {
            helper.Multiplayer.SendMessage(
                new SanityTwoAmSpecialDeathActionMessage
                {
                    SessionId = sessionId,
                    CorrelationId = action.CorrelationId,
                    PlayerKey = action.PlayerKey,
                    ScreenId = action.ScreenId,
                    AuthorityRevision = authorityRevision,
                    Revision = revision,
                    Kind = action.Kind,
                },
                SanityTwoAmSpecialDeathContract.ActionMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { playerId }
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.action-send-failed",
                $"The 2:00 client action message failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void SendSnapshotToClient(
        SanityTwoAmSpecialDeathSnapshot snapshot,
        long playerId
    )
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
                    MailQueued = snapshot.MailQueued,
                    Reason = snapshot.Reason,
                },
                SanityTwoAmSpecialDeathContract.SnapshotMessageType,
                modIDs: new[] { modId },
                playerIDs: new[] { playerId }
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.snapshot-send-failed",
                $"The 2:00 client snapshot failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void TryQueueMail(SanityTwoAmSpecialDeathSnapshot snapshot)
    {
        if (snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic)
            return;
        if (snapshot.MailQueued)
            return;
        try
        {
            var farmer = ResolveFarmer(snapshot.PlayerKey);
            if (farmer is null)
                throw new InvalidOperationException("The target farmer is unavailable.");
            if (ReferenceEquals(farmer, Game1.player))
            {
                if (!farmer.hasOrWillReceiveMail(SanityTwoAmSpecialDeathContract.MailId))
                {
                    Game1.addMailForTomorrow(
                        SanityTwoAmSpecialDeathContract.MailId,
                        noLetter: false,
                        sendToEveryone: false
                    );
                }
            }
            else if (!farmer.hasOrWillReceiveMail(SanityTwoAmSpecialDeathContract.MailId))
            {
                farmer.mailForTomorrow.Add(SanityTwoAmSpecialDeathContract.MailId);
            }
            stateMachine.Signal(
                snapshot.PlayerKey,
                snapshot.CorrelationId,
                SanityTwoAmSpecialDeathSignal.MailQueued,
                "mail-queued"
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.mail-queue-failed",
                $"The 2:00 mail queue failed and will be retried at the next lifecycle edge ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private static bool IsCurrentLocalPlayer(string playerKey)
    {
        return Game1.player is not null
            && string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(
                    Game1.player.UniqueMultiplayerID
                ),
                playerKey,
                StringComparison.Ordinal
            );
    }

    private static Farmer? ResolveFarmer(string playerKey)
    {
        if (IsCurrentLocalPlayer(playerKey))
            return Game1.player;
        return long.TryParse(
            playerKey,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var playerId
        )
            ? Game1.GetPlayer(playerId, true)
            : null;
    }

    private void FreezeControls()
    {
        if (controlsFrozen || Game1.player is null)
            return;
        Game1.player.CanMove = false;
        controlsFrozen = true;
    }

    private void RestoreControls()
    {
        if (!controlsFrozen)
            return;
        if (Context.IsWorldReady && Game1.player is not null)
            Game1.player.CanMove = true;
        controlsFrozen = false;
    }

    private void ClearSession(string reason)
    {
        foreach (var snapshot in stateMachine.SnapshotAll())
        {
            audio.RemoveDarknessWarningClaim(
                snapshot.PlayerKey,
                snapshot.ScreenId,
                snapshot.SessionId,
                snapshot.CorrelationId
            );
            if (snapshot.Kind == SanityTwoAmSpecialDeathFlowKind.DefaultClinic)
            {
                events.EndSpecialFlow(
                    snapshot.PlayerKey,
                    snapshot.ScreenId,
                    snapshot.SessionId,
                    SanityTwoAmSpecialDeathContract.SpecialEventId
                );
            }
        }
        audio.SetSpecialEventAudioAllowed(false);
        RestoreControls();
        stateMachine.ClearSession();
        nextPresentationTickByPlayer.Clear();
        hostRequestReceipts.Clear();
        clientReceipts.Clear();
        passOutReasons.ClearSession();
        pendingClientCorrelationId = string.Empty;
        pendingClientNonce = 0;
        pendingClientAuthorityRevision = -1;
        nextClientNonce = 1;
        pendingClientDeadlineTick = 0;
        clientSnapshotRequestPending = false;
        clientRunOriginalOnce = false;
        clientSpecialFlowActive = false;
        beginningOfficialNewDay = false;
        loggedReasons.Clear();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            LogOnce(
                reason,
                $"The 2:00 session state was cleared ({reason}).",
                LogLevel.Debug
            );
        }
    }

    private void ClearClientPresentation(string reason)
    {
        var currentCorrelationId = clientReceipts.CorrelationId;
        if (Game1.player is not null)
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                Game1.player.UniqueMultiplayerID
            );
            if (!string.IsNullOrEmpty(currentCorrelationId))
            {
                audio.RemoveDarknessWarningClaim(
                    playerKey,
                    Context.ScreenId,
                    lifecycle.SessionId,
                    currentCorrelationId
                );
                clientReceipts.Retire(currentCorrelationId);
            }
            events.EndSpecialFlow(
                playerKey,
                Context.ScreenId,
                lifecycle.SessionId,
                SanityTwoAmSpecialDeathContract.SpecialEventId
            );
        }
        audio.SetSpecialEventAudioAllowed(false);
        RestoreControls();
        pendingClientCorrelationId = string.Empty;
        pendingClientNonce = 0;
        pendingClientAuthorityRevision = -1;
        pendingClientDeadlineTick = 0;
        clientRunOriginalOnce = false;
        clientSpecialFlowActive = false;
        LogOnce(
            reason,
            $"The owner-local 2:00 presentation was cleared ({reason}).",
            LogLevel.Debug
        );
    }

    private void ShowTranslation(string key)
    {
        Game1.showGlobalMessage(helper.Translation.Get(key));
    }

    private static string CreateCorrelation(
        string sessionId,
        string playerKey,
        int totalDays
    )
    {
        var material = string.Concat(
            sessionId,
            "\n",
            playerKey,
            "\n",
            totalDays.ToString(CultureInfo.InvariantCulture),
            "\nSanityDarknessSpecialDeath"
        );
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var bytes = new byte[16];
        Buffer.BlockCopy(hash, 0, bytes, 0, bytes.Length);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes).ToString("N");
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // SMAPI has already closed its monitor/console writers by ProcessExit. Normal title,
        // day, save, and Disabled boundaries clear session state on the game thread; process
        // teardown must not re-enter those services or emit a final diagnostic.
    }

    private void LogOnce(string reason, string message, LogLevel level)
    {
        if (
            loggedReasons.Count >= MaximumLoggedReasons
            || !loggedReasons.Add(reason)
        )
        {
            return;
        }
        monitor.Log(message, level);
    }
}
