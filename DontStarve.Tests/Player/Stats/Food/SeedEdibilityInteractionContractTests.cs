using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class SeedEdibilityInteractionContractTests
{
    [Fact]
    public void Touch_mouse_left_bridge_preempts_vanilla_only_after_capturing_a_touch_target()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "SeedInput",
                "SeedEdibilityInteractionService.cs"
            )
        );

        Assert.Contains("e.Button == SButton.MouseLeft", source, StringComparison.Ordinal);
        Assert.Contains("TryBeginTouchFromButtonEvent();", source, StringComparison.Ordinal);
        Assert.Contains("CompleteTouchFromButtonEvent();", source, StringComparison.Ordinal);
        Assert.Contains("helper.Input.Suppress(SButton.MouseLeft);", source, StringComparison.Ordinal);
        Assert.Contains("ReplayNativeToolbarClick", source, StringComparison.Ordinal);
        Assert.Contains("Environment.TickCount64", source, StringComparison.Ordinal);
        Assert.Contains("who.eatHeldObject();", source, StringComparison.Ordinal);
        Assert.Contains("if (touches.Count != 1)", source, StringComparison.Ordinal);
        Assert.Contains("if (touches.Count > 1)", source, StringComparison.Ordinal);
        Assert.Contains("touch.LastPosition", source, StringComparison.Ordinal);
        Assert.Contains("touch.PointerId", source, StringComparison.Ordinal);
        Assert.Contains("VanillaActionButtonGuardInstalled", source, StringComparison.Ordinal);
        Assert.Contains("TouchLocationState.Released", source, StringComparison.Ordinal);
        Assert.Contains("e.Button == SButton.MouseRight", source, StringComparison.Ordinal);
        Assert.Contains("e.Button == SButton.ControllerA", source, StringComparison.Ordinal);
        Assert.Contains("e.Button == SButton.ControllerX", source, StringComparison.Ordinal);
    }
}
