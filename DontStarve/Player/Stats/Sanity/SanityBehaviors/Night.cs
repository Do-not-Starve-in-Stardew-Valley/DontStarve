using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 按季节日落时间计算夜间理智损耗；午夜到清晨户外损耗更高。
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

        // Stardew 不同季节天黑时间不同；这里按 1 分钟粒度拆分“日落后”和“午夜后”两段损耗。
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

        // 当前是白天。
        if (timeOfDay < nightfallStart && timeOfDay >= midnightEnd)
        {
            // 上一分钟还在清晨前，补足跨过 6:00 的午夜段。
            if (lastTimeOfDay < midnightEnd)
                midnightTime = midnightEnd - lastTimeOfDay;
        }
        // 当前是日落后。
        else if (timeOfDay >= nightfallStart)
        {
            // 上一分钟还是白天，只计算跨过日落线后的部分。
            if (lastTimeOfDay < nightfallStart && lastTimeOfDay >= midnightEnd)
                nightfallTime = timeOfDay - nightfallStart;
            // 上一分钟也在日落后。
            else
                nightfallTime = timeOfDay - lastTimeOfDay;
        }
        // 当前是午夜到清晨前。
        else
        {
            // 从前一天日落后跨到清晨前，要同时结算日落段和午夜段。
            if (lastTimeOfDay >= nightfallStart)
            {
                nightfallTime = 60 * 24 - lastTimeOfDay;
                midnightTime = timeOfDay;
            }
            // 上一分钟也在清晨前。
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
