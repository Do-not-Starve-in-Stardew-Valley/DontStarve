using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

// Number 通道专项测试（弹弓力度滑轮地基，2026-11-22）：schema 解析 fail-closed 校验、
// flat store 数字读写往返、resolver GetInt 读取、GMCM 滑轮注册与越界编辑拒绝。
// 样例 schema 复用冻结键名（加载器要求 IsFrozen），不触达 shipped config-options.json。
public sealed class ConfigNumberChannelTests
{
    private const string TestKey = ConfigKeys.SlingshotPower;

    [Fact]
    public void Number_option_parses_bounds_and_interval()
    {
        var result = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1))
        );

        Assert.Equal(ConfigSchemaStatus.Available, result.Status);
        var registry = Assert.IsType<ConfigRegistry>(result.Registry);
        Assert.True(registry.TryGet(TestKey, out var option));
        Assert.NotNull(option);
        Assert.Equal(ConfigOptionType.Number, option!.Type);
        Assert.Equal(50, option.DefaultValue.IntegerValue);
        Assert.Equal(25, option.Min);
        Assert.Equal(100, option.Max);
        Assert.Equal(5, option.Interval);
        Assert.Empty(option.AllowedValues);
    }

    [Theory]
    [InlineData(20, 25, 100, 5)] // default 出界
    [InlineData(101, 25, 100, 5)] // default 出界
    [InlineData(50, 100, 25, 5)] // min >= max
    [InlineData(50, 25, 100, 0)] // interval <= 0
    [InlineData(50, 25, 100, -5)] // interval 负数
    public void Invalid_bounds_fail_closed(
        int defaultValue,
        int min,
        int max,
        int interval
    )
    {
        // 单选项 schema：唯一选项非法 → 该选项被丢弃并留诊断，状态为 Degraded（空注册表）。
        var result = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(
                ConfigTestData.NumberOption(TestKey, defaultValue, min, max, interval, 1)
            )
        );

        Assert.Equal(ConfigSchemaStatus.Degraded, result.Status);
        Assert.NotNull(result.Registry);
        Assert.Empty(result.Registry!.Options);
        Assert.Contains(result.Diagnostics, reason => reason.StartsWith("schema.invalid-default:", StringComparison.Ordinal));
    }

    [Fact]
    public void Number_option_rejects_fractional_or_string_default()
    {
        var fractional = ConfigTestData.Schema(
            ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1)
                .Replace("\"Default\": 50", "\"Default\": 50.5", StringComparison.Ordinal)
        );
        var fractionalResult = ConfigSchemaLoader.Parse(fractional);
        Assert.Equal(ConfigSchemaStatus.Degraded, fractionalResult.Status);
        Assert.Empty(Assert.IsType<ConfigRegistry>(fractionalResult.Registry).Options);

        var text = ConfigTestData.Schema(
            ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1)
                .Replace("\"Default\": 50", "\"Default\": \"50\"", StringComparison.Ordinal)
        );
        var textResult = ConfigSchemaLoader.Parse(text);
        Assert.Equal(ConfigSchemaStatus.Degraded, textResult.Status);
        Assert.Empty(Assert.IsType<ConfigRegistry>(textResult.Registry).Options);
    }

    [Fact]
    public void Boolean_option_rejects_number_only_properties()
    {
        // Min/Max/Interval 是 Number 专属；Boolean 出现这些属性按契约违例 fail-closed。
        var result = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(
                ConfigTestData.BooleanOption(
                    ConfigKeys.EnableActivityMusic,
                    true,
                    false,
                    false,
                    1,
                    extraProperty: "\"Min\": 25"
                )
            )
        );

        Assert.Equal(ConfigSchemaStatus.Degraded, result.Status);
        Assert.Empty(Assert.IsType<ConfigRegistry>(result.Registry).Options);
    }

    [Fact]
    public void Number_values_round_trip_through_store_and_resolver()
    {
        var registry = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1))
        ).Registry;
        Assert.NotNull(registry);

        var file = new MemoryFlatConfigFileAccess($"{{\"{TestKey}\": 75}}");
        var loaded = FlatConfigValueStore.Load(registry!, file);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry!, store);

        // 已有值读取。
        var read = resolver.GetInt(TestKey);
        Assert.True(read.HasValue);
        Assert.Equal(75, read.Value);

        // 越界值 fail-closed 回落默认（resolver 层 unavailable）。
        var outOfRange = FlatConfigValueStore.Load(
            registry!,
            new MemoryFlatConfigFileAccess($"{{\"{TestKey}\": 200}}")
        ).Store;
        var fallbackResolver = new TypedConfigResolver(registry!, outOfRange!);
        var fallback = fallbackResolver.GetInt(TestKey);
        Assert.False(fallback.HasValue);
        Assert.Equal(ConfigValueStatus.UnavailableForKey, fallback.Status);

        // 小数与字符串形态 fail-closed。
        var fractional = FlatConfigValueStore.Load(
            registry!,
            new MemoryFlatConfigFileAccess($"{{\"{TestKey}\": 27.5}}")
        ).Store;
        Assert.False(
            new TypedConfigResolver(registry!, fractional!).GetInt(TestKey).HasValue
        );
        var text = FlatConfigValueStore.Load(
            registry!,
            new MemoryFlatConfigFileAccess($"{{\"{TestKey}\": \"50\"}}")
        ).Store;
        Assert.False(new TypedConfigResolver(registry!, text!).GetInt(TestKey).HasValue);

        // 保存写回裸整数字面量（JSON 数字，非字符串）。
        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [TestKey] = ConfigValue.Number(90),
            }
        );
        Assert.True(saved.Success, saved.Reason);
        resolver.Refresh();
        Assert.Equal(90, resolver.GetInt(TestKey).Value);
        using var document = JsonDocument.Parse(Assert.IsType<string>(file.LastWritten));
        Assert.Equal(
            90,
            document.RootElement.GetProperty(TestKey).GetInt32()
        );
    }

    [Fact]
    public void Save_rejects_out_of_range_number_updates()
    {
        var registry = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1))
        ).Registry;
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry!, file).Store
        );

        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [TestKey] = ConfigValue.Number(101),
            }
        );
        Assert.False(saved.Success);
        Assert.Equal($"config.invalid-update:{TestKey}", saved.Reason);
        Assert.Equal(0, file.WriteCount);
    }

    [Fact]
    public void Missing_number_key_resolves_to_schema_default()
    {
        var registry = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1))
        ).Registry;
        var resolver = new TypedConfigResolver(
            registry!,
            Assert.IsType<FlatConfigValueStore>(
                FlatConfigValueStore.Load(registry!, new MemoryFlatConfigFileAccess("{}")).Store
            )
        );

        var resolution = resolver.GetInt(TestKey);
        Assert.True(resolution.HasValue);
        Assert.Equal(ConfigValueStatus.AvailableWithDefault, resolution.Status);
        Assert.Equal(50, resolution.Value);
    }

    [Fact]
    public void Menu_registration_records_number_slider_and_rejects_out_of_range_edits()
    {
        var registry = ConfigSchemaLoader.Parse(
            ConfigTestData.Schema(ConfigTestData.NumberOption(TestKey, 50, 25, 100, 5, 1))
        ).Registry;
        var file = new MemoryFlatConfigFileAccess("{}");
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry!, file).Store
        );
        var resolver = new TypedConfigResolver(registry!, store);
        var menu = new RecordingConfigMenuApi();
        var diagnostics = new List<string>();

        var result = SchemaDrivenConfigMenuRegistrar.Register(
            registry!,
            resolver,
            store.TrySave,
            menu,
            JsonTranslationProvider.Default,
            diagnostics.Add,
            _ => { }
        );

        Assert.Equal(ConfigMenuRegistrationStatus.Available, result.Status);
        var field = Assert.Single(menu.NumberFields).Value;
        Assert.Equal(25, field.Min);
        Assert.Equal(100, field.Max);
        Assert.Equal(5, field.Interval);
        Assert.Equal(50, field.GetValue());
        Assert.Equal("50%", field.FormatValue(50));

        // 越界编辑在编辑层被拒绝（GMCM 滑轮本就不会发出越界值；防御手改路径）。
        field.SetValue(101);
        Assert.Contains($"gmcm.invalid-edit:{TestKey}", diagnostics);
        Assert.Equal(50, field.GetValue());
    }
}
