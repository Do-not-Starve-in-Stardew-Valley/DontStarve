#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Audio.StatusFeedback;
using DontStarve.Interface;
using DontStarve.Player.Death;
using DontStarve.Player.Stats;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace DontStarve.Player.Sleep;

/// <summary>
/// SMAPI boundary for the shared SleepSystem. The adapter owns no second clock: all realtime
/// steps are driven by the same ITimeAPI minute callbacks used by Hunger and Sanity.
/// </summary>
internal sealed class SmapiSleepLifecycleAdapter : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly ITimeAPI timeApi;
    private readonly SurvivalStateTimeOwner<Farmer> survivalTime;
    private readonly SanitySystemLifecycleCoordinator sanityLifecycle;
    private readonly Func<bool> sleepRecoveryBalanceProvider;
    private readonly Func<HungerDamageMode> hungerDamageModeProvider;
    private readonly SpecialPassOutSanitySettlement specialPassOutSanitySettlement;
    private readonly SleepOvernightSettlement overnight = new();
    private readonly HashSet<string> committedPlayers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pendingWakeDeaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pendingHiddenHospitalDeaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> hiddenHospitalReleaseRequested = new(StringComparer.Ordinal);
    private readonly Action<Farmer>? markHungerDeath;
    private readonly SmapiSleepEngineBridge engineBridge;
    private readonly SleepOwnerHealthState ownerHealth = new();
    private bool nightInProgress;
    private bool wakeStateCanWrite = true;
    private const string WakeStateKey = "DontStarve.Sleep.WakeEffects";
    private readonly SleepLifecycleState transportState = new();
    private readonly SleepRuntimeState contextState = new();
    private readonly SmapiBedResolver bedResolver = new();
    private readonly Dictionary<string, ActiveSleep> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pendingAttempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> clientSleeping = new(StringComparer.Ordinal);
    private readonly HashSet<string> suppressStartSleepObservation = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    // A persisted wake-death marker means the native save already contains the final zero HP.
    // Keep only a session-local load gate so the first post-fade Farmer.Update can own the real
    // kill-screen transition; never replace the saved HP with a synthetic positive value.
    private bool wakeDeathLoadReplayPending;
    private bool wakeDeathLoadGateArmed;
    private string wakeDeathLoadGatePlayerKey = string.Empty;
    private bool preWorldDeathLoadGateArmed;
    private string preWorldDeathLoadGatePlayerKey = string.Empty;
    private string wakeDeathCauseMarkedPlayerKey = string.Empty;
    // UpdateTicked must remove entries from active/pendingWakeDeaths without enumerating a
    // collection that it mutates. Reuse these buffers because this path runs at frame cadence.
    private readonly List<ActiveSleep> activeCleanupBuffer = new();
    private readonly List<string> completedWakeDeathBuffer = new();
    private SmapiSleepMultiplayerCoordinator multiplayer = null!;
    private bool disposed;

    internal SmapiSleepLifecycleAdapter(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        ITimeAPI timeApi,
        SurvivalStateTimeOwner<Farmer> survivalTime,
        SanitySystemLifecycleCoordinator sanityLifecycle,
        SmapiPassOutReasonService passOutReasons,
        Func<bool> sleepRecoveryBalanceProvider,
        Func<HungerDamageMode> hungerDamageModeProvider,
        SpecialPassOutSanitySettlement specialPassOutSanitySettlement,
        Action<Farmer>? markHungerDeath = null
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.survivalTime = survivalTime
            ?? throw new ArgumentNullException(nameof(survivalTime));
        this.sanityLifecycle = sanityLifecycle
            ?? throw new ArgumentNullException(nameof(sanityLifecycle));
        this.sleepRecoveryBalanceProvider = sleepRecoveryBalanceProvider
            ?? throw new ArgumentNullException(nameof(sleepRecoveryBalanceProvider));
        this.hungerDamageModeProvider = hungerDamageModeProvider
            ?? throw new ArgumentNullException(nameof(hungerDamageModeProvider));
        this.specialPassOutSanitySettlement = specialPassOutSanitySettlement
            ?? throw new ArgumentNullException(nameof(specialPassOutSanitySettlement));
        this.markHungerDeath = markHungerDeath;
        engineBridge = new SmapiSleepEngineBridge(this, monitor, modId);

        if (passOutReasons is null)
            throw new ArgumentNullException(nameof(passOutReasons));
        passOutReasons.StartSleepRequested += OnStartSleepRequested;
        passOutReasons.StartSleepObserved += OnStartSleepObserved;
        passOutReasons.VoluntarySleepCommitted += OnVoluntarySleepCommitted;

        sanityLifecycle.BindSleepContext(IsPlayerInSleepContext);

        timeApi.OnUpdate.Add(OnMinuteUpdate);
        timeApi.OnSync.Add(OnSync);
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;

        multiplayer = new SmapiSleepMultiplayerCoordinator(helper, modId, this);
        multiplayer.Initialize();
    }

    internal event Action<SleepPlayerSnapshot>? HostSnapshotChanged;

    internal event Action<Farmer>? HiddenHospitalWakeReady;


    internal SleepAuthorityRole Role => transportState.Role;

    internal string SessionId => transportState.SessionId;

    internal bool IsPlayerInSleepContext(string playerKey)
    {
        return active.ContainsKey(playerKey) || clientSleeping.Contains(playerKey);
    }

    internal SleepSnapshotMessage CreateFullSnapshot() =>
        transportState.CreateFullSnapshot();

    internal void OnTransportSessionStarted()
    {
        multiplayer.OnSessionStarted();
    }

    internal void ApplyClientSnapshot(SleepSnapshotMessage message)
    {
        if (transportState.Role != SleepAuthorityRole.Client)
        {
            var local = Game1.player;
            if (
                local is null
                || !transportState.BeginClientSession(
                    message.SessionId,
                    PlayerKey(local),
                    out _
                )
            )
                return;
        }
        var result = transportState.ApplyClientSnapshot(message);
        if (result.Status == SleepSnapshotApplyStatus.AppliedFull)
        {
            clientSleeping.Clear();
            foreach (var snapshot in message.Players)
            {
                if (snapshot.Active)
                    clientSleeping.Add(snapshot.PlayerKey);
            }
        }
        else if (result.Status == SleepSnapshotApplyStatus.AppliedDelta)
        {
            var snapshot = message.Players[0];
            if (snapshot.Active)
                clientSleeping.Add(snapshot.PlayerKey);
            else
                clientSleeping.Remove(snapshot.PlayerKey);
        }

        if (result.Status is SleepSnapshotApplyStatus.AppliedFull or SleepSnapshotApplyStatus.AppliedDelta)
        {
            foreach (var snapshot in message.Players)
            {
                if (!snapshot.Active)
                    survivalTime.EndSleep(snapshot.PlayerKey, snapshot.SessionId);

                var local = Game1.player;
                if (
                    local is not null
                    && ownerHealth.TryAccept(
                        snapshot,
                        PlayerKey(local),
                        out var health,
                        out var hungerDeath
                    )
                )
                {
                    ApplyOwnerHealth(local, health, hungerDeath);
                }
                if (active.TryGetValue(snapshot.PlayerKey, out var localEntry)
                    && localEntry.Context.SettlementId == snapshot.SettlementId)
                {
                    localEntry.Context = localEntry.Context with
                    {
                        Phase = snapshot.Phase,
                        CurrentMinute = snapshot.CurrentMinute,
                        TargetMinute = snapshot.TargetMinute,
                    };
                    if (snapshot.Phase == SleepPhase.BlackoutBatch)
                    {
                        localEntry.BlackoutStartMinute = snapshot.CurrentMinute;
                        localEntry.Progress = localEntry.Progress with
                        {
                            LastProcessedMinute = snapshot.CurrentMinute,
                            Phase = SleepPhase.BlackoutBatch,
                        };
                        nightInProgress = true;
                        if (!survivalTime.SetSleepPhase(
                            snapshot.PlayerKey,
                            snapshot.SessionId,
                            SurvivalStateTimePhase.BlackoutSleep
                        ))
                            LogOnce("sleep-client-blackout-survival-time-phase-rejected", LogLevel.Warn);
                    }
                    else if (snapshot.Phase == SleepPhase.Completed)
                        nightInProgress = true;
                    if (!snapshot.Active)
                    {
                        active.Remove(snapshot.PlayerKey);
                        contextState.TryInterrupt(snapshot.PlayerKey, SessionId, out _);
                    }
                }
            }
        }

        if (result.NeedsFullSnapshot)
            multiplayer.RequestFullSnapshot();
        else if (result.Status == SleepSnapshotApplyStatus.Rejected)
            LogOnce(result.Reason, LogLevel.Warn);
    }

    internal void HandleBeginRequest(SleepBeginRequest request, long senderPlayerId)
    {
        if (Role != SleepAuthorityRole.Host)
            return;
        if (
            request is null
            || !long.TryParse(
                request.PlayerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
        {
            LogOnce("sleep-begin-request-owner-is-invalid", LogLevel.Trace);
            return;
        }

        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        var reason = "sleep-begin-player-is-unavailable";
        var currentRevision = transportState.TryGetSnapshot(
            request.PlayerKey,
            out var current
        )
            ? current.Revision
            : 0;
        var validation = SleepLifecycleProtocol.ValidateBeginRequest(
            request,
            senderPlayerId,
            SessionId,
            player is not null,
            transportState.GetHighestNonce(request.PlayerKey),
            currentRevision
        );
        if (!validation.Accepted)
        {
            LogOnce(validation.Reason, LogLevel.Trace);
            multiplayer.SendBeginDecision(senderPlayerId, request, false, validation.Reason);
            if (validation.NeedsSnapshot)
                multiplayer.SendFullSnapshot(senderPlayerId);
            return;
        }
        if (!transportState.AcceptNonce(request.PlayerKey, request.Nonce, out reason))
        {
            multiplayer.SendBeginDecision(senderPlayerId, request, false, reason);
            return;
        }
        if (
            player is null
            || !TryBuildSnapshot(
                player,
                request.AttemptId,
                requireInBed: true,
                out var snapshot,
                out reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            multiplayer.SendBeginDecision(senderPlayerId, request, false, reason);
            return;
        }
        if (active.TryGetValue(request.PlayerKey, out var currentAttempt)
            && currentAttempt.AttemptId != request.AttemptId)
        {
            multiplayer.SendBeginDecision(senderPlayerId, request, false,
                "sleep-player-already-has-an-active-attempt");
            return;
        }
        BeginHostContext(player, snapshot, request.AttemptId, confirmed: false);
        var accepted = active.TryGetValue(request.PlayerKey, out var started)
            && started.AttemptId == request.AttemptId;
        multiplayer.SendBeginDecision(senderPlayerId, request, accepted,
            accepted ? "sleep-begin-processed" : "sleep-context-could-not-start");
    }

    internal void HandleBeginDecision(SleepBeginDecision decision)
    {
        if (Role != SleepAuthorityRole.Client || decision is null
            || decision.SessionId != SessionId || Game1.player is null
            || decision.PlayerKey != PlayerKey(Game1.player) || decision.Accepted)
            return;
        if ((active.TryGetValue(decision.PlayerKey, out var entry)
                && entry.AttemptId == decision.AttemptId)
            || (pendingAttempts.TryGetValue(decision.PlayerKey, out var pending)
                && pending == decision.AttemptId))
        {
            CancelAttempt(decision.PlayerKey, decision.AttemptId);
            ownerHealth.Clear();
            LogOnce(decision.Reason, LogLevel.Warn);
        }
    }

    internal void HandleCancelRequest(SleepCancelRequest request, long senderPlayerId)
    {
        if (Role != SleepAuthorityRole.Host || request is null)
            return;
        if (
            !long.TryParse(
                request.PlayerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var requestedPlayerId
            )
            || requestedPlayerId != senderPlayerId
        )
            return;

        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        var validation = SleepLifecycleProtocol.ValidateCancelRequest(
            request,
            senderPlayerId,
            SessionId,
            player is not null,
            transportState.GetHighestNonce(request.PlayerKey)
        );
        if (!validation.Accepted)
        {
            LogOnce(validation.Reason, LogLevel.Trace);
            return;
        }
        if (!transportState.AcceptNonce(request.PlayerKey, request.Nonce, out _))
            return;
        if (active.TryGetValue(request.PlayerKey, out var currentAttempt)
            && currentAttempt.AttemptId != request.AttemptId)
            return;
        CancelAttempt(request.PlayerKey, request.AttemptId);
    }

    internal void HandleSnapshotRequest(SleepSnapshotRequest request, long senderPlayerId)
    {
        if (Role != SleepAuthorityRole.Host || request is null)
            return;
        var expectedPlayerKey = senderPlayerId.ToString(CultureInfo.InvariantCulture);
        if (
            !string.Equals(request.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
            || (
                !string.IsNullOrEmpty(request.SessionId)
                && !string.Equals(request.SessionId, SessionId, StringComparison.Ordinal)
            )
        )
        {
            LogOnce("sleep-snapshot-request-owner-or-session-is-invalid", LogLevel.Trace);
            return;
        }
        multiplayer.SendFullSnapshot(senderPlayerId);
    }

    internal void OnPeerDisconnected(long playerId, bool wasHost)
    {
        var playerKey = playerId.ToString(CultureInfo.InvariantCulture);
        transportState.ForgetPeer(playerKey);
        if (wasHost)
        {
            clientSleeping.Clear();
            ClearRuntimeOnly();
            transportState.ClearSession();
            LogOnce("sleep-host-peer-disconnected-client-state-reset", LogLevel.Warn);
            return;
        }

        survivalTime.ForgetPlayer(playerKey);
        if (Role == SleepAuthorityRole.Host)
        {
            active.Remove(playerKey);
            contextState.TryInterrupt(playerKey, SessionId, out _);
        }
    }

    internal void OnPeerConnected(long playerId, bool isHost)
    {
        if (Role == SleepAuthorityRole.Host && !isHost)
            multiplayer.SendFullSnapshot(playerId);
        else if (Role == SleepAuthorityRole.Client && isHost)
            multiplayer.RequestFullSnapshot();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        timeApi.OnUpdate.Remove(OnMinuteUpdate);
        timeApi.OnSync.Remove(OnSync);
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.Saving -= OnSaving;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        helper.Events.Player.Warped -= OnWarped;
        multiplayer.Dispose();
        engineBridge.Dispose();

        sanityLifecycle.BindSleepContext(null);
        ClearRuntimeOnly();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // Farmer.Update can run once during the loader before SMAPI raises SaveLoaded. Preserve
        // that early zero-health gate until the wake marker has been read and classified.
        ClearRuntimeOnly(preservePreWorldDeathGate: true);
        overnight.Clear();
        pendingWakeDeaths.Clear();
        pendingHiddenHospitalDeaths.Clear();
        hiddenHospitalReleaseRequested.Clear();
        wakeStateCanWrite = true;
        wakeDeathLoadReplayPending = false;
        wakeDeathCauseMarkedPlayerKey = string.Empty;

        if (
            sanityLifecycle.AuthorityRole == SanityAuthorityRole.Host
            && SleepLifecycleProtocol.IsValidSessionId(sanityLifecycle.SessionId)
        )
        {
            transportState.BeginHostSession(sanityLifecycle.SessionId, out _);
            // Old DontStarve.Sleep deltas belong to the retired post-save pipeline. Leave
            // their raw data untouched; a malformed old receipt cannot disable normal sleep.
            try
            {
                var wake = helper.Data.ReadSaveData<SleepWakeEffectsData>(WakeStateKey);
                if (wake is not null && !wake.IsValid())
                    throw new InvalidOperationException("sleep-wake-state-is-invalid");
                if (wake is not null)
                {
                    foreach (var pair in wake.Normalize(CurrentDay()))
                        pendingWakeDeaths[pair.Key] = pair.Value;
                    foreach (var pair in wake.NormalizeHiddenHospital(CurrentDay()))
                    {
                        pendingWakeDeaths[pair.Key] = pair.Value;
                        pendingHiddenHospitalDeaths[pair.Key] = pair.Value;
                    }
                    PrunePendingWakeDeaths();
                }
            }
            catch (Exception exception)
            {
                wakeStateCanWrite = false;
                LogOnce($"sleep-wake-state-read-failed:{exception.GetType().Name}", LogLevel.Error);
            }
            MarkPendingWakeDeaths();
            wakeDeathLoadReplayPending = true;
            // Arm during SaveLoaded itself so no Farmer.Update can observe the persisted zero HP
            // with movement still enabled. Release is deliberately deferred to UpdateTicked,
            // after the load fade callback had a chance to finish.
            TryArmWakeDeathLoadGate();
            OnTransportSessionStarted();
        }
        else if (sanityLifecycle.AuthorityRole == SanityAuthorityRole.Client)
        {
            // Client sessions receive the authoritative session id with the first full snapshot.
            clientSleeping.Clear();
            multiplayer.RequestFullSnapshot();
        }

        if (!wakeDeathLoadGateArmed)
            ReleasePreWorldDeathLoadGate();
    }

    [EventPriority(EventPriority.High)]
    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (
            Role != SleepAuthorityRole.Host
            || !Context.IsMainPlayer
        )
            return;

        // Run before Hunger/Sanity's ordinary Saving subscribers serialize their values.
        // Each frozen result is absolute and is committed once; loading needs no sleep replay.
        foreach (var input in overnight.Snapshot())
        {
            if (committedPlayers.Contains(input.PlayerKey))
                continue;
            if (!long.TryParse(input.PlayerKey, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var id))
                continue;
            var farmer = Game1.GetPlayer(id, onlyOnline: false);
            if (farmer is null || !overnight.TryFinalize(input.PlayerKey, farmer.health,
                    ResolveHungerDamageMode(), out var result))
            {
                LogOnce("sleep-save-player-is-unavailable", LogLevel.Error);
                continue;
            }
            if (!survivalTime.CanAdvanceSleepInterval(
                input.PlayerKey,
                farmer,
                input.SessionId,
                input.BlackoutStartMinute,
                input.TargetMinute,
                SurvivalStateTimePhase.BlackoutSleep
            ))
            {
                LogOnce("sleep-blackout-survival-time-interval-rejected", LogLevel.Error);
                continue;
            }
            if (input.RecoveryBalance.SanityEnabled)
            {
                var sanity = farmer.SetSanity(
                    result.Sanity,
                    input.RecoveryBalance.SanitySettlementSource
                );
                if (sanity.Status == SanityChangeStatus.Rejected)
                {
                    LogOnce(sanity.Reason, LogLevel.Error);
                    continue;
                }
            }
            if (input.RecoveryBalance.HungerEnabled)
            {
                farmer.SetHunger(result.Hunger);
                HungerExtensions.ResumeAfterSleep(farmer);
            }
            ApplyFinalHealth(farmer, result);
            ApplyHiddenHospitalWakeHealth(farmer);
            // Register the blackout interval only after the existing joint result has been applied;
            // rejected Sanity writes can then retry Saving without consuming the owner phase first.
            if (!survivalTime.AdvanceSleepInterval(
                input.PlayerKey,
                farmer,
                input.SessionId,
                input.BlackoutStartMinute,
                input.TargetMinute,
                SurvivalStateTimePhase.BlackoutSleep,
                out _
            ))
            {
                LogOnce("sleep-blackout-survival-time-commit-rejected", LogLevel.Error);
                continue;
            }
            committedPlayers.Add(input.PlayerKey);
            if (active.TryGetValue(input.PlayerKey, out var entry))
            {
                entry.Progress = result;
                entry.Context = entry.Context.Complete(input.TargetMinute);
                PublishHostSnapshot(entry, activeValue: false);
            }
            LogRuntimeDiagnostic($"sleep.runtime.pre-save-applied player={input.PlayerKey} hunger={result.Hunger:F3} sanity={result.Sanity:F3} health={farmer.health}");
        }
        if (wakeStateCanWrite)
        {
            try
            {
                PrunePendingWakeDeaths();
                helper.Data.WriteSaveData(WakeStateKey,
                    SleepWakeEffectsData.From(
                        NaturalWakeDeathMarkers(),
                        pendingHiddenHospitalDeaths,
                        CurrentDay()
                    ));
            }
            catch (Exception exception)
            {
                wakeStateCanWrite = false;
                LogOnce($"sleep-wake-state-write-failed:{exception.GetType().Name}", LogLevel.Error);
            }
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (Role != SleepAuthorityRole.Host || !Context.IsMainPlayer)
            return;
        if (nightInProgress)
            return;
        if ((!sanityLifecycle.IsEnabled && !HungerExtensions.IsEnabled)
            || HungerExtensions.IsDebugLocked)
        {
            foreach (var entry in new List<ActiveSleep>(active.Values))
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
            nightInProgress = false;
            overnight.Clear();
            return;
        }

        nightInProgress = true;
        overnight.Clear();
        committedPlayers.Clear();
        ApplySpecialPassOutSanityAtDayEnding();
        var entries = new List<ActiveSleep>(active.Values);
        foreach (var entry in entries)
        {
            if (!entry.Confirmed)
            {
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
                continue;
            }

            var blackoutStart = timeApi.Time;
            var player = ResolvePlayer(entry.PlayerKey);
            var snapshot = entry.Snapshot with
            {
                Hunger = player?.GetHunger() ?? entry.Progress.Hunger,
                Sanity = player?.GetSanity() ?? entry.Progress.Sanity,
                Health = player is null
                    ? entry.Progress.Health
                    : SleepRules.ReconcileNativeHealth(entry.Progress.Health, player.health, entry.Snapshot.MaximumHealth),
                BlackoutStartMinute = blackoutStart,
                CalendarYear = Game1.year,
                CalendarSeason = Game1.season.ToString(),
                CalendarDay = Game1.dayOfMonth,
            };
            snapshot = SleepSanitySettlementPolicy.Apply(
                snapshot,
                specialPassOutSanitySettlement,
                SessionId,
                entry.PlayerKey,
                CurrentDay()
            );
            if (!overnight.TryPrepare(snapshot, out var reason))
            {
                LogOnce(reason, LogLevel.Error);
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
                continue;
            }
            entry.BlackoutStartMinute = blackoutStart;
            entry.Context = entry.Context.EnterBlackout(blackoutStart);
            if (!survivalTime.SetSleepPhase(
                entry.PlayerKey,
                entry.Context.SessionId,
                SurvivalStateTimePhase.BlackoutSleep
            ))
            {
                LogOnce("sleep-blackout-survival-time-phase-rejected", LogLevel.Error);
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
                continue;
            }
            LogRuntimeDiagnostic($"sleep.runtime.night-prepared player={entry.PlayerKey} start={blackoutStart} target={snapshot.TargetMinute}");
            PublishHostSnapshot(entry);
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        // The values are already in the native save. No numeric delta belongs here.
        ApplyPendingOwnerHealth();
        foreach (var farmer in Game1.getOnlineFarmers())
            ApplyHiddenHospitalWakeHealth(farmer);
        PrunePendingWakeDeaths();
        MarkPendingWakeDeaths();
        nightInProgress = false;
        survivalTime.EndAllSleepContexts();
        active.Clear();
        pendingAttempts.Clear();
        clientSleeping.Clear();
        suppressStartSleepObservation.Clear();
        contextState.Clear();
        overnight.Clear();
        committedPlayers.Clear();
        specialPassOutSanitySettlement.ClearSession(SessionId);
    }

    private void OnMinuteUpdate(long time)
    {
        if (
            Role != SleepAuthorityRole.Host
            || !Context.IsMainPlayer
            || (!sanityLifecycle.IsEnabled && !HungerExtensions.IsEnabled)
            || HungerExtensions.IsDebugLocked
        )
            return;

        foreach (var entry in new List<ActiveSleep>(active.Values))
        {
            if (entry.Context.Phase != SleepPhase.Realtime || time <= entry.Progress.LastProcessedMinute)
                continue;
            if (
                !long.TryParse(
                    entry.PlayerKey,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var playerId
                )
            )
                continue;
            var farmer = Game1.GetPlayer(playerId, onlyOnline: true);
            if (farmer is null)
                continue;

            var before = entry.Progress;
            // Native HP is integer. Keep our fractional paid recovery only while no external
            // health change has occurred; neither lose 0.6 HP each tick nor overwrite damage.
            var health = SleepRules.ReconcileNativeHealth(
                before.Health,
                farmer.health,
                entry.Snapshot.MaximumHealth
            );
            if (!survivalTime.AdvanceSleepInterval(
                entry.PlayerKey,
                farmer,
                entry.Context.SessionId,
                before.LastProcessedMinute,
                time,
                SurvivalStateTimePhase.RealtimeSleep,
                out _
            ))
            {
                LogOnce("sleep-realtime-survival-time-interval-rejected", LogLevel.Error);
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
                continue;
            }
            var snapshot = entry.Snapshot with
            {
                Hunger = farmer.GetHunger(),
                Sanity = farmer.GetSanity(),
                Health = health,
            };
            var step = SleepRules.SettleRealtime(
                snapshot,
                before.LastProcessedMinute,
                time,
                ResolveHungerDamageMode()
            ) with { Phase = SleepPhase.Realtime };
            var progress = new SleepProgress(
                before.ProcessedMinutes + step.ProcessedMinutes,
                step.LastProcessedMinute,
                step.Hunger,
                step.Sanity,
                step.Health,
                before.NaturalHungerConsumed + step.NaturalHungerConsumed,
                before.HungerPaidForRecovery + step.HungerPaidForRecovery,
                before.SanityRecovered + step.SanityRecovered,
                before.HungerDamage + step.HungerDamage,
                SleepPhase.Realtime
            );
            ApplyRealtimeProgress(farmer, before, progress);
            var hungerDeath = PlayerDeathCausePolicy.ShouldMarkHungerDeath(
                farmer.health,
                step.HungerDamage
            );
            if (hungerDeath)
            {
                entry.HungerDeath = true;
                markHungerDeath?.Invoke(farmer);
            }
            entry.Progress = progress;
            entry.Context = entry.Context with { CurrentMinute = time };
            PublishHostSnapshot(entry);
            if (farmer.health <= 0)
            {
                CancelAttempt(entry.PlayerKey, entry.AttemptId);
                CloseLocalSleepMenu(farmer);
            }
        }
    }

    private void OnSync(long time, long delta)
    {
        // Forward minutes are already delivered through OnUpdate; rewind only affects ordinary
        // behavior wait cursors and must not make Sleep replay a second real-time settlement.
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        AdvanceWakeDeathLoadGate();
        AdvanceHiddenHospitalWakeDeath();
        if (nightInProgress)
            return;
        if (Role == SleepAuthorityRole.Host && Context.IsMainPlayer)
        {
            if (active.Count > 0)
            {
                activeCleanupBuffer.Clear();
                foreach (var entry in active.Values)
                    activeCleanupBuffer.Add(entry);
                foreach (var entry in activeCleanupBuffer)
                {
                    var farmer = ResolvePlayer(entry.PlayerKey);
                    if (
                        entry.Context.Mode == SleepMode.Voluntary
                        && (farmer is null || farmer.timeWentToBed.Value == 0 || !farmer.isInBed.Value)
                    )
                        CancelAttempt(entry.PlayerKey, entry.AttemptId);
                }
            }
            if (pendingWakeDeaths.Count > 0)
            {
                completedWakeDeathBuffer.Clear();
                foreach (var key in pendingWakeDeaths.Keys)
                    if (
                        ResolvePlayer(key) is Farmer awake
                        && awake.health > 0
                        && !pendingHiddenHospitalDeaths.ContainsKey(key)
                    )
                        completedWakeDeathBuffer.Add(key);
                foreach (var key in completedWakeDeathBuffer)
                {
                    pendingWakeDeaths.Remove(key);
                    pendingHiddenHospitalDeaths.Remove(key);
                }
            }
        }
        if (Game1.player is null)
            return;
        if (Game1.activeClickableMenu is not null)
            return;

        var playerKey = PlayerKey(Game1.player);
        if (Game1.player.timeWentToBed.Value != 0)
            return;
        if (pendingAttempts.TryGetValue(playerKey, out var attemptId))
        {
            CancelAttempt(playerKey, attemptId);
            if (Role == SleepAuthorityRole.Client)
                multiplayer.SendCancelRequest(playerKey, attemptId);
        }
        else if (
            active.TryGetValue(playerKey, out var activeAttempt)
            && !activeAttempt.Confirmed
        )
        {
            CancelAttempt(playerKey, activeAttempt.AttemptId);
            if (Role == SleepAuthorityRole.Client)
                multiplayer.SendCancelRequest(playerKey, activeAttempt.AttemptId);
        }
    }

    private bool OnStartSleepRequested(Farmer farmer)
    {
        if (!CanUseCustomSleep() || farmer is null)
            return true;
        var playerKey = PlayerKey(farmer);
        if (pendingAttempts.ContainsKey(playerKey))
            return true;

        var attemptId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (
            !TryBuildSnapshot(
                farmer,
                attemptId,
                requireInBed: false,
                out var snapshot,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return true;
        }
        var prediction = SleepRules.SettleRealtime(
            snapshot,
            snapshot.SleepStartMinute,
            snapshot.TargetMinute,
            ResolveHungerDamageMode()
        );
        if (!SleepRules.ShouldConfirmHungerWarning(snapshot.RecoveryBalance, prediction))
            return true;

        if (
            !transportState.IssueConfirmationToken(
                playerKey,
                snapshot.SessionId,
                attemptId,
                out reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return true;
        }
        pendingAttempts[playerKey] = attemptId;
        try
        {
            var location = farmer.currentLocation;
            if (location is null)
                throw new InvalidOperationException("sleep-confirmation-location-is-null");
            location.createQuestionDialogue(
                helper.Translation.Get("sleep-confirmation.body").ToString(),
                location.createYesNoResponses(),
                OnConfirmationAnswered
            );
            return false;
        }
        catch (Exception exception)
        {
            CancelAttempt(playerKey, attemptId);
            LogOnce(
                $"sleep-confirmation-dialogue-failed:{exception.GetType().Name}",
                LogLevel.Warn
            );
            return true;
        }
    }

    private void OnConfirmationAnswered(Farmer who, string answer)
    {
        var playerKey = PlayerKey(who);
        if (!pendingAttempts.TryGetValue(playerKey, out var attemptId))
            return;
        if (!string.Equals(answer, "Yes", StringComparison.Ordinal))
        {
            CancelAttempt(playerKey, attemptId);
            return;
        }

        try
        {
            Game1.currentLocation?.answerDialogueAction(
                "Sleep_Yes",
                Array.Empty<string>()
            );
        }
        catch (Exception exception)
        {
            CancelAttempt(playerKey, attemptId);
            LogOnce(
                $"sleep-confirmation-retry-failed:{exception.GetType().Name}",
                LogLevel.Warn
            );
        }
    }

    private void OnStartSleepObserved(Farmer farmer)
    {
        if (!CanUseCustomSleep() || farmer is null)
            return;
        var playerKey = PlayerKey(farmer);
        if (
            pendingAttempts.ContainsKey(playerKey)
            && farmer.timeWentToBed.Value == 0
            && Game1.activeClickableMenu is not null
        )
            return;
        if (suppressStartSleepObservation.Remove(playerKey))
            return;
        var attemptId = pendingAttempts.TryGetValue(playerKey, out var pending)
            ? pending
            : Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (
            !TryBuildSnapshot(
                farmer,
                attemptId,
                requireInBed: true,
                out var snapshot,
                out var reason
            )
        )
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }

        LogRuntimeDiagnostic(
            $"sleep.runtime.start-observed player={snapshot.PlayerKey} attempt={attemptId} time={snapshot.SleepStartMinute}"
        );

        if (Role == SleepAuthorityRole.Host)
            BeginHostContext(farmer, snapshot, attemptId, confirmed: false);
        else if (Role == SleepAuthorityRole.Client)
        {
            BeginClientContext(snapshot, attemptId);
            multiplayer.SendBeginRequest(snapshot, attemptId);
        }
    }

    private void OnVoluntarySleepCommitted(
        Farmer farmer,
        PassOutReasonEvidence evidence
    )
    {
        if (Role != SleepAuthorityRole.Host || !Context.IsMainPlayer)
            return;

        foreach (var player in Game1.getOnlineFarmers())
        {
            if (!player.isInBed.Value)
                continue;
            var playerKey = PlayerKey(player);
            if (!active.TryGetValue(playerKey, out var entry))
            {
                var attemptId = pendingAttempts.TryGetValue(playerKey, out var pending)
                    ? pending
                    : Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                if (
                    !TryBuildSnapshot(
                        player,
                        attemptId,
                        requireInBed: true,
                        out var snapshot,
                        out var reason
                    )
                )
                {
                    LogOnce(reason, LogLevel.Warn);
                    continue;
                }
                BeginHostContext(player, snapshot, attemptId, confirmed: false);
                entry = active[playerKey];
            }

            entry.Confirmed = true;
            // doSleep only commits the intent. The real batch boundary is DayEnding; waiting
            // players remain on the live minute lane until the world actually stops.
            if (pendingAttempts.TryGetValue(playerKey, out var attemptIdToConsume))
            {
                transportState.ConsumeConfirmationToken(
                    playerKey,
                    SessionId,
                    attemptIdToConsume,
                    out _
                );
                pendingAttempts.Remove(playerKey);
            }
            suppressStartSleepObservation.Add(playerKey);
            PublishHostSnapshot(entry);
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer || Game1.player is null || nightInProgress)
            return;
        var playerKey = PlayerKey(Game1.player);
        if (!active.TryGetValue(playerKey, out var entry))
            return;
        if (entry.Context.Phase != SleepPhase.Realtime)
            return;
        CancelAttempt(playerKey, entry.AttemptId);
        if (Role == SleepAuthorityRole.Client)
            multiplayer.SendCancelRequest(playerKey, entry.AttemptId);
    }

    private void BeginHostContext(
        Farmer farmer,
        SleepSnapshot snapshot,
        string attemptId,
        bool confirmed
    )
    {
        var playerKey = snapshot.PlayerKey;
        if (active.ContainsKey(playerKey))
            return;
        var context = SleepContext.Begin(snapshot);
        if (!contextState.TryBegin(context, out var reason))
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        if (!survivalTime.BeginSleep(playerKey, context.SessionId))
        {
            contextState.TryInterrupt(playerKey, context.SessionId, out _);
            LogOnce("sleep-survival-time-context-rejected", LogLevel.Error);
            return;
        }
        var entry = new ActiveSleep(
            playerKey,
            attemptId,
            snapshot,
            context,
            SleepProgress.Start(snapshot, snapshot.SleepStartMinute)
        );
        active[playerKey] = entry;
        if (confirmed)
            MarkConfirmed(entry);
        else
            PublishHostSnapshot(entry);
    }

    private void BeginClientContext(SleepSnapshot snapshot, string attemptId)
    {
        var playerKey = snapshot.PlayerKey;
        if (active.ContainsKey(playerKey))
            return;
        var context = SleepContext.Begin(snapshot);
        if (!contextState.TryBegin(context, out var reason))
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        if (!survivalTime.BeginSleep(playerKey, context.SessionId))
        {
            contextState.TryInterrupt(playerKey, context.SessionId, out _);
            LogOnce("sleep-survival-time-context-rejected", LogLevel.Error);
            return;
        }
        active[playerKey] = new ActiveSleep(
            playerKey,
            attemptId,
            snapshot,
            context,
            SleepProgress.Start(snapshot, snapshot.SleepStartMinute)
        );
        ownerHealth.Begin(snapshot.SettlementId);
    }

    private void MarkConfirmed(ActiveSleep entry)
    {
        entry.Confirmed = true;
        entry.BlackoutStartMinute = timeApi.Time;
        entry.Context = entry.Context.EnterBlackout(entry.BlackoutStartMinute);
        entry.Progress = entry.Progress with
        {
            LastProcessedMinute = entry.BlackoutStartMinute,
            Phase = SleepPhase.BlackoutBatch,
        };
        if (!survivalTime.SetSleepPhase(
            entry.PlayerKey,
            entry.Context.SessionId,
            SurvivalStateTimePhase.BlackoutSleep
        ))
        {
            LogOnce("sleep-blackout-survival-time-phase-rejected", LogLevel.Error);
            CancelAttempt(entry.PlayerKey, entry.AttemptId);
            return;
        }
        PublishHostSnapshot(entry);
    }

    private void ApplyRealtimeProgress(
        Farmer farmer,
        SleepProgress before,
        SleepProgress progress
    )
    {
        farmer.SetHunger(Math.Max(0d, progress.Hunger));
        var sanityDelta = progress.Sanity - before.Sanity;
        if (sanityDelta > 0)
        {
            var result = farmer.ChangeSanity(
                sanityDelta,
                contextState.TryGet(PlayerKey(farmer), out var context)
                    ? context.RecoveryBalance.SanitySettlementSource
                    : SanityChangeSource.VoluntarySleep
            );
            if (result.Status == SanityChangeStatus.Rejected)
                LogOnce(result.Reason, LogLevel.Warn);
        }
        // 睡眠实时结算的健康推进是 overtime 语义：恢复/扣血都不播音（对齐 DST 睡觉）。
        StatusFeedbackHealthSuppression.SuppressNextHealthSound(
            farmer.UniqueMultiplayerID,
            farmer.health,
            HealthForGame(progress.Health)
        );
        farmer.health = HealthForGame(progress.Health);
    }

    private bool TryBuildSnapshot(
        Farmer farmer,
        string attemptId,
        bool requireInBed,
        out SleepSnapshot snapshot,
        out string reason,
        SleepMode mode = SleepMode.Voluntary
    )
    {
        snapshot = default;
        if (!CanUseCustomSleep())
        {
            reason = "sleep-custom-system-is-unavailable";
            return false;
        }
        var sessionId = SessionId;
        if (!SleepLifecycleProtocol.IsValidSessionId(sessionId))
        {
            reason = "sleep-session-is-not-ready";
            return false;
        }
        if (!SleepLifecycleProtocol.IsValidText(attemptId))
        {
            reason = "sleep-attempt-id-is-invalid";
            return false;
        }
        var playerKey = PlayerKey(farmer);
        if (!SleepLifecycleProtocol.IsValidPlayerKey(playerKey))
        {
            reason = "sleep-player-key-is-invalid";
            return false;
        }
        if (!bedResolver.TryResolve(farmer, requireInBed, out var bed, out reason))
            return false;
        var start = timeApi.Time;
        if (start < 0)
        {
            reason = "sleep-time-cursor-is-invalid";
            return false;
        }
        var target = ResolveNextSix(start);
        snapshot = new SleepSnapshot(
            playerKey,
            sessionId,
            string.Concat("settlement-", attemptId),
            mode,
            start,
            start,
            target,
            bed.BedId,
            farmer.GetHunger(),
            farmer.GetMaxHunger(),
            farmer.GetSanity(),
            farmer.GetMaxSanity(),
            farmer.health,
            farmer.maxHealth,
            (sleepRecoveryBalanceProvider() ? SleepRecoveryBalance.Default : SleepRecoveryBalance.Disabled) with
            {
                BedSanityMultiplier = bed.BedSanityMultiplier,
                HungerCostPerSanity = bed.HungerCostPerSanity,
                HungerEnabled = HungerExtensions.IsEnabled,
                SanityEnabled = sanityLifecycle.IsEnabled,
            },
            Game1.year,
            Game1.season.ToString(),
            Game1.dayOfMonth
        );
        snapshot = SleepSanitySettlementPolicy.Apply(
            snapshot,
            specialPassOutSanitySettlement,
            sessionId,
            playerKey,
            CurrentDay()
        );
        reason = "sleep-snapshot-captured";
        return true;
    }

    private void ApplySpecialPassOutSanityAtDayEnding()
    {
        foreach (var farmer in Game1.getOnlineFarmers())
        {
            var playerKey = PlayerKey(farmer);
            if (
                !specialPassOutSanitySettlement.TryResolve(
                    SessionId,
                    playerKey,
                    CurrentDay(),
                    farmer.GetSanity(),
                    out var overrideValue
                )
            )
                continue;

            var result = farmer.SetSanity(overrideValue.Sanity, overrideValue.Source);
            if (result.Status == SanityChangeStatus.Rejected)
                LogOnce(result.Reason, LogLevel.Error);
        }
    }

    private bool CanUseCustomSleep()
    {
        return !disposed
            && engineBridge.Available
            && Context.IsWorldReady
            && (sanityLifecycle.IsEnabled || HungerExtensions.IsEnabled)
            && !HungerExtensions.IsDebugLocked
            && (
                Role == SleepAuthorityRole.Host
                || Role == SleepAuthorityRole.Client
            )
            && SleepLifecycleProtocol.IsValidSessionId(SessionId);
    }

    private HungerDamageMode ResolveHungerDamageMode()
    {
        try
        {
            var mode = hungerDamageModeProvider();
            return Enum.IsDefined(typeof(HungerDamageMode), mode)
                ? mode
                : HungerDamageMode.Default;
        }
        catch (Exception exception)
        {
            LogOnce(
                $"sleep-hunger-damage-mode-unavailable:{exception.GetType().Name}",
                LogLevel.Warn
            );
            return HungerDamageMode.Default;
        }
    }

    private void PublishHostSnapshot(ActiveSleep entry, bool activeValue = true)
    {
        if (Role != SleepAuthorityRole.Host)
            return;
        var wireSnapshot = new SleepPlayerSnapshot
        {
            HasHealth = entry.Context.Phase != SleepPhase.BlackoutBatch,
            Health = HealthForGame(entry.Progress.Health),
            MaximumHealth = entry.Snapshot.MaximumHealth,
            HungerDeath = entry.HungerDeath,
            SessionId = SessionId,
            PlayerKey = entry.PlayerKey,
            Active = activeValue,
            Confirmed = entry.Confirmed,
            Mode = entry.Context.Mode,
            Phase = entry.Context.Phase,
            BedId = entry.Context.BedId,
            SettlementId = entry.Context.SettlementId,
            CurrentMinute = entry.Context.CurrentMinute,
            TargetMinute = entry.Context.TargetMinute,
        };
        if (!transportState.UpsertHostSnapshot(wireSnapshot, out var applied, out var reason))
        {
            LogOnce(reason, LogLevel.Warn);
            return;
        }
        entry.Revision = applied.Revision;
        HostSnapshotChanged?.Invoke(applied);
    }

    private void CancelAttempt(string playerKey, string attemptId)
    {
        var hadSleepContext = active.ContainsKey(playerKey) || clientSleeping.Contains(playerKey);
        if (active.TryGetValue(playerKey, out var entry))
        {
            survivalTime.EndSleep(playerKey, entry.Context.SessionId);
            if (Role == SleepAuthorityRole.Host)
                PublishHostSnapshot(entry, activeValue: false);
        }
        transportState.CancelAttempt(playerKey, SessionId, attemptId, out _);
        pendingAttempts.Remove(playerKey);
        active.Remove(playerKey);
        contextState.TryInterrupt(playerKey, SessionId, out _);
        clientSleeping.Remove(playerKey);
        if (hadSleepContext && ResolvePlayer(playerKey) is Farmer farmer)
            HungerExtensions.ResumeAfterSleep(farmer);
    }

    private void ClearRuntimeOnly(bool preservePreWorldDeathGate = false)
    {
        if (wakeDeathLoadGateArmed && !Game1.killScreen && Game1.player is Farmer farmer)
            farmer.CanMove = true;
        if (!preservePreWorldDeathGate)
            ReleasePreWorldDeathLoadGate();
        active.Clear();
        pendingAttempts.Clear();
        clientSleeping.Clear();
        suppressStartSleepObservation.Clear();
        contextState.Clear();
        overnight.Clear();
        committedPlayers.Clear();
        ownerHealth.Clear();
        nightInProgress = false;
        pendingHiddenHospitalDeaths.Clear();
        hiddenHospitalReleaseRequested.Clear();
        wakeDeathLoadReplayPending = false;
        wakeDeathLoadGateArmed = false;
        wakeDeathLoadGatePlayerKey = string.Empty;
        wakeDeathCauseMarkedPlayerKey = string.Empty;
        survivalTime.ClearSession();
        specialPassOutSanitySettlement.ClearSession(SessionId);
    }

    internal long KnownRevision(string playerKey) =>
        transportState.TryGetSnapshot(playerKey, out var snapshot) ? snapshot.Revision : 0;

    internal bool ShouldSuppressNativeBedHealth(Farmer farmer) =>
        engineBridge.Available && sleepRecoveryBalanceProvider() && HungerExtensions.IsEnabled
        && (
            farmer.timeWentToBed.Value != 0 && IsPlayerInSleepContext(PlayerKey(farmer))
            || pendingHiddenHospitalDeaths.ContainsKey(PlayerKey(farmer))
        );

    internal void OnNativeDayUpdated(Farmer farmer)
    {
        if (Role == SleepAuthorityRole.Host && nightInProgress
            && overnight.TryFinalize(PlayerKey(farmer), farmer.health,
                ResolveHungerDamageMode(), out var result))
        {
            ApplyFinalHealth(farmer, result);
        }
        ApplyHiddenHospitalWakeHealth(farmer);
        if (farmer.IsLocalPlayer)
            ApplyPendingOwnerHealth();
    }

    internal void OnNativeFarmhandSaving(Farmer farmer)
    {
        if (Role == SleepAuthorityRole.Host && nightInProgress
            && overnight.TryFinalize(PlayerKey(farmer), farmer.health,
                ResolveHungerDamageMode(), out var result))
        {
            ApplyFinalHealth(farmer, result);
            ApplyHiddenHospitalWakeHealth(farmer);
        }
    }

    private void ApplyFinalHealth(Farmer farmer, SleepProgress result)
    {
        // 睡眠结算（含恢复与空腹扣血）都是 overtime 语义：健康差分 watcher 不播音。
        StatusFeedbackHealthSuppression.SuppressNextHealthSound(
            farmer.UniqueMultiplayerID,
            farmer.health,
            Math.Min(farmer.maxHealth, HealthForGame(result.Health))
        );
        farmer.health = Math.Min(farmer.maxHealth, HealthForGame(result.Health));
        var shouldMarkHungerDeath =
            !active.TryGetValue(PlayerKey(farmer), out var activeEntry)
            || activeEntry.Context.Mode == SleepMode.Voluntary;
        if (
            shouldMarkHungerDeath
            &&
            PlayerDeathCausePolicy.ShouldMarkHungerDeath(
                farmer.health,
                result.HungerDamage
            )
        )
        {
            var playerKey = PlayerKey(farmer);
            pendingWakeDeaths[playerKey] = CurrentDay();
            if (active.TryGetValue(playerKey, out var entry))
                entry.HungerDeath = true;
            if (farmer.IsLocalPlayer)
                markHungerDeath?.Invoke(farmer);
        }
    }

    private void ApplyPendingOwnerHealth()
    {
        if (
            Game1.player is Farmer farmer
            && ownerHealth.CompleteNativeRecovery(out var health, out var hungerDeath)
        )
        {
            ApplyOwnerHealth(farmer, health, hungerDeath);
        }
    }

    private void ApplyOwnerHealth(Farmer farmer, int health, bool hungerDeath)
    {
        // 睡眠结算收尾的宿主健康回放同属 overtime 语义，抑制 watcher 播音。
        StatusFeedbackHealthSuppression.SuppressNextHealthSound(
            farmer.UniqueMultiplayerID,
            farmer.health,
            Math.Clamp(health, 0, farmer.maxHealth)
        );
        farmer.health = Math.Clamp(health, 0, farmer.maxHealth);
        if (farmer.health <= 0)
        {
            if (hungerDeath)
                markHungerDeath?.Invoke(farmer);
            CloseLocalSleepMenu(farmer);
        }
    }

    private void MarkPendingWakeDeaths()
    {
        if (
            Game1.player is Farmer farmer
            && farmer.health <= 0
            && pendingWakeDeaths.ContainsKey(PlayerKey(farmer))
            && !pendingHiddenHospitalDeaths.ContainsKey(PlayerKey(farmer))
        )
        {
            markHungerDeath?.Invoke(farmer);
            wakeDeathCauseMarkedPlayerKey = PlayerKey(farmer);
        }
    }

    internal bool RegisterHiddenHospitalDeath(Farmer farmer)
    {
        if (
            farmer is null
            || !SleepLifecycleProtocol.IsValidPlayerKey(PlayerKey(farmer))
            || (Role != SleepAuthorityRole.Host && Role != SleepAuthorityRole.Client)
        )
            return false;

        var playerKey = PlayerKey(farmer);
        var day = CurrentDay();
        pendingWakeDeaths[playerKey] = day;
        pendingHiddenHospitalDeaths[playerKey] = day;
        hiddenHospitalReleaseRequested.Remove(playerKey);

        if (Role != SleepAuthorityRole.Host || !Context.IsMainPlayer)
            return true;

        var attemptId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (
            !TryBuildSnapshot(
                farmer,
                attemptId,
                requireInBed: false,
                out var snapshot,
                out var reason,
                mode: SleepMode.TimeLimitPassOut
            )
        )
        {
            LogOnce(reason, LogLevel.Error);
            return false;
        }

        BeginHostContext(farmer, snapshot, attemptId, confirmed: true);
        return active.ContainsKey(playerKey);
    }

    private void ApplyHiddenHospitalWakeHealth(Farmer farmer)
    {
        var playerKey = PlayerKey(farmer);
        if (!pendingHiddenHospitalDeaths.ContainsKey(playerKey))
            return;

        StatusFeedbackHealthSuppression.SuppressNextHealthSound(
            farmer.UniqueMultiplayerID,
            farmer.health,
            0
        );
        farmer.health = 0;
        farmer.stamina = 0f;
        farmer.CanMove = false;
    }

    private void AdvanceHiddenHospitalWakeDeath()
    {
        if (Game1.player is not Farmer farmer)
            return;

        var playerKey = PlayerKey(farmer);
        if (
            hiddenHospitalReleaseRequested.Contains(playerKey)
            && (farmer.health > 0 || Game1.killScreen)
        )
        {
            pendingHiddenHospitalDeaths.Remove(playerKey);
            pendingWakeDeaths.Remove(playerKey);
            hiddenHospitalReleaseRequested.Remove(playerKey);
            return;
        }

        if (
            pendingHiddenHospitalDeaths.ContainsKey(playerKey)
            && farmer.health > 0
            && Context.IsWorldReady
            && !Game1.IsFading()
            && !Game1.eventUp
            && Game1.activeClickableMenu is null
            && !Game1.newDay
            && string.IsNullOrEmpty(Game1.messageAfterPause)
        )
        {
            ApplyHiddenHospitalWakeHealth(farmer);
            return;
        }

        if (
            hiddenHospitalReleaseRequested.Contains(playerKey)
            || !pendingHiddenHospitalDeaths.ContainsKey(playerKey)
            || !WakeDeathPresentationPolicy.ShouldReleaseHiddenHospital(
                markerPresent: true,
                farmer.health,
                Game1.killScreen,
                Context.IsWorldReady,
                Game1.IsFading(),
                Game1.eventUp,
                Game1.activeClickableMenu is not null,
                Game1.newDay,
                string.IsNullOrEmpty(Game1.messageAfterPause)
            )
        )
            return;

        hiddenHospitalReleaseRequested.Add(playerKey);
        HiddenHospitalWakeReady?.Invoke(farmer);
    }

    internal void BeforeNativeFarmerUpdate(Farmer farmer)
    {
        if (
            farmer is not null
            && pendingHiddenHospitalDeaths.ContainsKey(PlayerKey(farmer))
            && WakeDeathPresentationPolicy.ShouldHoldNativeDeath(
                WakeDeathPresentationMode.HiddenHospital,
                markerPresent: true,
                farmer.health,
                Game1.killScreen
            )
        )
        {
            farmer.CanMove = false;
            return;
        }

        if (
            farmer is null
            || !WakeDeathLoadGatePolicy.ShouldEarlyArm(
                Game1.hasLoadedGame,
                Context.IsWorldReady,
                farmer.IsLocalPlayer,
                farmer.health,
                Game1.killScreen,
                Game1.timeOfDay
            )
        )
            return;

        var playerKey = PlayerKey(farmer);
        if (
            !preWorldDeathLoadGateArmed
            || !string.Equals(preWorldDeathLoadGatePlayerKey, playerKey, StringComparison.Ordinal)
        )
        {
            preWorldDeathLoadGateArmed = true;
            preWorldDeathLoadGatePlayerKey = playerKey;
            LogRuntimeDiagnostic(
                $"sleep.runtime.wake-death-pre-world-gate armed player={playerKey} health={farmer.health}"
            );
        }
        farmer.CanMove = false;
    }

    private void AdvanceWakeDeathLoadGate()
    {
        if (!wakeDeathLoadReplayPending && !wakeDeathLoadGateArmed)
            return;

        if (!wakeDeathLoadGateArmed && !TryArmWakeDeathLoadGate())
            return;

        if (
            Role != SleepAuthorityRole.Host
            || !Context.IsMainPlayer
            || Game1.player is not Farmer farmer
        )
        {
            if (!wakeDeathLoadGateArmed)
                return;
            wakeDeathLoadReplayPending = false;
            wakeDeathLoadGateArmed = false;
            wakeDeathLoadGatePlayerKey = string.Empty;
            return;
        }

        var playerKey = PlayerKey(farmer);
        if (!wakeDeathLoadGateArmed)
        {
            return;
        }

        if (!string.Equals(playerKey, wakeDeathLoadGatePlayerKey, StringComparison.Ordinal))
        {
            wakeDeathLoadReplayPending = false;
            wakeDeathLoadGateArmed = false;
            wakeDeathLoadGatePlayerKey = string.Empty;
            return;
        }

        if (WakeDeathLoadGatePolicy.ShouldClear(wakeDeathLoadGateArmed, farmer.health, Game1.killScreen))
        {
            if (farmer.health > 0 && !Game1.killScreen)
                farmer.CanMove = true;
            wakeDeathLoadReplayPending = false;
            wakeDeathLoadGateArmed = false;
            wakeDeathLoadGatePlayerKey = string.Empty;
            return;
        }

        if (
            WakeDeathLoadGatePolicy.ShouldRelease(
                wakeDeathLoadGateArmed,
                farmer.health,
                Context.IsWorldReady,
                Game1.IsFading(),
                Game1.eventUp,
                Game1.activeClickableMenu is not null,
                Game1.killScreen
            )
        )
        {
            // onFadedBackInComplete normally opens this same gate. This fallback covers the
            // load path where an event/menu was still up when that callback ran.
            farmer.CanMove = true;
            wakeDeathLoadReplayPending = false;
            wakeDeathLoadGateArmed = false;
            wakeDeathLoadGatePlayerKey = string.Empty;
            LogRuntimeDiagnostic(
                $"sleep.runtime.wake-death-load-gate released player={playerKey} health={farmer.health}"
            );
        }
        else
        {
            // Another load-phase component may briefly reopen movement before the fade callback;
            // keep the owner-local gate closed until the stability predicate above is true.
            farmer.CanMove = false;
        }
    }

    private bool TryArmWakeDeathLoadGate()
    {
        if (
            wakeDeathLoadGateArmed
            || !wakeDeathLoadReplayPending
            || Role != SleepAuthorityRole.Host
            || !Context.IsMainPlayer
            || Game1.player is not Farmer farmer
        )
            return wakeDeathLoadGateArmed;

        var playerKey = PlayerKey(farmer);
        if (
            !WakeDeathLoadGatePolicy.ShouldArm(
                pendingWakeDeaths.ContainsKey(playerKey),
                farmer.health,
                Game1.killScreen
            )
        )
        {
            // The player is available and this load is not an unresolved wake death. Do not let
            // the load-only flag leak into an ordinary morning or later session.
            wakeDeathLoadReplayPending = false;
            return false;
        }

            wakeDeathLoadGateArmed = true;
            wakeDeathLoadGatePlayerKey = playerKey;
            preWorldDeathLoadGateArmed = false;
            preWorldDeathLoadGatePlayerKey = string.Empty;
            farmer.CanMove = false;
        if (
            !pendingHiddenHospitalDeaths.ContainsKey(playerKey)
            && !string.Equals(wakeDeathCauseMarkedPlayerKey, playerKey, StringComparison.Ordinal)
        )
        {
            markHungerDeath?.Invoke(farmer);
            wakeDeathCauseMarkedPlayerKey = playerKey;
        }
        LogRuntimeDiagnostic(
            $"sleep.runtime.wake-death-load-gate armed player={playerKey} health={farmer.health}"
        );
        return true;
    }

    private void ReleasePreWorldDeathLoadGate()
    {
        if (!preWorldDeathLoadGateArmed)
            return;

        if (
            Game1.player is Farmer farmer
            && string.Equals(
                preWorldDeathLoadGatePlayerKey,
                PlayerKey(farmer),
                StringComparison.Ordinal
            )
            && !Game1.killScreen
        )
            farmer.CanMove = true;

        preWorldDeathLoadGateArmed = false;
        preWorldDeathLoadGatePlayerKey = string.Empty;
    }

    private static void CloseLocalSleepMenu(Farmer farmer)
    {
        if (farmer.IsLocalPlayer && Game1.activeClickableMenu is ReadyCheckDialog ready)
        {
            ready.closeDialog(farmer);
            farmer.timeWentToBed.Value = 0;
            farmer.CanMove = true;
        }
    }

    internal static int HealthForGame(double health) => double.IsFinite(health)
        ? (int)Math.Clamp(Math.Floor(health + 1e-9), 0d, int.MaxValue) : 0;

    private static Farmer? ResolvePlayer(string key) =>
        long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? Game1.GetPlayer(id, onlyOnline: true) : null;

    private static Farmer? ResolvePlayer(string key, bool onlyOnline)
    {
        return long.TryParse(
            key,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var id
        )
            ? Game1.GetPlayer(id, onlyOnline)
            : null;
    }

    private void PrunePendingWakeDeaths()
    {
        var currentDay = CurrentDay();
        var remove = new List<string>();
        foreach (var pair in pendingWakeDeaths)
        {
            if (pair.Value < currentDay - SleepWakeEffectsPolicy.RetentionDays)
            {
                remove.Add(pair.Key);
                continue;
            }

            var farmer = ResolvePlayer(pair.Key, onlyOnline: false);
            // SaveLoaded can run before Game1 has reattached the farmer collection. Retain a
            // valid, recent marker through that transient null; the next tick can then arm the
            // load gate instead of silently losing the death replay context.
            if (
                farmer is not null
                && farmer.health > 0
                && !pendingHiddenHospitalDeaths.ContainsKey(pair.Key)
            )
                remove.Add(pair.Key);
        }

        foreach (var key in remove)
        {
            pendingWakeDeaths.Remove(key);
            pendingHiddenHospitalDeaths.Remove(key);
        }

        var normalized = SleepWakeEffectsPolicy.Normalize(
            pendingWakeDeaths.Keys,
            pendingWakeDeaths,
            currentDay
        );
        pendingWakeDeaths.Clear();
        foreach (var pair in normalized)
            pendingWakeDeaths[pair.Key] = pair.Value;

        var hiddenNormalized = SleepWakeEffectsPolicy.Normalize(
            pendingHiddenHospitalDeaths.Keys,
            pendingHiddenHospitalDeaths,
            currentDay
        );
        pendingHiddenHospitalDeaths.Clear();
        foreach (var pair in hiddenNormalized)
            pendingHiddenHospitalDeaths[pair.Key] = pair.Value;
    }

    private IReadOnlyDictionary<string, int> NaturalWakeDeathMarkers()
    {
        var natural = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in pendingWakeDeaths)
        {
            if (!pendingHiddenHospitalDeaths.ContainsKey(pair.Key))
                natural[pair.Key] = pair.Value;
        }
        return natural;
    }

    private static int CurrentDay() => Math.Max(0, Game1.Date.TotalDays);

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearRuntimeOnly();
        transportState.ClearSession();
    }

    private void LogOnce(string reason, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedReasons.Add(reason))
            return;
        monitor.Log($"Sleep lifecycle fail-closed ({reason}).", level);
    }

    private void LogRuntimeDiagnostic(string message)
    {
        monitor.Log(message, LogLevel.Debug);
    }

    private static string PlayerKey(Farmer farmer) =>
        farmer.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);

    private static long ResolveNextSix(long current)
    {
        var day = current / 1440;
        var hour = Game1.timeOfDay / 100 % 24;
        return hour < 6 ? day * 1440 + 360 : (day + 1) * 1440 + 360;
    }

    private sealed class ActiveSleep
    {
        internal ActiveSleep(
            string playerKey,
            string attemptId,
            SleepSnapshot snapshot,
            SleepContext context,
            SleepProgress progress
        )
        {
            PlayerKey = playerKey;
            AttemptId = attemptId;
            Snapshot = snapshot;
            Context = context;
            Progress = progress;
        }

        internal string PlayerKey { get; }

        internal string AttemptId { get; }

        internal SleepSnapshot Snapshot { get; }

        internal SleepContext Context { get; set; }

        internal SleepProgress Progress { get; set; }

        internal bool Confirmed { get; set; }

        internal bool HungerDeath { get; set; }

        internal long BlackoutStartMinute { get; set; } = -1;

        internal long Revision { get; set; }
    }
}
