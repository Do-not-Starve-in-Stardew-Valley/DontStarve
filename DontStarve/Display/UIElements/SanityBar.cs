using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly ISanitySystemState sanitySystemState;
    private readonly HashSet<string> reportedFailures = new(StringComparer.Ordinal);
    private CultureInfo culture = CultureInfo.InvariantCulture;

    internal SanityBar(ISanitySystemState sanitySystemState)
    {
        this.sanitySystemState = sanitySystemState;
    }

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
        if (
            !HudDisplayRules
                .ResolveSanityVisibility(sanitySystemState.IsEnabled)
                .ShowBar
        )
        {
            return;
        }

        var player = Game1.player;
        var sanity = player.GetSanity();
        var maxSanity = player.GetMaxSanity();

        var containerTexture = TextureLoader.SanityContainer;
        if (
            !HudDisplayRules.TryCreateSanityFrame(
                uiContext.HudLayout.SanityBounds,
                TextureLoader.FillerWidthMultiplier * Game1.pixelZoom,
                sanity,
                maxSanity,
                out var frame,
                out var reason
            )
        )
        {
            ReportFailureOnce(reason);
            return;
        }

        var spriteBatch = e.SpriteBatch;
        var containerBounds = frame.ContainerBounds;

        spriteBatch.Draw(
            containerTexture,
            new Rectangle(
                containerBounds.X,
                containerBounds.Y,
                containerBounds.Width,
                containerBounds.Height
            ),
            Color.White
        );

        spriteBatch.Draw(
            TextureLoader.SanityFiller,
            new Vector2(frame.FillerPosition.X, frame.FillerPosition.Y),
            new Rectangle(0, 0, frame.FillerWidth, frame.FillerHeight),
            Brushes.SanityBrush,
            3.138997f,
            new Vector2(0.5f, 0.5f),
            1f,
            SpriteEffects.None,
            1f
        );

        var mousePoint = Game1.getMousePosition(true);
        if (containerBounds.Contains(mousePoint.X, mousePoint.Y))
        {
            var label = uiContext.Helper.Translation.Get("sanity-hud.label").ToString();
            var information = uiContext.Helper.Translation
                .Get(
                    "sanity-hud.value",
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

        // DisplayManager 当前不持有 IMonitor；SMAPI 会接收标准错误，且按存档会话/reason 去重。
        Console.Error.WriteLine(
            $"[DontStarve][SanityHud] HUD skipped for invalid state (reason={reason})."
        );
    }
}
