using System.Collections.Generic;
using DontStarve.Buff.Buffs;
using DontStarve.Interface;
using StardewModdingAPI;

namespace DontStarve.Buff;

internal static class BuffManager
{
    // Non-time-related buffs collection
    private static readonly List<INonTimeRelatedBuff> nonTimeRelatedBuffs =
        new List<INonTimeRelatedBuff> { new ElectricityAppendBuff() };

    // Time-related buffs collection
    private static readonly List<ITimeRelatedBuff> timeRelatedBuffs = new List<ITimeRelatedBuff>
    {
        new HealthRestoreBuff(),
        new StaminaRestoreBuff(),
        new SanityRestoreBuff(),
    };

    /// <summary>
    /// Initializes all buffs
    /// </summary>
    internal static void Initialize(IModHelper helper)
    {
        // Time-related buffs
        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            var timeApi = helper.ModRegistry.GetApi<ITimeAPI>("Yurin.MinuteTimeHelper");
            if (timeApi == null)
                return;
            timeApi.OnUpdate.Add(Update);
            timeApi.OnSync.Add(Sync);
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);

        // Non-time-related buffs
        foreach (var b in nonTimeRelatedBuffs)
            b.Init(helper);
    }

    private static void Update(ulong time)
    {
        foreach (var b in timeRelatedBuffs)
            b.Update((long)time);
    }

    private static void Sync(ulong time, long delta)
    {
        foreach (var b in timeRelatedBuffs)
            b.Sync((long)time, delta);
    }

    private static void Load(IModHelper helper)
    {
        foreach (var b in timeRelatedBuffs)
            b.Load(helper);
    }

    private static void Save(IModHelper helper)
    {
        foreach (var b in timeRelatedBuffs)
            b.Save(helper);
    }
}
