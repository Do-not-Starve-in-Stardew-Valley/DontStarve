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
/// Renders the hunger bar and its hover label on the HUD.
/// </summary>
internal class HungerBar : INonTimeRelatedUIElement
{
    public void Init(IModHelper helper) { }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        var player = Game1.player;
        var hunger = player.GetHunger();
        var maxHunger = player.GetMaxHunger();
        var hudAnchor = uiContext.ViewportBottomRightAnchor;
        var spriteBatch = e.SpriteBatch;

        var containerW = TextureLoader.HungerContainerScaledWidth;
        var containerH = TextureLoader.HungerContainerScaledHeight;
        var fillerWidth = TextureLoader.HungerFillerScaledWidth;
        var containerX = (int)hudAnchor.X - 60;
        var containerY = (int)hudAnchor.Y - 240;

        spriteBatch.Draw(
            TextureLoader.HungerContainer,
            new Rectangle(containerX, containerY, containerW, containerH),
            Color.White
        );

        spriteBatch.Draw(
            TextureLoader.HungerFiller,
            new Vector2(hudAnchor.X - 24, hudAnchor.Y - 25),
            new Rectangle(0, 0, fillerWidth, (int)(hunger / maxHunger * 168)),
            Brushes.HungerBrush,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePoint = Game1.getMousePosition(true);
        var checkX = mousePoint.X >= containerX && mousePoint.X <= containerX + containerW;
        var checkY = mousePoint.Y >= containerY && mousePoint.Y <= containerY + containerH;

        if (checkX && checkY)
        {
            var information = $"Hunger: {Math.Round(hunger)}/{Math.Round(maxHunger)}";
            var textSize = Game1.dialogueFont.MeasureString(information);
            var posX = hudAnchor.X - 60;
            var posY = containerY - textSize.Y + 116;

            Game1.spriteBatch.DrawString(
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
