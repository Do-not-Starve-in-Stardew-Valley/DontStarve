using System.Globalization;
using DontStarve.Display.UIElements;
using Xunit;

namespace DontStarve.Tests.Display;

public sealed class LocalizedValueFormatterTests
{
    [Theory]
    [InlineData(10, "en-US", "+10")]
    [InlineData(-10, "en-US", "-10")]
    [InlineData(0, "en-US", "0")]
    [InlineData(0.588, "en-US", "+0.59")]
    [InlineData(-0.588, "en-US", "-0.59")]
    [InlineData(0.588, "fr-FR", "+0,59")]
    public void SignedFoodValueOwnsItsSignAndUsesTheRequestedCulture(
        double value,
        string cultureName,
        string expected
    )
    {
        Assert.True(
            LocalizedValueFormatter.TryFormatSigned(
                value,
                CultureInfo.GetCultureInfo(cultureName),
                out var formatted
            )
        );
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteFoodValueIsNotFormatted(double value)
    {
        Assert.False(
            LocalizedValueFormatter.TryFormatSigned(
                value,
                CultureInfo.InvariantCulture,
                out var formatted
            )
        );
        Assert.Equal(string.Empty, formatted);
    }

    [Fact]
    public void InvalidLocaleFallsBackToInvariantCulture()
    {
        var culture = LocalizedValueFormatter.ResolveCulture("!");

        Assert.Equal(CultureInfo.InvariantCulture, culture);
    }
}
