#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Audio.StatusFeedback;
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
    // 清晨 killScreen 置位后等待原版 PlayerKilled 事件出现的时间上限（约 3 秒）。事件由
    // checkForEvents 直调同步建立，正常路径同帧完成；超时只覆盖其它 mod 干扰等异常情形。
    private const int PendingDarknessDeathTimeoutTicks = 180;
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
    private readonly SmapiSanityTwoAmDarknessWaitService darknessWait;
    private readonly PlayerDeathCauseLedger deathCauses;
    private readonly SpecialPassOutSanitySettlement specialSanitySettlement;
    private readonly Func<Farmer, bool> registerPendingDarknessSettlement;
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
    // 清晨 pending-darkness 死亡序列的 fail-open 截止 tick（B3：killScreen 置位后事件迟迟未建
    // 就清 killScreen 放人，绝不静默卡死）；-1 表示没有在途序列。
    private long pendingDarknessDeathDeadlineTick = -1;
    // 修复2（2026-11-14）血 0 边界的原版接管观察：原版清晨 0 血死亡块触发后要经过
    // pauseTime(3000ms)→医院 warp→resetForPlayerEntry→checkForEvents 才建立 PlayerKilled
    // 事件，比 mod 直调链路慢一个数量级，观察窗必须盖住整条原版链。超时只报失败保留标记，
    // 绝不代管 killScreen（原版路径的 killScreen 不归 mod 清）。
    private const int VanillaDeathWatchTimeoutTicks = 900;
    private long vanillaDeathWatchDeadlineTick = -1;
    private bool vanillaDeathWatchSawKillScreen;
    // 修复1（2026-11-14）：同一在册标记实例的手工死亡计数只做一次。fail-open 有界重试会
    // 重新走 killScreen→checkForEvents，若每次都 TimesUnconscious++ 会把一次死亡计成多次；
    // 会话内以玩家键去重，新标记登记（夜间死亡路径）与会话清理时重置。
    private readonly HashSet<string> wakeDeathCountedPlayers = new(StringComparer.Ordinal);
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
        SmapiSanityTwoAmDarknessWaitService darknessWait,
        PlayerDeathCauseLedger deathCauses,
        SpecialPassOutSanitySettlement specialSanitySettlement,
        Func<Farmer, bool> registerPendingDarknessSettlement
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
        this.darknessWait = darknessWait ?? throw new ArgumentNullException(nameof(darknessWait));
        this.deathCauses = deathCauses ?? throw new ArgumentNullException(nameof(deathCauses));
        this.specialSanitySettlement = specialSanitySettlement ?? throw new ArgumentNullException(nameof(specialSanitySettlement));
        this.registerPendingDarknessSettlement = registerPendingDarknessSettlement ?? throw new ArgumentNullException(nameof(registerPendingDarknessSettlement));

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

    /// <summary>本机是否正在播放自己的 owner-local 黑暗演出（等待服务用它豁免本机冻结）。</summary>
    internal bool IsLocalPresentationActive => localPresentationEvent is not null;

    /// <summary>
    /// 清晨 pending-darkness 死亡序列的结果上报（修复1，2026-11-14）。Completed：PlayerKilled
    /// 事件建成或原版接管观察完成——睡眠适配器收到后把玩家移出 pendingDarknessDeaths，下次
    /// 存档把"已完成"覆盖到磁盘、读档不再重演。Failed：任一 fail-open——适配器保留标记，
    /// 下次存档仍落盘、读档必重演，防逃课标记绝不因 fail-open 丢失。
    /// </summary>
    internal event Action<Farmer>? PendingDarknessWakeCompleted;
    internal event Action<Farmer>? PendingDarknessWakeFailed;

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

        AdvancePendingDarknessWakeDeadline();
        AdvanceVanillaNativeDeathWatch();

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
            message.Kind is SanityTwoAmSpecialDeathActionKind.BeginPendingDarknessDeath
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
                if (action.Kind is SanityTwoAmSpecialDeathActionKind.BeginPendingDarknessDeath or SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut)
                    PrepareRemoteOutcome(snapshot, action.Kind);
                if (SanityPlayerKey.TryParseCanonicalPlayerId(action.PlayerKey, out var remotePlayerId))
                    SendActionToClient(action, snapshot, remotePlayerId);
                continue;
            }
            ApplyLocalAction(action, snapshot);
        }

        // 修复4：流开始/推进/结束都会改变主机侧活跃流数量。同步点放在 ApplyActions 末尾，
        // 保证等待广播与状态机迁移原子对齐（取消/失败/异常 warp 也都经 ApplyActions 走到这里）。
        darknessWait.SetHostActiveFlowCount(stateMachine.Count);
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
            // 以下六个动作是刻意空操作（协议保留三拍结构）：提示文本由 owner-local 生成的
            // SanityTwoAmDarknessEvent 用构造时传入的三条译文自行弹窗，音效由
            // OnLocalPresentationCommand 在事件命令回调里 owner-local 播放；
            // 这里若再 drawDialogueNoTyping/playSound 会在事件镜头上叠出第二层对话框或串玩家。
            case SanityTwoAmSpecialDeathActionKind.ShowPromptEntered:
            case SanityTwoAmSpecialDeathActionKind.PlayWarningCue:
            case SanityTwoAmSpecialDeathActionKind.ShowPromptWarning:
            case SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack:
            case SanityTwoAmSpecialDeathActionKind.PlayDeathSound:
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
            case SanityTwoAmSpecialDeathActionKind.BeginPendingDarknessDeath:
                if (snapshot is not null && Game1.player is Farmer deathFarmer)
                    BeginPendingDarknessDeath(deathFarmer);
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
            PlayOwnerLocalCue("ow");
            // 黑暗袭击长音与 ow 同拍正常播放（2026-11-14 恢复：用户实机确认 ow 在长音混音下
            // 可听，20261113 的临时静音措施移除）。仅本调用点属于特殊事件三拍演出；实机黑暗
            // 袭击路径（SmapiDarknessAttackService 等正常调用方）行为不受影响。
            audio.TriggerDarknessAttack();
            if (snapshot.Kind == SanityTwoAmSpecialDeathFlowKind.DefaultDeath)
                PlayOwnerLocalCue("death");
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

    /// <summary>
    /// owner-local 一次性原版音效（C.1 决议）：PlayLocal 只在本机 sound bank 直接出声，
    /// 绝不走 location.netAudio.Fire——playNearbySoundAll 的 PlayAll 分支在联机时会把音效
    /// 同步给全部 peer，分屏同地点时也会同步到另一屏，两者都违背"只有该玩家听得见"。
    /// location/position 传 null 使 GetVolumeForDistance 恒为 1：事件镜头被 InitializeEvent
    /// 冻结在屏外约 1000 格，带位置参数会被距离门压成 0 音量（上一轮 ow/death 无声真因）。
    /// 已知边界：分屏共享物理扬声器，与警告音同口径，本轮不声明分屏隔离。
    /// </summary>
    private void PlayOwnerLocalCue(string cueName)
    {
        try
        {
            Game1.sounds.PlayLocal(cueName, null, null, null, StardewValley.Audio.SoundContext.Default, out _);
        }
        catch (Exception exception)
        {
            LogOnce(
                $"passout.two-am.owner-local-cue-failed:{cueName}",
                $"The owner-local cue '{cueName}' failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
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

    /// <summary>
    /// Default 黑暗死亡的夜里收尾（医院直醒方案，2026-11-13 重写）：
    /// 不回家、不 warp 床，直接走无费用原地换日桥；PassOutNewDay 返回后同线程把
    /// lastSleepLocation/lastSleepPoint 覆写为原版复活目标（updatePause killScreen warp 的
    /// 同款 ReviveLocations/GSQ 解析，兜底 Hospital (20,12)），并置 sleptInTemporaryBed=true
    /// 让次日 BedFurniture.ApplyWakeUpPosition 的 CanWakeUpHere 走"临时床"分支精确落在复活
    /// tile（Hospital 地图不带 AllowWakeUpWithoutBed，复活 tile 也无床，这是唯一可行的
    /// 原版字段路径）。死亡标记由睡眠适配器持久化，读档必重演。
    /// </summary>
    private void BeginPendingDarknessDeath(Farmer farmer)
    {
        if (!registerPendingDarknessSettlement(farmer))
        {
            LogOnce(
                "passout.two-am.pending-darkness-settlement-registration-failed",
                "The pending-darkness overnight settlement could not be registered; the death marker stays armed and the wake death still replays.",
                LogLevel.Error
            );
        }
        // 新标记=新死亡实例：手工死亡计数预算随登记重置（修复1），上一次失败尝试的
        // 计数残留不得吞掉本次死亡的原版语义计数。
        wakeDeathCountedPlayers.Remove(PlayerKey(farmer));

        SanityTwoAmVanillaPassOutBridge.BeginNoFeeNewDayInPlace(farmer);
        var revive = ResolveDarknessReviveTarget(farmer);
        farmer.lastSleepLocation.Value = revive.Location;
        // ReviveTarget 是纯规则层类型（测试工程源码链接编译），Xna 类型在此适配层就地构造。
        farmer.lastSleepPoint.Value = new Microsoft.Xna.Framework.Point(revive.TileX, revive.TileY);
        farmer.sleptInTemporaryBed.Value = true;
    }

    /// <summary>
    /// 原版 updatePause killScreen warp 分支的同款复活目标解析：当前地点上下文的
    /// ReviveLocations（GSQ 门控，location 参数传 null 与原版一致）优先，否则落到原版
    /// 兜底复活 tile Hospital (20,12)。Data/LocationContexts.xnb 已解包核实：Default 上下文
    /// 的唯一无条件条目就是 Hospital (20,12)，因此常规路径与兜底 tile 一致。
    /// </summary>
    private static DarknessRevivePolicy.ReviveTarget ResolveDarknessReviveTarget(Farmer farmer)
    {
        var reviveLocations = farmer.currentLocation?.GetLocationContext().ReviveLocations
            ?? StardewValley.LocationContexts.Default.ReviveLocations;
        IEnumerable<DarknessRevivePolicy.ReviveCandidate>? candidates = null;
        if (reviveLocations is not null)
        {
            candidates = reviveLocations.Select(
                entry => new DarknessRevivePolicy.ReviveCandidate(
                    entry.Condition,
                    entry.Location,
                    entry.Position.X,
                    entry.Position.Y
                )
            );
        }
        return DarknessRevivePolicy.ResolveCore(
            candidates,
            condition => GameStateQuery.CheckConditions(condition, null, farmer)
        );
    }

    /// <summary>
    /// 次日"医院直醒"死亡序列（由睡眠适配器触发，读档重演走同一路径；血 0 边界由适配器
    /// 委托模式提前送入本拍点）。入口先分两条：
    /// <br/>① 血 0 边界（修复2，恰一次）：夜间结算血量恰为 0 时，原版清晨 0 血死亡块
    /// （Farmer.Update 的 CanMove+health&lt;=0+!killScreen+timeOfDay&lt;2600）已经或即将原生
    /// 触发本次死亡（killScreen→TimesUnconscious++→pauseTime 医院 warp→PlayerKilled 事件），
    /// mod 叠加自有序列会双扣费/双掉物/双计数。这里只同日 MarkDarkness 供台词 seam（ledger
    /// 按天键控，重复 Mark 幂等），不置 killScreen、不自增计数，并挂原版剧情观察。
    /// <br/>② 血量&gt;0 的 mod 自有序列：清晨同日 MarkDarkness（夜里 mark 会被 DayStarted
    /// 清掉，无法满足台词 seam 的同日硬要求）→ TimesUnconscious++（原版 0 血 killScreen 触发
    /// 时的手工镜像，同一在册标记只计一次，fail-open 重试不重复自增）→ 置 killScreen=true
    /// （pauseTime 保持 0，updatePause 的 :4282 pauseTime 门不触发，不产生多余 warp/fade）→
    /// 直调 checkForEvents 原地建立原版 PlayerKilled 事件（原版只在 resetForPlayerEntry 里调
    /// 它，清晨直醒不经过新的进入点）。事件建立后 vanilla 清 killScreen、扣费/掉物/10 血/
    /// 哈维黑暗台词，ledger 由既有 seam 消费。
    /// 四条 fail-open 出口（守卫早退/checkForEvents 抛异常/事件未建/超时）均上报 Failed，
    /// 适配器保留标记；成功上报 Completed，适配器从落盘排除（修复1）。
    /// </summary>
    internal void BeginPendingDarknessWakeDeath(Farmer farmer)
    {
        if (disposed)
            return;
        if (
            farmer is null
            || !farmer.IsLocalPlayer
            || !ReferenceEquals(farmer, Game1.player)
            || Game1.currentLocation is null
        )
        {
            // 防逃课可见性（B4）：此守卫在稳定门后正常不可达，一旦触发必须留 Error 供排查。
            // 修复1：守卫早退属于 fail-open，上报 Failed 让适配器保留标记（下次存档仍落盘、
            // 读档必重演），不再出现"死亡未完成却把标记从磁盘清掉"的逃课窗口。
            LogOnce(
                "passout.two-am.pending-darkness-wake-guard-bail",
                $"The pending-darkness wake death was skipped by its guard (farmer={farmer?.UniqueMultiplayerID}, isLocal={farmer?.IsLocalPlayer}, location={Game1.currentLocation?.NameOrUniqueName ?? "<null>"}); the marker is kept and the death will replay.",
                LogLevel.Error
            );
            ReportPendingDarknessWakeOutcome(farmer, completed: false);
            return;
        }

        // 修复3：先丢弃夜里演出遗留的在途 warp（详见 DiscardStaleNightPresentationWarp），
        // 保证"人已在复活地点、无在途 warp"之后再起死亡事件；事件结束的 eventFinished 才会
        // 按原版语义生成同地点的收尾 LocationRequest，玩家稳在医院复活 tile。
        DiscardStaleNightPresentationWarp();

        // 修复2（血 0 边界，恰一次）：委托原版死亡块。MarkDarkness 的时序保证——本拍点由
        // 适配器的 UpdateTicked 触发，SMAPI 在同一 tick 内先于 UpdateTicked 抛 DayStarted
        // （SCore.cs 预更新段），台词 seam 的 DayStarted ledger 清理已跑完，同日 mark 不会被
        // 二次清掉；原版 checkForEvents 在死亡块触发约 3 秒（pauseTime）后才消费 ledger。
        if (farmer.health <= 0)
        {
            deathCauses.MarkDarkness(farmer);
            vanillaDeathWatchDeadlineTick = Game1.ticks + VanillaDeathWatchTimeoutTicks;
            vanillaDeathWatchSawKillScreen = false;
            return;
        }

        if (wakeDeathCountedPlayers.Add(PlayerKey(farmer)))
        {
            deathCauses.MarkDarkness(farmer);
            Game1.stats.TimesUnconscious++;
        }
        // 复活重置静音（2026-11-14，用户实机反馈"复活时血量反馈音效播一次"）：本分支（血量>0
        // 的 mod 直置 killScreen 重演）里玩家从未进入 ≤0 死亡态，健康差分 watcher 的"持续死亡态
        // 复活重置"规则（2026-11-11）前提不成立；读档重演旧档时血量基线可能是 100，原版
        // checkForEvents killScreen 块（1.6.15 GameLocation.cs:15614，事件建立与否都会执行）平写
        // health=10 会被 watcher 当离散下降播音。按既有 overtime 抑制口径（与
        // ApplySpecialPassOutWakeHealthAfterDayUpdate 同一 seam）在原版写值前发放抑制令牌：
        // 血量已是 10 则不发令牌也无差分（幂等重演不受影响），血量≠10 则令牌恰好吞掉这次
        // 系统重置差分，其后真实伤害照常播音。血 0 委托分支（上方早退）走原版死亡屏，
        // 仍由持续死亡态规则静音，不需要令牌；理智（HealthDeath）/饥饿（Revive）的复活重设
        // 走既有 Continuous 分类，天然静音，均不在此重复处理。
        StatusFeedbackHealthSuppression.SuppressNextHealthSound(
            farmer.UniqueMultiplayerID,
            farmer.health,
            DontStarve.Player.Sleep.PendingDarknessWakePolicy.VanillaReviveHealth
        );
        Game1.killScreen = true;
        pendingDarknessDeathDeadlineTick = Game1.ticks + PendingDarknessDeathTimeoutTicks;
        try
        {
            Game1.currentLocation.checkForEvents();
        }
        catch (Exception exception)
        {
            LogOnce(
                "passout.two-am.pending-darkness-wake-event-threw",
                $"The pending-darkness wake death event could not start ({exception.GetType().Name}: {exception.Message}); the fail-open clears the kill screen.",
                LogLevel.Error
            );
            FailOpenPendingDarknessWakeDeath();
            ReportPendingDarknessWakeOutcome(farmer, completed: false);
            return;
        }

        pendingDarknessDeathDeadlineTick = -1;
        if (!Game1.eventUp)
        {
            // 原版 checkForEvents 在未建事件时也会清 killScreen 并补 health=10；这里补
            // Error 日志保证不静默，并上报 Failed 保留标记（修复1），玩家在复活地点自由
            // 行动、读档后重演。
            LogOnce(
                "passout.two-am.pending-darkness-wake-event-missing",
                "The vanilla PlayerKilled event was not created at the wake location; the kill screen was consumed fail-open.",
                LogLevel.Error
            );
            ReportPendingDarknessWakeOutcome(farmer, completed: false);
            return;
        }
        ReportPendingDarknessWakeOutcome(farmer, completed: true);
    }

    private void FailOpenPendingDarknessWakeDeath()
    {
        Game1.killScreen = false;
        pendingDarknessDeathDeadlineTick = -1;
    }

    /// <summary>清晨死亡序列的 fail-open 超时：killScreen 置位后事件迟迟未建则清掉放人。
    /// 修复1：超时与"killScreen 被外部清掉且事件未建"都是 fail-open，上报 Failed 保留标记；
    /// 事件异步建成则上报 Completed（同帧直调路径已在方法内上报，此处幂等）。</summary>
    private void AdvancePendingDarknessWakeDeadline()
    {
        if (pendingDarknessDeathDeadlineTick < 0)
            return;
        if (Game1.player is not Farmer farmer)
        {
            pendingDarknessDeathDeadlineTick = -1;
            return;
        }
        if (Game1.eventUp)
        {
            // 事件在直调检查之后才异步建成：死亡确认完成，上报 Completed 清标记。
            pendingDarknessDeathDeadlineTick = -1;
            ReportPendingDarknessWakeOutcome(farmer, completed: true);
            return;
        }
        if (!Game1.killScreen)
        {
            // killScreen 在途被外部清掉且事件未建：死亡未完成，按 fail-open 处理。
            pendingDarknessDeathDeadlineTick = -1;
            LogOnce(
                "passout.two-am.pending-darkness-wake-kill-screen-cleared",
                "The pending-darkness wake kill screen was cleared externally before the PlayerKilled event appeared; the marker is kept.",
                LogLevel.Error
            );
            ReportPendingDarknessWakeOutcome(farmer, completed: false);
            return;
        }
        if (Game1.ticks <= pendingDarknessDeathDeadlineTick)
            return;

        LogOnce(
            "passout.two-am.pending-darkness-wake-event-timeout",
            "The pending-darkness wake death event never appeared; the fail-open clears the kill screen so the farmer is not trapped.",
            LogLevel.Error
        );
        FailOpenPendingDarknessWakeDeath();
        ReportPendingDarknessWakeOutcome(farmer, completed: false);
    }

    /// <summary>修复2 血 0 边界的原版接管观察：原版死亡块触发（killScreen 置位）→医院
    /// warp→checkForEvents 建立 PlayerKilled 事件（eventUp=true）或原版 killScreen 链走完
    /// （checkForEvents 清 killScreen 并补 10 血，短事件可能瞬间结束）→死亡确认完成，上报
    /// Completed 让适配器清标记——稳定门事后看到的是"标记已不在册"，不会再补刀。原版死亡
    /// 块始终未触发（如外部持续冻结 CanMove）或被其它 mod 提前抬血时观察超时，只报 Failed
    /// 保留标记，有界重试，绝不代管 killScreen、绝不清原版状态。</summary>
    private void AdvanceVanillaNativeDeathWatch()
    {
        if (vanillaDeathWatchDeadlineTick < 0)
            return;
        if (Game1.player is not Farmer farmer)
        {
            ResetVanillaNativeDeathWatch();
            return;
        }
        if (Game1.killScreen)
            vanillaDeathWatchSawKillScreen = true;
        if (Game1.eventUp)
        {
            ResetVanillaNativeDeathWatch();
            ReportPendingDarknessWakeOutcome(farmer, completed: true);
            return;
        }
        if (vanillaDeathWatchSawKillScreen && !Game1.killScreen && farmer.health > 0)
        {
            ResetVanillaNativeDeathWatch();
            ReportPendingDarknessWakeOutcome(farmer, completed: true);
            return;
        }
        if (Game1.ticks <= vanillaDeathWatchDeadlineTick)
            return;

        ResetVanillaNativeDeathWatch();
        LogOnce(
            "passout.two-am.pending-darkness-vanilla-watch-timeout",
            "The vanilla morning death block never resolved the pending darkness death; the marker is kept and the death will replay.",
            LogLevel.Error
        );
        ReportPendingDarknessWakeOutcome(farmer, completed: false);
    }

    private void ResetVanillaNativeDeathWatch()
    {
        vanillaDeathWatchDeadlineTick = -1;
        vanillaDeathWatchSawKillScreen = false;
    }

    private void ReportPendingDarknessWakeOutcome(Farmer? farmer, bool completed)
    {
        if (farmer is null)
            return;
        if (completed)
            PendingDarknessWakeCompleted?.Invoke(farmer);
        else
            PendingDarknessWakeFailed?.Invoke(farmer);
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
        // 血量上限随同一条结算登记携带：夜里写入的 health 会被 vanilla dayupdate 满血回填，
        // 睡眠记账在 dayupdate 之后的保存前最终值缝按 WakeHealthCap 写回（见
        // SpecialPassOutSanitySettlement.TryResolveHealth）。
        if (
            !specialSanitySettlement.RegisterNonLethal(
                lifecycle.SessionId,
                PlayerKey(farmer),
                Math.Max(0, Game1.Date.TotalDays),
                health,
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
            var wakeHealth = SanityTwoAmVanillaPassOutPolicy.ResolveNonLethalWakeHealth(
                snapshot.CurrentHealth,
                snapshot.MaximumHealth
            );
            farmer.health = wakeHealth;
            // 远端农场工同样把血量上限随结算登记携带（主机权威），供 dayupdate 后的
            // 保存前最终值缝写回（SaveFarmhand prefix / OnSaving）。
            _ = specialSanitySettlement.RegisterNonLethal(
                lifecycle.SessionId,
                snapshot.PlayerKey,
                Math.Max(0, Game1.Date.TotalDays),
                wakeHealth,
                out _
            );
        }
        else
        {
            registerPendingDarknessSettlement(farmer);
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
            // 修复4：中途加入的对端补发当前等待状态，避免新对端错过已开始的等待广播。
            darknessWait.SendWaitStateTo(e.Peer.PlayerID);
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
            // 修复4：断线玩家的流永远等不到 outcome 上报，不取消会让全体等待端被卡死；
            // 取消后走 ApplyActions → SetHostActiveFlowCount，流归零时自动广播放行。
            if (stateMachine.TryGetSnapshot(key, out var snapshot) && snapshot.Phase != SanityTwoAmSpecialDeathPhase.Completed)
                ApplyActions(stateMachine.CancelPlayer(key, snapshot.CorrelationId, "passout.two-am.peer-disconnected"));
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
        // 修复4：会话清理必须无条件解除本机等待并广播放行，保证读档/回标题/停用/断线
        // 不会把任何一端卡在黑屏冻结里。
        darknessWait.ClearSession();
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
        pendingDarknessDeathDeadlineTick = -1;
        ResetVanillaNativeDeathWatch();
        wakeDeathCountedPlayers.Clear();
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

    // Game1._isWarping 是 protected static（1.6.15 Game1.cs:409），只能反射清位；
    // Game1.locationRequest 是 public static，可直接置空。
    private static readonly System.Reflection.FieldInfo? isWarpingField =
        AccessTools.Field(typeof(Game1), "_isWarping");

    /// <summary>
    /// 修复3（2026-11-14）：丢弃夜里特殊演出 eventFinished 遗留的在途 warp 请求。
    /// 原版机制（1.6.15 反编译取证）：三拍演出事件经 startEvent 自动获得
    /// exitLocation=当前地点（GameLocation.cs:15727-15729），事件结束走 Game1.eventFinished →
    /// warpFarmer(exitLocation, positionBeforeEvent) 的"回到事件前位置"收尾 warp
    /// （Game1.cs:7252-7261）；但原地换日桥在同一回调链里立即接管换日，Game1.NewDay 置
    /// nonWarpFade=true（Game1.cs:10372），该 warp 的 fade 被换日掠过，请求残留挂在
    /// Game1.locationRequest/_isWarping 上。次日清晨 ApplyWakeUpPosition 用直接赋值把玩家
    /// 放进医院（BedFurniture.cs:186-189，无 warpFarmer、不消费这条请求），单机新一天收尾的
    /// PollForEndOfNewDaySync 也只做 resetForPlayerEntry、不清请求。若不在死亡事件前丢弃，
    /// 事件结束的 exitEvent 把 nonWarpFade 置回 false 后，下一次 fade 完成会把它落地
    /// （Game1.onFadeToBlackComplete → player.currentLocation = locationRequest.Location，
    /// Game1.cs:6315），玩家被传送回昨晚昏迷地点（实机 2026-11-14 复现，SMAPI-latest 05:50:42
    /// trigger=warp location=Town 且无 Warping TRACE）。
    /// 清晨直醒序列的契约是"人已在复活地点、无在途 warp"，这里显式丢弃陈旧请求；血 0 委托
    /// 路径随后的原版医院 warp 会发起自己的 warpFarmer，不受本清空影响。
    /// <br/>修复4（2026-11-14，实机"诊所门全失灵"）：在丢弃陈旧请求之后，同拍对齐
    /// nonWarpFade/fade 四件套（门失灵根因推演见更新记录；vanilla 死亡链的 killScreen warp
    /// 由 updatePause 发起，发起前 nonWarpFade 恒为 false、fade 周期刚被完整消费；本 mod 的
    /// 直置 killScreen 序列绕过该通道，若夜里 NewDay 置 true 的 nonWarpFade 未被 AfterNewDay
    /// / fade 完成 #2 清理（该清理只有一帧窗口，且被 end-of-night 段 UpdateOther 停更、外部
    /// mod 冻结 messagePause 等情形跳过），exitEvent→eventFinished→同地点收尾 warp 的落地
    /// fade 会被 Game1.cs:6181 的 `!nonWarpFade` 门跳过，传送状态机滞留。此处按 vanilla
    /// updatePause-warp 前的状态口径无条件归零（eventUp 期间绝不触碰，防打断在播事件）；
    /// 幂等，正常清晨全为 no-op。
    /// </summary>
    private void DiscardStaleNightPresentationWarp()
    {
        if (Game1.locationRequest is not null)
        {
            Game1.locationRequest = null;
            try
            {
                if (isWarpingField is null)
                {
                    LogOnce(
                        "passout.two-am.is-warping-field-unavailable",
                        "Game1._isWarping was not found; the stale warp request was dropped but the warping flag may linger until the next warp.",
                        LogLevel.Warn
                    );
                }
                else
                {
                    isWarpingField.SetValue(null, false);
                }
            }
            catch (Exception exception)
            {
                LogOnce(
                    "passout.two-am.stale-warp-clear-failed",
                    $"The stale warp flag could not be cleared ({exception.GetType().Name}: {exception.Message}); the stale warp request itself was already dropped.",
                    LogLevel.Warn
                );
            }
        }
        if (Game1.eventUp)
        {
            return;
        }
        Game1.nonWarpFade = false;
        Game1.fadeToBlack = false;
        Game1.fadeIn = false;
        Game1.fadeToBlackAlpha = 0f;
    }

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
