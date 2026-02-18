using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Sanity;

internal static class Night
{
    private static long wait;

    internal static void update(long time)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        var timeOfDay = time % (60 * 24);
        var lastTimeOfDay = (time - 1) % (60 * 24);
        var nightfallTime = 0L;
        var midnightTime = 0L;

        var nightfallStart =
            Game1.season switch
            {
                Season.Spring => 20,
                Season.Summer => 20,
                Season.Fall => 19,
                Season.Winter => 18,
                _ => throw new Exception("Unknown Season"),
            } * 60;

        const long midnightEnd = 6 * 60;

        // 当前时间是白天
        if (timeOfDay < nightfallStart && timeOfDay >= midnightEnd)
        {
            // 上次时间是凌晨
            if (lastTimeOfDay < midnightEnd)
                midnightTime = midnightEnd - lastTimeOfDay;
        }
        // 当前时间是黄昏
        else if (timeOfDay >= nightfallStart)
        {
            // 上次时间是白天
            if (lastTimeOfDay < nightfallStart && lastTimeOfDay >= midnightEnd)
                nightfallTime = timeOfDay - nightfallStart;
            // 上次时间是黄昏
            else
                nightfallTime = timeOfDay - lastTimeOfDay;
        }
        // 当前时间是凌晨
        else
        {
            // 上次时间是黄昏
            if (lastTimeOfDay >= nightfallStart)
            {
                nightfallTime = 60 * 24 - lastTimeOfDay;
                midnightTime = timeOfDay;
            }
            // 上次时间是凌晨
            else
            {
                midnightTime = timeOfDay - lastTimeOfDay;
            }
        }

        var nightfallSanity = nightfallTime * 0.0588;
        var midnightSanity = midnightTime * (Game1.currentLocation.IsOutdoors ? 0.1176 : 0.0588);
        var value = nightfallSanity + midnightSanity;
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
        var data = helper.Data.ReadSaveData<NightData>("DontStarve.Sanity.Night");
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData("DontStarve.Sanity.Night", new NightData { wait = wait });
    }
}

internal class NightData
{
    internal long wait { get; init; }
}
