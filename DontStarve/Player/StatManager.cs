using System.Collections.Generic;
using DontStarve.Config;
using DontStarve.Interface;
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
        TypedConfigResolver configResolver,
        PassOutReasonLedger passOutReasonLedger
    )
    {
        var sanity = new Sanity(
            monitor,
            modId,
            configResolver,
            passOutReasonLedger
        );
        var stats = new List<IStat>
        {
            new Hunger(),
            sanity,
        };
        foreach (var s in stats)
            s.Init(helper, timeApi);

        return sanity.Lifecycle;
    }
}
