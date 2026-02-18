using System;
using System.Text;
using DontStarve.Player.Hunger;
using DontStarve.Player.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using EatFood = DontStarve.Player.Hunger.EatFood;

namespace DontStarve;

internal static class Hud
{
    private static Vector2 barPosition
    {
        get
        {
            var sizeUi = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
            return new Vector2(sizeUi.X - (Game1.showingHealth ? 171 : 116), sizeUi.Y);
        }
    }

    internal static void OnRenderingHud(IModHelper helper, RenderingHudEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.CurrentEvent != null)
            return;
        OnRenderingHunger(e);
        OnRenderingSanity(e);
        OnRenderingTooltip(helper, e);
    }

    private static void OnRenderingHunger(RenderingHudEventArgs e)
    {
        var player = Game1.player;
        var hunger = player.getHunger();
        var maxHunger = player.getMaxHunger();

        e.SpriteBatch.Draw(
            TextureLoader.HungerContainer,
            new Rectangle(
                (int)barPosition.X - 60,
                (int)barPosition.Y - 240,
                TextureLoader.HungerContainer.Width * 4,
                TextureLoader.HungerContainer.Height * 4
            ),
            Color.White
        );

        e.SpriteBatch.Draw(
            TextureLoader.HungerFiller,
            new Vector2(barPosition.X - 24, barPosition.Y - 25),
            new Rectangle(
                0,
                0,
                TextureLoader.HungerFiller.Width * 6 * Game1.pixelZoom,
                (int)(hunger / maxHunger * 168)
            ),
            Brushes.HungerBrush,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePosition = new Vector2(
            Game1.getMousePosition(true).X,
            Game1.getMousePosition(true).Y
        );
        var checkXGreater = mousePosition.X >= barPosition.X - 60;
        var checkXLess =
            mousePosition.X <= barPosition.X - 60 + TextureLoader.SanityContainer.Width * 4;
        var checkYGreater = mousePosition.Y >= barPosition.Y - 240;
        var checkYLess =
            mousePosition.Y <= barPosition.Y - 240 + TextureLoader.SanityContainer.Height * 4;
        var checkX = checkXGreater && checkXLess;
        var checkY = checkYGreater && checkYLess;

        if (checkX && checkY)
        {
            var information = $"Hunger: {Math.Round(hunger)}/{Math.Round(maxHunger)}";
            var textSize = Game1.dialogueFont.MeasureString(information);
            var textPosition = new Vector2(-12, textSize.X);

            Game1.spriteBatch.DrawString(
                Game1.dialogueFont,
                information,
                new Vector2(
                    barPosition.X - 60 + textPosition.X,
                    barPosition.Y - 240 + TextureLoader.SanityContainer.Height + 8
                ),
                new Color(255, 255, 255),
                0f,
                new Vector2(textPosition.Y, 0),
                1,
                SpriteEffects.None,
                0f
            );
        }
    }

    private static void OnRenderingSanity(RenderingHudEventArgs e)
    {
        var player = Game1.player;
        var sanity = player.getSanity();
        var maxSanity = player.getMaxSanity();

        e.SpriteBatch.Draw(
            TextureLoader.SanityContainer,
            new Rectangle(
                (int)barPosition.X,
                (int)barPosition.Y - 240,
                TextureLoader.SanityContainer.Width * 4,
                TextureLoader.SanityContainer.Height * 4
            ),
            Color.White
        );

        e.SpriteBatch.Draw(
            TextureLoader.SanityFiller,
            new Vector2(barPosition.X + 36, barPosition.Y - 25),
            new Rectangle(
                0,
                0,
                TextureLoader.SanityFiller.Width * 6 * Game1.pixelZoom,
                (int)(sanity / maxSanity * 168)
            ),
            Brushes.SanityBrush,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePosition = new Vector2(
            Game1.getMousePosition(true).X,
            Game1.getMousePosition(true).Y
        );
        var checkXGreater = mousePosition.X >= barPosition.X;
        var checkXLess = mousePosition.X <= barPosition.X + TextureLoader.SanityContainer.Width * 4;
        var checkYGreater = mousePosition.Y >= barPosition.Y - 240;
        var checkYLess =
            mousePosition.Y <= barPosition.Y - 240 + TextureLoader.SanityContainer.Height * 4;
        var checkX = checkXGreater && checkXLess;
        var checkY = checkYGreater && checkYLess;

        if (checkX && checkY)
        {
            var information = $"Sanity: {Math.Round(sanity)}/{Math.Round(maxSanity)}";
            var textSize = Game1.dialogueFont.MeasureString(information);
            var textPosition = new Vector2(-12, textSize.X);

            Game1.spriteBatch.DrawString(
                Game1.dialogueFont,
                information,
                new Vector2(
                    barPosition.X + textPosition.X,
                    barPosition.Y - 240 + TextureLoader.SanityContainer.Height + 8
                ),
                new Color(255, 255, 255),
                0f,
                new Vector2(textPosition.Y, 0),
                1,
                SpriteEffects.None,
                0f
            );
        }
    }

    private static void OnRenderingTooltip(IModHelper helper, RenderingHudEventArgs e)
    {
        var player = Game1.player;

        var activeObject = player.ActiveObject;
        if (activeObject != null)
        {
            double? foodHunger = EatFood.foodHunger.TryGetValue(
                activeObject.ItemId,
                out var hungerSanity
            )
                ? hungerSanity
                : null;
            double? foodSanity = Player.Sanity.EatFood.foodSanity.TryGetValue(
                activeObject.ItemId,
                out var sanityValue
            )
                ? sanityValue
                : null;
            if (foodSanity != null || foodHunger != null)
            {
                var sizeUi = new Vector2(Game1.uiViewport.Width, Game1.uiViewport.Height);
                var text = new StringBuilder();
                if (foodHunger != null)
                    text.Append(
                        helper.Translation.Get("hunger-tooltip", new { value = foodHunger })
                    );

                if (foodHunger != null && foodSanity != null)
                    text.AppendLine();

                if (foodSanity != null)
                    text.Append(
                        helper.Translation.Get("sanity-tooltip", new { value = foodSanity })
                    );

                var textSize = Game1.smallFont.MeasureString(text);
                var spriteBatch = e.SpriteBatch;
                IClickableMenu.drawTextureBox(
                    spriteBatch,
                    Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    (int)(sizeUi.X / 2) - (int)(textSize.X / 2 + 25),
                    (int)sizeUi.Y - 125 - (int)(textSize.Y + 25),
                    (int)(textSize.X + 50),
                    (int)(textSize.Y + 40),
                    Color.White * 1,
                    1,
                    false,
                    1
                );
                Utility.drawTextWithShadow(
                    spriteBatch,
                    text,
                    Game1.smallFont,
                    new Vector2(
                        (int)(sizeUi.X / 2) - (int)(textSize.X / 2 + 25) + 25,
                        (int)sizeUi.Y - 125 - (int)(textSize.Y + 25) + 20
                    ),
                    Game1.textColor
                );
            }
        }
    }
}
