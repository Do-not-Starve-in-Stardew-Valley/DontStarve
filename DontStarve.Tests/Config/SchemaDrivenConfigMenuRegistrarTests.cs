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
            new[]
            {
                "dontstarve.config.config.game-mechanics.page",
                "dontstarve.config.config.hunger.page",
                "dontstarve.config.config.sanity.page",
                "dontstarve.config.config.health.page",
                "dontstarve.config.config.darkness.page",
                "dontstarve.config.config.detail-adjustments.page",
                "dontstarve.config.config.balance-compatibility.page",
                "dontstarve.config.config.vanilla-balance.page",
                "dontstarve.config.config.operations-settings.page",
                "dontstarve.config.config.combat-settings.page",
                "dontstarve.config.config.visuals-audio.page",
                "dontstarve.config.config.screen-filter.page",
                "dontstarve.config.config.audio-effects.page",
            },
            menu.Pages
        );
        Assert.Equal(
            new[]
            {
                "dontstarve.config.config.game-mechanics.page",
                "dontstarve.config.config.balance-compatibility.page",
                "dontstarve.config.config.operations-settings.page",
                "dontstarve.config.config.visuals-audio.page",
                "dontstarve.config.config.hunger.page",
                "dontstarve.config.config.sanity.page",
                "dontstarve.config.config.health.page",
                "dontstarve.config.config.darkness.page",
                "dontstarve.config.config.detail-adjustments.page",
                "dontstarve.config.config.vanilla-balance.page",
                "dontstarve.config.config.combat-settings.page",
                "dontstarve.config.config.screen-filter.page",
                "dontstarve.config.config.audio-effects.page",
            },
            menu.PageLinks
        );
        Assert.Equal(
            registry.Options.Select(option => option.Key),
            menu.FieldOrder
        );
        Assert.Equal(23, menu.BooleanFields.Count);
        Assert.Equal(7, menu.EnumFields.Count);
        Assert.Empty(menu.KeybindFields);
        Assert.Single(menu.NumberFields);
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableHungerSystem].GetValue()
        );
        Assert.True(menu.BooleanFields[ConfigKeys.SleepRecoveryBalance].GetValue());
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableSeedEdibility].GetValue()
        );
        Assert.False(
            menu.BooleanFields[ConfigKeys.EnableDawnDuskMusic].GetValue()
        );
        Assert.True(menu.BooleanFields[ConfigKeys.EnableActivityMusic].GetValue());
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableFarmingMusic].GetValue()
        );
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableSanityVignette].GetValue()
        );
        Assert.True(
            menu.BooleanFields[ConfigKeys.EnableLowSanityScreenDistortion].GetValue()
        );
        Assert.True(
            menu.BooleanFields[ConfigKeys.SlingshotAutoRelease].GetValue()
        );

        var profile = menu.EnumFields[ConfigKeys.MonsterDifficultyProfile];
        Assert.Equal(
            new[] { "Stardew", "Compatible", "DontStarve", "Fusion" },
            profile.AllowedValues
        );
        Assert.Equal("Don't Starve", profile.FormatAllowedValue("DontStarve"));
        Assert.Equal("Unknown", profile.FormatAllowedValue("Unknown"));
        Assert.Equal(
            new[] { "Default", "NonLethal", "Disabled" },
            menu.EnumFields[ConfigKeys.HungerDamageMode].AllowedValues
        );
        Assert.Equal(
            new[] { "all", "clubAndDagger", "swordOnly", "off" },
            menu.EnumFields[ConfigKeys.AutoSpecialMode].AllowedValues
        );
        Assert.Equal("On", menu.EnumFields[ConfigKeys.AutoSpecialMode].FormatAllowedValue("all"));

        // 弹弓力度滑轮（Number 通道）：滑轮边界/步长/默认值（用户拍板默认最高 100%）/百分比显示格式。
        var slingshotPower = menu.NumberFields[ConfigKeys.SlingshotPower];
        Assert.Equal(25, slingshotPower.Min);
        Assert.Equal(100, slingshotPower.Max);
        Assert.Equal(5, slingshotPower.Interval);
        Assert.Equal(100, slingshotPower.GetValue());
        Assert.Equal("100%", slingshotPower.FormatValue(100));
        Assert.Equal("50%", slingshotPower.FormatValue(50));
        Assert.Equal("25%", slingshotPower.FormatValue(25));
        Assert.Empty(diagnostics);

        foreach (var field in menu.BooleanFields.Values)
            _ = field.GetValue();
        foreach (var field in menu.EnumFields.Values)
        {
            _ = field.GetValue();
            foreach (var allowed in field.AllowedValues)
                _ = field.FormatAllowedValue(allowed);
        }
        foreach (var field in menu.KeybindFields.Values)
            _ = field.GetValue();
        foreach (var field in menu.NumberFields.Values)
        {
            _ = field.GetValue();
            _ = field.FormatValue(field.GetValue());
        }
        menu.BooleanFields[ConfigKeys.EnableSanitySystem].SetValue(false);
        menu.NumberFields[ConfigKeys.SlingshotPower].SetValue(75);
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
    public void KeybindChannelRoundTripsCanonicalButtonNameThroughStoreAndResolver()
    {
        // 真实 shipped schema 已无 keybind 选项（智能索敌/自动战斗 2026-11-15 移除）；
        // 本测试用样例 schema 覆盖键位通道（ConfigTestData.KeybindOption）。
        var parse = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(
                ConfigTestData.KeybindOption("TestKeybindChannel", "None", 1),
                ConfigTestData.KeybindOption("TestKeybindChannelAlt", "None", 2)
            )
        );
        Assert.True(
            parse.Status == ConfigSchemaStatus.Available,
            $"schema status={parse.Status} reason={parse.Reason} diagnostics=[{string.Join(", ", parse.Diagnostics)}]"
        );
        var registry = Assert.IsType<ConfigRegistry>(parse.Registry);
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var resolver = new TypedConfigResolver(registry, store);
        var menu = new RecordingConfigMenuApi();
        var completed = new List<FlatConfigSaveResult>();

        var result = SchemaDrivenConfigMenuRegistrar.Register(
            registry,
            resolver,
            SaveAndRefresh,
            menu,
            JsonTranslationProvider.Default,
            _ => { },
            completed.Add
        );

        Assert.True(
            result.Status == ConfigMenuRegistrationStatus.Available,
            $"status={result.Status} reason={result.Reason}"
        );
        var smartTarget = menu.KeybindFields["TestKeybindChannel"];
        var autoCombat = menu.KeybindFields["TestKeybindChannelAlt"];

        // 缺省值：未绑定恒为 "None"。
        Assert.Equal("None", smartTarget.GetValue());
        Assert.Equal("None", autoCombat.GetValue());

        // 绑定后保存：raw 值是规范枚举名字符串（JSON string），resolver 读回同一字符串。
        smartTarget.SetValue("OemSemicolon");
        Assert.NotNull(menu.SaveAction);
        menu.SaveAction!();

        Assert.True(Assert.Single(completed).Success);
        using (var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten)))
        {
            Assert.Equal(
                "OemSemicolon",
                document.RootElement.GetProperty("TestKeybindChannel").GetString()
            );
            Assert.Equal(
                "None",
                document.RootElement.GetProperty("TestKeybindChannelAlt").GetString()
            );
        }

        Assert.Equal(
            "OemSemicolon",
            resolver.GetKeybind("TestKeybindChannel").Value
        );
        Assert.Equal("None", resolver.GetKeybind("TestKeybindChannelAlt").Value);

        // 反向 round-trip：写回 "None" 保存后仍是未绑定语义。
        autoCombat.SetValue("None");
        smartTarget.SetValue("None");
        menu.SaveAction!();

        Assert.Equal(2, completed.Count);
        Assert.True(completed[1].Success);
        using (var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten)))
        {
            Assert.Equal(
                "None",
                document.RootElement.GetProperty("TestKeybindChannel").GetString()
            );
        }
        Assert.Equal("None", resolver.GetKeybind("TestKeybindChannel").Value);

        // 非法编辑：编辑会话 fail-closed 拒绝，当前值保持不变，不抛异常。
        smartTarget.SetValue("not a button!");
        Assert.Equal("None", smartTarget.GetValue());

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
    public void InvalidKeybindRawValueIsUnavailableAndEditSessionFallsBackToDefault()
    {
        var parse = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(
                ConfigTestData.KeybindOption("TestKeybindChannel", "None", 1)
            )
        );
        Assert.True(
            parse.Status == ConfigSchemaStatus.Available,
            $"schema status={parse.Status} reason={parse.Reason} diagnostics=[{string.Join(", ", parse.Diagnostics)}]"
        );
        var registry = Assert.IsType<ConfigRegistry>(parse.Registry);
        var file = new MemoryFlatConfigFileAccess(
            "{\"TestKeybindChannel\":\"not a button!\"}"
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
            store.TrySave,
            menu,
            JsonTranslationProvider.Default,
            diagnostics.Add,
            _ => { }
        );

        // 垃圾 raw 值被 resolver 判为不可用，不会进入保存通道，也不会让注册退化。
        var raw = resolver.GetKeybind("TestKeybindChannel");
        Assert.False(raw.HasValue);
        Assert.Equal(ConfigValueStatus.UnavailableForKey, raw.Status);
        Assert.True(
            result.Status == ConfigMenuRegistrationStatus.Available,
            $"status={result.Status} reason={result.Reason}"
        );
        Assert.Empty(diagnostics);
        Assert.Equal(
            "None",
            menu.KeybindFields["TestKeybindChannel"].GetValue()
        );
        Assert.Equal(0, file.WriteCount);
    }

    [Fact]
    public void KeybindNameContractRejectsGarbageAndAcceptsButtonNames()
    {
        // 纯逻辑层用标识符形状校验（测试工程不引用 Stardew）锁定与 SButton.ToString()
        // 相同的合法形状；SMAPI 侧的 Enum.TryParse + IsDefined 在此契约上做精确名单过滤。
        Assert.True(ConfigKeybind.IsValidButtonName("None"));
        Assert.True(ConfigKeybind.IsValidButtonName("OemSemicolon"));
        Assert.True(ConfigKeybind.IsValidButtonName("LeftControl"));
        Assert.True(ConfigKeybind.IsValidButtonName("D0"));
        Assert.False(ConfigKeybind.IsValidButtonName(""));
        Assert.False(ConfigKeybind.IsValidButtonName(" "));
        Assert.False(ConfigKeybind.IsValidButtonName("not a button!"));
        Assert.False(ConfigKeybind.IsValidButtonName("42"));
        Assert.False(ConfigKeybind.IsValidButtonName(null));

        Assert.Equal("None", ConfigKeybind.Unbound);
        Assert.Equal(
            ConfigOptionType.Keybind,
            ConfigValue.Keybind("OemSemicolon").Type
        );
        Assert.Equal(
            "OemSemicolon",
            ConfigValue.Keybind("OemSemicolon").CanonicalText
        );
        Assert.Throws<ArgumentException>(() => ConfigValue.Keybind("not a button!"));
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

        Assert.True(
            result.Status == ConfigMenuRegistrationStatus.Available,
            $"status={result.Status} reason={result.Reason}"
        );
        var sanity = menu.BooleanFields[ConfigKeys.EnableSanitySystem];
        var hunger = menu.BooleanFields[ConfigKeys.EnableHungerSystem];
        var seedEdibility = menu.BooleanFields[ConfigKeys.EnableSeedEdibility];
        var darkHand = menu.EnumFields[ConfigKeys.DarkHandMode];
        Assert.Equal(
            "Hunger",
            menu.PageTitleGetters["dontstarve.config.config.hunger.page"]()
        );
        Assert.Equal("Hunger", hunger.GetName());
        Assert.Equal("Enable the Hunger system.", hunger.GetTooltip());
        Assert.Equal("Enable seed edibility", seedEdibility.GetName());
        Assert.Equal(
            "Sanity",
            menu.PageTitleGetters["dontstarve.config.config.sanity.page"]()
        );
        Assert.Equal("Sanity", sanity.GetName());
        Assert.Equal("Enable the Sanity system.", sanity.GetTooltip());
        Assert.Equal("Fire Thief", darkHand.FormatAllowedValue("FireThief"));

        translations.SetValues(JsonTranslationProvider.Zh.Values);

        Assert.Equal(
            "理智",
            menu.PageTitleGetters["dontstarve.config.config.sanity.page"]()
        );
        Assert.Equal("理智", sanity.GetName());
        Assert.Equal("启用理智系统", sanity.GetTooltip());
        Assert.Equal(
            "饥饿",
            menu.PageTitleGetters["dontstarve.config.config.hunger.page"]()
        );
        Assert.Equal("饥饿", hunger.GetName());
        Assert.Equal("启用饥饿系统", hunger.GetTooltip());
        Assert.Equal("种子可食用", seedEdibility.GetName());
        Assert.Equal("允许玩家通过右键物品栏强制食用种子", seedEdibility.GetTooltip());
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
                Assert.Equal(31, document.RootElement.EnumerateObject().Count(property =>
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
        Assert.Equal(30, partial.RegisteredKeys.Count);
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
            "config.game-mechanics.page",
            "config.sanity.page",
            "config.enable-sanity-system.name",
            "config.enable-sanity-system.tooltip",
            10,
            20,
            10
        );
        var music = new ConfigOptionDefinition(
            ConfigKeys.EnableDawnDuskMusic,
            ConfigOptionType.Boolean,
            ConfigValue.Boolean(true),
            Array.Empty<string>(),
            false,
            false,
            "config.visuals-audio.page",
            "config.audio-effects.page",
            "config.enable-dawn-dusk-music.name",
            "config.enable-dawn-dusk-music.tooltip",
            30,
            20,
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
    public void DefaultAndChineseI18nKeysAlignWithSchemaAndCurrentApprovedCopy()
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
            AssertTranslation(option.ParentPageI18n);
            AssertTranslation(option.PageI18n);
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

        Assert.Equal(
            "Affects monster stat values.",
            defaultValues["config.monster-difficulty-profile.tooltip"]
        );
        Assert.Equal(
            "影响怪物的数值强度",
            zhValues["config.monster-difficulty-profile.tooltip"]
        );
        Assert.Equal(
            "Affects how Dark Hands interact with the world.",
            defaultValues["config.dark-hand-mode.tooltip"]
        );
        Assert.Equal(
            "影响暗影之手与世界的交互方式",
            zhValues["config.dark-hand-mode.tooltip"]
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

    internal List<string> Pages { get; } = new();

    internal List<string> PageLinks { get; } = new();

    internal Dictionary<string, Func<string>> PageTitleGetters { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, Func<string>> PageLinkTextGetters { get; } =
        new(StringComparer.Ordinal);

    internal List<string> FieldOrder { get; } = new();

    internal Dictionary<string, RecordedBooleanField> BooleanFields { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, RecordedEnumField> EnumFields { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, RecordedKeybindField> KeybindFields { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, RecordedNumberField> NumberFields { get; } =
        new(StringComparer.Ordinal);

    public void Register(Action reset, Action save)
    {
        RegisterCount++;
        if (ThrowOnRegister)
            throw new MissingMethodException("simulated GMCM mismatch");

        ResetAction = reset;
        SaveAction = save;
    }

    public void AddPage(string pageId, Func<string> getTitle)
    {
        if (PageTitleGetters.ContainsKey(pageId))
            return;

        Pages.Add(pageId);
        PageTitleGetters.Add(pageId, getTitle);
    }

    public void AddPageLink(string pageId, Func<string> getText)
    {
        PageLinks.Add(pageId);
        PageLinkTextGetters.Add(pageId, getText);
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

    public void AddKeybind(
        string fieldId,
        Func<string> getValue,
        Action<string> setValue,
        Func<string> getName,
        Func<string> getTooltip
    )
    {
        ThrowIfRequested(fieldId);
        FieldOrder.Add(fieldId);
        KeybindFields.Add(
            fieldId,
            new RecordedKeybindField(getValue, setValue, getName, getTooltip)
        );
    }

    public void AddNumber(
        string fieldId,
        Func<int> getValue,
        Action<int> setValue,
        Func<string> getName,
        Func<string> getTooltip,
        int min,
        int max,
        int interval,
        Func<int, string> formatValue
    )
    {
        ThrowIfRequested(fieldId);
        FieldOrder.Add(fieldId);
        NumberFields.Add(
            fieldId,
            new RecordedNumberField(
                getValue,
                setValue,
                getName,
                getTooltip,
                min,
                max,
                interval,
                formatValue
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

internal sealed record RecordedKeybindField(
    Func<string> GetValue,
    Action<string> SetValue,
    Func<string> GetName,
    Func<string> GetTooltip
);

internal sealed record RecordedNumberField(
    Func<int> GetValue,
    Action<int> SetValue,
    Func<string> GetName,
    Func<string> GetTooltip,
    int Min,
    int Max,
    int Interval,
    Func<int, string> FormatValue
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
