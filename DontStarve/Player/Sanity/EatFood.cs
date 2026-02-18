using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Sanity;

internal static class EatFood
{
    public static Dictionary<string, double> foodSanity { get; private set; } = null!;
    private static Item lastFood;
    private static bool lastEating;

    internal static void init(IModHelper helper)
    {
        foodSanity = helper.ModContent.Load<Dictionary<string, double>>("assets/sanity/food.json");
        helper.Events.GameLoop.UpdateTicking += update;
    }

    private static void update(object sender, UpdateTickingEventArgs e)
    {
        var player = Game1.player;
        var isEating = player.isEating;
        if (!isEating && lastEating)
        {
            var sanity = foodSanity.GetValueOrDefault(lastFood!.ItemId, 0);
            player.setSanity(player.getSanity() + sanity);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
