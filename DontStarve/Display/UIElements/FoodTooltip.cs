using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using HungerEatFood = DontStarve.Player.Stats.Hunger.HungerBehaviors.EatFood;
using SanityEatFood = DontStarve.Player.Stats.Sanity.SanityBehaviors.EatFood;

namespace DontStarve.Display.UIElements;

/// <summary>
/// Renders a tooltip at the bottom of the screen showing hunger/sanity values for held food items.
/// </summary>
internal class FoodTooltip : INonTimeRelatedUIElement
{
    public void Init(IModHelper helper) { }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        var player = Game1.player;
        var activeObject = player.ActiveObject;
        if (activeObject == null)
            return;

        double? foodHunger = HungerEatFood.FoodHunger.TryGetValue(
            activeObject.ItemId,
            out var hungerValue
        )
            ? hungerValue
            : null;
        double? foodSanity = SanityEatFood.FoodSanity.TryGetValue(
            activeObject.ItemId,
            out var sanityValue
        )
            ? sanityValue
            : null;

        if (foodHunger == null && foodSanity == null)
            return;

        var sizeUi = uiContext.ViewportSize;
        var spriteBatch = e.SpriteBatch;

        var parts = new System.Collections.Generic.List<string>(2);
        if (foodHunger != null)
            parts.Add(
                uiContext.Helper.Translation.Get("hunger-tooltip", new { value = foodHunger })
            );
        if (foodSanity != null)
            parts.Add(
                uiContext.Helper.Translation.Get("sanity-tooltip", new { value = foodSanity })
            );

        var textStr = string.Join("\n", parts);
        var textSize = Game1.smallFont.MeasureString(textStr);

        var boxX = (int)(sizeUi.X / 2) - (int)(textSize.X / 2 + 25);
        var boxY = (int)sizeUi.Y - 125 - (int)(textSize.Y + 25);
        var boxW = (int)(textSize.X + 50);
        var boxH = (int)(textSize.Y + 40);

        IClickableMenu.drawTextureBox(
            spriteBatch,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            boxX,
            boxY,
            boxW,
            boxH,
            Color.White * 1,
            1,
            false,
            1
        );

        Utility.drawTextWithShadow(
            spriteBatch,
            textStr,
            Game1.smallFont,
            new Vector2(boxX + 25, boxY + 20),
            Game1.textColor
        );
    }
}
