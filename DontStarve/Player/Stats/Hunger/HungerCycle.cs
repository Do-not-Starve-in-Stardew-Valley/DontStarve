using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// 按内部分钟 tick 消耗饥饿；饥饿归零后按节奏扣生命。
/// </summary>
internal class HungerCycle : ITimeRelatedBehavior
{
    // 只保存循环内部节奏，不保存主饥饿值；主值由 DontStarve.Hunger 单独持久化。
    private const string SAVE_KEY = "DontStarve.Hunger.HungerCycle";
    private bool lastHasHunger;
    private long lastTime;

    // 时间被同步回退时用 wait 抵消未来 tick，避免同一段游戏时间重复扣饥饿或扣血。
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

        if (player.GetHunger() > 0)
        {
            player.SetHunger(player.GetHunger() - 0.052f);
            lastHasHunger = true;
        }
        else
        {
            if (lastHasHunger)
                if (player.health > 0)
                    player.health -= Math.Min(4, player.health);

            var delta = time - lastTime;
            if (delta >= 3)
            {
                if (player.health > 0)
                    player.health -= Math.Min(4, player.health);
                lastTime = time;
            }

            lastHasHunger = false;
        }
    }

    public void Sync(long _, long delta)
    {
        // 正向分钟已由 TimeApi 逐分钟发布；回退只累加旧 wait，保留玩家存档中的兼容游标。
        if (delta < 0)
            wait += -delta;
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<HungerCycleData>(SAVE_KEY);
        lastHasHunger = data?.LastHasHunger ?? false;
        lastTime = data?.LastTime ?? 0;
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new HungerCycleData
            {
                LastHasHunger = lastHasHunger,
                LastTime = lastTime,
                Wait = wait,
            }
        );
    }
}

internal class HungerCycleData
{
    public bool LastHasHunger { get; init; }
    public long LastTime { get; init; }
    public long Wait { get; init; }
}
