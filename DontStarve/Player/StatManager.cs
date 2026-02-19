using System.Collections.Generic;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using StardewModdingAPI;

namespace DontStarve.Player;

internal static class StatManager
{
    private static readonly List<IStat> stats = new List<IStat> { new Hunger(), new Sanity() };

    internal static void Initialize(IModHelper helper)
    {
        foreach (var s in stats)
            s.Init(helper);
    }
}
