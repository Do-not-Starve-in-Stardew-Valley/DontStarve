using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class GameplayConfigFingerprintTests
{
    [Fact]
    public void PropertyOrderDoesNotChangeCanonicalTextOrHash()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        const string first =
            "{\"EnableHungerSystem\":false,"
            + "\"EnableSeedEdibility\":true,"
            + "\"EnableSanitySystem\":true,"
            + "\"SanityMonsterIntensity\":\"Default\","
            + "\"DarkHandMode\":\"FireThief\","
            + "\"EnableNaturalDarkness\":true,"
            + "\"DarknessDamageMode\":\"Default\","
            + "\"MonsterDifficultyProfile\":\"Compatible\","
            + "\"EnableJunimoBlessing\":false,"
            + "\"EnableSanityVisualEffects\":true,"
            + "\"EnableDawnDuskMusic\":false}";
        const string reversed =
            "{\"EnableDawnDuskMusic\":false,"
            + "\"EnableHungerSystem\":false,"
            + "\"EnableSeedEdibility\":true,"
            + "\"EnableSanityVisualEffects\":true,"
            + "\"EnableJunimoBlessing\":false,"
            + "\"MonsterDifficultyProfile\":\"Compatible\","
            + "\"DarknessDamageMode\":\"Default\","
            + "\"EnableNaturalDarkness\":true,"
            + "\"DarkHandMode\":\"FireThief\","
            + "\"SanityMonsterIntensity\":\"Default\","
            + "\"EnableSanitySystem\":true}";

        var left = Fingerprint(registry, first);
        var right = Fingerprint(registry, reversed);

        Assert.True(left.IsAvailable, left.PublicIdentifier);
        Assert.Equal(left.CanonicalText, right.CanonicalText);
        Assert.Equal(left.FullHash, right.FullHash);
        Assert.Equal(left.PublicIdentifier, right.PublicIdentifier);
        Assert.Equal(left.FullHash[..12], left.PublicIdentifier);
        Assert.Equal(64, left.FullHash.Length);
        Assert.Equal(
            "9E1EFC6ED5D94DA8D0D4929D4212C209D0D033177E363A7BE983584A65F61748",
            left.FullHash
        );
        Assert.Equal(
            "SchemaVersion=1\n"
                + "DarkHandMode=FireThief\n"
                + "DarknessDamageMode=Default\n"
                + "EnableHungerSystem=false\n"
                + "EnableJunimoBlessing=false\n"
                + "EnableNaturalDarkness=true\n"
                + "EnableSanitySystem=true\n"
                + "EnableSeedEdibility=true\n"
                + "MonsterDifficultyProfile=Compatible\n"
                + "SanityMonsterIntensity=Default\n",
            left.CanonicalText
        );
    }

    [Theory]
    [InlineData("{\"DarknessDamageMode\":\"Off\"}")]
    [InlineData("{\"EnableNaturalDarkness\":false}")]
    public void WorldStateValueChangeChangesFingerprint(string changedJson)
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var baseline = Fingerprint(registry, "{}");
        var changed = Fingerprint(registry, changedJson);

        Assert.True(baseline.IsAvailable);
        Assert.True(changed.IsAvailable);
        Assert.NotEqual(baseline.FullHash, changed.FullHash);
    }

    [Theory]
    [InlineData("{\"EnableSanityVisualEffects\":false}")]
    [InlineData("{\"EnableLowSanityScreenDistortion\":false}")]
    [InlineData("{\"EnableSanityVignette\":false}")]
    [InlineData("{\"EnableDawnDuskMusic\":false}")]
    [InlineData("{\"UnknownLegacy\":123}")]
    public void LocalOrUnknownValueDoesNotChangeFingerprint(string changedJson)
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var baseline = Fingerprint(registry, "{}");
        var changed = Fingerprint(registry, changedJson);

        Assert.Equal(baseline.CanonicalText, changed.CanonicalText);
        Assert.Equal(baseline.FullHash, changed.FullHash);
    }

    [Fact]
    public void InvalidRequiredWorldValueReturnsUnavailableInsteadOfDefaultHash()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var fingerprint = Fingerprint(
            registry,
            "{\"SanityMonsterIntensity\":\"Impossible\"}"
        );

        Assert.False(fingerprint.IsAvailable);
        Assert.Equal(
            $"fingerprint.value-unavailable:{ConfigKeys.SanityMonsterIntensity}",
            fingerprint.Reason
        );
        Assert.Equal($"Unavailable:{fingerprint.Reason}", fingerprint.PublicIdentifier);
        Assert.Empty(fingerprint.FullHash);
        Assert.Empty(fingerprint.CanonicalText);
    }

    [Fact]
    public void MissingRequiredWorldSchemaOptionReturnsUnavailable()
    {
        var schema = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(
                ConfigTestData.BooleanOption(
                    ConfigKeys.EnableDawnDuskMusic,
                    true,
                    false,
                    false,
                    90
                )
            )
        );
        var registry = Assert.IsType<ConfigRegistry>(schema.Registry);
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);

        var fingerprint = GameplayConfigFingerprint.Create(registry, resolver);

        Assert.False(fingerprint.IsAvailable);
        Assert.StartsWith("Unavailable:fingerprint.value-unavailable:", fingerprint.PublicIdentifier, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulSaveRefreshesResolverAndFingerprintOnlyAfterWrite()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var before = GameplayConfigFingerprint.Create(registry, resolver);

        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableSanitySystem] = ConfigValue.Boolean(false),
            }
        );
        Assert.True(saved.Success, saved.Reason);
        resolver.Refresh();
        var after = GameplayConfigFingerprint.Create(registry, resolver);

        Assert.False(resolver.GetBoolean(ConfigKeys.EnableSanitySystem).Value);
        Assert.NotEqual(before.FullHash, after.FullHash);
    }

    private static GameplayConfigFingerprint Fingerprint(
        ConfigRegistry registry,
        string json
    )
    {
        var file = new MemoryFlatConfigFileAccess(json);
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        return GameplayConfigFingerprint.Create(
            registry,
            new TypedConfigResolver(registry, store)
        );
    }
}
