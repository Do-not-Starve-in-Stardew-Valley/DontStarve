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
