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

        var location = player.currentLocation;
        if (location == null)
            return;

        var playerPosition = player.Tile;
        var value = 0.0;
        var junimoValue = 0.0;

        // 只枚举当前玩家所在地点；Farmers 不在 characters 中，附近旁观者不会替代当前 player。
        foreach (var npc in location.characters)
        {
            var distance = Util.Distance(playerPosition, npc.Tile);
            if (npc is Junimo or JunimoHarvester)
            {
                // player.Tile 与 npc.Tile 都是格坐标；Junimo 也必须应用相同的 10 格线性衰减。
                junimoValue += SanityBehaviorRules.CalculateFriendlyNpcRecovery(
                    FriendlyNpcSanityKind.Junimo,
                    0,
                    distance
                );
                continue;
            }

            if (npc is Child or Pet)
            {
                var childOrPetLevel = player.getFriendshipHeartLevelForNPC(npc.Name);
                value += SanityBehaviorRules.CalculateFriendlyNpcRecovery(
                    FriendlyNpcSanityKind.ChildOrPet,
                    childOrPetLevel,
                    distance
                );
                continue;
            }

            if (!npc.IsVillager)
                continue;

            var isSpouse = npc.Name == player.spouse;
            var friendshipLevel = isSpouse
                ? 0
                : player.getFriendshipHeartLevelForNPC(npc.Name);
            value += SanityBehaviorRules.CalculateFriendlyNpcRecovery(
                isSpouse
                    ? FriendlyNpcSanityKind.Spouse
                    : FriendlyNpcSanityKind.Villager,
                friendshipLevel,
                distance
            );
        }

        if (value > 0)
            player.ChangeSanity(value, SanityChangeSource.Npc);
        if (junimoValue > 0)
            player.ChangeSanity(junimoValue, SanityChangeSource.Junimo);
    }

    public void Sync(long _, long delta)
    {
        if (delta < 0)
            wait += -delta;
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
