using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace DontStarve.Player.Sanity;

internal static class NearNpc
{
    private static long wait;

    internal static void update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        var location = Game1.currentLocation;
        var playerPosition = player.Position;
        var value = 0.0;
        foreach (var villager in location.characters.Where(npc => npc.IsVillager))
        {
            var villagerPosition = villager.Position;
            var distance = Math.Sqrt(
                Math.Pow(villagerPosition.X - playerPosition.X, 2)
                    + Math.Pow(villagerPosition.Y - playerPosition.Y, 2)
            );
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
            var villagerPosition = npc.Position;
            var distance = Math.Sqrt(
                Math.Pow(villagerPosition.X - playerPosition.X, 2)
                    + Math.Pow(villagerPosition.Y - playerPosition.Y, 2)
            );
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
            var distance = Util.distance(playerPosition, villagerPosition);
            var percentage = 1 - distance / 10;
            if (percentage > 0)
                value += 0.294;
        }

        if (value > 0)
            player.setSanity(player.getSanity() + value);
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
        var data = helper.Data.ReadSaveData<NearNpcData>("DontStarve.Sanity.NearNpc");
        wait = data?.wait ?? 0;
    }

    internal static void save(IModHelper helper)
    {
        helper.Data.WriteSaveData("DontStarve.Sanity.NearNpc", new NearNpcData { wait = wait });
    }
}

internal class NearNpcData
{
    internal long wait { get; init; }
}
