using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger.HungerBehaviors;

/// <summary>
/// Detects when the player finishes eating and restores hunger based on food.json.
/// </summary>
internal class EatFood : INonTimeRelatedBehavior
{
    public static Dictionary<string, float> FoodHunger { get; private set; }

    private Item lastFood;
    private bool lastEating;

    public void Init(IModHelper helper)
    {
        FoodHunger = helper.ModContent.Load<Dictionary<string, float>>("Asset/Hunger/food.json");
        helper.Events.GameLoop.UpdateTicking += (_, _) => Update();
    }

    private void Update()
    {
        var player = Game1.player;
        if (player == null)
            return;

        var isEating = player.isEating;
        if (!isEating && lastEating && lastFood != null)
        {
            var hunger = FoodHunger?.GetValueOrDefault(lastFood.ItemId, 0f) ?? 0f;
            player.SetHunger(player.GetHunger() + hunger);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
