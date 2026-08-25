using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class SchemaDrivenConfigMenuRegistrarTests
{
    [Fact]
    public void ShippedSchemaRegistersBooleanAndEnumFieldsInOrderWithoutWriting()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(
            "{\"EnableDawnDuskMusic\":false}"
        );
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var menu = new RecordingConfigMenuApi();
        var diagnostics = new List<string>();

        var result = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            SaveAndRefresh,
            menu,
            JsonTranslationProvider.Default,
            diagnostics.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Available, result.Status);
        Assert.Equal(
            registry.Options.Select(option => option.Key),
            result.RegisteredKeys
        );
        Assert.Equal(
            new[] { "config.sanity.section", "config.music.section" },
            menu.Sections
        );
        Assert.Equal(
            registry.Options.Select(option => option.Key),
            menu.FieldOrder
        );
        Assert.Equal(7, menu.BooleanFields.Count);
        Assert.Equal(4, menu.EnumFields.Count);
        Assert.False(
            menu.BooleanFields[ConfigKeys.EnableDawnDuskMusic].GetValue()
        );
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableSanityVignette].GetValue()
        );
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableLowSanityScreenDistortion].GetValue()
        );

        var profile = menu.EnumFields[ConfigKeys.MonsterDifficultyProfile];
        Assert.Equal(
            new[] { "Stardew", "Compatible", "DontStarve", "Fusion" },
            profile.AllowedValues
        );
        Assert.Equal("Don't Starve (Beta)", profile.FormatAllowedValue("DontStarve"));
        Assert.Equal("Unknown", profile.FormatAllowedValue("Unknown"));
        Assert.Empty(diagnostics);

        foreach (var field in menu.BooleanFields.Values)
            _ = field.GetValue();
        foreach (var field in menu.EnumFields.Values)
        {
            _ = field.GetValue();
            foreach (var allowed in field.AllowedValues)
                _ = field.FormatAllowedValue(allowed);
        }
        menu.BooleanFields[ConfigKeys.EnableSanitySystem].SetValue(false);
        profile.SetValue("Fusion");
        Assert.NotNull(menu.ResetAction);
        menu.ResetAction!();

        Assert.Equal(1, file.ReadCount);
        Assert.Equal(0, file.WriteCount);

        FlatConfigSaveResult SaveAndRefresh(
            IReadOnlyDictionary<string, ConfigValue> updates
        )
        {
            var saved = store.TrySave(updates);
            if (saved.Success)
                resolver.Refresh();
            return saved;
        }
    }

    [Fact]
    public void EnumEditSavesStableRawValueAndPreservesLegacyMusicFalse()
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
                var menu = new RecordingConfigMenuApi();
                var completed = new List<FlatConfigSaveResult>();

                var result = SchemaDrivenConfigMenuRegistrar.Register(
                    runtime,
                    menu,
                    JsonTranslationProvider.Zh,
                    _ => { },
                    completed.Add
                );
                Assert.Equal(ConfigMenuRegistrationStatus.Available, result.Status);
                Assert.False(
                    menu.BooleanFields[ConfigKeys.EnableDawnDuskMusic].GetValue()
                );

                menu.EnumFields[ConfigKeys.DarkHandMode].SetValue("Thief");
                Assert.Equal(
                    "窃取",
                    menu.EnumFields[ConfigKeys.DarkHandMode]
                        .FormatAllowedValue("Thief")
                );
                Assert.Equal(
                    "{\"UnknownLegacy\":{\"Keep\":7},\"EnableDawnDuskMusic\":false}",
                    File.ReadAllText(path)
                );

                Assert.NotNull(menu.SaveAction);
                menu.SaveAction!();

                var saved = Assert.Single(completed);
                Assert.True(saved.Success, saved.Reason);
                Assert.Equal(
                    "Thief",
                    runtime.Resolver.GetEnum(ConfigKeys.DarkHandMode).Value
                );
                Assert.False(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value
                );
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(
                    "Thief",
                    document.RootElement.GetProperty(ConfigKeys.DarkHandMode).GetString()
                );
                Assert.False(
                    document.RootElement
                        .GetProperty(ConfigKeys.EnableDawnDuskMusic)
                        .GetBoolean()
                );
                Assert.Equal(
                    7,
                    document.RootElement
                        .GetProperty("UnknownLegacy")
                        .GetProperty("Keep")
                        .GetInt32()
                );
            }
        );
    }

    [Fact]
    public void RegisteredConfigTextUsesTheLocaleAvailableWhenGmcmDraws()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var translations = new MutableTranslationProvider(
            JsonTranslationProvider.Default.Values
        );
        var menu = new RecordingConfigMenuApi();

        var result = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            store.TrySave,
            menu,
            translations,
            _ => { },
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Available, result.Status);
        var sanity = menu.BooleanFields[ConfigKeys.EnableSanitySystem];
        var darkHand = menu.EnumFields[ConfigKeys.DarkHandMode];
        Assert.Equal(
            "Sanity",
            menu.SectionTextGetters["config.sanity.section"]()
        );
        Assert.Equal("Enable Sanity system", sanity.GetName());
        Assert.Equal("Fire Thief", darkHand.FormatAllowedValue("FireThief"));

        translations.SetValues(JsonTranslationProvider.Zh.Values);

        Assert.Equal(
            "理智",
            menu.SectionTextGetters["config.sanity.section"]()
        );
        Assert.Equal("启用理智系统", sanity.GetName());
        Assert.Equal("启用理智系统及其玩法效果。", sanity.GetTooltip());
        Assert.Equal("偷火", darkHand.FormatAllowedValue("FireThief"));
    }

    [Fact]
    public void ResetOnlyChangesEditBufferUntilAtomicSaveAndReload()
    {
        WithTemporaryConfig(
            "{\"UnknownLegacy\":true,\"EnableDawnDuskMusic\":false,\"SanityMonsterIntensity\":\"Many\"}",
            path =>
            {
                var original = File.ReadAllText(path);
                var loaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                var runtime = Assert.IsType<ConfigurationRuntime>(loaded.Runtime);
                var menu = new RecordingConfigMenuApi();
                var completed = new List<FlatConfigSaveResult>();
                SchemaDrivenConfigMenuRegistrar.Register(
                    runtime,
                    menu,
                    JsonTranslationProvider.Default,
                    _ => { },
                    completed.Add
                );

                Assert.NotNull(menu.ResetAction);
                menu.ResetAction!();

                Assert.True(
                    menu.BooleanFields[ConfigKeys.EnableDawnDuskMusic].GetValue()
                );
                Assert.Equal(
                    "Default",
                    menu.EnumFields[ConfigKeys.SanityMonsterIntensity].GetValue()
                );
                Assert.Equal(original, File.ReadAllText(path));
                Assert.False(
                    runtime.Resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value
                );

                Assert.NotNull(menu.SaveAction);
                menu.SaveAction!();

                Assert.True(Assert.Single(completed).Success);
                var reloaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                var reloadedRuntime = Assert.IsType<ConfigurationRuntime>(
                    reloaded.Runtime
                );
                Assert.True(
                    reloadedRuntime.Resolver
                        .GetBoolean(ConfigKeys.EnableDawnDuskMusic)
                        .Value
                );
                Assert.Equal(
                    "Default",
                    reloadedRuntime.Resolver
                        .GetEnum(ConfigKeys.SanityMonsterIntensity)
                        .Value
                );
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Assert.True(document.RootElement.GetProperty("UnknownLegacy").GetBoolean());
                Assert.Equal(11, document.RootElement.EnumerateObject().Count(property =>
                    ConfigKeys.IsFrozen(property.Name)
                ));
            }
        );
    }

    [Fact]
    public void BadConfigAndMissingGmcmNeverWriteOrRegister()
    {
        WithTemporaryConfig(
            "{",
            path =>
            {
                var loaded = ConfigurationRuntime.Load(
                    ConfigTestData.ShippedSchemaPath,
                    path
                );
                Assert.Null(loaded.Runtime);
                var menu = new RecordingConfigMenuApi();
                var diagnostics = new List<string>();

                var badConfig = SchemaDrivenConfigMenuRegistrar.Register(
                    loaded.Runtime,
                    menu,
                    JsonTranslationProvider.Default,
                    diagnostics.Add,
                    _ => { }
                );

                Assert.Equal(
                    ConfigMenuRegistrationStatus.Unavailable,
                    badConfig.Status
                );
                Assert.Equal("gmcm.config-unavailable", badConfig.Reason);
                Assert.Equal(0, menu.RegisterCount);
                Assert.Equal("{", File.ReadAllText(path));
            }
        );

        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(
            "{\"EnableDawnDuskMusic\":false}"
        );
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var reasons = new List<string>();

        var noGmcm = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            store.TrySave,
            null,
            JsonTranslationProvider.Default,
            reasons.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Unavailable, noGmcm.Status);
        Assert.Equal("gmcm.not-installed", noGmcm.Reason);
        Assert.Equal(new[] { "gmcm.not-installed" }, reasons);
        Assert.Equal(0, file.WriteCount);
        Assert.Equal("{\"EnableDawnDuskMusic\":false}", file.Content);
    }

    [Fact]
    public void ApiMismatchAndSingleOptionFailureAreBounded()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var mismatchMenu = new RecordingConfigMenuApi { ThrowOnRegister = true };
        var mismatchReasons = new List<string>();

        var mismatch = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            store.TrySave,
            mismatchMenu,
            JsonTranslationProvider.Default,
            mismatchReasons.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Unavailable, mismatch.Status);
        Assert.Equal(new[] { "gmcm.api-mismatch" }, mismatchReasons);
        Assert.Empty(mismatchMenu.FieldOrder);

        var partialMenu = new RecordingConfigMenuApi
        {
            ThrowOnFieldId = ConfigKeys.DarkHandMode,
        };
        var partialReasons = new List<string>();
        var partial = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            store.TrySave,
            partialMenu,
            JsonTranslationProvider.Default,
            partialReasons.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Degraded, partial.Status);
        Assert.Equal(10, partial.RegisteredKeys.Count);
        Assert.DoesNotContain(ConfigKeys.DarkHandMode, partial.RegisteredKeys);
        Assert.Equal(
            new[] { $"gmcm.option-registration-failed:{ConfigKeys.DarkHandMode}" },
            partialReasons
        );
        Assert.Equal(0, file.WriteCount);
    }

    [Fact]
    public void FailedAtomicSaveKeepsActiveResolverAndNeverSignalsSuccessfulApply()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(
            "{\"EnableDawnDuskMusic\":false}"
        )
        {
            FailWrite = true,
        };
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var menu = new RecordingConfigMenuApi();
        var reasons = new List<string>();
        var successfulApplyCount = 0;
        SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            SaveAndRefresh,
            menu,
            JsonTranslationProvider.Default,
            reasons.Add,
            result =>
            {
                if (result.Success)
                    successfulApplyCount++;
            }
        );

        menu.BooleanFields[ConfigKeys.EnableDawnDuskMusic].SetValue(true);
        Assert.NotNull(menu.SaveAction);
        menu.SaveAction!();

        Assert.Equal(1, file.WriteCount);
        Assert.Equal(0, successfulApplyCount);
        Assert.False(resolver.GetBoolean(ConfigKeys.EnableDawnDuskMusic).Value);
        Assert.Equal("{\"EnableDawnDuskMusic\":false}", file.Content);
        Assert.Equal(
            new[] { "gmcm.save-failed:config.write-failed" },
            reasons
        );

        FlatConfigSaveResult SaveAndRefresh(
            IReadOnlyDictionary<string, ConfigValue> updates
        )
        {
            var saved = store.TrySave(updates);
            if (saved.Success)
                resolver.Refresh();
            return saved;
        }
    }

    [Fact]
    public void UnknownRegistryTypeIsSkippedWithoutHidingValidOption()
    {
        var unsupported = new ConfigOptionDefinition(
            ConfigKeys.EnableSanitySystem,
            (ConfigOptionType)999,
            ConfigValue.Boolean(true),
            Array.Empty<string>(),
            true,
            true,
            "config.sanity.section",
            "config.enable-sanity-system.name",
            "config.enable-sanity-system.tooltip",
            10
        );
        var music = new ConfigOptionDefinition(
            ConfigKeys.EnableDawnDuskMusic,
            ConfigOptionType.Boolean,
            ConfigValue.Boolean(true),
            Array.Empty<string>(),
            false,
            false,
            "config.music.section",
            "config.enable-dawn-dusk-music.name",
            "config.enable-dawn-dusk-music.tooltip",
            90
        );
        var registry = new ConfigRegistry(1, new[] { unsupported, music });
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var menu = new RecordingConfigMenuApi();
        var reasons = new List<string>();

        var result = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            store.TrySave,
            menu,
            JsonTranslationProvider.Default,
            reasons.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Degraded, result.Status);
        Assert.Equal(new[] { ConfigKeys.EnableDawnDuskMusic }, result.RegisteredKeys);
        Assert.Equal(
            new[] { $"gmcm.unsupported-type:{ConfigKeys.EnableSanitySystem}" },
            reasons
        );
        Assert.Equal(0, file.WriteCount);
    }

    [Fact]
    public void DefaultAndChineseI18nKeysAlignWithSchemaAndRiskCopy()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var defaultValues = JsonTranslationProvider.Default.Values;
        var zhValues = JsonTranslationProvider.Zh.Values;

        Assert.Equal(
            defaultValues.Keys.OrderBy(key => key, StringComparer.Ordinal),
            zhValues.Keys.OrderBy(key => key, StringComparer.Ordinal)
        );
        foreach (var option in registry.Options)
        {
            AssertTranslation(option.SectionI18n);
            AssertTranslation(option.NameI18n);
            AssertTranslation(option.TooltipI18n);
            foreach (var allowedValue in option.AllowedValues)
            {
                AssertTranslation(
                    SchemaDrivenConfigMenuRegistrar.GetEnumValueI18nKey(
                        option,
                        allowedValue
                    )
                );
            }
        }

        Assert.Contains(
            "may currently use identical numeric values",
            defaultValues["config.monster-difficulty-profile.tooltip"],
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "目前可能使用相同的数值",
            zhValues["config.monster-difficulty-profile.tooltip"],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "permanently delete",
            defaultValues["config.dark-hand-mode.tooltip"],
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "永久删除",
            zhValues["config.dark-hand-mode.tooltip"],
            StringComparison.Ordinal
        );

        void AssertTranslation(string key)
        {
            Assert.True(defaultValues.ContainsKey(key), $"default missing {key}");
            Assert.True(zhValues.ContainsKey(key), $"zh missing {key}");
            Assert.False(string.IsNullOrWhiteSpace(defaultValues[key]));
            Assert.False(string.IsNullOrWhiteSpace(zhValues[key]));
        }
    }

    private static void WithTemporaryConfig(string content, Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dontstarve-gmcm-{Guid.NewGuid():N}"
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

internal sealed class RecordingConfigMenuApi : IConfigMenuRegistrationApi
{
    internal int RegisterCount { get; private set; }

    internal bool ThrowOnRegister { get; init; }

    internal string? ThrowOnFieldId { get; init; }

    internal Action? ResetAction { get; private set; }

    internal Action? SaveAction { get; private set; }

    internal List<string> Sections { get; } = new();

    internal Dictionary<string, Func<string>> SectionTextGetters { get; } =
        new(StringComparer.Ordinal);

    internal List<string> FieldOrder { get; } = new();

    internal Dictionary<string, RecordedBooleanField> BooleanFields { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, RecordedEnumField> EnumFields { get; } =
        new(StringComparer.Ordinal);

    public void Register(Action reset, Action save)
    {
        RegisterCount++;
        if (ThrowOnRegister)
            throw new MissingMethodException("simulated GMCM mismatch");

        ResetAction = reset;
        SaveAction = save;
    }

    public void AddSection(string sectionId, Func<string> getText)
    {
        Sections.Add(sectionId);
        SectionTextGetters.Add(sectionId, getText);
    }

    public void AddBoolean(
        string fieldId,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> getName,
        Func<string> getTooltip
    )
    {
        ThrowIfRequested(fieldId);
        FieldOrder.Add(fieldId);
        BooleanFields.Add(
            fieldId,
            new RecordedBooleanField(getValue, setValue, getName, getTooltip)
        );
    }

    public void AddEnum(
        string fieldId,
        Func<string> getValue,
        Action<string> setValue,
        Func<string> getName,
        Func<string> getTooltip,
        string[] allowedValues,
        Func<string, string> formatAllowedValue
    )
    {
        ThrowIfRequested(fieldId);
        FieldOrder.Add(fieldId);
        EnumFields.Add(
            fieldId,
            new RecordedEnumField(
                getValue,
                setValue,
                getName,
                getTooltip,
                allowedValues,
                formatAllowedValue
            )
        );
    }

    private void ThrowIfRequested(string fieldId)
    {
        if (string.Equals(ThrowOnFieldId, fieldId, StringComparison.Ordinal))
            throw new InvalidOperationException("simulated field failure");
    }
}

internal sealed record RecordedBooleanField(
    Func<bool> GetValue,
    Action<bool> SetValue,
    Func<string> GetName,
    Func<string> GetTooltip
);

internal sealed record RecordedEnumField(
    Func<string> GetValue,
    Action<string> SetValue,
    Func<string> GetName,
    Func<string> GetTooltip,
    string[] AllowedValues,
    Func<string, string> FormatAllowedValue
);

internal sealed class JsonTranslationProvider : IConfigMenuTranslationProvider
{
    private static readonly Lazy<JsonTranslationProvider> DefaultProvider = new(
        () => Load("default.json")
    );
    private static readonly Lazy<JsonTranslationProvider> ZhProvider = new(
        () => Load("zh.json")
    );
    private readonly IReadOnlyDictionary<string, string> values;

    private JsonTranslationProvider(IReadOnlyDictionary<string, string> values)
    {
        this.values = values;
    }

    internal static JsonTranslationProvider Default => DefaultProvider.Value;

    internal static JsonTranslationProvider Zh => ZhProvider.Value;

    internal IReadOnlyDictionary<string, string> Values => values;

    public bool TryGet(string key, out string value)
    {
        return values.TryGetValue(key, out value!);
    }

    private static JsonTranslationProvider Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "I18n", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            loaded.Add(property.Name, property.Value.GetString() ?? string.Empty);

        return new JsonTranslationProvider(loaded);
    }
}

internal sealed class MutableTranslationProvider : IConfigMenuTranslationProvider
{
    private IReadOnlyDictionary<string, string> values;

    internal MutableTranslationProvider(IReadOnlyDictionary<string, string> values)
    {
        this.values = values;
    }

    internal void SetValues(IReadOnlyDictionary<string, string> values)
    {
        this.values = values;
    }

    public bool TryGet(string key, out string value)
    {
        return values.TryGetValue(key, out value!);
    }
}
