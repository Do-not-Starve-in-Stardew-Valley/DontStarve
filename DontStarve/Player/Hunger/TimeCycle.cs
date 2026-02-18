using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Hunger;

internal static class TimeCycle
{
    private static bool lastHasHunger;
    private static long lastTime;
    private static long wait;

    internal static void update(long time)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;

        if (player.getHunger() > 0)
        {
            player.setHunger(player.getHunger() - 0.052f);
            lastHasHunger = true;
        }
        else
        {
            if (lastHasHunger)
                if (player.health < player.maxHealth)
                    player.health -= Math.Min(4, player.health);

            var delta = time - lastTime;
            if (delta >= 3)
            {
                if (player.health < player.maxHealth)
                    player.health -= Math.Min(4, player.health);

                lastTime = time;
            }

            lastHasHunger = false;
        }
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
        var data = helper.Data.ReadSaveData<TimeCycleData>("DontStarve.Sanity.Night");
        lastHasHunger = data?.lastHasHunger ?? false;
        lastTime = data?.lastTime ?? 0;
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            "DontStarve.Sanity.Night",
            new TimeCycleData
            {
                lastHasHunger = lastHasHunger,
                lastTime = lastTime,
                wait = wait,
            }
        );
    }
}

internal class TimeCycleData
{
    internal bool lastHasHunger { get; init; }
    internal long lastTime { get; init; }
    internal long wait { get; init; }
}
