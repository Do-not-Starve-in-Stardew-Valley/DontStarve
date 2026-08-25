using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityWorldColourPolicyTests
{
    [Theory]
    [InlineData(1750, 1800, 2000, 0)]
    [InlineData(1800, 1800, 2000, 1)]
    [InlineData(1950, 1800, 2000, 1)]
    [InlineData(2000, 1800, 2000, 2)]
    [InlineData(1500, 1500, 1700, 1)]
    public void Phase_is_selected_from_the_existing_stardew_light_boundaries(
        int timeOfDay,
        int duskStart,
        int nightStart,
        int expectedPhase
    )
    {
        Assert.Equal(
            (SanityWorldColourPhase)expectedPhase,
            SanityWorldColourPolicy.ResolvePhase(
                timeOfDay,
                duskStart,
                nightStart
            )
        );
    }

    [Fact]
    public void Phase_vertex_codes_are_stable_and_distinct()
    {
        Assert.Equal(
            SanityWorldColourPolicy.DayVertexCode,
            SanityWorldColourPolicy.ToVertexCode(SanityWorldColourPhase.Day)
        );
        Assert.Equal(
            SanityWorldColourPolicy.DuskVertexCode,
            SanityWorldColourPolicy.ToVertexCode(SanityWorldColourPhase.Dusk)
        );
        Assert.Equal(
            SanityWorldColourPolicy.NightVertexCode,
            SanityWorldColourPolicy.ToVertexCode(SanityWorldColourPhase.Night)
        );
        Assert.True(
            SanityWorldColourPolicy.DayVertexCode
                < SanityWorldColourPolicy.DuskVertexCode
        );
        Assert.True(
            SanityWorldColourPolicy.DuskVertexCode
                < SanityWorldColourPolicy.NightVertexCode
        );
    }
}
