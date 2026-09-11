using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityMinigameVisualClassifierTests
{
    [Fact]
    public void BobberBarIsFishingEvenWhenAnEventAlsoReportsAnotherMinigame()
    {
        var context = SanityMinigameVisualClassifier.Resolve(
            hasBobberBar: true,
            isExhibitionFishing: false,
            isIceFishing: false,
            hasOtherMinigame: true
        );

        Assert.Equal(SanityMinigameVisualContext.Fishing, context);
    }

    [Theory]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, false, true, 1)]
    [InlineData(false, false, false, 2)]
    public void FishingWhitelistIsSeparatedFromOtherMinigames(
        bool hasBobberBar,
        bool isExhibitionFishing,
        bool isIceFishing,
        int expected
    )
    {
        var context = SanityMinigameVisualClassifier.Resolve(
            hasBobberBar,
            isExhibitionFishing,
            isIceFishing,
            hasOtherMinigame: expected == 2
        );

        Assert.Equal((SanityMinigameVisualContext)expected, context);
    }

    [Fact]
    public void NoMinigameKeepsNormalVisualRules()
    {
        var context = SanityMinigameVisualClassifier.Resolve(
            hasBobberBar: false,
            isExhibitionFishing: false,
            isIceFishing: false,
            hasOtherMinigame: false
        );

        Assert.Equal(SanityMinigameVisualContext.None, context);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public void Only_other_minigames_pause_local_low_sanity_audio(
        int contextValue,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            SanityMinigameVisualClassifier.ShouldPauseLocalAudio(
                (SanityMinigameVisualContext)contextValue
            )
        );
    }

    [Fact]
    public void NonWhitelistedFishingGameIsOther()
    {
        // A FishingGame outside fall16 is represented by the generic Other input. This protects
        // the visual whitelist from silently expanding to every festival FishingGame.
        var context = SanityMinigameVisualClassifier.Resolve(
            hasBobberBar: false,
            isExhibitionFishing: false,
            isIceFishing: false,
            hasOtherMinigame: true
        );

        Assert.Equal(SanityMinigameVisualContext.Other, context);
    }

}
