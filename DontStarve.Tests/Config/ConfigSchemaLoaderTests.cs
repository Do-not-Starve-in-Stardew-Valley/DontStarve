using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class ConfigSchemaLoaderTests
{
    [Fact]
    public void ShippedSchemaPublishesFrozenEightOptionRegistry()
    {
        var result = ConfigSchemaLoader.LoadFromFile(ConfigTestData.ShippedSchemaPath);

        Assert.Equal(ConfigSchemaStatus.Available, result.Status);
        Assert.Equal("schema.available", result.Reason);
        Assert.Empty(result.Diagnostics);
        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Equal(1, registry.SchemaVersion);
        Assert.Equal(8, registry.Options.Count);

        AssertBoolean(registry, ConfigKeys.EnableSanitySystem, true, true, true, 10);
        AssertEnum(
            registry,
            ConfigKeys.SanityMonsterIntensity,
            "Default",
            new[] { "None", "Less", "Default", "More", "Many", "Insane" },
            true,
            true,
            20
        );
        AssertEnum(
            registry,
            ConfigKeys.DarkHandMode,
            "FireThief",
            new[] { "Off", "FireThief", "Harassment", "Thief" },
            true,
            true,
            25
        );
        AssertBoolean(
            registry,
            ConfigKeys.EnableSanityVisualEffects,
            true,
            true,
            false,
            30
        );
        AssertEnum(
            registry,
            ConfigKeys.DarknessDamageMode,
            "Default",
            new[] { "Off", "NonLethal", "Default" },
            true,
            true,
            40
        );
        AssertEnum(
            registry,
            ConfigKeys.MonsterDifficultyProfile,
            "Compatible",
            new[] { "Stardew", "Compatible", "DontStarve", "Fusion" },
            true,
            true,
            50
        );
        AssertBoolean(
            registry,
            ConfigKeys.EnableJunimoBlessing,
            false,
            true,
            true,
            60
        );
        AssertBoolean(
            registry,
            ConfigKeys.EnableDawnDuskMusic,
            true,
            false,
            false,
            90
        );

        Assert.Equal(
            "config.sanity.section",
            Get(registry, ConfigKeys.EnableSanitySystem).SectionI18n
        );
        Assert.Equal(
            "config.music.section",
            Get(registry, ConfigKeys.EnableDawnDuskMusic).SectionI18n
        );
        Assert.All(
            registry.Options,
            option =>
            {
                var kebab = ToKebabCase(option.Key);
                Assert.Equal(
                    option.Key == ConfigKeys.EnableDawnDuskMusic
                        ? "config.music.section"
                        : "config.sanity.section",
                    option.SectionI18n
                );
                Assert.Equal($"config.{kebab}.name", option.NameI18n);
                Assert.Equal($"config.{kebab}.tooltip", option.TooltipI18n);
            }
        );
    }

    [Fact]
    public void MissingSchemaIsUnavailable()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dontstarve-missing-schema-{Guid.NewGuid():N}.json"
        );

        var result = ConfigSchemaLoader.LoadFromFile(path);

        Assert.Equal(ConfigSchemaStatus.Unavailable, result.Status);
        Assert.Equal("schema.missing", result.Reason);
        Assert.Null(result.Registry);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":1,\"Options\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"Options\":[]}")]
    [InlineData("{\"SchemaVersion\":\"1\",\"Options\":[{}]}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Options\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Options\":[],\"Unknown\":true}")]
    public void InvalidRootDoesNotPublishRegistry(string json)
    {
        var result = ConfigSchemaLoader.Parse(json);

        Assert.Equal(ConfigSchemaStatus.Unavailable, result.Status);
        Assert.Null(result.Registry);
        Assert.StartsWith("schema.", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedVersionIsUnavailable()
    {
        var result = ConfigSchemaLoader.Parse(
            "{\"SchemaVersion\":2,\"Options\":[{}]}"
        );

        Assert.Equal(ConfigSchemaStatus.Unavailable, result.Status);
        Assert.Equal("schema.unsupported-version", result.Reason);
        Assert.Null(result.Registry);
    }

    [Fact]
    public void InvalidSingleOptionIsSkippedWhileOtherOptionRemains()
    {
        var valid = ConfigTestData.BooleanOption(
            ConfigKeys.EnableDawnDuskMusic,
            true,
            false,
            false,
            90
        );
        var invalid = ConfigTestData.BooleanOption(
            ConfigKeys.EnableJunimoBlessing,
            false,
            true,
            true,
            60,
            "\"UnknownField\": true"
        );

        var result = ConfigSchemaLoader.Parse(ConfigTestData.Schema(invalid, valid));

        Assert.Equal(ConfigSchemaStatus.Degraded, result.Status);
        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Single(registry.Options);
        Assert.Equal(ConfigKeys.EnableDawnDuskMusic, registry.Options[0].Key);
        Assert.Contains(
            $"schema.invalid-option:{ConfigKeys.EnableJunimoBlessing}",
            result.Diagnostics
        );
    }

    [Fact]
    public void OrdinalIgnoreCaseCollisionSkipsEveryConflictingOption()
    {
        var exact = ConfigTestData.BooleanOption(
            ConfigKeys.EnableSanitySystem,
            true,
            true,
            true,
            10
        );
        var differentCase = ConfigTestData.BooleanOption(
            "enablesanitysystem",
            true,
            true,
            true,
            11
        );
        var survivor = ConfigTestData.BooleanOption(
            ConfigKeys.EnableDawnDuskMusic,
            true,
            false,
            false,
            90
        );

        var result = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(exact, differentCase, survivor)
        );

        Assert.Equal(ConfigSchemaStatus.Degraded, result.Status);
        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Single(registry.Options);
        Assert.Equal(ConfigKeys.EnableDawnDuskMusic, registry.Options[0].Key);
        Assert.Equal(2, result.Diagnostics.Count(reason => reason.StartsWith("schema.duplicate-key:", StringComparison.Ordinal)));
    }

    [Fact]
    public void BooleanWithStringDefaultIsSkipped()
    {
        var invalid = ConfigTestData.BooleanOption(
            ConfigKeys.EnableSanitySystem,
            true,
            true,
            true,
            10
        ).Replace("\"Default\": true", "\"Default\": \"true\"", StringComparison.Ordinal);

        var result = ConfigSchemaLoader.Parse(ConfigTestData.Schema(invalid));

        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Empty(registry.Options);
        Assert.Contains(
            $"schema.invalid-default:{ConfigKeys.EnableSanitySystem}",
            result.Diagnostics
        );
    }

    [Fact]
    public void EnumDefaultOutsideAllowedValuesIsSkipped()
    {
        var invalid = ConfigTestData.EnumOption(
            ConfigKeys.DarknessDamageMode,
            "Impossible",
            new[] { "Off", "NonLethal", "Default" },
            true,
            true,
            40
        );

        var result = ConfigSchemaLoader.Parse(ConfigTestData.Schema(invalid));

        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Empty(registry.Options);
        Assert.Contains(
            $"schema.invalid-default:{ConfigKeys.DarknessDamageMode}",
            result.Diagnostics
        );
    }

    [Fact]
    public void EnumWithDuplicateAllowedValueIsSkipped()
    {
        var invalid = ConfigTestData.EnumOption(
            ConfigKeys.DarknessDamageMode,
            "Default",
            new[] { "Off", "Default", "Default" },
            true,
            true,
            40
        );

        var result = ConfigSchemaLoader.Parse(ConfigTestData.Schema(invalid));

        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Empty(registry.Options);
        Assert.Contains(
            $"schema.invalid-option:{ConfigKeys.DarknessDamageMode}",
            result.Diagnostics
        );
    }

    [Fact]
    public void DuplicateOrderSkipsEveryConflictingOption()
    {
        var first = ConfigTestData.BooleanOption(
            ConfigKeys.EnableSanitySystem,
            true,
            true,
            true,
            10
        );
        var second = ConfigTestData.BooleanOption(
            ConfigKeys.EnableJunimoBlessing,
            false,
            true,
            true,
            10
        );

        var result = ConfigSchemaLoader.Parse(ConfigTestData.Schema(first, second));

        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.Empty(registry.Options);
        Assert.Equal(2, result.Diagnostics.Count(reason => reason == "schema.duplicate-order:10"));
    }

    private static void AssertBoolean(
        ConfigRegistry registry,
        string key,
        bool defaultValue,
        bool exposeToContentPatcher,
        bool affectsWorldState,
        int order
    )
    {
        var option = Get(registry, key);
        Assert.Equal(ConfigOptionType.Boolean, option.Type);
        Assert.Equal(defaultValue, option.DefaultValue.BooleanValue);
        Assert.Empty(option.AllowedValues);
        Assert.Equal(exposeToContentPatcher, option.ExposeToContentPatcher);
        Assert.Equal(affectsWorldState, option.AffectsWorldState);
        Assert.Equal(order, option.Order);
    }

    private static void AssertEnum(
        ConfigRegistry registry,
        string key,
        string defaultValue,
        IReadOnlyList<string> allowedValues,
        bool exposeToContentPatcher,
        bool affectsWorldState,
        int order
    )
    {
        var option = Get(registry, key);
        Assert.Equal(ConfigOptionType.Enum, option.Type);
        Assert.Equal(defaultValue, option.DefaultValue.EnumValue);
        Assert.Equal(allowedValues, option.AllowedValues);
        Assert.Equal(exposeToContentPatcher, option.ExposeToContentPatcher);
        Assert.Equal(affectsWorldState, option.AffectsWorldState);
        Assert.Equal(order, option.Order);
    }

    private static ConfigOptionDefinition Get(ConfigRegistry registry, string key)
    {
        Assert.True(registry.TryGet(key, out var option));
        return Assert.IsType<ConfigOptionDefinition>(option);
    }

    private static string ToKebabCase(string value)
    {
        var characters = new List<char>();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
                characters.Add('-');

            characters.Add(char.ToLowerInvariant(character));
        }

        return new string(characters.ToArray());
    }
}
