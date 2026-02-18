using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Hunger;

internal static class EatFood
{
    public static Dictionary<string, float> foodHunger { get; private set; } = null!;
    private static Item lastFood;
    private static bool lastEating;

    internal static void init(IModHelper helper)
    {
        foodHunger = helper.ModContent.Load<Dictionary<string, float>>("assets/hunger/food.json");
        helper.Events.GameLoop.UpdateTicking += (_, _) => update();
    }

    private static void update()
    {
        var player = Game1.player;
        var isEating = player.isEating;
        if (!isEating && lastEating)
        {
            var sanity = foodHunger.GetValueOrDefault(lastFood!.ItemId, 0);
            player.setHunger(player.getHunger() + sanity);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
