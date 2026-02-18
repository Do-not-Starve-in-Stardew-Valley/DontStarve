using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Sanity;

internal static class MineShaft
{
    private static long wait;

    internal static void update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        var location = Game1.currentLocation;
        var value = 0.0;
        if (location is StardewValley.Locations.MineShaft mineShaft)
        {
            // 矿井
            value = 0.0588;
            // 黑暗层
            if (mineShaft.isDarkArea())
                value = 0.588;

            // 骷髅矿井
            if (mineShaft.mineLevel > 120)
                value = 0.1176;

            // 骷髅矿井 880 层以上
            if (mineShaft.mineLevel > 1000)
                value = 0.2352;

            // 采石场矿井
            if (mineShaft.isQuarryArea)
                value = 0.1764;

            // 感染层
            if (mineShaft.isSlimeArea)
                value += 0.1176;

            // 地牢层
            if (mineShaft.isMonsterArea)
                value += 0.2352;

            // 史前层
            if (mineShaft.isDinoArea)
                value += 0.2352;

            // 危险矿井
            if (mineShaft.GetAdditionalDifficulty() > 0)
            {
                // 骷髅矿井
                if (mineShaft.mineLevel > 120)
                    value += 0.2352;
                else
                    value += 0.1176;
            }
        }
        // 火山矿井
        else if (location is VolcanoDungeon)
        {
            value = 0.1176;
        }

        if (value > 0)
            player.setSanity(player.getSanity() - value);
    }

    internal static void sync(long time, long delta)
    {
        if (delta < 0)
            wait += -delta;
        else
            for (var i = 0; i <= delta; i++)
                update(time);
    }

    internal static void load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<MineShaftData>("DontStarve.Sanity.MineShaft");
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData("DontStarve.Sanity.MineShaft", new MineShaftData { wait = wait });
    }
}

internal class MineShaftData
{
    internal long wait { get; init; }
}
