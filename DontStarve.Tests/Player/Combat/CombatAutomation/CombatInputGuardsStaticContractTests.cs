using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 守卫提取（设计稿阶段 2）回归契约：CombatInputGuards 必须逐字承载从惊恐循环提取的
/// 两套判定；惊恐循环服务不得保留私有副本（双份逻辑 = 行为漂移温床）。
/// </summary>
public sealed class CombatInputGuardsStaticContractTests
{
    [Fact]
    public void Shared_guard_keeps_the_suspend_semantics_verbatim()
    {
        var source = Guard();

        Assert.Contains("internal static bool IsGameInteractionSuspended()", source, StringComparison.Ordinal);
        Assert.Contains("Game1.activeClickableMenu is not null", source, StringComparison.Ordinal);
        Assert.Contains("Game1.paused", source, StringComparison.Ordinal);
        Assert.Contains("!Game1.game1.IsActiveNoOverlay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_guard_keeps_every_panic_interruption_reason()
    {
        var source = Guard();

        Assert.Contains("internal static string? DescribePanicInterruption(Farmer player)", source, StringComparison.Ordinal);
        Assert.Contains("return \"world-not-ready\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"event-active\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"minigame\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"warping\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"fade-to-black\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"kill-screen\";", source, StringComparison.Ordinal);
        Assert.Contains("SanityIdlePanicInterruptionPolicy.UsingToolReason", source, StringComparison.Ordinal);
        Assert.Contains("SanityIdlePanicInterruptionPolicy.UsingSlingshotReason", source, StringComparison.Ordinal);
        Assert.Contains("SanityIdlePanicInterruptionPolicy.EatingReason", source, StringComparison.Ordinal);
        Assert.Contains("return $\"freezePause={player.freezePause}\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"cannot-move\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"moving\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"riding-horse\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"sitting\";", source, StringComparison.Ordinal);
        Assert.Contains("return \"swimming\";", source, StringComparison.Ordinal);
        // 表情豁免契约：isEmoteAnimating 期间 CanMove=false 不算打断（实机教训）。
        Assert.Contains("!player.isEmoteAnimating", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Panic_loop_service_delegates_to_the_shared_guard_without_a_private_copy()
    {
        var service = PanicLoopService();

        Assert.DoesNotContain("private static bool IsGameInteractionSuspended()", service, StringComparison.Ordinal);
        Assert.DoesNotContain("private static string? DescribePanicInterruption(Farmer player)", service, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.IsGameInteractionSuspended()", service, StringComparison.Ordinal);
        Assert.Contains("CombatInputGuards.DescribePanicInterruption(player)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void World_level_blocker_stays_aligned_with_the_interruption_list()
    {
        var source = Guard();

        Assert.Contains("internal static bool HasWorldLevelBlocker()", source, StringComparison.Ordinal);
        Assert.Contains("Game1.eventUp", source, StringComparison.Ordinal);
        Assert.Contains("Game1.farmEvent is not null", source, StringComparison.Ordinal);
        Assert.Contains("Game1.currentMinigame is not null", source, StringComparison.Ordinal);
        Assert.Contains("Game1.isWarping", source, StringComparison.Ordinal);
        Assert.Contains("Game1.fadeToBlack", source, StringComparison.Ordinal);
        Assert.Contains("Game1.killScreen", source, StringComparison.Ordinal);
    }

    private static string Guard()
    {
        return ReadContract("CombatInputGuards.cs");
    }

    private static string PanicLoopService()
    {
        return ReadContract("SanityIdlePanicLoopService.cs");
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "CombatAutomation", fileName)
        );
    }
}
