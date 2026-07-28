using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Stats.Sanity;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using HungerEatFood = DontStarve.Player.Stats.Hunger.HungerBehaviors.EatFood;
using SanityEatFood = DontStarve.Player.Stats.Sanity.SanityBehaviors.EatFood;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 在屏幕底部显示手持食物的饥饿、理智和 Buff 文案；只读资源表与对象数据，不改变物品效果。
/// </summary>
internal class FoodTooltip : INonTimeRelatedUIElement
{
    private readonly ISanitySystemState sanitySystemState;
    private FoodBuffTooltipFormatter buffFormatter;
    private CultureInfo culture = CultureInfo.InvariantCulture;

    internal FoodTooltip(ISanitySystemState sanitySystemState)
    {
        this.sanitySystemState = sanitySystemState;
    }

    public void Init(IModHelper helper)
    {
        this.buffFormatter = new FoodBuffTooltipFormatter(helper);
        this.culture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);
        helper.Events.Content.LocaleChanged += (_, e) =>
            this.culture = LocalizedValueFormatter.ResolveCulture(e.NewLocale);
    }

    public void Render(RenderingHudEventArgs e, UIRenderContext uiContext)
    {
        if (
            Game1.activeClickableMenu is not null
            ||
            !HudDisplayRules
                .ResolveSanityVisibility(sanitySystemState.IsEnabled)
                .ShowHeldFoodTooltip
        )
        {
            return;
        }

        var player = Game1.player;
        var activeObject = player.ActiveObject;
        if (activeObject == null)
            return;

        // DS 自己的饥饿/理智表和 Stardew Data/Objects 的 Buff 字段是两套来源，tooltip 只在显示层合并。
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
        var buffLines =
            this.buffFormatter?.GetLines(activeObject) ?? Array.Empty<string>();
        var formattedSanity = string.Empty;
        var hasFormattedSanity =
            foodSanity.HasValue
            && LocalizedValueFormatter.TryFormatSigned(
                foodSanity.Value,
                this.culture,
                out formattedSanity
            );

        if (foodHunger == null && !hasFormattedSanity && buffLines.Count == 0)
            return;

        var sizeUi = uiContext.ViewportSize;
        var spriteBatch = e.SpriteBatch;

        var parts = new List<string>(2 + buffLines.Count);
        if (foodHunger != null)
            parts.Add(
                uiContext.Helper.Translation.Get("hunger-tooltip", new { value = foodHunger })
            );
        if (hasFormattedSanity)
        {
            // 该 held-item 提示独立读取食物表；阶段 02 的总开关不得把数据提示一并隐藏。
            parts.Add(
                uiContext.Helper.Translation.Get(
                    "sanity-tooltip.food-once",
                    new { value = formattedSanity }
                )
            );
        }
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
