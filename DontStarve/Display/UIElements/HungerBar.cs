using System;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Resource;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 绘制饥饿条和悬停数值；状态读取来自 HungerExtensions。
/// </summary>
internal class HungerBar : INonTimeRelatedUIElement
{
    public void Init(IModHelper helper) { }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        var player = Game1.player;
        var hunger = player.GetHunger();
        var maxHunger = player.GetMaxHunger();

        var containerBounds = uiContext.HudLayout.HungerBounds;
        var spriteBatch = e.SpriteBatch;

        var fillerWidth = TextureLoader.HungerFillerScaledWidth;

        spriteBatch.Draw(
            TextureLoader.HungerContainer,
            new Rectangle(
                containerBounds.X,
                containerBounds.Y,
                containerBounds.Width,
                containerBounds.Height
            ),
            Color.White
        );

        var fillerPosition = HudDisplayRules.GetFillerPosition(containerBounds);
        spriteBatch.Draw(
            TextureLoader.HungerFiller,
            new Vector2(fillerPosition.X, fillerPosition.Y),
            new Rectangle(
                0,
                0,
                fillerWidth,
                HudDisplayRules.GetFillerHeight(hunger, maxHunger)
            ),
            Brushes.HungerBrush,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePoint = Game1.getMousePosition(true);
        if (containerBounds.Contains(mousePoint.X, mousePoint.Y))
        {
            var information = $"Hunger: {Math.Round(hunger)}/{Math.Round(maxHunger)}";
            var textSize = Game1.dialogueFont.MeasureString(information);
            var posX = containerBounds.X;
            var posY =
                containerBounds.Y
                + containerBounds.Height / 2f
                + 4f
                - textSize.Y;

            spriteBatch.DrawString(
                Game1.dialogueFont,
                information,
                new Vector2(posX, posY),
                Color.White,
                0f,
                new Vector2(textSize.X, 0f),
                1f,
                SpriteEffects.None,
                0f
            );
        }
    }
}
