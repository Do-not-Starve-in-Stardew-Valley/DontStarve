using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Reduces sanity based on proximity to monsters. Closer and stronger monsters drain more.
/// </summary>
internal class NearMonster : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.NearMonster";
    private Dictionary<string, double> monsterSanity = null!;
    private long wait;

    public void Init(IModHelper helper)
    {
        monsterSanity = helper.ModContent.Load<Dictionary<string, double>>(
            "Asset/Sanity/monster.json"
        );
    }

    public void Update(long _)
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
        var playerPosition = player.Tile;
        var value = 0.0;
        foreach (var monster in location.characters.Where(npc => npc is Monster))
        {
            var monsterPosition = monster.Tile;
            var distance = Util.Distance(playerPosition, monsterPosition);
            var percentage = 1 - distance / 10;
            if (percentage > 0)
                value += monsterSanity.GetValueOrDefault(monster.Name, 0) * percentage;
        }

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
        var data = helper.Data.ReadSaveData<NearMonsterData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new NearMonsterData { Wait = wait });
    }
}

internal class NearMonsterData
{
    public long Wait { get; init; }
}
