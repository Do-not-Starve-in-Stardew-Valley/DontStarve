using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 检测吃食动作结束，并按 food.json 恢复或扣减理智值。
/// </summary>
internal class EatFood : INonTimeRelatedBehavior
{
    public static Dictionary<string, double> FoodSanity { get; private set; }

    private Item lastFood;
    private bool lastEating;

    public void Init(IModHelper helper)
    {
        // 食物理智值走资源表，key 使用 Stardew 物品 ItemId；不要把具体食物硬编码进行为逻辑。
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
            // isEating 从 true 变 false 表示吃食动作刚结束，此时用上一帧 itemToEat 结算恢复值。
            var sanity = FoodSanity?.GetValueOrDefault(lastFood.ItemId, 0.0) ?? 0.0;
            player.SetSanity(player.GetSanity() + sanity);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
