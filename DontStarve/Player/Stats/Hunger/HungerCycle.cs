using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Drains hunger over time and applies HP penalties when hunger reaches zero.
/// Driven by MinuteTimeHelper ticks.
/// </summary>
internal class HungerCycle : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Hunger.HungerCycle";
    private bool lastHasHunger;
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
