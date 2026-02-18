using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Applies a sanity bonus or penalty at the end of each day based on sleep time.
/// Registers its own SMAPI events in Init.
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
