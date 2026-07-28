using System.Text.Json;
using Xunit;

namespace DontStarve.Tests.Display;

public sealed class DisplayI18nTests
{
    [Fact]
    public void DefaultAndChineseDisplayKeysStayAligned()
    {
        using var english = ReadLocale("default.json");
        using var chinese = ReadLocale("zh.json");
        var englishKeys = english.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var chineseKeys = chinese.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(englishKeys, chineseKeys);
        Assert.Contains("sanity-hud.label", englishKeys);
        Assert.Contains("sanity-hud.value", englishKeys);
        Assert.Contains("sanity-tooltip", englishKeys);
    }

    [Theory]
    [InlineData("default.json")]
    [InlineData("zh.json")]
    public void SanityFoodTemplateDoesNotOwnAFixedPlusSign(string localeFile)
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement.GetProperty("sanity-tooltip").GetString();

        Assert.NotNull(template);
        Assert.DoesNotContain("+", template, StringComparison.Ordinal);
        Assert.Contains("{{value}}", template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default.json")]
    [InlineData("zh.json")]
    public void SanityHudTemplateOwnsLabelCurrentAndMaximumPlaceholders(
        string localeFile
    )
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement.GetProperty("sanity-hud.value").GetString();

        Assert.NotNull(template);
        Assert.Contains("{{label}}", template, StringComparison.Ordinal);
        Assert.Contains("{{current}}", template, StringComparison.Ordinal);
        Assert.Contains("{{maximum}}", template, StringComparison.Ordinal);
    }

    private static JsonDocument ReadLocale(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "I18n", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
