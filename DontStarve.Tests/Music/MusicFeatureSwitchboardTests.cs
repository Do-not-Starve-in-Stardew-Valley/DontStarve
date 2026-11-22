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
        var farmingTransitions = new List<bool>();
        var switchboard = new MusicFeatureSwitchboard(
            dawnDuskTransitions.Add,
            activityTransitions.Add,
            farmingTransitions.Add
        );
        switchboard.Initialize(
            dawnDuskEnabled: true,
            activityMusicEnabled: true,
            farmingMusicEnabled: true
        );
        switchboard.SetDawnDuskEnabled(false);

        Assert.False(switchboard.DawnDuskEnabled);
        Assert.True(switchboard.ActivityMusicEnabled);
        Assert.True(switchboard.FarmingMusicEnabled);
        Assert.Equal(new[] { true, false }, dawnDuskTransitions);
        Assert.Equal(new[] { true }, activityTransitions);
        Assert.Equal(new[] { true }, farmingTransitions);

        switchboard.SetActivityMusicEnabled(false);
        switchboard.SetDawnDuskEnabled(false);

        Assert.Equal(new[] { true, false }, dawnDuskTransitions);
        Assert.Equal(new[] { true, false }, activityTransitions);
        Assert.Equal(new[] { true }, farmingTransitions);
    }

    [Fact]
    public void ChangingFarmingMusicDoesNotToggleTheMasterLanes()
    {
        // 耕种音乐是活动音乐总开关下的半独立子开关：它自己的开合不得触碰两条主车道，
        // 总开关关闭时它依然可以继续变化（生效与否由运行时总开关决定）。
        var dawnDuskTransitions = new List<bool>();
        var activityTransitions = new List<bool>();
        var farmingTransitions = new List<bool>();
        var switchboard = new MusicFeatureSwitchboard(
            dawnDuskTransitions.Add,
            activityTransitions.Add,
            farmingTransitions.Add
        );
        switchboard.Initialize(
            dawnDuskEnabled: true,
            activityMusicEnabled: true,
            farmingMusicEnabled: true
        );

        switchboard.SetFarmingMusicEnabled(false);

        Assert.False(switchboard.FarmingMusicEnabled);
        Assert.True(switchboard.DawnDuskEnabled);
        Assert.True(switchboard.ActivityMusicEnabled);
        Assert.Equal(new[] { true }, dawnDuskTransitions);
        Assert.Equal(new[] { true }, activityTransitions);
        Assert.Equal(new[] { true, false }, farmingTransitions);

        // 总开关关闭后，子开关继续切换不产生主车道回调。
        switchboard.SetActivityMusicEnabled(false);
        switchboard.SetFarmingMusicEnabled(true);

        Assert.False(switchboard.ActivityMusicEnabled);
        Assert.True(switchboard.FarmingMusicEnabled);
        Assert.Equal(new[] { true, false }, activityTransitions);
        Assert.Equal(new[] { true, false, true }, farmingTransitions);
    }
}
