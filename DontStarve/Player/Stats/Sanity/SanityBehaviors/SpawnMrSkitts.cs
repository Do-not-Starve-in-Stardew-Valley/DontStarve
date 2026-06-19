using System;
using DontStarve.Critter;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 低理智幻觉：理智低于 83.5% 时触发，之后每 20 个内部分钟最多生成一次。
/// </summary>
internal class SpawnMrSkitts : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.SpawnMrSkitts";
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

        if (player.GetSanity() <= player.GetMaxSanity() * 0.835)
        {
            var delta = time - lastTime;
            if (delta >= 20 || lastSanity > player.GetMaxSanity() * 0.835)
            {
                var playerPosition = player.Position;
                var xStart = player.Position.X - 10 * Game1.tileSize;
                var xEnd = player.Position.X + 10 * Game1.tileSize;
                var yStart = player.Position.Y - 10 * Game1.tileSize;
                var yEnd = player.Position.Y + 10 * Game1.tileSize;
                Vector2 spawnPosition;
                do
                {
                    // 保持在玩家 5-10 格环形范围内，避免贴脸生成或生成到太远处看不见。
                    spawnPosition = new Vector2(
                        xStart + random.NextSingle() * (xEnd - xStart),
                        yStart + random.NextSingle() * (yEnd - yStart)
                    );
                } while (
                    Util.Distance(playerPosition, spawnPosition)
                        is > 10 * Game1.tileSize
                            or < 5 * Game1.tileSize
                );

                location.critters?.Add(new MrSkitts(spawnPosition));
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
        var data = helper.Data.ReadSaveData<SpawnMrSkittsData>(SAVE_KEY);
        lastSanity = data?.LastSanity ?? 0;
        lastTime = data?.LastTime ?? 0;
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new SpawnMrSkittsData
            {
                LastSanity = lastSanity,
                LastTime = lastTime,
                Wait = wait,
            }
        );
    }
}

internal class SpawnMrSkittsData
{
    public double LastSanity { get; init; }
    public long LastTime { get; init; }
    public long Wait { get; init; }
}
