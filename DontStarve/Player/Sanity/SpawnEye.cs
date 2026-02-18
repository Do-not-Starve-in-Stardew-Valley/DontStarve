using System;
using DontStarve.Critter;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Sanity;

internal static class SpawnEye
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

        if (player.getSanity() <= player.getMaxSanity() * 0.6)
        {
            var delta = time - lastTime;
            if (delta >= 20 || lastSanity > player.getMaxSanity() * 0.6)
            {
                var playerPosition = player.Position;
                var xStart = player.Position.X - 15 * Game1.tileSize;
                var xEnd = player.Position.X + 15 * Game1.tileSize;
                var yStart = player.Position.Y - 15 * Game1.tileSize;
                var yEnd = player.Position.Y + 15 * Game1.tileSize;
                Vector2 spawnPosition;
                do
                {
                    spawnPosition = new Vector2(
                        xStart + random.NextSingle() * (xEnd - xStart),
                        yStart + random.NextSingle() * (yEnd - yStart)
                    );
                } while (
                    Util.distance(playerPosition, spawnPosition)
                        is > 15 * Game1.tileSize
                            or < 5 * Game1.tileSize
                );

                location.critters?.Add(new Eye(spawnPosition));
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
        var data = helper.Data.ReadSaveData<SpawnEyeData>("DontStarve.Sanity.SpawnEye");
        lastSanity = data?.lastSanity ?? 0;
        lastTime = data?.lastTime ?? 0;
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            "DontStarve.Sanity.SpawnEye",
            new SpawnEyeData
            {
                lastSanity = lastSanity,
                lastTime = lastTime,
                wait = wait,
            }
        );
    }
}

internal class SpawnEyeData
{
    internal double lastSanity { get; init; }
    internal long lastTime { get; init; }
    internal long wait { get; init; }
}
