#nullable enable

using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class HealthDeathRecoveryRetryPolicyTests
{
    [Theory]
    [InlineData(true, false, 10, false, true, true, true, true, true)]
    [InlineData(false, false, 10, false, true, true, true, true, false)]
    [InlineData(true, true, 10, false, true, true, true, true, false)]
    [InlineData(true, false, 0, false, true, true, true, true, false)]
    [InlineData(true, false, 10, true, true, true, true, true, false)]
    [InlineData(true, false, 10, false, false, true, true, true, false)]
    [InlineData(true, false, 10, false, true, false, true, true, false)]
    [InlineData(true, false, 10, false, true, true, false, true, false)]
    [InlineData(true, false, 10, false, true, true, true, false, false)]
    public void Queue_requires_a_confirmed_host_transition_during_load(
        bool wasKillScreen,
        bool isKillScreen,
        int health,
        bool worldReady,
        bool isMainPlayer,
        bool isHost,
        bool sessionValid,
        bool farmerAvailable,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            HealthDeathRecoveryRetryPolicy.ShouldQueue(
                wasKillScreen,
                isKillScreen,
                health,
                worldReady,
                isMainPlayer,
                isHost,
                sessionValid,
                farmerAvailable
            )
        );
    }

    [Theory]
    [InlineData(true, 10, false, true, true, true, true)]
    [InlineData(true, 10, false, false, true, true, true)]
    [InlineData(true, 10, true, true, true, true, true)]
    [InlineData(false, 10, false, true, true, true, true)]
    public void Retry_requires_the_same_transition_to_be_still_recoverable(
        bool pending,
        int health,
        bool isKillScreen,
        bool worldReady,
        bool isMainPlayer,
        bool isHost,
        bool sessionValid
    )
    {
        var expected = pending
            && health > 0
            && !isKillScreen
            && worldReady
            && isMainPlayer
            && isHost
            && sessionValid;

        Assert.Equal(
            expected,
            HealthDeathRecoveryRetryPolicy.ShouldAttempt(
                pending,
                health,
                isKillScreen,
                worldReady,
                isMainPlayer,
                isHost,
                sessionValid
            )
        );
    }
}
