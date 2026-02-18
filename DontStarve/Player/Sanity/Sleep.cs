using System;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Sanity;

internal static class Sleep
{
    private static int lastTime;

    internal static void timeChange(TimeChangedEventArgs e)
    {
        lastTime = e.NewTime;
    }

    internal static void dayEnding()
    {
        Console.WriteLine($"Day Ending: {lastTime}");
        var player = Game1.player;
        var timescale = lastTime % 100 / 10 + lastTime / 100 * 6;
        if (timescale == 156)
        {
            player.setSanity(player.getSanity() - 20);
        }
        else
        {
            var passTimescale = 156 - timescale;
            player.setSanity(player.getSanity() + passTimescale * 3);
        }
    }
}
