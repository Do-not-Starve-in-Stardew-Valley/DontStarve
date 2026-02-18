using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff;

internal static class Stamina
{
    private const string BUFF = "DS_Heal_Stamina";
    private static bool lastHasBuff;
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
        var hasBuff = player.hasBuff(BUFF);

        if (hasBuff && !lastHasBuff)
        {
            lastTime = time;
            wait = 0;
        }

        if (lastHasBuff)
        {
            var delta = time - lastTime;
            if (delta >= 3)
            {
                if (player.Stamina < player.MaxStamina)
                    player.Stamina += Math.Min(1, player.MaxStamina - player.Stamina);

                lastTime = time;
            }
        }

        lastHasBuff = hasBuff;
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
        var data = helper.Data.ReadSaveData<StaminaData>("DontStarve.Buff.Stamina");
        lastHasBuff = data?.lastHasBuff ?? false;
        lastTime = data?.lastTime ?? 0;
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            "DontStarve.Buff.Stamina",
            new StaminaData
            {
                lastHasBuff = lastHasBuff,
                lastTime = lastTime,
                wait = wait,
            }
        );
    }
}

internal class StaminaData
{
    internal bool lastHasBuff { get; init; }
    internal long lastTime { get; init; }
    internal long wait { get; init; }
}
