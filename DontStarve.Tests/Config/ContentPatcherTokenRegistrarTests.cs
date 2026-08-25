using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class ContentPatcherTokenRegistrarTests
{
    private static readonly string[] ExposedKeys =
    {
        ConfigKeys.EnableSanitySystem,
        ConfigKeys.SanityMonsterIntensity,
        ConfigKeys.DarkHandMode,
        ConfigKeys.EnableSanityVisualEffects,
        ConfigKeys.DarknessDamageMode,
        ConfigKeys.MonsterDifficultyProfile,
        ConfigKeys.EnableJunimoBlessing,
    };

    [Fact]
    public void ShippedSchemaRegistersSevenBareKeysWithCanonicalValues()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var resolver = CreateResolver(
            registry,
            "{\n"
                + "  \"EnableSanitySystem\": false,\n"
                + "  \"SanityMonsterIntensity\": \"Many\",\n"
                + "  \"DarkHandMode\": \"Thief\",\n"
                + "  \"EnableSanityVisualEffects\": false,\n"
                + "  \"DarknessDamageMode\": \"NonLethal\",\n"
                + "  \"MonsterDifficultyProfile\": \"DontStarve\",\n"
                + "  \"EnableJunimoBlessing\": true,\n"
                + "  \"EnableDawnDuskMusic\": false\n"
                + "}"
        );
        var api = new RecordingTokenApi();
        var diagnostics = new List<string>();

        var result = ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new AvailableApiProvider(api),
            diagnostics.Add
        );

        Assert.Equal(ContentPatcherTokenRegistrationStatus.Available, result.Status);
        Assert.Equal("cp.tokens-registered", result.Reason);
        Assert.Equal(ExposedKeys, result.RegisteredKeys);
        Assert.Equal(ExposedKeys, api.TokenOrder);
        Assert.Equal("false", api.GetSingle(ConfigKeys.EnableSanitySystem));
        Assert.Equal("Many", api.GetSingle(ConfigKeys.SanityMonsterIntensity));
        Assert.Equal("Thief", api.GetSingle(ConfigKeys.DarkHandMode));
        Assert.Equal("false", api.GetSingle(ConfigKeys.EnableSanityVisualEffects));
        Assert.Equal("NonLethal", api.GetSingle(ConfigKeys.DarknessDamageMode));
        Assert.Equal("DontStarve", api.GetSingle(ConfigKeys.MonsterDifficultyProfile));
        Assert.Equal("true", api.GetSingle(ConfigKeys.EnableJunimoBlessing));
        Assert.False(api.Contains(ConfigKeys.EnableSanityVignette));
        Assert.False(api.Contains(ConfigKeys.EnableLowSanityScreenDistortion));
        Assert.False(api.Contains(ConfigKeys.EnableDawnDuskMusic));
        Assert.Null(api.Query("UnknownToken"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TokenCallbackReadsRefreshedResolverSnapshotWithoutDiskIo()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var api = new RecordingTokenApi();
        ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new AvailableApiProvider(api),
            _ => { }
        );

        Assert.Equal("true", api.GetSingle(ConfigKeys.EnableSanitySystem));
        Assert.Equal(1, file.ReadCount);

        var save = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.EnableSanitySystem] = ConfigValue.Boolean(false),
            }
        );
        Assert.True(save.Success, save.Reason);
        resolver.Refresh();

        Assert.Equal("false", api.GetSingle(ConfigKeys.EnableSanitySystem));
        Assert.Equal(1, file.ReadCount);
        Assert.Equal(1, file.WriteCount);
    }

    [Fact]
    public void InvalidKnownValueReturnsNullAndReportsOnce()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var resolver = CreateResolver(
            registry,
            "{\"EnableSanitySystem\":\"not-a-boolean\"}"
        );
        var api = new RecordingTokenApi();
        var diagnostics = new List<string>();
        ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new AvailableApiProvider(api),
            diagnostics.Add
        );

        Assert.Null(api.Query(ConfigKeys.EnableSanitySystem));
        Assert.Null(api.Query(ConfigKeys.EnableSanitySystem));
        Assert.Equal(
            new[] { $"cp.token-value-unavailable:{ConfigKeys.EnableSanitySystem}" },
            diagnostics
        );
    }

    [Theory]
    [InlineData("cp.not-installed")]
    [InlineData("cp.version-too-old")]
    [InlineData("cp.api-mismatch")]
    public void UnavailableIntegrationFailsClosedWithStableReason(string reason)
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var resolver = CreateResolver(registry, "{}");
        var diagnostics = new List<string>();

        var result = ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new UnavailableApiProvider(reason),
            diagnostics.Add
        );

        Assert.Equal(ContentPatcherTokenRegistrationStatus.Unavailable, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(result.RegisteredKeys);
        Assert.Equal(new[] { reason }, diagnostics);
    }

    [Fact]
    public void SingleRegistrationFailureIsBoundedAndOtherTokensRemainAvailable()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var resolver = CreateResolver(registry, "{}");
        var api = new RecordingTokenApi(ConfigKeys.DarkHandMode);
        var diagnostics = new List<string>();

        var result = ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new AvailableApiProvider(api),
            diagnostics.Add
        );

        Assert.Equal(ContentPatcherTokenRegistrationStatus.Degraded, result.Status);
        Assert.Equal("cp.tokens-registration-degraded", result.Reason);
        Assert.DoesNotContain(ConfigKeys.DarkHandMode, result.RegisteredKeys);
        Assert.Equal(6, result.RegisteredKeys.Count);
        Assert.Equal(
            new[] { $"cp.token-registration-failed:{ConfigKeys.DarkHandMode}" },
            diagnostics
        );
    }

    [Fact]
    public void ProviderExceptionFailsClosedWithoutRegisteringTokens()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var resolver = CreateResolver(registry, "{}");
        var diagnostics = new List<string>();

        var result = ContentPatcherTokenRegistrar.Register(
            registry,
            resolver,
            new ThrowingApiProvider(),
            diagnostics.Add
        );

        Assert.Equal(ContentPatcherTokenRegistrationStatus.Unavailable, result.Status);
        Assert.Equal("cp.api-probe-failed", result.Reason);
        Assert.Equal(new[] { "cp.api-probe-failed" }, diagnostics);
    }

    private static TypedConfigResolver CreateResolver(
        ConfigRegistry registry,
        string json
    )
    {
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(
                registry,
                new MemoryFlatConfigFileAccess(json)
            ).Store
        );
        return new TypedConfigResolver(registry, store);
    }

    private sealed class AvailableApiProvider : IContentPatcherTokenApiProvider
    {
        private readonly IContentPatcherTokenRegistrationApi api;

        internal AvailableApiProvider(IContentPatcherTokenRegistrationApi api)
        {
            this.api = api;
        }

        public bool TryGetApi(
            out IContentPatcherTokenRegistrationApi? found,
            out string reason
        )
        {
            found = api;
            reason = "cp.api-available";
            return true;
        }
    }

    private sealed class UnavailableApiProvider : IContentPatcherTokenApiProvider
    {
        private readonly string reason;

        internal UnavailableApiProvider(string reason)
        {
            this.reason = reason;
        }

        public bool TryGetApi(
            out IContentPatcherTokenRegistrationApi? api,
            out string unavailableReason
        )
        {
            api = null;
            unavailableReason = reason;
            return false;
        }
    }

    private sealed class ThrowingApiProvider : IContentPatcherTokenApiProvider
    {
        public bool TryGetApi(
            out IContentPatcherTokenRegistrationApi? api,
            out string reason
        )
        {
            throw new InvalidOperationException("synthetic API failure");
        }
    }

    private sealed class RecordingTokenApi : IContentPatcherTokenRegistrationApi
    {
        private readonly Dictionary<string, Func<IEnumerable<string>?>> callbacks =
            new(StringComparer.Ordinal);
        private readonly string? failKey;

        internal RecordingTokenApi(string? failKey = null)
        {
            this.failKey = failKey;
        }

        internal IReadOnlyList<string> TokenOrder => callbacks.Keys.ToArray();

        public void RegisterToken(
            string name,
            Func<IEnumerable<string>?> getValue
        )
        {
            if (string.Equals(name, failKey, StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic registration failure");

            callbacks.Add(name, getValue);
        }

        internal bool Contains(string key)
        {
            return callbacks.ContainsKey(key);
        }

        internal IEnumerable<string>? Query(string key)
        {
            return callbacks.TryGetValue(key, out var callback) ? callback() : null;
        }

        internal string GetSingle(string key)
        {
            var values = Query(key);
            Assert.NotNull(values);
            return Assert.Single(values!);
        }
    }
}
