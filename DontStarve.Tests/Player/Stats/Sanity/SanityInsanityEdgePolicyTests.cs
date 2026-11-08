using System.Reflection;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityInsanityEdgePolicyTests
{
    [Theory]
    [InlineData(false, 0.15d, "Entered")]
    [InlineData(false, 0.1499d, "Entered")]
    [InlineData(false, 0.1501d, "NoChange")]
    [InlineData(true, 0.1749d, "NoChange")]
    [InlineData(true, 0.175d, "Exited")]
    public void Insane_edge_policy_uses_dst_hysteresis(
        bool currentlyInsane,
        double ratio,
        string expectedTransition
    )
    {
        var policy = typeof(SanityInsanityEdgePolicyTests).Assembly.GetType(
            "DontStarve.Player.Stats.Sanity.Audio.SanityInsanityEdgePolicy"
        );
        Assert.NotNull(policy);
        var method = policy!.GetMethod(
            "Observe",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object[] { currentlyInsane, ratio });

        Assert.Equal(expectedTransition, actual?.ToString());
    }

    [Fact]
    public void Insane_edge_policy_exposes_a_fifteen_second_suppression_window()
    {
        var policy = typeof(SanityInsanityEdgePolicyTests).Assembly.GetType(
            "DontStarve.Player.Stats.Sanity.Audio.SanityInsanityEdgePolicy"
        );
        Assert.NotNull(policy);
        var field = policy!.GetField(
            "SuppressionSeconds",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        Assert.Equal(15d, field!.GetValue(null));
    }
}
