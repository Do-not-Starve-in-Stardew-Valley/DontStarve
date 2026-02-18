using DontStarve.Player.Sanity;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

internal class SanityRestoreBuff : ITimeRelatedBuff
{
    private const string SANITY_RESTORE_BUFF_ID = "DS_BUFF_SANITY_RESTORE";
    private const string SAVE_KEY = "DontStarve.Buff.SanityRestore";
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

        var hasBuff = player.hasBuff(SANITY_RESTORE_BUFF_ID);
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
                player.setSanity(player.getSanity() + 1);
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
        var data = helper.Data.ReadSaveData<SanityRestoreData>(SAVE_KEY);
        lastHasBuff = data?.LastHasBuff ?? false;
        lastTime = data?.LastTime ?? 0;
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new SanityRestoreData
            {
                LastHasBuff = lastHasBuff,
                LastTime = lastTime,
                Wait = wait,
            }
        );
    }
}

internal class SanityRestoreData
{
    public bool LastHasBuff { get; init; }
    public long LastTime { get; init; }
    public long Wait { get; init; }
}
