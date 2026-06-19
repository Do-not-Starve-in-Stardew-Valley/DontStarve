using System.Collections.Generic;
using DontStarve.Buff.Buffs;
using DontStarve.Interface;
using StardewModdingAPI;

namespace DontStarve.Buff;

internal static class BuffManager
{
    // 非 TimeApi 驱动的 Buff 仍可能自己注册 SMAPI 事件，例如每秒恢复或监听存档读写。
    private static readonly List<INonTimeRelatedBuff> nonTimeRelatedBuffs =
        new List<INonTimeRelatedBuff>
        {
            new ElectricityAppendBuff(),
            new HealthRestoreBuff(),
            new StaminaRestoreBuff(),
            new SanityRestoreBuff(),
        };

    // 只有确实依赖内部分钟 tick 的 Buff 才放这里，避免恢复类 Buff 被游戏内时间速度影响总量。
    private static readonly List<ITimeRelatedBuff> timeRelatedBuffs = new();

    /// <summary>
    /// 初始化 Buff 模块，并按 TimeApi 驱动和自管事件两类分开注册。
    /// </summary>
    internal static void Initialize(IModHelper helper, ITimeAPI timeApi)
    {
        if (timeRelatedBuffs.Count > 0)
        {
            timeApi.OnUpdate.Add(Update);
            timeApi.OnSync.Add(Sync);
            helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
            helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        }

        foreach (var b in nonTimeRelatedBuffs)
            b.Init(helper);
    }

    private static void Update(long time)
    {
        foreach (var b in timeRelatedBuffs)
            b.Update(time);
    }

    private static void Sync(long time, long delta)
    {
        foreach (var b in timeRelatedBuffs)
            b.Sync(time, delta);
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
