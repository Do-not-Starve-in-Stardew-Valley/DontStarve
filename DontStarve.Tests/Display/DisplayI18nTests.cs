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
        Assert.Contains("sanity-tooltip.equipment-per-minute", englishKeys);
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
    public void HungerTemplateDoesNotOwnAFixedPlusSign(string localeFile)
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement.GetProperty("hunger-tooltip").GetString();

        Assert.NotNull(template);
        Assert.DoesNotContain("+", template, StringComparison.Ordinal);
        Assert.Contains("{{value}}", template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default.json", "Hunger", "Sanity", "when eaten")]
    [InlineData("zh.json", "饱食", "理智", "食用时")]
    public void DescriptiveSurvivalTemplatesRetainTheirContextualLabels(
        string localeFile,
        string hungerLabel,
        string sanityLabel,
        string foodContext
    )
    {
        using var locale = ReadLocale(localeFile);
        var hungerTemplate = locale.RootElement.GetProperty("hunger-tooltip").GetString();
        var foodSanityTemplate = locale.RootElement
            .GetProperty("sanity-tooltip.food-once")
            .GetString();

        Assert.NotNull(hungerTemplate);
        Assert.NotNull(foodSanityTemplate);
        Assert.Contains(hungerLabel, hungerTemplate, StringComparison.Ordinal);
        Assert.Contains(sanityLabel, foodSanityTemplate, StringComparison.Ordinal);
        Assert.Contains(foodContext, foodSanityTemplate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default.json", "per minute while equipped")]
    [InlineData("zh.json", "装备时每分钟")]
    public void EquipmentSanityTemplateRetainsEquipmentContext(
        string localeFile,
        string equipmentContext
    )
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement
            .GetProperty("sanity-tooltip.equipment-per-minute")
            .GetString();

        Assert.NotNull(template);
        Assert.Contains("{{value}}", template, StringComparison.Ordinal);
        Assert.Contains(equipmentContext, template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default.json")]
    [InlineData("zh.json")]
    public void ExtraMachineConfigCombatTemplatesExposeFormattedValues(string localeFile)
    {
        using var locale = ReadLocale(localeFile);
        foreach (
            var key in new[]
            {
                "food-buff-tooltip.immunity",
                "food-buff-tooltip.attack-multiplier",
                "food-buff-tooltip.knockback-multiplier",
                "food-buff-tooltip.weapon-speed-multiplier",
                "food-buff-tooltip.critical-chance-multiplier",
                "food-buff-tooltip.critical-power-multiplier",
                "food-buff-tooltip.weapon-precision-multiplier",
            }
        )
        {
            var template = locale.RootElement.GetProperty(key).GetString();
            Assert.NotNull(template);
            Assert.Contains("{{value}}", template, StringComparison.Ordinal);
        }
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
