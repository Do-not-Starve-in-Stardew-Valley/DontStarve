using DontStarve.Music;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class MusicFeatureSwitchboardTests
{
    [Fact]
    public void ChangingOneMusicLaneDoesNotToggleTheOtherLane()
    {
        var dawnDuskTransitions = new List<bool>();
        var activityTransitions = new List<bool>();
        var switchboard = new MusicFeatureSwitchboard(
            dawnDuskTransitions.Add,
            activityTransitions.Add
        );
        switchboard.Initialize(dawnDuskEnabled: true, activityMusicEnabled: true);
        switchboard.SetDawnDuskEnabled(false);

        Assert.False(switchboard.DawnDuskEnabled);
        Assert.True(switchboard.ActivityMusicEnabled);
        Assert.Equal(new[] { true, false }, dawnDuskTransitions);
        Assert.Equal(new[] { true }, activityTransitions);

        switchboard.SetActivityMusicEnabled(false);
        switchboard.SetDawnDuskEnabled(false);

        Assert.Equal(new[] { true, false }, dawnDuskTransitions);
        Assert.Equal(new[] { true, false }, activityTransitions);
    }
}
