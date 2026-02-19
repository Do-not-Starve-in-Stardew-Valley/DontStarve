using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Reduces sanity based on the current mine shaft type and danger level.
/// </summary>
internal class MineShaft : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.MineShaft";
    private long wait;

    public void Update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        if (player == null)
            return;

        var location = Game1.currentLocation;
        var value = 0.0;

        if (location is MineShaftLocation mineShaft)
        {
            value = 0.0588;
            if (mineShaft.isDarkArea())
                value = 0.588;
            if (mineShaft.mineLevel > 120)
                value = 0.1176;
            if (mineShaft.mineLevel > 1000)
                value = 0.2352;
            if (mineShaft.isQuarryArea)
                value = 0.1764;
            if (mineShaft.isSlimeArea)
                value += 0.1176;
            if (mineShaft.isMonsterArea)
                value += 0.2352;
            if (mineShaft.isDinoArea)
                value += 0.2352;
            if (mineShaft.GetAdditionalDifficulty() > 0)
            {
                if (mineShaft.mineLevel > 120)
                    value += 0.2352;
                else
                    value += 0.1176;
            }
        }
        else if (location is VolcanoDungeon)
        {
            value = 0.1176;
        }

        if (value > 0)
            player.SetSanity(player.GetSanity() - value);
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
        var data = helper.Data.ReadSaveData<MineShaftData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new MineShaftData { Wait = wait });
    }
}

internal class MineShaftData
{
    public long Wait { get; init; }
}
