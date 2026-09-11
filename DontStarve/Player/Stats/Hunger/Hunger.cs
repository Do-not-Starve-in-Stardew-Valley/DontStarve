using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Player.Stats.Hunger.HungerBehaviors;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

internal class Hunger : IStat
{
    // 主饥饿值的存档 key，历史存档依赖它；改名前必须做兼容迁移。
    private const string SAVE_KEY = "DontStarve.Hunger";
    private static readonly List<INonTimeRelatedBehavior> nonTimeRelatedBehaviors =
        new List<INonTimeRelatedBehavior> { new EatFood() };

    private static readonly List<ITimeRelatedBehavior> timeRelatedBehaviors =
        new List<ITimeRelatedBehavior> { new HungerCycle() };

    public void Init(IModHelper helper, ITimeAPI timeApi)
    {
        foreach (var b in nonTimeRelatedBehaviors)
            b.Init(helper);
        foreach (var b in timeRelatedBehaviors)
            b.Init(helper);

        timeApi.OnUpdate.Add(Update);
        timeApi.OnSync.Add(Sync);

        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetRuntime();
    }

    private static void Update(long time)
    {
        foreach (var b in timeRelatedBehaviors)
            b.Update(time);
    }

    private static void Sync(long time, long delta)
    {
        foreach (var b in timeRelatedBehaviors)
            b.Sync(time, delta);
    }

    private static void Load(IModHelper helper)
    {
        if (!Context.IsMainPlayer)
        {
            // 客机不能读取主机专用 save-data；这里只建立当前进程的临时安全影子，绝不回写旧档。
            ResetRuntime();
            return;
        }

        var data = helper.Data.ReadSaveData<HungerData>(SAVE_KEY);
        // 缺失旧字段时回到满值，避免因为旧存档或首次安装直接进入饥饿惩罚。
        HungerExtensions.farmerHunger = data?.Hunger ?? HungerExtensions.DefaultMaxHunger;
        foreach (var b in timeRelatedBehaviors)
            b.Load(helper);
    }

    private static void Save(IModHelper helper)
    {
        if (!Context.IsMainPlayer)
            return;

        helper.Data.WriteSaveData(
            SAVE_KEY,
            new HungerData { Hunger = HungerExtensions.farmerHunger }
        );
        foreach (var b in timeRelatedBehaviors)
            b.Save(helper);
    }

    private static void ResetRuntime()
    {
        HungerExtensions.ResetRuntime();
        foreach (var behavior in timeRelatedBehaviors)
        {
            if (behavior is HungerCycle cycle)
                cycle.ResetRuntime();
        }
    }
}

internal class HungerData
{
    public double? Hunger { get; init; }
}

public static class HungerExtensions
{
    internal const double DefaultMaxHunger = 150;
    private const double FARMER_MAX_HUNGER = DefaultMaxHunger;
    private static bool systemEnabled;

    // 当前实现是全局静态状态，不是按 Farmer 实例隔离；多人和切换存档改造时必须先拆这里。
    internal static double farmerHunger;

    internal static bool IsEnabled => systemEnabled;

    internal static void SetEnabled(bool enabled)
    {
        systemEnabled = enabled;
    }

    internal static void ResetRuntime()
    {
        farmerHunger = DefaultMaxHunger;
    }

    public static double GetMaxHunger(this Farmer _) => FARMER_MAX_HUNGER;

    public static double GetHunger(this Farmer _) => farmerHunger;

    public static void SetHunger(this Farmer farmer, double value)
    {
        if (!IsEnabled)
            return;

        if (value < 0)
            farmerHunger = 0;
        else if (value > farmer.GetMaxHunger())
            farmerHunger = farmer.GetMaxHunger();
        else
            farmerHunger = value;
    }
}
