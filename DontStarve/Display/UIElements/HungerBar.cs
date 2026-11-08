using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly HashSet<string> reportedFailures = new(StringComparer.Ordinal);
    private CultureInfo culture = CultureInfo.InvariantCulture;

    public void Init(IModHelper helper)
    {
        culture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);
        helper.Events.Content.LocaleChanged += (_, e) =>
            culture = LocalizedValueFormatter.ResolveCulture(e.NewLocale);
        helper.Events.GameLoop.SaveLoaded += (_, _) => reportedFailures.Clear();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => reportedFailures.Clear();
    }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        if (!uiContext.ShowingHunger)
            return;

        var player = Game1.player;
        var hunger = player.GetHunger();
        var maxHunger = player.GetMaxHunger();

        if (
            !HudDisplayRules.TryCreateHungerFrame(
                uiContext.HudLayout.HungerBounds,
                fillerWidth: TextureLoader.HungerFillerScaledWidth,
                current: hunger,
                maximum: maxHunger,
                out var frame,
                out var reason
            )
        )
        {
            ReportFailureOnce(reason);
            return;
        }

        var containerBounds = frame.ContainerBounds;
        var spriteBatch = e.SpriteBatch;

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
        // HungerFiller already contains #CEAF3D; a colored tint would multiply it again.
        spriteBatch.Draw(
            TextureLoader.HungerFiller,
            new Vector2(fillerPosition.X, fillerPosition.Y),
            new Rectangle(
                0,
                0,
                frame.FillerWidth,
                frame.FillerHeight
            ),
            Color.White,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePoint = Game1.getMousePosition(true);
        if (containerBounds.Contains(mousePoint.X, mousePoint.Y))
        {
            var label = uiContext.Helper.Translation.Get("hunger-hud.label").ToString();
            var information = uiContext.Helper.Translation
                .Get(
                    "hunger-hud.value",
                    new
                    {
                        label,
                        current = LocalizedValueFormatter.FormatWhole(frame.Current, culture),
                        maximum = LocalizedValueFormatter.FormatWhole(frame.Maximum, culture),
                    }
                )
                .ToString();
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

    private void ReportFailureOnce(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || !reportedFailures.Add(reason))
            return;

        Console.Error.WriteLine(
            $"[DontStarve][HungerHud] HUD skipped for invalid state (reason={reason})."
        );
    }
}
