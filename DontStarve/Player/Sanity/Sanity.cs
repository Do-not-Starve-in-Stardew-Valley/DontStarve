using DontStarve.Integration;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Sanity;

public static class Sanity
{
    private const double FARMER_MAX_SANITY = 150;
    private static double farmerSanity;

    internal static void init(IModHelper helper)
    {
        EatFood.init(helper);
        NearMonster.init(helper);
        Wearing.init(helper);

        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            var timeApi = helper.ModRegistry.GetApi<TimeApi>("Yurin.MinuteTimeHelper");
            if (timeApi == null)
                return;
            timeApi.OnUpdate.Add(update);
            timeApi.OnSync.Add(sync);
        };

        helper.Events.GameLoop.SaveLoaded += (_, _) => load(helper);
        helper.Events.GameLoop.Saving += (_, _) => save(helper);
        helper.Events.GameLoop.TimeChanged += (_, e) => timeChange(e);
        helper.Events.GameLoop.DayEnding += (_, _) => dayEnding();
    }

    private static void update(ulong time)
    {
        NearMonster.update((long)time);
        Night.update((long)time);
        Wearing.update((long)time);
        NearNpc.update((long)time);
        MineShaft.update((long)time);
        SpawnMrSkitts.update((long)time);
        SpawnDarkHand.update((long)time);
        SpawnDarkWatcher.update((long)time);
        SpawnEye.update((long)time);
        SpawnCreeperFear.update((long)time);
        SpawnTerrifyingSharpBeak.update((long)time);
    }

    private static void sync(ulong time, long delta)
    {
        NearMonster.sync((long)time, delta);
        Night.sync((long)time, delta);
        Wearing.sync((long)time, delta);
        NearNpc.sync((long)time, delta);
        MineShaft.sync((long)time, delta);
        SpawnMrSkitts.sync((long)time, delta);
        SpawnDarkHand.sync((long)time, delta);
        SpawnDarkWatcher.sync((long)time, delta);
        SpawnEye.sync((long)time, delta);
        SpawnCreeperFear.sync((long)time, delta);
        SpawnTerrifyingSharpBeak.sync((long)time, delta);
    }

    private static void load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<SanityData>("DontStarve.Sanity");
        farmerSanity = data?.sanity ?? FARMER_MAX_SANITY;
        NearMonster.load(helper);
        Night.load(helper);
        Wearing.load(helper);
        NearNpc.load(helper);
        MineShaft.load(helper);
        SpawnMrSkitts.load(helper);
        SpawnDarkHand.load(helper);
        SpawnDarkWatcher.load(helper);
        SpawnEye.load(helper);
        SpawnCreeperFear.load(helper);
        SpawnTerrifyingSharpBeak.load(helper);
    }

    private static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData("DontStarve.Sanity", new SanityData { sanity = farmerSanity });
        NearMonster.save(helper);
        Night.save(helper);
        Wearing.save(helper);
        NearNpc.save(helper);
        MineShaft.save(helper);
        SpawnMrSkitts.save(helper);
        SpawnDarkHand.save(helper);
        SpawnDarkWatcher.save(helper);
        SpawnEye.save(helper);
        SpawnCreeperFear.save(helper);
        SpawnTerrifyingSharpBeak.save(helper);
    }

    private static void timeChange(TimeChangedEventArgs e)
    {
        Sleep.timeChange(e);
    }

    private static void dayEnding()
    {
        Sleep.dayEnding();
    }

    public static double getMaxSanity(this Farmer _)
    {
        return FARMER_MAX_SANITY;
    }

    public static double getSanity(this Farmer _)
    {
        return farmerSanity;
    }

    public static void setSanity(this Farmer farmer, double value)
    {
        if (value < 0)
            farmerSanity = 0;
        else if (value > farmer.getMaxSanity())
            farmerSanity = farmer.getMaxSanity();
        else
            farmerSanity = value;
    }
}

internal class SanityData
{
    internal double? sanity { get; init; }
}
