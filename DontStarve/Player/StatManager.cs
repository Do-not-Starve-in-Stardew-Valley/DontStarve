using System;
using System.Collections.Generic;
using DontStarve.Config;
using DontStarve.Display;
using DontStarve.Interface;
using DontStarve.Player.Death;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;

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
        out HungerDebugController hungerDebugController
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
        var stats = new List<IStat>
        {
            hunger,
            sanity,
        };
        foreach (var s in stats)
            s.Init(helper, timeApi);

        return sanity.Lifecycle;
    }
}
