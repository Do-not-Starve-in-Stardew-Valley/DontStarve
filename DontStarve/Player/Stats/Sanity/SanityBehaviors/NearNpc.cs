using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据附近友方角色恢复理智；配偶、好友、孩子、宠物和祝尼魔使用不同权重。
/// </summary>
internal class NearNpc : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.NearNpc";
    private long wait;

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

        // 只按当前地点角色计算 10 格内影响，避免离屏 NPC 也持续给理智加成。
        foreach (var villager in location.characters.Where(npc => npc.IsVillager))
        {
            var villagerPosition = villager.Tile;
            var distance = Util.Distance(playerPosition, villagerPosition);
            var percentage = 1 - distance / 10;
            if (percentage > 0)
            {
                if (villager.Name == player.spouse)
                {
                    value += 1.176 * percentage;
                }
                else
                {
                    var level = player.getFriendshipHeartLevelForNPC(villager.Name);
                    if (level >= 8)
                        value += 0.588 * percentage;
                    else if (level >= 5)
                        value += 0.294 * percentage;
                }
            }
        }

        foreach (var npc in location.characters.Where(npc => npc is Child or Pet))
        {
            var villagerPosition = npc.Tile;
            var distance = Util.Distance(playerPosition, villagerPosition);
            var percentage = 1 - distance / 10;
            if (percentage > 0)
            {
                var level = player.getFriendshipHeartLevelForNPC(npc.Name);
                if (level == 5)
                    value += 0.588 * percentage;
                else if (level >= 3)
                    value += 0.294 * percentage;
                else
                    value += 0.147 * percentage;
            }
        }

        foreach (var npc in location.characters.Where(npc => npc is Junimo or JunimoHarvester))
        {
            var villagerPosition = npc.Position;
            var distance = Util.Distance(playerPosition, villagerPosition);
            var percentage = 1 - distance / 10;
            if (percentage > 0)
                value += 0.294;
        }

        if (value > 0)
            player.SetSanity(player.GetSanity() + value);
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
        var data = helper.Data.ReadSaveData<NearNpcData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new NearNpcData { Wait = wait });
    }
}

internal class NearNpcData
{
    public long Wait { get; init; }
}
