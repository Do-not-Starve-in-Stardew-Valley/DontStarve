using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据附近怪物降低理智：资源表给基础值，10 格内按距离线性衰减。
/// </summary>
internal class NearMonster : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.NearMonster";
    private Dictionary<string, double> monsterSanity = null!;
    private long wait;

    public void Init(IModHelper helper)
    {
        // key 使用 Stardew 怪物 Name；扩表前要先确认游戏内实际名称。
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

        // 每分钟只扫当前地点 characters，避免跨地点或全局 NPC 扫描进入高频路径。
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
