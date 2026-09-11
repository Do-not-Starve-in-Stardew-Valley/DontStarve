#nullable enable

using DontStarve.Player.Stats.Food;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class SeedFoodInputRulesTests
{
    [Fact]
    public void WorldMenuAndGameplayGatesAreRequired()
    {
        var valid = Context();
        Assert.True(valid.IsEligible);
        Assert.False((valid with { WorldReady = false }).IsEligible);
        Assert.False((valid with { HasActiveMenu = true }).IsEligible);
        Assert.False((valid with { HasActiveEvent = true }).IsEligible);
        Assert.False((valid with { HasActiveMinigame = true }).IsEligible);
        Assert.False((valid with { IsFading = true }).IsEligible);
        Assert.False((valid with { IsEating = true }).IsEligible);
        Assert.False((valid with { Enabled = false }).IsEligible);
        Assert.False((valid with { IsTargetSeed = false }).IsEligible);
    }

    [Fact]
    public void AOrXAloneDoesNotTriggerButTheOverlapDoesOnce()
    {
        var state = new SeedFoodGamepadChordState();
        var context = Context();

        Assert.Null(state.Press(SeedFoodGamepadButton.A, context));
        state.Release(SeedFoodGamepadButton.A);
        Assert.Null(state.Press(SeedFoodGamepadButton.X, context));
    }

    [Fact]
    public void GamepadChordCandidateIsProducedOnSecondButtonAndResetsAfterBothRelease()
    {
        var state = new SeedFoodGamepadChordState();
        var context = Context();

        Assert.Null(state.Press(SeedFoodGamepadButton.A, context));
        var candidate = state.Press(SeedFoodGamepadButton.X, context);
        Assert.NotNull(candidate);
        Assert.Equal(2, candidate.Value.Slot);
        Assert.Null(state.Press(SeedFoodGamepadButton.X, context));

        state.Release(SeedFoodGamepadButton.A);
        state.Release(SeedFoodGamepadButton.X);
        Assert.Null(state.Press(SeedFoodGamepadButton.X, context));
        Assert.NotNull(state.Press(SeedFoodGamepadButton.A, context));
    }

    [Fact]
    public void DisabledChordDoesNotTriggerAndDoesNotLeakIntoNextEnabledChord()
    {
        var state = new SeedFoodGamepadChordState();
        var disabled = Context() with { Enabled = false };
        Assert.Null(state.Press(SeedFoodGamepadButton.A, disabled));
        Assert.Null(state.Press(SeedFoodGamepadButton.X, disabled));
        state.Reset();

        Assert.Null(state.Press(SeedFoodGamepadButton.A, Context()));
        Assert.NotNull(state.Press(SeedFoodGamepadButton.X, Context()));
    }

    [Fact]
    public void TouchShortPressDragLeaveStackChangeAndMultitouchCancel()
    {
        var state = new SeedFoodTouchGestureState();
        var context = Context();
        var origin = new SeedFoodPoint(100, 200);

        Assert.True(state.TryBegin(7, origin, 1000, context));
        Assert.Null(state.Release(7, origin, 1999, context));

        Assert.True(state.TryBegin(7, origin, 3000, context));
        state.Observe(7, new SeedFoodPoint(116, 200), context, multiplePointers: false);
        Assert.False(state.IsActive);

        Assert.True(state.TryBegin(7, origin, 5000, context));
        state.Observe(7, origin, context with { Slot = 3 }, multiplePointers: false);
        Assert.False(state.IsActive);

        Assert.True(state.TryBegin(7, origin, 7000, context));
        state.Observe(7, origin, context with { Stack = 2 }, multiplePointers: false);
        Assert.False(state.IsActive);

        Assert.True(state.TryBegin(7, origin, 9000, context));
        state.Observe(7, origin, context, multiplePointers: true);
        Assert.False(state.IsActive);
    }

    [Fact]
    public void TouchTriggersOnlyAfterOneSecondAndReleaseAtOriginalPoint()
    {
        var state = new SeedFoodTouchGestureState();
        var context = Context();
        var origin = new SeedFoodPoint(100, 200);

        Assert.True(state.TryBegin(4, origin, 1000, context));
        var candidate = state.Release(4, origin, 2000, context);
        Assert.NotNull(candidate);
        Assert.Equal(SeedFoodInputDevice.Touch, candidate.Value.Device);

        Assert.True(state.TryBegin(4, origin, 3000, context));
        Assert.Null(state.Release(4, new SeedFoodPoint(116, 200), 4000, context));
    }

    private static SeedFoodInputContext Context()
    {
        return new SeedFoodInputContext(
            Enabled: true,
            WorldReady: true,
            HasActiveMenu: false,
            HasActiveEvent: false,
            HasActiveMinigame: false,
            IsFading: false,
            IsEating: false,
            IsTargetSeed: true,
            Slot: 2,
            ItemId: "ModCrop.Seeds",
            Stack: 4
        );
    }
}
