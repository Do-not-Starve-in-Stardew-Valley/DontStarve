using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
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
    internal const string DarknessAttack = "darkness-attack";
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
        if (disposed)
            return message ?? throw new ArgumentNullException(nameof(message));
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var normalizedTag = NormalizeTag(groupTag);
        FadeExisting(normalizedTag);

        var taggedMessage = message as TaggedHudMessage ?? TaggedHudMessage.CopyFrom(message);
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

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (disposed || messagesByGroup.Count == 0)
            return;

        List<string> emptyGroups = null;
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

    private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
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
