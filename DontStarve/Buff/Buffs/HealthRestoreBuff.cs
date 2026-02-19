using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

internal class HealthRestoreBuff : ITimeRelatedBuff
{
    private const string HEALTH_RESTORE_BUFF_ID = "DS_BUFF_HEALTH_RESTORE";
    private const string SAVE_KEY = "DontStarve.Buff.HealthRestore";
    private bool lastHasBuff;
    private long lastTime;
    private long wait;

    public void Update(long time)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        if (player == null)
            return;

        var hasBuff = player.hasBuff(HEALTH_RESTORE_BUFF_ID);
        if (hasBuff && !lastHasBuff)
        {
            lastTime = time;
            wait = 0;
        }

        if (hasBuff)
        {
            var delta = time - lastTime;
            if (delta >= 3)
            {
                if (player.health < player.maxHealth)
                    player.health += Math.Min(2, player.maxHealth - player.health);

                lastTime = time;
            }
        }

        lastHasBuff = hasBuff;
    }

    public void Sync(long time, long delta)
    {
        if (delta < 0)
            wait += -delta;
        else
            for (var i = 0; i <= delta; i++)
                Update(time);
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<HealthRestoreData>(SAVE_KEY);
        lastHasBuff = data?.LastHasBuff ?? false;
        lastTime = data?.LastTime ?? 0;
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new HealthRestoreData
            {
                LastHasBuff = lastHasBuff,
                LastTime = lastTime,
                Wait = wait,
            }
        );
    }
}

internal class HealthRestoreData
{
    public bool LastHasBuff { get; init; }
    public long LastTime { get; init; }
    public long Wait { get; init; }
}
