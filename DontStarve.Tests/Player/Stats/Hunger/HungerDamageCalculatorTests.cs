using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerDamageCalculatorTests
{
    [Fact]
    public void Default_mode_returns_direct_health_reduction_without_weapon_damage()
    {
        var result = new HungerDamageCalculator().Calculate(HungerDamageMode.Default, 20, 100);

        Assert.Equal(1, result.Damage);
        Assert.Equal(19, result.RemainingHealth);
    }

    [Fact]
    public void Non_lethal_mode_stops_at_hunger_floor()
    {
        var result = new HungerDamageService(HungerDamageMode.NonLethal).Resolve(21, 100);

        Assert.Equal(1, result.Damage);
        Assert.Equal(20, result.RemainingHealth);
        Assert.Equal(20, result.NonLethalFloor);
    }

    [Fact]
    public void Disabled_mode_has_no_health_effect()
    {
        var result = new HungerDamageService(HungerDamageMode.Disabled).Resolve(20, 100);

        Assert.Equal(0, result.Damage);
        Assert.Equal(20, result.RemainingHealth);
    }
}
