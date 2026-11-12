#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;
using DontStarve.Resource;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Display;

/// <summary>
/// 进食恢复弹窗提交器：把一次已完成的食物结算事实转成原版左下角 HUD 消息
/// （"+X 饥饿" / "+X 理智"，图标用本 mod 胃/大脑纹理）。只负责 i18n 文案、图标与提交；
/// 是否弹、弹多少由 <see cref="FoodRestoreHudRule"/> 判定，本类不读取也不修改任何
/// 游戏状态（UI 分层红线：弹窗提交不改数值）。
/// </summary>
/// <remarks>
/// 与原版的有意差异：原版 Game1.addHUDMessage 对相同 type 的消息做数字累加合并
/// （重置寿命、累加 number）。本 mod 的 TaggedHudMessageService 给每条消息分配唯一
/// native type 并按组淡出，因此快速连吃两样食物时两条恢复消息各自独立弹出、
/// 同组旧消息快速淡出，不做合并。走 TaggedHudMessageService 的分组通道而非
/// Game1.addHUDMessage 直发，是为了守住「不触碰原版/其它 mod 消息」的既有契约。
/// </remarks>
internal sealed class FoodRestoreHudMessenger
{
    // 与原版 doneEating 的 +体力/+生命 弹窗同款寿命（HUDMessage(string, int whatType) 的 5250ms）。
    private const float RestoreMessageMilliseconds = 5250f;

    private readonly IModHelper helper;
    private readonly TaggedHudMessageService hudMessages;

    internal FoodRestoreHudMessenger(IModHelper helper, TaggedHudMessageService hudMessages)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.hudMessages = hudMessages ?? throw new ArgumentNullException(nameof(hudMessages));
    }

    /// <summary>弹一条 "+X 饥饿"；X 为规则判定后的显示数值。</summary>
    internal void ShowHungerRestore(int amount)
    {
        Submit(
            "food-restore.hunger",
            amount,
            HudMessageGroupTags.FoodRestoreHunger,
            TextureLoader.HungerIcon
        );
    }

    /// <summary>弹一条 "+X 理智"；X 为规则判定后的显示数值。</summary>
    internal void ShowSanityRestore(int amount)
    {
        Submit(
            "food-restore.sanity",
            amount,
            HudMessageGroupTags.FoodRestoreSanity,
            TextureLoader.SanityBrain
        );
    }

    private void Submit(string translationKey, int amount, string groupTag, Texture2D icon)
    {
        var text = helper
            .Translation.Get(translationKey, new { value = amount })
            .ToString();
        hudMessages.Add(
            new HUDMessage(text, RestoreMessageMilliseconds),
            groupTag,
            icon
        );
    }
}
