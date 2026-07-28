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

        // Stardew 不同季节天黑时间不同；这里按 1 分钟粒度拆分“日落后”和“午夜后”两段损耗。
        var nightfallStart =
            Game1.season switch
            {
                Season.Spring => 20,
                Season.Summer => 20,
                Season.Fall => 19,
                Season.Winter => 18,
                _ => throw new InvalidOperationException("unknown-season"),
            } * 60;
        var location = player.currentLocation;
        if (location == null)
            return;

        var value = SanityBehaviorRules.CalculateNightLossForMinute(
            time,
            nightfallStart,
            location.IsOutdoors
        );
        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Night);
    }

    public void Sync(long _, long delta)
    {
        if (delta < 0)
            wait += -delta;
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
