using DontStarve.Debug;
using Xunit;

namespace DontStarve.Tests.Debug;

public sealed class DebugCommandValueParserTests
{
    [Theory]
    [InlineData("150", 100d, 100d)]
    [InlineData("37.5%", 80d, 30d)]
    [InlineData("0%", 140d, 0d)]
    public void Resolves_and_clamps_absolute_or_percentage_targets(
        string raw,
        double maximum,
        double expected
    )
    {
        var success = DebugCommandValueParser.TryResolveTarget(
            raw,
            maximum,
            out var target,
            out _
        );

        Assert.True(success);
        Assert.Equal(expected, target, precision: 10);
    }

    [Theory]
    [InlineData("-1", 100d, "debug-value-is-negative")]
    [InlineData("101%", 100d, "debug-percentage-is-out-of-range")]
    [InlineData("10", double.NaN, "debug-maximum-is-invalid")]
    public void Rejects_invalid_debug_targets(
        string raw,
        double maximum,
        string expectedReason
    )
    {
        var success = DebugCommandValueParser.TryResolveTarget(
            raw,
            maximum,
            out var target,
            out var reason
        );

        Assert.False(success);
        Assert.Equal(0d, target);
        Assert.Equal(expectedReason, reason);
    }

}
