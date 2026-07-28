#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Config;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.PassOut;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity;

internal sealed class Sanity : IStat
{
    private readonly SanityPersistenceStore persistenceStore = new();
    private readonly SanityChangeService changeService;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly IMonitor monitor;
    private readonly string modId;

    private readonly List<INonTimeRelatedBehavior> nonTimeRelatedBehaviors;

    private readonly List<ITimeRelatedBehavior> timeRelatedBehaviors =
        new()
        {
            new NearMonster(),
            new Night(),
            new Wearing(),
            new NearNpc(),
            new MineShaft(),
        };

    private SanityPersistenceResult? persistenceSession;
    private SmapiSanityMultiplayerCoordinator multiplayer = null!;
    private string loadedSaveIdentity = string.Empty;
    private string loggedPersistenceReason = string.Empty;
    private string loggedRuntimeReason = string.Empty;
    private readonly HashSet<string> loggedBehaviorFailures =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedTierDiagnostics =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedShadowBudgetDiagnostics =
        new(StringComparer.Ordinal);

    internal Sanity(
        IMonitor monitor,
        string modId,
        TypedConfigResolver? configResolver,
        PassOutReasonLedger passOutReasonLedger
    )
    {
        this.monitor = monitor;
        this.modId = modId;
        changeService = new SanityChangeService(
            new DefaultSanityMaximumProvider(),
            configResolver is null
                ? new UnavailableSanityMonsterIntensityProvider(
                    "config.runtime-unavailable"
                )
                : new TypedConfigSanityMonsterIntensityProvider(configResolver)
        );
        lifecycle = new SanitySystemLifecycleCoordinator(changeService);
        nonTimeRelatedBehaviors =
            new()
            {
                new EatFood(),
                new Sleep(passOutReasonLedger, lifecycle, reason =>
                    monitor.Log(
                        $"Sanity behavior failed closed (behavior=Sleep, source=Sleep, reason={reason}).",
                        LogLevel.Error
                    )
                ),
            };
    }

    internal SanitySystemLifecycleCoordinator Lifecycle => lifecycle;

    public void Init(IModHelper helper, ITimeAPI timeApi)
    {
        SanityExtensions.Bind(changeService);
        changeService.TierStateDiagnosticRaised += LogTierDiagnosticOnce;
        changeService.ShadowBudgetDiagnosticRaised +=
            LogShadowBudgetDiagnosticOnce;

        foreach (var behavior in nonTimeRelatedBehaviors)
            behavior.Init(helper);
        foreach (var behavior in timeRelatedBehaviors)
            behavior.Init(helper);

        timeApi.OnUpdate.Add(Update);
        timeApi.OnSync.Add(Sync);

        multiplayer = new SmapiSanityMultiplayerCoordinator(
            helper,
            monitor,
            modId,
            changeService
        );
        multiplayer.Initialize();

        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            ClearSession(SanitySessionBoundary.ReturnedToTitle);
        helper.Events.Player.Warped += OnWarped;
        helper.Events.GameLoop.DayStarted += (_, _) =>
            lifecycle.HandleWorldBoundary(SanityWorldBoundary.DayStarted);

        // 先完成所有 behavior、tier、budget、cleanup、multiplayer 与显示所需状态注册，
        // ModEntry 才会应用实际 EnableSanitySystem 值。
        lifecycle.Initialize();
        monitor.Log(
            $"Sanity 4.0: 旧生成停用，新幻觉尚未实现 ({LegacySanitySpawnRetirement.SchedulingStatus}; {LegacySanitySpawnRetirement.ReplacementStatus}; residual-cleanup={LegacySanitySpawnRetirement.ResidualCleanupReason}).",
            LogLevel.Debug
        );
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (e.IsLocalPlayer && Context.IsWorldReady)
            lifecycle.HandleWorldBoundary(SanityWorldBoundary.Warp);
    }

    private void Update(long time)
    {
        foreach (var behavior in timeRelatedBehaviors)
        {
            try
            {
                behavior.Update(time);
            }
            catch (Exception ex)
            {
                LogBehaviorFailureOnce("update", behavior, ex);
            }
        }
    }

    private void Sync(long time, long delta)
    {
        foreach (var behavior in timeRelatedBehaviors)
        {
            try
            {
                behavior.Sync(time, delta);
            }
            catch (Exception ex)
            {
                LogBehaviorFailureOnce("sync", behavior, ex);
            }
        }
    }

    private void Load(IModHelper helper)
    {
        loggedPersistenceReason = string.Empty;
        loggedRuntimeReason = string.Empty;
        loggedBehaviorFailures.Clear();
        loggedTierDiagnostics.Clear();
        loggedShadowBudgetDiagnostics.Clear();

        if (
            !TryGetPlayerKey(Game1.MasterPlayer, out var masterPlayerKey)
            || !TryGetPlayerKey(Game1.player, out var localPlayerKey)
            || !TryGetSaveIdentity(masterPlayerKey, out var saveIdentity)
        )
        {
            FailRuntimeSession("save-or-player-identity-is-unavailable");
            return;
        }

        // Split-screen 的后续 screen 会再次进入同一共享 mod 实例；只补玩家，绝不重读磁盘或换 session。
        if (
            changeService.Role == SanityAuthorityRole.Host
            && string.Equals(
                loadedSaveIdentity,
                saveIdentity,
                StringComparison.Ordinal
            )
        )
        {
            EnsureOnlineHostPlayers();
            multiplayer.OnSessionStarted();
            return;
        }

        if (
            changeService.Role == SanityAuthorityRole.Client
            && string.Equals(
                loadedSaveIdentity,
                saveIdentity,
                StringComparison.Ordinal
            )
        )
        {
            multiplayer.OnSessionStarted();
            return;
        }

        ClearSession();
        loadedSaveIdentity = saveIdentity;

        if (Context.IsMainPlayer)
        {
            persistenceSession = persistenceStore.Load(
                new SmapiSanitySaveDataAccess(helper.Data),
                masterPlayerKey,
                SanityExtensions.DefaultMaxSanity
            );
            LogPersistenceErrorOnce(persistenceSession);

            if (
                !changeService.BeginHostSession(
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    persistenceSession,
                    masterPlayerKey,
                    out var reason
                )
            )
            {
                FailRuntimeSession(reason);
                return;
            }

            EnsureOnlineHostPlayers();
            foreach (var behavior in timeRelatedBehaviors)
                behavior.Load(helper);
        }
        else
        {
            // 客户端不读主 Sanity save-data，也不建立可保存的伪 session。
            persistenceSession = SanityPersistenceResult.ReadOnlyDefault(
                SanityPersistenceStatus.ReadOnlyClient,
                "save-data-is-main-player-only",
                SanityExtensions.DefaultMaxSanity
            );
            if (!changeService.BeginClientSession(localPlayerKey, out var reason))
            {
                FailRuntimeSession(reason);
                return;
            }
        }

        multiplayer.OnSessionStarted();
    }

    private void Save(IModHelper helper)
    {
        // Saving 只由主 screen 执行。客户端与 split-screen farmhand 不触碰任何 save-data key。
        if (
            !Context.IsMainPlayer
            || changeService.Role != SanityAuthorityRole.Host
        )
        {
            return;
        }

        if (!TryGetPlayerKey(Game1.MasterPlayer, out var masterPlayerKey))
        {
            FailRuntimeSession("master-player-id-is-unavailable");
            return;
        }

        EnsureOnlineHostPlayers();
        if (persistenceSession is null)
        {
            persistenceSession = SanityPersistenceResult.ReadOnlyDefault(
                SanityPersistenceStatus.ReadOnlyError,
                "sanity-session-was-not-loaded",
                SanityExtensions.DefaultMaxSanity
            );
        }
        else
        {
            persistenceSession = persistenceStore.SavePlayers(
                new SmapiSanitySaveDataAccess(helper.Data),
                persistenceSession,
                changeService.CaptureSaveInputs(),
                masterPlayerKey
            );
            changeService.UpdatePersistenceBase(persistenceSession);
        }

        LogPersistenceErrorOnce(persistenceSession);
        foreach (var behavior in timeRelatedBehaviors)
            behavior.Save(helper);
    }

    private void EnsureOnlineHostPlayers()
    {
        if (changeService.Role != SanityAuthorityRole.Host)
            return;

        foreach (var player in Game1.getOnlineFarmers())
        {
            if (!TryGetPlayerKey(player, out var playerKey))
            {
                LogRuntimeErrorOnce("online-player-id-is-unavailable");
                continue;
            }
            if (
                !changeService.TryEnsureHostPlayer(
                    playerKey,
                    out _,
                    out var reason
                )
            )
            {
                LogRuntimeErrorOnce(reason);
            }
        }
    }

    private void ClearSession(
        SanitySessionBoundary boundary = SanitySessionBoundary.WorldCleanup
    )
    {
        persistenceSession = null;
        loadedSaveIdentity = string.Empty;
        loggedPersistenceReason = string.Empty;
        loggedRuntimeReason = string.Empty;
        loggedBehaviorFailures.Clear();
        loggedTierDiagnostics.Clear();
        loggedShadowBudgetDiagnostics.Clear();
        lifecycle.ClearSession(boundary);
        if (multiplayer is not null)
            multiplayer.ResetSessionDiagnostics();
    }

    private void FailRuntimeSession(string reason)
    {
        loadedSaveIdentity = string.Empty;
        lifecycle.ClearSession();
        persistenceSession = SanityPersistenceResult.ReadOnlyDefault(
            SanityPersistenceStatus.ReadOnlyError,
            reason,
            SanityExtensions.DefaultMaxSanity
        );
        LogRuntimeErrorOnce(reason);
    }

    private static bool TryGetPlayerKey(Farmer? player, out string playerKey)
    {
        if (player is null)
        {
            playerKey = string.Empty;
            return false;
        }

        playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        return SanityPlayerKey.IsCanonical(playerKey);
    }

    private static bool TryGetSaveIdentity(
        string masterPlayerKey,
        out string saveIdentity
    )
    {
        if (!SanityPlayerKey.IsCanonical(masterPlayerKey))
        {
            saveIdentity = string.Empty;
            return false;
        }

        saveIdentity = string.Concat(
            Game1.uniqueIDForThisGame.ToString(CultureInfo.InvariantCulture),
            ":",
            masterPlayerKey
        );
        return true;
    }

    private void LogPersistenceErrorOnce(SanityPersistenceResult result)
    {
        if (
            result.Status != SanityPersistenceStatus.ReadOnlyError
            || string.Equals(
                loggedPersistenceReason,
                result.Reason,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        loggedPersistenceReason = result.Reason;
        monitor.Log(
            $"Sanity persistence is read-only for this save session ({result.Reason}). The original Sanity save entries were not overwritten.",
            LogLevel.Error
        );
    }

    private void LogRuntimeErrorOnce(string reason)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || string.Equals(loggedRuntimeReason, reason, StringComparison.Ordinal)
        )
        {
            return;
        }

        loggedRuntimeReason = reason;
        monitor.Log($"Sanity runtime session is fail-closed ({reason}).", LogLevel.Error);
    }

    private void LogBehaviorFailureOnce(
        string phase,
        ITimeRelatedBehavior behavior,
        Exception exception
    )
    {
        var behaviorName = behavior.GetType().Name;
        var source = behaviorName switch
        {
            nameof(NearMonster) => nameof(SanityChangeSource.Monster),
            nameof(Night) => nameof(SanityChangeSource.Night),
            nameof(Wearing) => nameof(SanityChangeSource.Equipment),
            nameof(NearNpc) => "Npc/Junimo",
            nameof(MineShaft) => nameof(SanityChangeSource.Mine),
            _ when behaviorName.StartsWith("Spawn", StringComparison.Ordinal) =>
                "LegacySpawn",
            _ => nameof(SanityChangeSource.Unknown),
        };
        var key = string.Concat(
            phase,
            ":",
            behaviorName,
            ":",
            exception.GetType().FullName
        );
        if (!loggedBehaviorFailures.Add(key))
            return;

        monitor.Log(
            $"Sanity behavior callback failed and was not retried (behavior={behaviorName}, source={source}, phase={phase}, exception={exception.GetType().Name}). Other behaviors continue.",
            LogLevel.Error
        );
    }

    private void LogTierDiagnosticOnce(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedTierDiagnostics.Add(reason))
            return;

        monitor.Log(
            $"Sanity tier snapshot was rejected and the affected owner was fail-closed ({reason}).",
            LogLevel.Warn
        );
    }

    private void LogShadowBudgetDiagnosticOnce(string reason)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || !loggedShadowBudgetDiagnostics.Add(reason)
        )
        {
            return;
        }

        monitor.Log(
            $"Sanity shadow budget was fail-closed ({reason}). No spawn permit was issued.",
            LogLevel.Warn
        );
    }
}

public static class SanityExtensions
{
    internal const double DefaultMaxSanity = SanitySaveData.CurrentDefaultMaxSanity;
    private static SanityChangeService? changeService;

    internal static void Bind(SanityChangeService service)
    {
        changeService = service;
    }

    public static double GetMaxSanity(this Farmer farmer)
    {
        return TryGetPlayerKey(farmer, out var playerKey)
            ? changeService?.GetMaximum(playerKey) ?? DefaultMaxSanity
            : DefaultMaxSanity;
    }

    public static double GetSanity(this Farmer farmer)
    {
        return TryGetPlayerKey(farmer, out var playerKey)
            ? changeService?.GetCurrent(playerKey) ?? DefaultMaxSanity
            : DefaultMaxSanity;
    }

    public static void SetSanity(this Farmer farmer, double value)
    {
        _ = farmer.SetSanity(value, SanityChangeSource.Administration);
    }

    internal static SanityChangeResult SetSanity(
        this Farmer farmer,
        double value,
        SanityChangeSource source
    )
    {
        if (changeService is null)
            return SanityChangeResult.Rejected("sanity-service-is-not-bound", source);
        if (!TryGetPlayerKey(farmer, out var playerKey))
            return SanityChangeResult.Rejected("player-key-is-unavailable", source);
        return changeService.Set(playerKey, value, source);
    }

    internal static SanityChangeResult ChangeSanity(
        this Farmer farmer,
        double delta,
        SanityChangeSource source,
        string interactionId = ""
    )
    {
        if (changeService is null)
            return SanityChangeResult.Rejected("sanity-service-is-not-bound");
        if (!TryGetPlayerKey(farmer, out var playerKey))
            return SanityChangeResult.Rejected("player-key-is-unavailable");
        return changeService.Change(playerKey, delta, source, interactionId);
    }

    private static bool TryGetPlayerKey(Farmer? farmer, out string playerKey)
    {
        if (farmer is null)
        {
            playerKey = string.Empty;
            return false;
        }

        playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            farmer.UniqueMultiplayerID
        );
        return SanityPlayerKey.IsCanonical(playerKey);
    }
}
