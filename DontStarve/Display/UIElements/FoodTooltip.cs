using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 旧版屏幕底部手持属性面板。保留实现以便回退，但 DisplayManager 不再注册它；当前属性
/// 统一由原版集中 tooltip 显示。
/// </summary>
internal class FoodTooltip : INonTimeRelatedUIElement
{
    private readonly ISanitySystemState sanitySystemState;
    private FoodBuffTooltipFormatter buffFormatter;

    internal FoodTooltip(ISanitySystemState sanitySystemState)
    {
        this.sanitySystemState = sanitySystemState;
    }

    public void Init(IModHelper helper)
    {
        this.buffFormatter = new FoodBuffTooltipFormatter(helper);
    }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        if (
            Game1.activeClickableMenu is not null
        )
        {
            return;
        }

        var player = Game1.player;
        var activeItem = player.ActiveItem;
        if (activeItem == null)
            return;

        var buffLines =
            this.buffFormatter?.GetLines(activeItem, this.sanitySystemState.IsEnabled)
            ?? Array.Empty<string>();

        if (buffLines.Count == 0)
            return;

        var sizeUi = uiContext.ViewportSize;
        var spriteBatch = e.SpriteBatch;

        var parts = new List<string>(buffLines.Count);
        parts.AddRange(buffLines);

        var maxTextWidth = (int)Math.Max(220, Math.Min(560, sizeUi.X - 100));
        var textStr = WrapText(string.Join("\n", parts), maxTextWidth);
        var textSize = Game1.smallFont.MeasureString(textStr);

        var boxW = (int)Math.Min(Math.Max(100, sizeUi.X - 20), textSize.X + 50);
        var boxH = (int)(textSize.Y + 40);

        // 贴近原版底部道具提示位置，同时给窄窗口留 10px 边距。
        var boxX = Math.Max(10, (int)(sizeUi.X / 2) - boxW / 2);
        var boxY = Math.Max(10, (int)sizeUi.Y - 125 - boxH);

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

    private static string WrapText(string text, int maxWidth)
    {
        var wrapped = new List<string>();
        foreach (var line in text.Split('\n'))
            WrapLine(line, maxWidth, wrapped);

        return string.Join("\n", wrapped);
    }

    private static void WrapLine(string line, int maxWidth, List<string> output)
    {
        if (Game1.smallFont.MeasureString(line).X <= maxWidth)
        {
            output.Add(line);
            return;
        }

        var current = string.Empty;
        foreach (var word in line.Split(' '))
        {
            if (Game1.smallFont.MeasureString(word).X > maxWidth)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    output.Add(current);
                    current = string.Empty;
                }

                // i18n 文案或物品名可能没有空格，超宽单词也要硬拆，否则 tooltip 会溢出屏幕。
                WrapLongWord(word, maxWidth, output);
                continue;
            }

            var candidate = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            if (Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrEmpty(current))
                output.Add(current);
            current = word;
        }

        if (!string.IsNullOrEmpty(current))
            output.Add(current);
    }

    private static void WrapLongWord(string word, int maxWidth, List<string> output)
    {
        var current = string.Empty;
        foreach (var c in word)
        {
            var candidate = current + c;
            if (current.Length == 0 || Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            output.Add(current);
            current = c.ToString();
        }

        if (!string.IsNullOrEmpty(current))
            output.Add(current);
    }
}
