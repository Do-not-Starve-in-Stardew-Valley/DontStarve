using DontStarve.Player.Stats.Sanity.Minigames;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MinigameDangerGatePolicyTests
{
    [Fact]
    public void DisabledConfigurationDoesNotBlockOrChangeVisualPolicy()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: false,
            storyException: false,
            nearbyMonster: true,
            hostileShadowTargeting: true,
            starving: true,
            isMultiplayer: true,
            multiplayerDanger: true,
            multiplayerStateAvailable: true
        );

        Assert.True(decision.Allowed);
        Assert.Equal(MinigameDangerReason.None, decision.Reason);
    }

    [Fact]
    public void AbigailStoryExceptionWinsOverEveryDangerCondition()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: true,
            nearbyMonster: true,
            hostileShadowTargeting: true,
            starving: true,
            isMultiplayer: true,
            multiplayerDanger: true,
            multiplayerStateAvailable: false
        );

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AbigailGameIsNotAnExceptionWithoutTheValidatedStoryContext()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster: true,
            hostileShadowTargeting: false,
            starving: false,
            isMultiplayer: false,
            multiplayerDanger: false,
            multiplayerStateAvailable: true
        );

        Assert.False(decision.Allowed);
        Assert.Equal(MinigameDangerReason.NearbyMonster, decision.Reason);
    }

    [Theory]
    [InlineData(true, false, false, false, true, 1)]
    [InlineData(false, true, false, false, true, 2)]
    [InlineData(false, false, false, true, true, 3)]
    [InlineData(false, false, false, false, false, 4)]
    [InlineData(false, false, true, false, true, 5)]
    public void EachDangerSourceBlocksWhenEnabled(
        bool nearbyMonster,
        bool hostileShadowTargeting,
        bool starving,
        bool multiplayerDanger,
        bool multiplayerStateAvailable,
        int expectedReason
    )
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster,
            hostileShadowTargeting,
            starving,
            isMultiplayer: true,
            multiplayerDanger,
            multiplayerStateAvailable
        );

        Assert.False(decision.Allowed);
        Assert.Equal((MinigameDangerReason)expectedReason, decision.Reason);
    }

    [Fact]
    public void StarvingPlayerIsBlockedEvenInSinglePlayer()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster: false,
            hostileShadowTargeting: false,
            starving: true,
            isMultiplayer: false,
            multiplayerDanger: false,
            multiplayerStateAvailable: true
        );

        Assert.False(decision.Allowed);
        Assert.Equal(MinigameDangerReason.Starving, decision.Reason);
    }

    [Fact]
    public void PhysicalDangerReasonsWinOverStarvationForTheReportedReason()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster: true,
            hostileShadowTargeting: false,
            starving: true,
            isMultiplayer: false,
            multiplayerDanger: false,
            multiplayerStateAvailable: true
        );

        Assert.False(decision.Allowed);
        Assert.Equal(MinigameDangerReason.NearbyMonster, decision.Reason);
    }

    [Fact]
    public void StarvingPlayerIsBlockedEvenWhenMultiplayerStateIsUnavailable()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster: false,
            hostileShadowTargeting: false,
            starving: true,
            isMultiplayer: true,
            multiplayerDanger: false,
            multiplayerStateAvailable: false
        );

        Assert.False(decision.Allowed);
        Assert.Equal(MinigameDangerReason.Starving, decision.Reason);
    }

    [Fact]
    public void SinglePlayerDoesNotUseDangerTierAsASeparateCondition()
    {
        var decision = MinigameDangerGatePolicy.Evaluate(
            blockingEnabled: true,
            storyException: false,
            nearbyMonster: false,
            hostileShadowTargeting: false,
            starving: false,
            isMultiplayer: false,
            multiplayerDanger: true,
            multiplayerStateAvailable: false
        );

        Assert.True(decision.Allowed);
    }
}
