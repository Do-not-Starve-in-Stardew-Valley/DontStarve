using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger.HungerBehaviors;

/// <summary>
/// 检测吃食动作结束，并按 food.json 恢复饥饿值。
/// </summary>
internal class EatFood : INonTimeRelatedBehavior
{
    public static Dictionary<string, float> FoodHunger { get; private set; }

    private Item lastFood;
    private bool lastEating;

    public void Init(IModHelper helper)
    {
        // 食物数值走资源表，key 使用 Stardew 物品 ItemId；不要把具体食物硬编码进行为逻辑。
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
            // isEating 从 true 变 false 表示吃食动作刚结束，此时用上一帧 itemToEat 结算恢复值。
            var hunger = FoodHunger?.GetValueOrDefault(lastFood.ItemId, 0f) ?? 0f;
            player.SetHunger(player.GetHunger() + hunger);
        }

        lastFood = player.itemToEat;
        lastEating = player.isEating;
    }
}
