#nullable enable

using System.Collections.Generic;
using DontStarve.Player.Stats.Food;
using DontStarve.Player.Stats.Hunger;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger.HungerBehaviors;

/// <summary>
/// 检测每个本地玩家吃食动作结束。主机直接结算，客机提交物品事实给主机重算。
/// </summary>
internal sealed class EatFood : INonTimeRelatedBehavior
{
    public static Dictionary<string, double> FoodHunger { get; private set; } = new();

    private readonly HungerChangeService changeService;
    private Item? lastFood;
    private bool lastEating;

    internal EatFood(HungerChangeService changeService)
    {
        this.changeService = changeService;
    }

    public void Init(IModHelper helper)
    {
        FoodHunger = helper.ModContent.Load<Dictionary<string, double>>(
            "Asset/Hunger/food.json"
        );
        FoodRuleRuntime.SetHunger(FoodHunger);
        helper.Events.GameLoop.UpdateTicking += (_, _) => Update();
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetTracking();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetTracking();
    }

    private void Update()
    {
        var player = Game1.player;
        if (player is null)
            return;

        if (!HungerExtensions.IsEnabled)
        {
            lastFood = player.itemToEat;
            lastEating = player.isEating;
            return;
        }

        var isEating = player.isEating;
        if (
            !isEating
            && lastEating
            && lastFood is not null
            && HungerFoodCompletionPolicy.ShouldUseLegacyFoodEdge(
                changeService.Role,
                HungerFoodCompletionPatch.IsInstalled,
                changeService.HostFoodCompletionSupported
            )
        )
        {
            HungerExtensions.ApplyFood(player, lastFood);
        }

        lastFood = player.itemToEat;
        lastEating = isEating;
    }

    private void ResetTracking()
    {
        lastFood = null;
        lastEating = false;
    }
}
