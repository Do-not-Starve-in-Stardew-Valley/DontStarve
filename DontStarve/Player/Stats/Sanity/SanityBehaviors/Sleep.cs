using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 按当天最后记录到的时间结算睡眠理智；睡太晚会扣理智，早睡按剩余时间恢复。
/// </summary>
internal class Sleep : INonTimeRelatedBehavior
{
    private int lastTime;

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.TimeChanged += (_, e) => TimeChange(e);
        helper.Events.GameLoop.DayEnding += (_, _) => DayEnding();
    }

    private void TimeChange(TimeChangedEventArgs e)
    {
        lastTime = e.NewTime;
    }

    private void DayEnding()
    {
        var player = Game1.player;
        if (player == null)
            return;

        var timescale = lastTime % 100 / 10 + lastTime / 100 * 6;
        // Stardew 最晚按 2:00 结束一天，换算成 10 分钟格是 26 * 6 = 156。
        if (timescale == 156)
        {
            player.SetSanity(player.GetSanity() - 20);
        }
        else
        {
            var passTimescale = 156 - timescale;
            player.SetSanity(player.GetSanity() + passTimescale * 3);
        }
    }
}
