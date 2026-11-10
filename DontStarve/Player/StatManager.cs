using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Config;
using DontStarve.Display;
using DontStarve.Interface;
using DontStarve.Player.Death;
using DontStarve.Player.Stats;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player;

internal static class StatManager
{
    internal static SanitySystemLifecycleCoordinator Initialize(
        IModHelper helper,
        ITimeAPI timeApi,
        IMonitor monitor,
        string modId,
        TaggedHudMessageService hudMessages,
        TypedConfigResolver configResolver,
        PassOutReasonLedger passOutReasonLedger,
        PlayerDeathCauseLedger playerDeathCauseLedger,
        Func<HungerDamageMode> hungerDamageModeProvider,
        out HungerDebugController hungerDebugController,
        out SurvivalStateTimeOwner<Farmer> survivalTime
    )
    {
        var sanity = new Sanity(
            monitor,
            modId,
            configResolver,
            passOutReasonLedger
        );
        var hunger = new Hunger(
            monitor,
            modId,
            hudMessages,
            playerDeathCauseLedger,
            hungerDamageModeProvider
        );
        hungerDebugController = hunger.DebugController;
        survivalTime = new SurvivalStateTimeOwner<Farmer>(
            timeApi,
            Game1.getOnlineFarmers,
            player => player.UniqueMultiplayerID == 0
                ? string.Empty
                : player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture),
            message => monitor.Log(message, LogLevel.Error)
        );
        var stats = new List<IStat>
        {
            hunger,
            sanity,
        };
        foreach (var s in stats)
            s.Init(helper, survivalTime);

        return sanity.Lifecycle;
    }
}
