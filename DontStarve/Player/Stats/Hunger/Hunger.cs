using System.Collections.Generic;
using DontStarve.Interface;
using DontStarve.Player.Stats.Hunger.HungerBehaviors;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

internal class Hunger : IStat
{
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
        var data = helper.Data.ReadSaveData<HungerData>(SAVE_KEY);
        HungerExtensions.farmerHunger = data?.Hunger ?? HungerExtensions.DefaultMaxHunger;
        foreach (var b in timeRelatedBehaviors)
            b.Load(helper);
    }

    private static void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new HungerData { Hunger = HungerExtensions.farmerHunger }
        );
        foreach (var b in timeRelatedBehaviors)
            b.Save(helper);
    }
}

internal class HungerData
{
    public float? Hunger { get; init; }
}

public static class HungerExtensions
{
    internal const float DefaultMaxHunger = 150;
    private const float FARMER_MAX_HUNGER = DefaultMaxHunger;
    internal static float farmerHunger;

    public static float GetMaxHunger(this Farmer _) => FARMER_MAX_HUNGER;

    public static float GetHunger(this Farmer _) => farmerHunger;

    public static void SetHunger(this Farmer farmer, float value)
    {
        if (value < 0)
            farmerHunger = 0;
        else if (value > farmer.GetMaxHunger())
            farmerHunger = farmer.GetMaxHunger();
        else
            farmerHunger = value;
    }
}
