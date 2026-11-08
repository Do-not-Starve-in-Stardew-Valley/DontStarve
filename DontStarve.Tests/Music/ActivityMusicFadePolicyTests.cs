using DontStarve.Music;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicFadePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WorkAndFarmingUseTheMeasuredDstFiveSecondTransition(int rawKind)
    {
        var kind = (ActivityMusicKind)rawKind;
        Assert.Equal(5f, ActivityMusicFadePolicy.GetFadeInSeconds(kind));
        Assert.Equal(5f, ActivityMusicFadePolicy.GetFadeOutSeconds(kind));
    }

    [Fact]
    public void CombatUsesImmediateStartAndMeasuredReleaseTail()
    {
        Assert.Equal(0f, ActivityMusicFadePolicy.GetFadeInSeconds(ActivityMusicKind.Combat));
        Assert.Equal(1f, ActivityMusicFadePolicy.GetFadeOutSeconds(ActivityMusicKind.Combat));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void DawnDuskExternalUpdatesAdvanceOnlyWorkAndFarmingFadeOuts(
        int rawKind,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ActivityMusicFadePolicy.ShouldAdvanceDuringDawnDusk((ActivityMusicKind)rawKind)
        );
    }

    [Fact]
    public void CombatImmediatelyPreemptsWorkAndFarming()
    {
        Assert.True(
            ActivityMusicFadePolicy.IsImmediateCombatPreemption(
                ActivityMusicKind.Combat,
                ActivityMusicKind.Work
            )
        );
        Assert.True(
            ActivityMusicFadePolicy.IsImmediateCombatPreemption(
                ActivityMusicKind.Combat,
                ActivityMusicKind.Farming
            )
        );
        Assert.False(
            ActivityMusicFadePolicy.IsImmediateCombatPreemption(
                ActivityMusicKind.Work,
                ActivityMusicKind.Combat
            )
        );
        Assert.False(
            ActivityMusicFadePolicy.IsImmediateCombatPreemption(
                ActivityMusicKind.Farming,
                ActivityMusicKind.Work
            )
        );
    }

    [Fact]
    public void FadeInMovesTowardTargetAndReachesItAtTheBound()
    {
        const float duration = 5f;
        var halfway = ActivityMusicFadePolicy.Advance(
            0f,
            1f,
            duration / 2,
            duration
        );
        var complete = ActivityMusicFadePolicy.Advance(
            halfway,
            1f,
            duration,
            duration
        );

        Assert.InRange(halfway, 0.49f, 0.51f);
        Assert.Equal(1f, complete);
    }

    [Fact]
    public void FadeOutMovesTowardSilenceAndNeverOvershoots()
    {
        const float duration = 5f;
        var halfway = ActivityMusicFadePolicy.Advance(
            1f,
            0f,
            duration / 2,
            duration
        );
        var complete = ActivityMusicFadePolicy.Advance(
            halfway,
            0f,
            duration,
            duration
        );

        Assert.InRange(halfway, 0.49f, 0.51f);
        Assert.Equal(0f, complete);
    }

    [Fact]
    public void InvalidElapsedTimeDoesNotChangeVolume()
    {
        Assert.Equal(
            0.4f,
            ActivityMusicFadePolicy.Advance(0.4f, 1f, -1, 5f)
        );
    }
}
