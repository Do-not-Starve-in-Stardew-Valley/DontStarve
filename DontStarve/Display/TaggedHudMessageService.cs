#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Display;

/// <summary>
/// Internal group names for HUD messages owned by this mod. The group is deliberately kept
/// outside Stardew's native HUDMessage.type merge key: native type merging changes the existing
/// message text/count and resets its lifetime instead of fading it out.
/// </summary>
internal static class HudMessageGroupTags
{
    internal const string Default = "default";
    internal const string DarknessAttack = "darkness-attack";

    // 进食恢复弹窗（+X 饥饿 / +X 理智）各占一组：饥饿与理智消息互不触发对方组内淡出，
    // 同一系统快速连吃时沿用既有组惯例（旧消息快速淡出、新消息弹出），不做原版数字累加合并。
    internal const string FoodRestoreHunger = "food-restore-hunger";
    internal const string FoodRestoreSanity = "food-restore-sanity";
}

/// <summary>
/// Adds HUD messages with an internal group tag and fades only this mod's previous messages in
/// that group. It never removes or edits messages created by the game or another mod.
/// </summary>
internal sealed class TaggedHudMessageService : IDisposable
{
    private const float DefaultCornerTextboxMilliseconds = 5250f;
    private const float FastFadeMilliseconds = 250f;
    private const string NativeTypePrefix = "DontStarve.HudMessageGroup.";

    private readonly IModHelper helper;
    private readonly Dictionary<string, List<TaggedHudMessage>> messagesByGroup = new(
        StringComparer.Ordinal
    );
    private long nextMessageId;
    private bool disposed;

    internal TaggedHudMessageService(IModHelper helper)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    /// <summary>
    /// Add an arbitrary standard HUD message to a tagged group. The returned instance is the
    /// instance actually submitted to Stardew, so callers can retain its normal HUDMessage type.
    /// </summary>
    internal HUDMessage Add(HUDMessage message, string groupTag)
    {
        return AddCore(message, groupTag, customIcon: null);
    }

    /// <summary>
    /// Add a standard HUD message that draws a mod-provided icon texture inside the vanilla
    /// icon slot instead of the vanilla type sprite. Existing Add/AddCornerTextbox signatures
    /// stay untouched; the icon is delivered through this dedicated overload so callers that
    /// do not need it keep the old behavior byte-for-byte.
    /// </summary>
    internal HUDMessage Add(HUDMessage message, string groupTag, Texture2D customIcon)
    {
        ArgumentNullException.ThrowIfNull(customIcon);
        return AddCore(message, groupTag, customIcon);
    }

    private HUDMessage AddCore(HUDMessage message, string groupTag, Texture2D? customIcon)
    {
        if (disposed)
            return message ?? throw new ArgumentNullException(nameof(message));
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var normalizedTag = NormalizeTag(groupTag);
        FadeExisting(normalizedTag);

        var taggedMessage = message as TaggedHudMessage ?? TaggedHudMessage.CopyFrom(message);
        taggedMessage.CustomIcon = customIcon;
        taggedMessage.type = CreateNativeType(normalizedTag);
        Game1.addHUDMessage(taggedMessage);

        if (!messagesByGroup.TryGetValue(normalizedTag, out var messages))
        {
            messages = new List<TaggedHudMessage>();
            messagesByGroup.Add(normalizedTag, messages);
        }
        messages.Add(taggedMessage);
        return taggedMessage;
    }

    /// <summary>Add a parsed corner textbox message to a tagged group.</summary>
    internal HUDMessage AddCornerTextbox(
        string message,
        string groupTag,
        double durationSeconds = 0d
    )
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var parsed = Game1.parseText(message, Game1.dialogueFont, 384);
        var milliseconds =
            double.IsFinite(durationSeconds) && durationSeconds > 0d
                ? (float)Math.Clamp(durationSeconds * 1000d, 1d, 60000d)
                : DefaultCornerTextboxMilliseconds;
        return Add(
            new HUDMessage(parsed, milliseconds)
            {
                noIcon = true,
            },
            groupTag
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        messagesByGroup.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (disposed || messagesByGroup.Count == 0)
            return;

        List<string>? emptyGroups = null;
        foreach (var pair in messagesByGroup)
        {
            var messages = pair.Value;
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                if (messages[index].IsFinished)
                    messages.RemoveAt(index);
            }
            if (messages.Count == 0)
            {
                emptyGroups ??= new List<string>();
                emptyGroups.Add(pair.Key);
            }
        }

        if (emptyGroups is null)
            return;
        foreach (var group in emptyGroups)
            messagesByGroup.Remove(group);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        messagesByGroup.Clear();
    }

    private void FadeExisting(string groupTag)
    {
        if (!messagesByGroup.TryGetValue(groupTag, out var messages))
            return;
        foreach (var message in messages)
            message.BeginFastFade();
    }

    private string CreateNativeType(string groupTag)
    {
        nextMessageId = nextMessageId == long.MaxValue ? 1L : nextMessageId + 1L;
        return $"{NativeTypePrefix}{groupTag}.{nextMessageId}";
    }

    private static string NormalizeTag(string groupTag)
    {
        if (string.IsNullOrWhiteSpace(groupTag))
            throw new ArgumentException("A HUD message group tag is required.", nameof(groupTag));
        return groupTag.Trim();
    }

    private sealed class TaggedHudMessage : HUDMessage
    {
        private bool fastFadeRequested;

        private TaggedHudMessage(string message, float timeLeft)
            : base(message, timeLeft)
        {
        }

        internal bool IsFinished { get; private set; }

        /// <summary>
        /// 自定义图标纹理：非 null 时 draw 用它替换原版 whatType 槽位精灵；null 时保持
        /// base 原版绘制完全不变（whatType/messageSubject 行为与原版一致）。
        /// </summary>
        internal Texture2D? CustomIcon { get; set; }

        internal static TaggedHudMessage CopyFrom(HUDMessage source)
        {
            return new TaggedHudMessage(source.message ?? string.Empty, source.timeLeft)
            {
                transparency = source.transparency,
                number = source.number,
                whatType = source.whatType,
                achievement = source.achievement,
                noIcon = source.noIcon,
                messageSubject = source.messageSubject,
            };
        }

        internal void BeginFastFade()
        {
            if (IsFinished)
                return;
            fastFadeRequested = true;
            timeLeft = -1f;
        }

        /// <summary>
        /// 带自定义图标时的原版对齐绘制：逐行复刻 1.6.15 HUDMessage.draw 的图标消息布局
        /// （titleSafeArea 左下角锚点、(293,360,26,24) 黑框 4x、文字列、ref heightUsed 向上
        /// 堆叠、小地图与窄视口修正），仅图标槽不画 whatType 闪电/心，改画本 mod 纹理。
        /// noIcon 或未给图标时回落 base，原版路径零改动。
        /// </summary>
        public override void draw(SpriteBatch b, int i, ref int heightUsed)
        {
            var icon = CustomIcon;
            if (icon is null || noIcon)
            {
                base.draw(b, i, ref heightUsed);
                return;
            }

            _ = i;
            // 以下布局代码对照 references/StardewValley-1.6.15-Decompiled/.../HUDMessage.cs:150-203，
            // 除图标槽外不得改动；数值漂移会破坏与原版消息并排堆叠时的对齐。
            // 说明：反编译源里的 Viewport.GetTitleSafeArea() 在本机游戏 MonoGame fork 中
            // 以只读属性 Viewport.TitleSafeArea 暴露（桌面端语义一致），这里用属性取同一锚点。
            Rectangle titleSafeArea = Game1.graphics.GraphicsDevice.Viewport.TitleSafeArea;
            int boxHeight = 112;
            Vector2 vector = new(
                titleSafeArea.Left + 16,
                titleSafeArea.Bottom - boxHeight - heightUsed - 64
            );
            heightUsed += boxHeight;
            if (Game1.isOutdoorMapSmallerThanViewport())
            {
                vector.X = Math.Max(titleSafeArea.Left + 16, -Game1.uiViewport.X + 16);
            }
            if (Game1.uiViewport.Width < 1400)
            {
                vector.Y -= 48f;
            }
            b.Draw(
                Game1.mouseCursors,
                vector,
                new Rectangle(293, 360, 26, 24),
                Color.White * transparency,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                1f
            );
            float textWidth = Game1.smallFont.MeasureString(message ?? "").X;
            b.Draw(
                Game1.mouseCursors,
                new Vector2(vector.X + 104f, vector.Y),
                new Rectangle(319, 360, 1, 24),
                Color.White * transparency,
                0f,
                Vector2.Zero,
                new Vector2(textWidth, 4f),
                SpriteEffects.None,
                1f
            );
            b.Draw(
                Game1.mouseCursors,
                new Vector2(vector.X + 104f + textWidth, vector.Y),
                new Rectangle(323, 360, 6, 24),
                Color.White * transparency,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                1f
            );
            vector.X += 16f;
            vector.Y += 16f;
            // 图标槽：原版把 16x16 精灵以中心原点画在槽中心（vector + 32,32），scale 为
            // 4 + timeLeft 弹出增量。这里保留同一槽中心与弹出放大，纹理最大边等比映射到
            // 原版 16px 基线（16x16 纹理时与原版完全一致）；放大溢出槽边也是原版行为。
            Vector2 slotCenter = vector + new Vector2(8f, 8f) * 4f;
            float popScale = 4f + Math.Max(0f, (timeLeft - 3000f) / 900f);
            float fitToVanillaSlot = 16f / Math.Max(icon.Width, icon.Height);
            b.Draw(
                icon,
                slotCenter,
                null,
                Color.White * transparency,
                0f,
                new Vector2(icon.Width / 2f, icon.Height / 2f),
                popScale * fitToVanillaSlot,
                SpriteEffects.None,
                1f
            );
            vector.X += 51f;
            vector.Y += 51f;
            if (number > 1)
            {
                Utility.drawTinyDigits(number, b, vector, 3f, 1f, Color.White * transparency);
            }
            vector.X += 32f;
            vector.Y -= 33f;
            Utility.drawTextWithShadow(
                b,
                message ?? "",
                Game1.smallFont,
                vector,
                Game1.textColor * transparency,
                1f,
                1f,
                -1,
                -1,
                transparency
            );
        }

        public override bool update(GameTime time)
        {
            if (!fastFadeRequested)
            {
                IsFinished = base.update(time);
                return IsFinished;
            }

            var elapsedMilliseconds = Math.Max(0d, time.ElapsedGameTime.TotalMilliseconds);
            if (elapsedMilliseconds > 0d)
            {
                transparency -= (float)(elapsedMilliseconds / FastFadeMilliseconds);
            }
            timeLeft = -1f;
            if (transparency > 0f)
                return false;

            transparency = 0f;
            IsFinished = true;
            return true;
        }
    }
}
