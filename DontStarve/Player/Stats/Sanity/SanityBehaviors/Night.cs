using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Adjusts sanity over time based on the in-game time of day (nightfall lowers sanity).
/// </summary>
internal class Night : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.Night";
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

        // Current time is daytime
        if (timeOfDay < nightfallStart && timeOfDay >= midnightEnd)
        {
            // Previous time was pre-dawn
            if (lastTimeOfDay < midnightEnd)
                midnightTime = midnightEnd - lastTimeOfDay;
        }
        // Current time is dusk/evening
        else if (timeOfDay >= nightfallStart)
        {
            // Previous time was daytime
            if (lastTimeOfDay < nightfallStart && lastTimeOfDay >= midnightEnd)
                nightfallTime = timeOfDay - nightfallStart;
            // Previous time was also evening
            else
                nightfallTime = timeOfDay - lastTimeOfDay;
        }
        // Current time is pre-dawn
        else
        {
            // Previous time was evening
            if (lastTimeOfDay >= nightfallStart)
            {
                nightfallTime = 60 * 24 - lastTimeOfDay;
                midnightTime = timeOfDay;
            }
            // Previous time was also pre-dawn
            else
            {
                midnightTime = timeOfDay - lastTimeOfDay;
            }
        }

        var nightfallSanity = nightfallTime * 0.0588;
        var midnightSanity = midnightTime * (Game1.currentLocation.IsOutdoors ? 0.1176 : 0.0588);
        var value = nightfallSanity + midnightSanity;
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
        var data = helper.Data.ReadSaveData<NightData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new NightData { Wait = wait });
    }
}

internal class NightData
{
    public long Wait { get; init; }
}
