using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerEquipmentDataTests
{
    [Fact]
    public void EmptyEquipmentTableIsAvailableAndUsesNeutralDefaults()
    {
        var result = HungerEquipmentDataLoader.Parse("{}");

        Assert.Equal(HungerEquipmentDataStatus.Available, result.Status);
        Assert.Empty(result.Values);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ValidItemRecordReadsBothFieldsByItemId()
    {
        var result = HungerEquipmentDataLoader.Parse(
            "{\"123\":{\"ConsumptionMultiplier\":0.8,\"FixedRecovery\":1.25}}"
        );

        Assert.Equal(HungerEquipmentDataStatus.Available, result.Status);
        Assert.True(result.Values.TryGetValue("123", out var effect));
        Assert.Equal(0.8d, effect.ConsumptionMultiplier);
        Assert.Equal(1.25d, effect.FixedRecoveryPerMinute);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[]")]
    public void InvalidEquipmentRootFailsClosedWithStableDiagnostics(string json)
    {
        var result = HungerEquipmentDataLoader.Parse(json);

        Assert.Equal(HungerEquipmentDataStatus.Unavailable, result.Status);
        Assert.Empty(result.Values);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("{\"123\":{\"ConsumptionMultiplier\":0}}")]
    [InlineData("{\"123\":{\"Unknown\":1}}")]
    public void InvalidEquipmentEntryDegradesWithoutUsingItsValue(string json)
    {
        var result = HungerEquipmentDataLoader.Parse(json);

        Assert.Equal(HungerEquipmentDataStatus.Degraded, result.Status);
        Assert.Empty(result.Values);
        Assert.Contains("equipment.invalid-entry:123", result.Diagnostics);
    }

    [Fact]
    public void OneBadRecordDoesNotDiscardOtherEquipmentRecords()
    {
        var result = HungerEquipmentDataLoader.Parse(
            "{\"bad\":{\"FixedRecovery\":-1},\"good\":{\"FixedRecovery\":2}}"
        );

        Assert.Equal(HungerEquipmentDataStatus.Degraded, result.Status);
        Assert.DoesNotContain("bad", result.Values.Keys);
        Assert.True(result.Values.TryGetValue("good", out var effect));
        Assert.Equal(2d, effect.FixedRecoveryPerMinute);
        Assert.Contains("equipment.invalid-entry:bad", result.Diagnostics);
    }
}
