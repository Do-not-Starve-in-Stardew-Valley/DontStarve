using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmVanillaPassOutBridgeTests
{
    [Theory]
    [InlineData(100, 100, 20)]
    [InlineData(100, 275, 55)]
    [InlineData(8, 100, 8)]
    [InlineData(0, 100, 0)]
    public void NonLethalWakeHealthUsesCapturedHealthAndCeilingTwentyPercentFloor(
        int capturedHealth,
        int maximumHealth,
        int expected
    )
    {
        Assert.Equal(
            expected,
            SanityTwoAmVanillaPassOutPolicy.ResolveNonLethalWakeHealth(
                capturedHealth,
                maximumHealth
            )
        );
    }
}
