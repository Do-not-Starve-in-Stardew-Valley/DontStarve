#nullable enable

using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerEquipmentRulesTests
{
    [Fact]
    public void AllEquipmentMultipliersAreMultipliedAndMissingSlotsAreNeutral()
    {
        var loadout = new HungerEquipmentLoadout(
            hat: new HungerEquipmentEffect(0.8d, 1.5d),
            shirt: new HungerEquipmentEffect(1.3d, 2d)
        );

        Assert.Equal(1.04d, HungerEquipmentRules.CalculateConsumptionMultiplier(loadout), 12);
        Assert.Equal(3.5d, HungerEquipmentRules.CalculateFixedRecoveryPerMinute(loadout), 12);
    }

    [Fact]
    public void MissingEquipmentHasNeutralMultiplierAndZeroFixedRecovery()
    {
        var loadout = new HungerEquipmentLoadout();

        Assert.Equal(1d, HungerEquipmentRules.CalculateConsumptionMultiplier(loadout), 12);
        Assert.Equal(0d, HungerEquipmentRules.CalculateFixedRecoveryPerMinute(loadout), 12);
        Assert.Equal(0d, HungerEquipmentRules.CalculateFixedRecoveryForMinutes(loadout, 0), 12);
    }

    [Fact]
    public void FixedRecoveryIsSeparateFromNaturalConsumptionAndScalesByValidMinutes()
    {
        var loadout = new HungerEquipmentLoadout(
            hat: new HungerEquipmentEffect(0.8d, 2d)
        );

        Assert.Equal(2d, HungerEquipmentRules.CalculateFixedRecoveryForMinutes(loadout, 1), 12);
        Assert.Equal(6d, HungerEquipmentRules.CalculateFixedRecoveryForMinutes(loadout, 3), 12);
        Assert.Equal(0.0416d, HungerRules.CalculateNaturalLoss(
            1,
            HungerEquipmentRules.CalculateConsumptionMultiplier(loadout)
        ), 12);
    }
}
