using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class NpcFlashlightPolicyTests
{
    [Fact]
    public void FullDarkAndCompletedNightPlansEnableTheFlashlight()
    {
        Assert.True(
            ShouldEmit(NaturalDarknessPhase.FullDark, lightmapWhiteBlend: 0f)
        );
        Assert.True(
            ShouldEmit(
                NaturalDarknessPhase.NightThreeSeconds,
                NpcFlashlightPolicy.FullyDarkWhiteBlendThreshold
            )
        );
        Assert.True(
            ShouldEmit(NaturalDarknessPhase.MineNight, lightmapWhiteBlend: 1f)
        );
    }

    [Fact]
    public void EarlyNightAndMineDuskDoNotEnableTheFlashlight()
    {
        Assert.False(
            ShouldEmit(
                NaturalDarknessPhase.NightThreeSeconds,
                NpcFlashlightPolicy.FullyDarkWhiteBlendThreshold - 0.001f
            )
        );
        Assert.False(ShouldEmit(NaturalDarknessPhase.MineDusk, 1f));
        Assert.False(ShouldEmit(NaturalDarknessPhase.None, 1f));
    }

    [Fact]
    public void KrobusAndMonstersAreNotEligibleForNpcFlashlights()
    {
        Assert.False(
            NpcFlashlightPolicy.IsEligibleNpc(
                NpcFlashlightPolicy.KrobusName,
                isMonster: false
            )
        );
        Assert.False(
            NpcFlashlightPolicy.IsEligibleNpc("Green Slime", isMonster: true)
        );
        Assert.True(
            NpcFlashlightPolicy.IsEligibleNpc("Abigail", isMonster: false)
        );
    }

    [Theory]
    [InlineData(0, -1.5707964f)]
    [InlineData(1, 0f)]
    [InlineData(2, 1.5707964f)]
    [InlineData(3, 3.1415927f)]
    public void StandardNpcDirectionsMapToForwardConeRotations(
        int facingDirection,
        float expectedRadians
    )
    {
        Assert.Equal(
            expectedRadians,
            NpcFlashlightPolicy.GetConeRotationRadians(facingDirection),
            precision: 5
        );
    }

    [Fact]
    public void FlashlightGeometryKeepsTheRequestedBodyRadiusAndConeDimensions()
    {
        Assert.Equal(1f, NpcFlashlightPolicy.BodyLightRadiusTiles);
        Assert.Equal(5f, NpcFlashlightPolicy.ConeRangeTiles);
        Assert.Equal(320, NpcFlashlightPolicy.ConeRangePixels);
        Assert.Equal(45f, NpcFlashlightPolicy.ConeAngleDegrees);
        Assert.InRange(
            NpcFlashlightPolicy.GetConeHalfWidthPixels(
                NpcFlashlightPolicy.ConeRangePixels
            ),
            132f,
            133f
        );
        Assert.Equal(268, NpcFlashlightPolicy.GetConeTextureHeightPixels());
    }

    private static bool ShouldEmit(
        NaturalDarknessPhase phase,
        float lightmapWhiteBlend
    )
    {
        return NpcFlashlightPolicy.ShouldEmit(
            new NpcFlashlightSceneInput(phase, lightmapWhiteBlend)
        );
    }
}
