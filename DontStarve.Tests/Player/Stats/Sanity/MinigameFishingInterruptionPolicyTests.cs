using DontStarve.Player.Stats.Sanity.Minigames;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MinigameFishingInterruptionPolicyTests
{
    [Fact]
    public void ActualMonsterDamageDuringBobberBarInterrupts()
    {
        Assert.True(
            MinigameFishingInterruptionPolicy.ShouldInterrupt(
                isMultiplayer: true,
                blockingEnabled: true,
                hasBobberBar: true,
                bobberBarResultHandled: false,
                damageSourceIsMonster: true,
                healthBefore: 100,
                healthAfter: 99
            )
        );
    }

    [Theory]
    [InlineData(false, true, true, false, true, 100, 99)]
    [InlineData(true, false, true, false, true, 100, 99)]
    [InlineData(true, true, false, false, true, 100, 99)]
    [InlineData(true, true, true, true, true, 100, 99)]
    [InlineData(true, true, true, false, false, 100, 99)]
    [InlineData(true, true, true, false, true, 100, 100)]
    [InlineData(true, true, true, false, true, 100, 101)]
    public void NonMatchingDamageStatesDoNotInterrupt(
        bool isMultiplayer,
        bool blockingEnabled,
        bool hasBobberBar,
        bool bobberBarResultHandled,
        bool damageSourceIsMonster,
        int healthBefore,
        int healthAfter
    )
    {
        Assert.False(
            MinigameFishingInterruptionPolicy.ShouldInterrupt(
                isMultiplayer,
                blockingEnabled,
                hasBobberBar,
                bobberBarResultHandled,
                damageSourceIsMonster,
                healthBefore,
                healthAfter
            )
        );
    }

    [Fact]
    public void SinglePlayerWaitingForBiteDoesNotInterrupt()
    {
        Assert.False(
            MinigameFishingInterruptionPolicy.ShouldInterrupt(
                isMultiplayer: false,
                blockingEnabled: true,
                hasBobberBar: false,
                bobberBarResultHandled: false,
                damageSourceIsMonster: true,
                healthBefore: 100,
                healthAfter: 99
            )
        );
    }
}
