using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class FlatConfigValueStoreTests
{
    [Fact]
    public void MissingConfigUsesDefaultsWithoutWritingUntilControlledSave()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(null, isMissing: true);

        var loaded = FlatConfigValueStore.Load(registry, file);

        Assert.Equal(FlatConfigLoadStatus.AvailableWithDefaults, loaded.Status);
        Assert.Equal("config.missing-use-schema-defaults", loaded.Reason);
        Assert.Equal(0, file.WriteCount);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry, store);
        var music = resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic);
        Assert.True(music.HasValue);
        Assert.True(music.Value);
        Assert.Equal(ConfigValueStatus.AvailableWithDefault, music.Status);

        var saved = store.TrySave(new Dictionary<string, ConfigValue>());

        Assert.True(saved.Success, saved.Reason);
        Assert.Equal(1, file.WriteCount);
        using var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten));
        Assert.Equal(8, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void ExistingMusicFalseAndUnknownRootValueSurviveSaveReload()
    {
        const string original =
            "{\n"
            + "  \"UnknownLegacy\": { \"Nested\": [1, true, \"keep\"] },\n"
            + "  \"EnableDawnDuskMusic\": false\n"
            + "}";
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(original);
        var loaded = FlatConfigValueStore.Load(registry, file);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry, store);

        var before = resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic);
        Assert.True(before.HasValue);
        Assert.False(before.Value);
        var modConfig = new ModConfig();
        modConfig.LoadEnableDawnDuskMusic(before.Value);
        Assert.False(modConfig.EnableDawnDuskMusic);
        Assert.False(modConfig.IsEnableDawnDuskMusicDirty);

        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(false),
            }
        );
        Assert.True(saved.Success, saved.Reason);
        resolver.Refresh();

        using (var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten)))
        {
            var root = document.RootElement;
            Assert.False(root.GetProperty(ConfigKeys.EnableDawnDuskMusic).GetBoolean());
            var nested = root.GetProperty("UnknownLegacy").GetProperty("Nested");
            Assert.Equal(3, nested.GetArrayLength());
            Assert.Equal(1, nested[0].GetInt32());
            Assert.True(nested[1].GetBoolean());
            Assert.Equal("keep", nested[2].GetString());
            Assert.Equal(8, root.EnumerateObject().Count(property => ConfigKeys.IsFrozen(property.Name)));
        }

        var reloaded = FlatConfigValueStore.Load(registry, file);
        var reloadedStore = Assert.IsType<FlatConfigValueStore>(reloaded.Store);
        var reloadedResolver = new TypedConfigResolver(registry, reloadedStore);
        var after = reloadedResolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic);
        Assert.True(after.HasValue);
        Assert.False(after.Value);
        Assert.Equal(ConfigValueStatus.Available, after.Status);
    }

    [Fact]
    public void InvalidKnownValueIsUnavailableAndRawValueIsPreserved()
    {
        const string original =
            "{\n"
            + "  \"SanityMonsterIntensity\": 42,\n"
            + "  \"EnableDawnDuskMusic\": false\n"
            + "}";
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(original);
        var loaded = FlatConfigValueStore.Load(registry, file);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry, store);

        var invalid = resolver.GetEnum(ConfigKeys.SanityMonsterIntensity);
        Assert.False(invalid.HasValue);
        Assert.Equal(ConfigValueStatus.UnavailableForKey, invalid.Status);
        Assert.Equal(
            $"config.invalid-value:{ConfigKeys.SanityMonsterIntensity}",
            invalid.Reason
        );

        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(true),
            }
        );

        Assert.True(saved.Success, saved.Reason);
        using var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten));
        Assert.Equal(
            42,
            document.RootElement.GetProperty(ConfigKeys.SanityMonsterIntensity).GetInt32()
        );
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"EnableDawnDuskMusic\":true,\"EnableDawnDuskMusic\":false}")]
    public void InvalidConfigRootIsUnavailableAndNeverWritten(string json)
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(json);

        var loaded = FlatConfigValueStore.Load(registry, file);

        Assert.Equal(FlatConfigLoadStatus.Unavailable, loaded.Status);
        Assert.Equal("config.invalid-json", loaded.Reason);
        Assert.Null(loaded.Store);
        Assert.Equal(0, file.WriteCount);
        Assert.Equal(json, file.Content);
    }

    [Fact]
    public void FailedAtomicWriteDoesNotPublishProposedValues()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(
            "{\"EnableDawnDuskMusic\":false}"
        )
        {
            FailWrite = true,
        };
        var loaded = FlatConfigValueStore.Load(registry, file);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry, store);

        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(true),
            }
        );
        resolver.Refresh();

        Assert.False(saved.Success);
        Assert.Equal("config.write-failed", saved.Reason);
        Assert.False(resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value);
        Assert.Equal("{\"EnableDawnDuskMusic\":false}", file.Content);
    }

    [Fact]
    public void SaveRejectsUnknownOrWrongTypedUpdateBeforeWriting()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess("{}");
        var loaded = FlatConfigValueStore.Load(registry, file);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);

        var unknown = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                ["Unknown"] = ConfigValue.Boolean(true),
            }
        );
        var wrongType = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Enum("Default"),
            }
        );

        Assert.False(unknown.Success);
        Assert.False(wrongType.Success);
        Assert.Equal(0, file.WriteCount);
        Assert.Equal("{}", file.Content);
    }

    [Fact]
    public void ValidEnumUsesStableEnglishValueAndWrongCaseIsUnavailable()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var validFile = new MemoryFlatConfigFileAccess(
            "{\"DarkHandMode\":\"Harassment\"}"
        );
        var invalidFile = new MemoryFlatConfigFileAccess(
            "{\"DarkHandMode\":\"harassment\"}"
        );

        var validStore = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, validFile).Store
        );
        var invalidStore = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, invalidFile).Store
        );

        var valid = new TypedConfigResolver(registry, validStore).GetEnum(
            ConfigKeys.DarkHandMode
        );
        var invalid = new TypedConfigResolver(registry, invalidStore).GetEnum(
            ConfigKeys.DarkHandMode
        );
        Assert.True(valid.HasValue);
        Assert.Equal("Harassment", valid.Value);
        Assert.False(invalid.HasValue);
        Assert.Equal(ConfigValueStatus.UnavailableForKey, invalid.Status);
    }

    [Fact]
    public void PhysicalStoreAtomicallySavesAndReloadsFlatRoot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dontstarve-config-store-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(
                path,
                "{\"UnknownLegacy\":true,\"EnableDawnDuskMusic\":false}"
            );
            var registry = ConfigTestData.LoadShippedRegistry();
            var store = Assert.IsType<FlatConfigValueStore>(
                FlatConfigValueStore.Load(
                    registry,
                    new PhysicalFlatConfigFileAccess(path)
                ).Store
            );

            var saved = store.TrySave(
                new Dictionary<string, ConfigValue>
                {
                    [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(false),
                }
            );
            Assert.True(saved.Success, saved.Reason);

            var reloaded = Assert.IsType<FlatConfigValueStore>(
                FlatConfigValueStore.Load(
                    registry,
                    new PhysicalFlatConfigFileAccess(path)
                ).Store
            );
            var resolver = new TypedConfigResolver(registry, reloaded);
            Assert.False(resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.GetProperty("UnknownLegacy").GetBoolean());
            Assert.Equal(
                8,
                document.RootElement.EnumerateObject().Count(property =>
                    ConfigKeys.IsFrozen(property.Name)
                )
            );
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
