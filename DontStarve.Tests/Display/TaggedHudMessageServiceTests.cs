using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Display;

public sealed class TaggedHudMessageServiceTests
{
    [Fact]
    public void Tagged_message_groups_use_unique_native_keys_and_fast_fade_owned_instances()
    {
        var source = ReadSource("TaggedHudMessageService.cs");

        Assert.Contains("internal HUDMessage Add(HUDMessage message, string groupTag)", source, StringComparison.Ordinal);
        Assert.Contains("FadeExisting(normalizedTag);", source, StringComparison.Ordinal);
        Assert.Contains("taggedMessage.type = CreateNativeType(normalizedTag);", source, StringComparison.Ordinal);
        Assert.Contains("Game1.addHUDMessage(taggedMessage);", source, StringComparison.Ordinal);
        Assert.Contains("internal const string DarknessAttack = \"darkness-attack\";", source, StringComparison.Ordinal);
        Assert.Contains("private const float FastFadeMilliseconds = 250f;", source, StringComparison.Ordinal);
        Assert.Contains("message.BeginFastFade();", source, StringComparison.Ordinal);
        Assert.Contains("public override bool update(GameTime time)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages.Clear", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages.Remove", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_icon_messages_keep_vanilla_layout_and_never_clear_hud_messages()
    {
        var source = ReadSource("TaggedHudMessageService.cs");

        // 图标支持：专用重载，现有 Add/AddCornerTextbox 签名保持向后兼容。
        Assert.Contains("internal HUDMessage Add(HUDMessage message, string groupTag, Texture2D customIcon)", source, StringComparison.Ordinal);
        // 图标绘制：draw override 存在，noIcon / 未给图标时回落 base 原版绘制。
        Assert.Contains("public override void draw(SpriteBatch b, int i, ref int heightUsed)", source, StringComparison.Ordinal);
        Assert.Contains("base.draw(b, i, ref heightUsed);", source, StringComparison.Ordinal);
        // 图标槽复刻：保留原版黑框源矩形与 timeLeft 驱动的弹出放大，仅纹理替换。
        Assert.Contains("new Rectangle(293, 360, 26, 24)", source, StringComparison.Ordinal);
        Assert.Contains("(timeLeft - 3000f) / 900f", source, StringComparison.Ordinal);
        // 堆叠契约：draw 仍按原版推进 ref heightUsed，不做任何 hudMessages 清理。
        Assert.Contains("heightUsed += boxHeight;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages.Clear", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages.Remove", source, StringComparison.Ordinal);
        // 进食恢复弹窗组：饥饿/理智各占一组，互不触发对方组内淡出。
        Assert.Contains("internal const string FoodRestoreHunger = \"food-restore-hunger\";", source, StringComparison.Ordinal);
        Assert.Contains("internal const string FoodRestoreSanity = \"food-restore-sanity\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Food_restore_messenger_submits_through_the_tagged_group_lane_with_mod_icons()
    {
        var messenger = ReadContract("HudMessages", "FoodRestoreHudMessenger.cs");

        // 文案与图标：i18n key + 胃/大脑纹理成对出现。
        Assert.Contains("\"food-restore.hunger\"", messenger, StringComparison.Ordinal);
        Assert.Contains("\"food-restore.sanity\"", messenger, StringComparison.Ordinal);
        Assert.Contains("TextureLoader.HungerIcon", messenger, StringComparison.Ordinal);
        Assert.Contains("TextureLoader.SanityBrain", messenger, StringComparison.Ordinal);
        // 提交通道：必须走 TaggedHudMessageService 分组通道（同组旧消息快速淡出），
        // 不直接调用 Game1.addHUDMessage（那是原版同 type 数字累加合并的入口）。
        Assert.Contains("hudMessages.Add(", messenger, StringComparison.Ordinal);
        Assert.Contains("HudMessageGroupTags.FoodRestoreHunger", messenger, StringComparison.Ordinal);
        Assert.Contains("HudMessageGroupTags.FoodRestoreSanity", messenger, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.addHUDMessage(", messenger, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages", messenger, StringComparison.Ordinal);
        // UI 分层红线：弹窗提交不修改任何游戏状态与数值。
        Assert.DoesNotContain("Game1.player", messenger, StringComparison.Ordinal);
    }

    [Fact]
    public void Hunger_and_sanity_food_restore_popups_are_gated_by_the_shared_rule()
    {
        var hunger = ReadContract("OnlinePersistence", "Hunger.cs");
        var sanity = ReadContract("LegacySpawn", "Sanity.cs");

        foreach (var source in new[] { hunger, sanity })
        {
            // 判定必须走纯规则类，接线处不得自造数值语义。
            Assert.Contains("FoodRestoreHudRule.Decide(", source, StringComparison.Ordinal);
            // 弹窗提交器经可选 seam 注入；提交只发生在 Show 分支。
            Assert.Contains("foodRestoreHud?.Show", source, StringComparison.Ordinal);
            // 不允许绕过 TaggedHudMessageService 直发原版消息。
            Assert.DoesNotContain("Game1.addHUDMessage(", source, StringComparison.Ordinal);
        }

        // Hunger 双路径门控：主机结算按种类 Food，客机回放按附加字段 DeltaKind。
        Assert.Contains("HungerChangeKind.Food", hunger, StringComparison.Ordinal);
        Assert.Contains("HungerChangeKind.Food", ReadContract("OnlinePersistence", "HungerRuntimeState.cs"), StringComparison.Ordinal);
        // Sanity 门控：主机结算按来源 Food，客机回放按附加字段 DeltaSource。
        Assert.Contains("SanityChangeSource.Food", sanity, StringComparison.Ordinal);
        Assert.Contains("DeltaSource", ReadContract("OnlinePersistence", "SanityChangeService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Darkness_local_and_remote_prompts_share_only_the_darkness_message_group()
    {
        var local = ReadContract(
            "DarknessAttack",
            "SmapiDarknessAttackService.cs"
        );
        var remote = ReadContract(
            "DarknessAttack",
            "SmapiEnvironmentLightMultiplayerCoordinator.cs"
        );

        Assert.Contains("hudMessages.AddCornerTextbox(", local, StringComparison.Ordinal);
        Assert.Contains("HudMessageGroupTags.DarknessAttack", local, StringComparison.Ordinal);
        Assert.Contains("hudMessages.AddCornerTextbox(", remote, StringComparison.Ordinal);
        Assert.Contains("HudMessageGroupTags.DarknessAttack", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages", local, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.hudMessages", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void Hunger_uses_default_group_without_touching_darkness_group()
    {
        var messages = ReadSource("TaggedHudMessageService.cs");
        var hunger = ReadContract("OnlinePersistence", "Hunger.cs");

        Assert.Contains("internal const string Default = \"default\";", messages, StringComparison.Ordinal);
        Assert.Contains("internal const string DarknessAttack = \"darkness-attack\";", messages, StringComparison.Ordinal);
        Assert.Contains("FadeExisting(normalizedTag);", messages, StringComparison.Ordinal);
        Assert.Contains("hudMessages.AddCornerTextbox(", hunger, StringComparison.Ordinal);
        Assert.Contains("HudMessageGroupTags.Default", hunger, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.addHUDMessage(", hunger, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
    {
        return ReadContract("HudMessages", fileName);
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
