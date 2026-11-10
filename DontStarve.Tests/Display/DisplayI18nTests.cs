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
        Assert.Contains("hunger-hud.label", englishKeys);
        Assert.Contains("hunger-hud.value", englishKeys);
        Assert.Contains("hunger-low.warning", englishKeys);
        Assert.Contains("sanity-tooltip", englishKeys);
        Assert.Contains("sanity-tooltip.equipment-per-minute", englishKeys);
        Assert.Contains("sanity.shadow-monster.creeper-fear.name", englishKeys);
        Assert.Contains("sanity.shadow-monster.terrorbeak.name", englishKeys);
    }

    [Fact]
    public void ChineseHungerHudLabelUsesShortHudTerm()
    {
        using var chinese = ReadLocale("zh.json");

        Assert.Equal(
            "饥饿",
            chinese.RootElement.GetProperty("hunger-hud.label").GetString()
        );
        Assert.Equal(
            "{{value}} 饥饿值",
            chinese.RootElement.GetProperty("hunger-tooltip").GetString()
        );
    }

    [Theory]
    [InlineData("default.json", "You need food......")]
    [InlineData("zh.json", "你需要食物......")]
    public void HungerLowWarningUsesNeedFoodWording(
        string localeFile,
        string expectedWarning
    )
    {
        using var locale = ReadLocale(localeFile);

        Assert.Equal(
            expectedWarning,
            locale.RootElement.GetProperty("hunger-low.warning").GetString()
        );
    }

    [Theory]
    [InlineData("default.json", "Creeper Fear", "Terrorbeak")]
    [InlineData("zh.json", "爬行恐惧", "恐怖尖喙")]
    public void ShadowMonsterNamesHaveLocalizedValues(
        string localeFile,
        string creeperFearName,
        string terrorbeakName
    )
    {
        using var locale = ReadLocale(localeFile);

        Assert.Equal(
            creeperFearName,
            locale.RootElement
                .GetProperty("sanity.shadow-monster.creeper-fear.name")
                .GetString()
        );
        Assert.Equal(
            terrorbeakName,
            locale.RootElement
                .GetProperty("sanity.shadow-monster.terrorbeak.name")
                .GetString()
        );
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
    [InlineData("zh.json", "饥饿值", "理智", "食用时")]
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
    [InlineData("default.json", "Sanity aura:", "/min")]
    [InlineData("zh.json", "理智光环：", "/分钟")]
    public void EquipmentSanityTemplateRetainsEquipmentContext(
        string localeFile,
        string equipmentContext,
        string perMinuteUnit
    )
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement
            .GetProperty("sanity-tooltip.equipment-per-minute")
            .GetString();

        Assert.NotNull(template);
        Assert.Contains("{{value}}", template, StringComparison.Ordinal);
        Assert.Contains(equipmentContext, template, StringComparison.Ordinal);
        // 2026-11-10 用户定稿格式："理智光环：{{value}}/分钟" / "Sanity aura: {{value}}/min"。
        Assert.Contains(perMinuteUnit, template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default.json")]
    [InlineData("zh.json")]
    public void ExtendedBuffCombatTemplatesExposeFormattedValues(string localeFile)
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

    [Theory]
    [InlineData("default.json")]
    [InlineData("zh.json")]
    public void HungerHudTemplateOwnsLabelCurrentAndMaximumPlaceholders(
        string localeFile
    )
    {
        using var locale = ReadLocale(localeFile);
        var template = locale.RootElement.GetProperty("hunger-hud.value").GetString();

        Assert.NotNull(template);
        Assert.Contains("{{label}}", template, StringComparison.Ordinal);
        Assert.Contains("{{current}}", template, StringComparison.Ordinal);
        Assert.Contains("{{maximum}}", template, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerDeathDialogueTranslationsMatchTheFrozenChineseText()
    {
        using var english = ReadLocale("default.json");
        using var chinese = ReadLocale("zh.json");
        var expected = new Dictionary<string, (string English, string Chinese)>
        {
            ["hunger-death.hospital.1"] = (
                "Someone found you collapsed on the floor from low blood sugar... so I gave you an emergency glucose infusion!",
                "有人发现你低血糖倒在地上……所以我给你做了紧急葡萄糖输液！"
            ),
            ["hunger-death.hospital.2"] = (
                "Remember to eat properly, okay?",
                "一定要记得好好吃饭，好吗？"
            ),
            ["darkness-death.hospital.1"] = (
                "Someone found you collapsed on the floor covered in wounds... so I performed emergency surgery on you!",
                "有人发现你浑身是伤地倒在地上……所以我给你做了紧急手术！"
            ),
            ["darkness-death.hospital.2"] = (
                "It hasn't been very safe at night lately. Make sure to bring a light source when you go out, and don't stay out too late, okay?",
                "最近夜间不太安全，出门一定要带好光源，不要待得太晚，好吗？"
            ),
            ["hunger-death.mine.linus.1"] = (
                "...I found you passed out in the mines from hunger. You were lucky I happened to be passing by and gave you a few Sweet Berries!",
                "……我发现你在矿山饿晕过去。你很幸运，我刚巧路过，给你吃了几颗甜梅！"
            ),
            ["hunger-death.mine.linus.2"] = (
                "If you're having trouble in life, come talk to me, okay?",
                "如果生活上有什么困难和我说，好吗？"
            ),
            ["hunger-death.mine.robin.1"] = (
                "...I found you passed out in the mines from hunger. I thought you were a shriveled-up walking stick!",
                "……我发现你在矿山饿晕过去。我还以为地上有一只干瘪的竹节虫！"
            ),
            ["hunger-death.mine.robin.2"] = (
                "Remember to bring food next time, okay?",
                "下次，记得带食物，好吗？"
            ),
            ["hunger-death.mine.clint.1"] = (
                "...I found you passed out in the mines from hunger. I dragged you all the way here, and you are shockingly light!",
                "……我发现你在矿山饿晕过去。我一路上把你拖到这儿来，你的体重真的惊人地轻！"
            ),
            ["hunger-death.mine.clint.2"] = (
                "Even if you're dieting, you need to eat enough before going into the mines. Remember to bring food next time, okay?",
                "即使是节食，也得要吃饱才能下矿，下次记得带食物，好吗？"
            ),
            ["hunger-death.mine.maru.1"] = (
                "...I found you passed out in the mines from hunger. I was looking for minerals and almost didn't see you!",
                "……我发现你在矿山饿晕过去。我当时正在寻找矿产，差点就没看见你！"
            ),
            ["hunger-death.mine.maru.2"] = (
                "Remember to bring more food next time, okay?",
                "下次记得多带点食物，好吗？"
            ),
            ["hunger-death.mine.maru-spouse.1"] = (
                "...I found you unconscious in the mines from hunger. You had been gone so long that I started to worry!",
                "……我发现你在矿山不省人事。你去了那么长时间，我开始担心起你来！"
            ),
            ["hunger-death.mine.maru-spouse.2"] = (
                "Please, please bring more food next time, okay?",
                "下次一定一定要多带点食物，好吗？"
            ),
            ["hunger-death.island.willy.2"] = (
                "I was so worried... I thought you were so hungry you had to drink seawater to survive! Bring plenty of food when you're out there...",
                "我太担心了……我以为你饿到要喝海水了充饥！在外面要多带点食物啊……"
            ),
            ["hunger-death.island.willy.3"] = (
                "This island doesn't have food everywhere.",
                "这个岛屿可不是哪里都有吃的。"
            ),
            ["hunger-death.island.leo.2"] = (
                "My bird friends found you and told me you were passed out from hunger, so I dragged you here.",
                "我的鸟儿朋友们发现你，告诉我你饿晕了，所以我拖着你把你带到了这里。"
            ),
            ["hunger-death.island.leo.3"] = (
                "Please take better care of yourself out there. I don't want to be sad again...",
                "在外面还请多注意身体。我不想再伤心一次了……"
            ),
        };

        foreach (var pair in expected)
        {
            Assert.Equal(
                pair.Value.English,
                english.RootElement.GetProperty(pair.Key).GetString()
            );
            Assert.Equal(
                pair.Value.Chinese,
                chinese.RootElement.GetProperty(pair.Key).GetString()
            );
        }
    }

    private static JsonDocument ReadLocale(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "I18n", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
