using DontStarve.Music;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicArbitrationTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 15)]
    [InlineData(3, 10)]
    public void ActivityDurationsMatchTheFrozenRetentionContract(int rawKind, int expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            ActivityMusicArbitration.GetDurationSeconds((ActivityMusicKind)rawKind)
        );
    }

    [Fact]
    public void CombatReplacesFarmingAndLowerPriorityWorkCannotReplaceCombat()
    {
        Assert.Equal(
            ActivityMusicStateTransition.Replaced,
            ActivityMusicArbitration.ResolveTransition(
                ActivityMusicKind.Farming,
                ActivityMusicKind.Combat,
                sameTrack: false
            )
        );
        Assert.Equal(
            ActivityMusicStateTransition.IgnoredLowerPriority,
            ActivityMusicArbitration.ResolveTransition(
                ActivityMusicKind.Combat,
                ActivityMusicKind.Work,
                sameTrack: false
            )
        );
    }

    [Fact]
    public void SameTrackRefreshesWithoutReplacementAndTrackChangeReplaces()
    {
        Assert.Equal(
            ActivityMusicStateTransition.Refreshed,
            ActivityMusicArbitration.ResolveTransition(
                ActivityMusicKind.Work,
                ActivityMusicKind.Work,
                sameTrack: true
            )
        );
        Assert.Equal(
            ActivityMusicStateTransition.Replaced,
            ActivityMusicArbitration.ResolveTransition(
                ActivityMusicKind.Work,
                ActivityMusicKind.Work,
                sameTrack: false
            )
        );
    }

}
