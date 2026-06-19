using System;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Resource;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 绘制理智条和悬停数值；状态读取来自 SanityExtensions。
/// </summary>
internal class SanityBar : INonTimeRelatedUIElement
{
    public void Init(IModHelper helper) { }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        var player = Game1.player;
        var sanity = player.GetSanity();
        var maxSanity = player.GetMaxSanity();

        // 右下角锚点已在 UIRenderContext 中根据原版生命 HUD 让位。
        var hudAnchor = uiContext.ViewportBottomRightAnchor;

        e.SpriteBatch.Draw(
            TextureLoader.SanityContainer,
            new Rectangle(
                (int)hudAnchor.X,
                (int)hudAnchor.Y - 240,
                TextureLoader.SanityContainer.Width * 4,
                TextureLoader.SanityContainer.Height * 4
            ),
            Color.White
        );

        e.SpriteBatch.Draw(
            TextureLoader.SanityFiller,
            new Vector2(hudAnchor.X + 36, hudAnchor.Y - 25),
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
        var spriteBatch = e.SpriteBatch;

        var containerW = TextureLoader.SanityContainerScaledWidth;
        var containerH = TextureLoader.SanityContainerScaledHeight;
        var fillerWidth = TextureLoader.SanityFillerScaledWidth;
        var containerX = (int)hudAnchor.X;
        var containerY = (int)hudAnchor.Y - 240;

        spriteBatch.Draw(
            TextureLoader.SanityContainer,
            new Rectangle(containerX, containerY, containerW, containerH),
            Color.White
        );

        spriteBatch.Draw(
            TextureLoader.SanityFiller,
            new Vector2(hudAnchor.X + 36, hudAnchor.Y - 25),
            new Rectangle(0, 0, fillerWidth, (int)(sanity / maxSanity * 168)),
            Brushes.SanityBrush,
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
            var information = $"Sanity: {Math.Round(sanity)}/{Math.Round(maxSanity)}";
            var textSize = Game1.dialogueFont.MeasureString(information);
            var posX = hudAnchor.X;
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
