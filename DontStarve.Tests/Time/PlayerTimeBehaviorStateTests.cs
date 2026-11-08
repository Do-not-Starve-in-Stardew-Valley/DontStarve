using DontStarve.Player.Stats;
using Xunit;

namespace DontStarve.Tests.Time;

public sealed class PlayerTimeBehaviorStateTests
{
    [Fact]
    public void RewindWaitBelongsToThePlayerKeyThatWasRewound()
    {
        var state = new PlayerTimeBehaviorState();
        state.AddRewindWait("host", -2);

        Assert.False(state.TryEnterMinute("host", isSleeping: false));
        Assert.False(state.TryEnterMinute("host", isSleeping: false));
        Assert.True(state.TryEnterMinute("host", isSleeping: false));
        Assert.True(state.TryEnterMinute("farmhand", isSleeping: false));
    }

    [Fact]
    public void PositiveSyncDoesNotCreateWait()
    {
        var state = new PlayerTimeBehaviorState();

        state.AddRewindWait("host", 3);

        Assert.True(state.TryEnterMinute("host", isSleeping: false));
    }

    [Fact]
    public void SleepingPlayerDoesNotConsumeOrCreateOrdinaryTimeBehaviorWork()
    {
        var state = new PlayerTimeBehaviorState();
        state.AddRewindWait("host", -1);

        Assert.False(state.TryEnterMinute("host", isSleeping: true));
        Assert.False(state.TryEnterMinute("host", isSleeping: false));
        Assert.True(state.TryEnterMinute("host", isSleeping: false));
    }

    [Fact]
    public void CapturedWaitsExcludeConsumedAndInvalidEntries()
    {
        var state = new PlayerTimeBehaviorState();
        state.AddRewindWait("host", -2);
        state.AddRewindWait("farmhand", -1);

        Assert.False(state.TryEnterMinute("host", isSleeping: false));

        var waits = state.CaptureWaits();

        Assert.Equal(1, waits["host"]);
        Assert.Equal(1, waits["farmhand"]);
    }

    [Fact]
    public void ClearRemovesOnlyTheSelectedPlayerWait()
    {
        var state = new PlayerTimeBehaviorState();
        state.AddRewindWait("host", -2);
        state.AddRewindWait("farmhand", -1);

        state.Clear("host");

        Assert.True(state.TryEnterMinute("host", isSleeping: false));
        Assert.False(state.TryEnterMinute("farmhand", isSleeping: false));
    }
}
