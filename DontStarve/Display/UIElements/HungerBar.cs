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
internal class HungerBar : INonTimeRelatedUIElement, IHudStatusBarProvider
{
    private const string BarId = "hunger";
    private const int HudLayoutOrder = 200;

    public string HudBarId => BarId;

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

    public bool TryGetLayoutRequest(out HudBarLayoutRequest request)
    {
        if (!HungerExtensions.IsEnabled)
        {
            request = default;
            return false;
        }

        request = new HudBarLayoutRequest(
            BarId,
            HudLayoutOrder,
            TextureLoader.HungerContainerScaledWidth,
            TextureLoader.HungerContainerScaledHeight
        );
        return true;
    }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        if (!uiContext.StatusBarBounds.HasValue)
            return;
        var statusBarBounds = uiContext.StatusBarBounds.Value;

        var player = Game1.player;
        var hunger = player.GetHunger();
        var maxHunger = player.GetMaxHunger();

        if (!HungerHudRules.TryCreateValues(hunger, maxHunger, out var values, out var reason))
        {
            ReportFailureOnce(reason);
            return;
        }

        if (
            !HudStatusBarFrameRules.TryCreateFrame(
                statusBarBounds,
                TextureLoader.HungerFillerScaledWidth,
                values.FillRatio,
                out var frame,
                out reason
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

        // HungerFiller already contains #CEAF3D; a colored tint would multiply it again.
        spriteBatch.Draw(
            TextureLoader.HungerFiller,
            new Vector2(frame.FillerPosition.X, frame.FillerPosition.Y),
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
        if (frame.HitBounds.Contains(mousePoint.X, mousePoint.Y))
        {
            var label = uiContext.Helper.Translation.Get("hunger-hud.label").ToString();
            var information = uiContext.Helper.Translation
                .Get(
                    "hunger-hud.value",
                    new
                    {
                        label,
                        current = LocalizedValueFormatter.FormatWhole(values.Current, culture),
                        maximum = LocalizedValueFormatter.FormatWhole(values.Maximum, culture),
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
