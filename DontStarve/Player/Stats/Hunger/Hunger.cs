#nullable enable

using System;
using System.Globalization;
using DontStarve.Display;
using DontStarve.Interface;
using DontStarve.Player.Death;
using DontStarve.Player.Stats.Food;
using DontStarve.Player.Stats.Hunger.HungerBehaviors;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

internal sealed class Hunger : IStat
{
    private const string SAVE_KEY = "DontStarve.Hunger";

    private readonly HungerChangeService changeService = new();
    private readonly HungerPersistenceStore persistenceStore = new();
    private readonly HungerEquipmentRuntime equipmentRuntime = new();
    private readonly PlayerDeathCauseLedger deathCauseLedger;
    private readonly HungerCycle hungerCycle;
    private readonly EatFood eatFood;
    private readonly HungerDebugController debugController;
    private readonly TaggedHudMessageService hudMessages;
    private readonly string modId;
    private readonly IMonitor monitor;
    private HungerDeathDialogueService? deathDialogue;
    private SmapiHungerMultiplayerCoordinator multiplayer = null!;
    private HungerPersistenceResult? persistenceSession;
    private string loggedPersistenceReason = string.Empty;
    private string loadedSaveIdentity = string.Empty;

    internal Hunger(
        IMonitor monitor,
        string modId,
        TaggedHudMessageService hudMessages,
        PlayerDeathCauseLedger deathCauseLedger,
        Func<HungerDamageMode>? damageModeProvider = null
    )
    {
        this.monitor = monitor;
        this.modId = modId;
        this.hudMessages = hudMessages ?? throw new ArgumentNullException(nameof(hudMessages));
        this.deathCauseLedger = deathCauseLedger
            ?? throw new ArgumentNullException(nameof(deathCauseLedger));
        hungerCycle = new HungerCycle(
            changeService,
            monitor,
            equipmentRuntime,
            damageModeProvider,
            deathCauseLedger
        );
        eatFood = new EatFood(changeService);
        debugController = new HungerDebugController(
            changeService,
            () => HungerExtensions.IsEnabled
        );
    }

    internal HungerDebugController DebugController => debugController;

    public void Init(IModHelper helper, ITimeAPI timeApi)
    {
        HungerExtensions.Bind(changeService);
        HungerExtensions.BindSleepBoundary(hungerCycle.ResumeAfterSleep);
        equipmentRuntime.Load(helper, monitor);
        deathDialogue = new HungerDeathDialogueService(
            helper,
            monitor,
            modId,
            deathCauseLedger
        );
        hungerCycle.SetLowWarningHandler(
            player =>
            {
                if (
                    Game1.player is null
                    || Game1.player.UniqueMultiplayerID != player.UniqueMultiplayerID
                )
                {
                    return;
                }

                hudMessages.AddCornerTextbox(
                    helper.Translation.Get("hunger-low.warning").ToString(),
                    HudMessageGroupTags.Default
                );
            }
        );
        eatFood.Init(helper);

        multiplayer = new SmapiHungerMultiplayerCoordinator(
            helper,
            monitor,
            modId,
            changeService
        );
        multiplayer.Initialize();

        timeApi.OnUpdate.Add(Update);
        timeApi.OnSync.Add(Sync);
        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearSession();
    }

    private void Update(long time)
    {
        hungerCycle.Update(time);
    }

    private void Sync(long time, long delta)
    {
        hungerCycle.Sync(time, delta);
    }

    private void Load(IModHelper helper)
    {
        if (
            !TryGetPlayerKey(Game1.MasterPlayer, out var masterPlayerKey)
            || !TryGetPlayerKey(Game1.player, out var localPlayerKey)
            || !TryGetSaveIdentity(masterPlayerKey, out var saveIdentity)
        )
        {
            ClearSession();
            return;
        }

        // Split-screen 后续 screen 进入同一个共享 mod 实例时只补在线玩家，不重复读档。
        if (
            changeService.Role == HungerAuthorityRole.Host
            && string.Equals(loadedSaveIdentity, saveIdentity, StringComparison.Ordinal)
        )
        {
            EnsureOnlineHostPlayers();
            multiplayer.OnSessionStarted();
            return;
        }
        if (
            changeService.Role == HungerAuthorityRole.Client
            && string.Equals(loadedSaveIdentity, saveIdentity, StringComparison.Ordinal)
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
                new SmapiHungerSaveDataAccess(helper.Data),
                masterPlayerKey,
                HungerRules.DefaultMaximum
            );
            LogPersistenceFailure(helper, persistenceSession);
            if (
                !changeService.BeginHostSession(
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    persistenceSession,
                    masterPlayerKey,
                    out _
                )
            )
            {
                ClearSession();
                return;
            }

            EnsureOnlineHostPlayers();
            hungerCycle.Load(helper);
        }
        else
        {
            // 客机不读取主机存档，只从主机快照建立本地只读影子。
            if (!changeService.BeginClientSession(localPlayerKey, out _))
            {
                ClearSession();
                return;
            }
        }

        multiplayer.OnSessionStarted();
    }

    private void Save(IModHelper helper)
    {
        if (!Context.IsMainPlayer || changeService.Role != HungerAuthorityRole.Host)
            return;

        EnsureOnlineHostPlayers();
        if (persistenceSession is null)
        {
            monitor.Log(
                "Hunger save skipped because the persistence session was not loaded.",
                LogLevel.Error
            );
        }
        else
        {
            persistenceSession = persistenceStore.Save(
                new SmapiHungerSaveDataAccess(helper.Data),
                persistenceSession,
                changeService.CaptureSaveData()
            );
            changeService.UpdatePersistenceBase(persistenceSession);
            LogPersistenceFailure(helper, persistenceSession);
        }
        hungerCycle.Save(helper);
    }

    private void EnsureOnlineHostPlayers()
    {
        if (changeService.Role != HungerAuthorityRole.Host)
            return;

        foreach (var player in Game1.getOnlineFarmers())
        {
            var playerKey = FromPlayerId(player.UniqueMultiplayerID);
            changeService.TryEnsureHostPlayer(playerKey, out _, out _);
        }
    }

    private void ClearSession()
    {
        loadedSaveIdentity = string.Empty;
        persistenceSession = null;
        loggedPersistenceReason = string.Empty;
        changeService.ClearSession();
        hungerCycle.ResetRuntime();
        deathCauseLedger.Clear();
        deathDialogue?.ClearRuntime();
        multiplayer?.ResetSessionDiagnostics();
    }

    private static bool TryGetPlayerKey(Farmer? player, out string playerKey)
    {
        if (player is null)
        {
            playerKey = string.Empty;
            return false;
        }

        playerKey = FromPlayerId(player.UniqueMultiplayerID);
        return HungerProtocol.IsValidPlayerKey(playerKey);
    }

    private static bool TryGetSaveIdentity(string masterPlayerKey, out string saveIdentity)
    {
        if (!HungerProtocol.IsValidPlayerKey(masterPlayerKey))
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

    private static string FromPlayerId(long playerId) =>
        playerId.ToString(CultureInfo.InvariantCulture);

    private void LogPersistenceFailure(
        IModHelper helper,
        HungerPersistenceResult persistence
    )
    {
        if (
            persistence.Capability == HungerPersistenceCapability.ReadOnly
            && !string.Equals(loggedPersistenceReason, persistence.Reason, StringComparison.Ordinal)
        )
        {
            loggedPersistenceReason = persistence.Reason;
            monitor.Log(
                $"Hunger persistence entered read-only mode: {persistence.Reason}",
                LogLevel.Error
            );
        }
    }
}

public static class HungerExtensions
{
    internal const double DefaultMaxHunger = 150d;
    private static HungerChangeService? changeService;
    private static Action<Farmer>? sleepBoundary;
    private static bool systemEnabled;
    private static Func<string, bool>? sleepContextResolver;

    internal static bool IsEnabled => systemEnabled;

    internal static bool IsDebugLocked => changeService?.IsDebugLocked == true;

    internal static void Bind(HungerChangeService service)
    {
        changeService = service;
    }

    internal static void BindSleepBoundary(Action<Farmer>? callback) => sleepBoundary = callback;

    internal static void ResumeAfterSleep(Farmer farmer) => sleepBoundary?.Invoke(farmer);

    internal static void SetEnabled(bool enabled)
    {
        systemEnabled = enabled;
    }

    internal static void BindSleepContext(Func<string, bool>? resolver)
    {
        sleepContextResolver = resolver;
    }

    internal static bool IsPlayerInSleepContext(string playerKey)
    {
        try
        {
            return sleepContextResolver?.Invoke(playerKey) == true;
        }
        catch
        {
            return false;
        }
    }

    internal static void ResetRuntime()
    {
        changeService?.ClearSession();
    }

    public static double GetMaxHunger(this Farmer farmer)
    {
        return TryGetPlayerKey(farmer, out var playerKey)
            ? changeService?.GetMaximum(playerKey) ?? DefaultMaxHunger
            : DefaultMaxHunger;
    }

    public static double GetHunger(this Farmer farmer)
    {
        return TryGetPlayerKey(farmer, out var playerKey)
            ? changeService?.GetCurrent(playerKey) ?? DefaultMaxHunger
            : DefaultMaxHunger;
    }

    public static void SetHunger(this Farmer farmer, double value)
    {
        if (
            !IsEnabled
            || changeService is null
            || !TryGetPlayerKey(farmer, out var playerKey)
        )
            return;

        // Set 是主机权威操作。客户端不能用本地写入制造只在本机存在的 Hunger 值。
        changeService.SetHostValue(playerKey, value);
    }

    /// <summary>
    /// Restores Hunger at the shared confirmed true-death boundary used by Sanity. The change
    /// still goes through the host state service so revision and multiplayer snapshot behavior
    /// remain identical to food and ordinary Hunger updates.
    /// </summary>
    internal static HungerChangeResult ApplyHealthDeathRecovery(Farmer farmer)
    {
        if (!IsEnabled)
            return HungerChangeResult.Rejected("hunger-death-recovery-disabled");
        if (changeService is null || !TryGetPlayerKey(farmer, out var playerKey))
            return HungerChangeResult.Rejected("hunger-death-recovery-is-unavailable");

        var reviveValue = HungerRules.CalculateReviveValue(farmer.GetMaxHunger());
        return changeService.SetHostValue(playerKey, reviveValue);
    }

    internal static HungerChangeResult ApplyFood(Farmer farmer, Item food)
    {
        if (
            !IsEnabled
            || changeService is null
            || !TryGetPlayerKey(farmer, out var playerKey)
        )
            return HungerChangeResult.Rejected("hunger-food-change-is-unavailable");

        if (
            changeService.Role == HungerAuthorityRole.Client
            && changeService.HostFoodCompletionSupported
        )
            return HungerChangeResult.Rejected("host-food-completion-is-authoritative");

        if (StarfruitFoodRules.IsStarfruit(food.ItemId))
        {
            var delta = StarfruitFoodRules.GetFillDelta(
                farmer.GetHunger(),
                farmer.GetMaxHunger()
            );
            return changeService.Role == HungerAuthorityRole.Host
                ? changeService.ApplyFood(playerKey, delta)
                : changeService.QueueFood(playerKey, food.ItemId);
        }

        if (!FoodRuleRuntime.TryGetHunger(food, out var hunger))
            return HungerChangeResult.Rejected("food-hunger-rule-is-missing");

        return changeService.Role == HungerAuthorityRole.Host
            ? changeService.ApplyFood(playerKey, hunger)
            : changeService.QueueFood(playerKey, food.ItemId);
    }

    private static bool TryGetPlayerKey(Farmer? farmer, out string playerKey)
    {
        if (farmer is null)
        {
            playerKey = string.Empty;
            return false;
        }

        playerKey = farmer.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);
        return HungerProtocol.IsValidPlayerKey(playerKey);
    }
}
