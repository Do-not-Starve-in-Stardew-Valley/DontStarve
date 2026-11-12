using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerFishingCastGatePolicyTests
{
    [Fact]
    public void StarvingLocalPlayerCannotStartANewCast()
    {
        Assert.True(
            HungerFishingCastGatePolicy.ShouldBlock(
                isLocalPlayer: true,
                eventUp: false,
                hungerEnabled: true,
                starving: true
            )
        );
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    public void OnlyStarvingLocalPlayersOutsideEventsAreBlocked(
        bool isLocalPlayer,
        bool eventUp,
        bool hungerEnabled,
        bool starving
    )
    {
        Assert.False(
            HungerFishingCastGatePolicy.ShouldBlock(
                isLocalPlayer,
                eventUp,
                hungerEnabled,
                starving
            )
        );
    }

    [Fact]
    public void DisabledHungerSystemNeverBlocksEvenWithSavedZeroHunger()
    {
        // 与睡眠危险确认同一条口径：饥饿系统关闭时存档残留的 0 值不得误拦。
        Assert.False(
            HungerFishingCastGatePolicy.ShouldBlock(
                isLocalPlayer: true,
                eventUp: false,
                hungerEnabled: false,
                starving: true
            )
        );
    }
}
