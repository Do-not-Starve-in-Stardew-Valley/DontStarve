using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Detects when the player finishes eating and restores sanity based on food.json.
/// </summary>
internal class EatFood : INonTimeRelatedBehavior
{
    public static Dictionary<string, double> FoodSanity { get; private set; }

    private Item lastFood;
    private bool lastEating;

    public void Init(IModHelper helper)
    {
        FoodSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/food.json");
        helper.Events.GameLoop.UpdateTicking += Update;
    }

    private void Update(object sender, UpdateTickingEventArgs e)
    {
        var player = Game1.player;
        if (player == null)
            return;

        var isEating = player.isEating;
        if (!isEating && lastEating && lastFood != null)
        {
            var sanity = FoodSanity?.GetValueOrDefault(lastFood.ItemId, 0.0) ?? 0.0;
            player.SetSanity(player.GetSanity() + sanity);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
