using System;
using System.Collections.Generic;
using DontStarve.Buff.Buffs;
using DontStarve.Integration;
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
            var timeApi = helper.ModRegistry.GetApi<TimeApi>("Yurin.MinuteTimeHelper");
            if (timeApi == null)
                return;
            timeApi.OnUpdate.Add(Update);
            timeApi.OnSync.Add(Sync);
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);

        // Non-time-related buffs
        nonTimeRelatedBuffs.ForEach(b => b.Init(helper));
    }

    private static void Update(ulong time) => timeRelatedBuffs.ForEach(b => b.Update((long)time));

    private static void Sync(ulong time, long delta) =>
        timeRelatedBuffs.ForEach(b => b.Sync((long)time, delta));

    private static void Load(IModHelper helper) => timeRelatedBuffs.ForEach(b => b.Load(helper));

    private static void Save(IModHelper helper) => timeRelatedBuffs.ForEach(b => b.Save(helper));
}
