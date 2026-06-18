using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity;

internal class Sanity : IStat
{
    // 主理智值的存档 key，历史存档依赖它；改名前必须做兼容迁移。
    private const string SAVE_KEY = "DontStarve.Sanity";

    private static readonly List<INonTimeRelatedBehavior> nonTimeRelatedBehaviors =
        new List<INonTimeRelatedBehavior> { new EatFood(), new Sleep() };

    private static readonly List<ITimeRelatedBehavior> timeRelatedBehaviors =
        new List<ITimeRelatedBehavior>
        {
            new NearMonster(),
            new Night(),
            new Wearing(),
            new NearNpc(),
            new MineShaft(),
            new SpawnMrSkitts(),
            new SpawnDarkHand(),
            new SpawnDarkWatcher(),
            new SpawnEye(),
            new SpawnCreeperFear(),
            new SpawnTerrifyingSharpBeak(),
        };

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
        var data = helper.Data.ReadSaveData<SanityData>(SAVE_KEY);
        // 缺失旧字段时回到满值，避免因为旧存档或首次安装直接进入低理智逻辑。
        SanityExtensions.farmerSanity = data?.Sanity ?? SanityExtensions.DefaultMaxSanity;
        foreach (var b in timeRelatedBehaviors)
            b.Load(helper);
    }

    private static void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new SanityData { Sanity = SanityExtensions.farmerSanity }
        );
        foreach (var b in timeRelatedBehaviors)
            b.Save(helper);
    }
}

internal class SanityData
{
    public double? Sanity { get; init; }
}

public static class SanityExtensions
{
    internal const double DefaultMaxSanity = 150;
    private const double FARMER_MAX_SANITY = DefaultMaxSanity;

    // 当前实现是全局静态状态，不是按 Farmer 实例隔离；多人和切换存档改造时必须先拆这里。
    internal static double farmerSanity;

    public static double GetMaxSanity(this Farmer _) => FARMER_MAX_SANITY;

    public static double GetSanity(this Farmer _) => farmerSanity;

    public static void SetSanity(this Farmer farmer, double value)
    {
        if (value < 0)
            farmerSanity = 0;
        else if (value > farmer.GetMaxSanity())
            farmerSanity = farmer.GetMaxSanity();
        else
            farmerSanity = value;
    }
}
