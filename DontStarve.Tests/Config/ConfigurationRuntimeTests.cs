using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class ConfigurationRuntimeTests
{
    [Fact]
    public void RuntimeSaveReloadPreservesUnknownAndRefreshesTypedCache()
    {
        WithTemporaryConfig(
            "{\"UnknownLegacy\":{\"Keep\":7},\"EnableDawnDuskMusic\":false}",
            path =>
            {
                var loaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                var runtime = Assert.IsType<ConfigurationRuntime>(loaded.Runtime);
                Assert.False(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value
                );
                var originalFingerprint = runtime.Fingerprint.FullHash;

                var saved = runtime.TrySave(
                    new Dictionary<string, ConfigValue>
                    {
                        [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(true),
                        [ConfigKeys.EnableSanitySystem] = ConfigValue.Boolean(false),
                    }
                );

                Assert.True(saved.Success, saved.Reason);
                Assert.True(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value
                );
                Assert.False(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableSanitySystem).Value
                );
                Assert.NotEqual(originalFingerprint, runtime.Fingerprint.FullHash);
                using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    Assert.Equal(
                        7,
                        document.RootElement
                            .GetProperty("UnknownLegacy")
                            .GetProperty("Keep")
                            .GetInt32()
                    );
                }

                var reloaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                var reloadedRuntime = Assert.IsType<ConfigurationRuntime>(reloaded.Runtime);
                Assert.True(
                    reloadedRuntime.Resolver
                        .GetBoolean(ConfigKeys.EnableDawnDuskMusic)
                        .Value
                );
                Assert.False(
                    reloadedRuntime.Resolver.GetBoolean(ConfigKeys.EnableSanitySystem).Value
                );
                Assert.Equal(runtime.Fingerprint.FullHash, reloadedRuntime.Fingerprint.FullHash);
            }
        );
    }

    [Fact]
    public void InvalidConfigRootLeavesOriginalFileAndRuntimeUnavailable()
    {
        WithTemporaryConfig(
            "{",
            path =>
            {
                var loaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );

                Assert.False(loaded.IsAvailable);
                Assert.Null(loaded.Runtime);
                Assert.Equal("config.invalid-json", loaded.Reason);
                Assert.Equal("{", File.ReadAllText(path));
            }
        );
    }

    [Fact]
    public void LegacyMusicResetPersistsTrueWithoutTypedSmapiRewrite()
    {
        WithTemporaryConfig(
            "{\"EnableDawnDuskMusic\":false}",
            path =>
            {
                var loaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                var runtime = Assert.IsType<ConfigurationRuntime>(loaded.Runtime);
                var music = runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic);
                Assert.True(music.HasValue);
                Assert.False(music.Value);
                var config = new ModConfig();
                config.LoadEnableDawnDuskMusic(music.Value);

                config.EnableDawnDuskMusic = true;
                Assert.True(config.IsEnableDawnDuskMusicDirty);
                var saved = runtime.TrySave(
                    new Dictionary<string, ConfigValue>
                    {
                        [ConfigKeys.EnableDawnDuskMusic] = ConfigValue.Boolean(
                            config.EnableDawnDuskMusic
                        ),
                    }
                );
                Assert.True(saved.Success, saved.Reason);
                config.LoadEnableDawnDuskMusic(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value
                );

                Assert.True(config.EnableDawnDuskMusic);
                Assert.False(config.IsEnableDawnDuskMusicDirty);
                var reloaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                Assert.True(
                    Assert.IsType<ConfigurationRuntime>(reloaded.Runtime)
                        .Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic)
                        .Value
                );
            }
        );
    }

    private static void WithTemporaryConfig(string content, Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dontstarve-config-runtime-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, content);
            action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
