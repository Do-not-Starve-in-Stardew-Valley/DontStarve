using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarknessSpecialEventAudioContractTests
{
    [Fact]
    public void EventSuspensionKeepsSpecialAttackLaneEligible()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "AudioPool",
                "SanitySmapiAudioService.cs"
            )
        );

        Assert.Contains(
            "(suspended && !specialEventAudioAllowed)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("darknessAttackLane?.StopPlayback();", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("if (!specialEventAudioAllowed)", StringComparison.Ordinal)
                < source.LastIndexOf("darknessAttackLane?.StopPlayback();", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void DialogueUpGateIsExemptWhileSpecialEventAudioIsAllowed()
    {
        // 修复1（2026-11-13）：事件弹窗会置 Game1.dialogueUp=true，两个警告音暂停入口
        //（进程级 dialogue 门与逐 claim 合并）都必须按 specialEventAudioAllowed 豁免；
        // 豁免只包 dialogueUp/Game1.paused，不包小游戏暂停。
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "AudioPool",
                "SanitySmapiAudioService.cs"
            )
        );

        Assert.Contains(
            "(Game1.paused || Game1.dialogueUp) && !specialEventAudioAllowed",
            source,
            StringComparison.Ordinal
        );
        var firstGate = source.IndexOf(
            "(Game1.paused || Game1.dialogueUp) && !specialEventAudioAllowed",
            StringComparison.Ordinal
        );
        Assert.Contains(
            "((Game1.paused || Game1.dialogueUp) && !specialEventAudioAllowed)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "localMinigameScreens.Contains(screenId)",
            source[firstGate..],
            StringComparison.Ordinal
        );
    }
}
