using System;
using DontStarve.Critter;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Sanity;

internal static class SpawnDarkWatcher
{
    private static double lastSanity;
    private static long lastTime;
    private static long wait;
    private static readonly Random random = new();

    internal static void update(long time)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        var location = Game1.currentLocation;

        if (player.getSanity() <= player.getMaxSanity() * 0.65)
        {
            var delta = time - lastTime;
            if (delta >= 20 || lastSanity > player.getMaxSanity() * 0.65)
            {
                var xStart = Game1.viewport.X;
                var xEnd = Game1.viewport.Width + Game1.viewport.X;
                var yStart = Game1.viewport.Y;
                var yEnd = Game1.viewport.Height + Game1.viewport.Y;
                var spawnPosition = new Vector2(
                    xStart + random.NextSingle() * (xEnd - xStart),
                    yStart + random.NextSingle() * (yEnd - yStart)
                );
                if (spawnPosition.X > spawnPosition.Y)
                    spawnPosition.Y = yStart;
                else if (spawnPosition.X < spawnPosition.Y)
                    spawnPosition.X = xStart;

                location.critters?.Add(new DarkWatcher(spawnPosition));
                lastTime = time;
            }
        }

        lastSanity = player.getSanity();
    }

    internal static void sync(long time, long delta)
    {
        if (delta < 0)
            wait += -delta;
        else
            for (var i = 0; i <= delta; i++)
                update(time);
    }

    internal static void load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<SpawnDarkWatcherData>(
            "DontStarve.Sanity.SpawnDarkWatcher"
        );
        lastSanity = data?.lastSanity ?? 0;
        lastTime = data?.lastTime ?? 0;
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            "DontStarve.Sanity.SpawnDarkWatcher",
            new SpawnDarkWatcherData
            {
                lastSanity = lastSanity,
                lastTime = lastTime,
                wait = wait,
            }
        );
    }
}

internal class SpawnDarkWatcherData
{
    internal double lastSanity { get; init; }
    internal long lastTime { get; init; }
    internal long wait { get; init; }
}
