using System;
using DontStarve.Critter;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

internal class SpawnDarkWatcher : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.SpawnDarkWatcher";
    private double lastSanity;
    private long lastTime;
    private long wait;
    private readonly Random random = new();

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

        var location = Game1.currentLocation;

        if (player.GetSanity() <= player.GetMaxSanity() * 0.65)
        {
            var delta = time - lastTime;
            if (delta >= 20 || lastSanity > player.GetMaxSanity() * 0.65)
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

        lastSanity = player.GetSanity();
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
        var data = helper.Data.ReadSaveData<SpawnDarkWatcherData>(SAVE_KEY);
        lastSanity = data?.LastSanity ?? 0;
        lastTime = data?.LastTime ?? 0;
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new SpawnDarkWatcherData
            {
                LastSanity = lastSanity,
                LastTime = lastTime,
                Wait = wait,
            }
        );
    }
}

internal class SpawnDarkWatcherData
{
    public double LastSanity { get; init; }
    public long LastTime { get; init; }
    public long Wait { get; init; }
}
