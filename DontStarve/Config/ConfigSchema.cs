#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Config;

internal static class ConfigKeys
{
    internal const string EnableHungerSystem = "EnableHungerSystem";
    internal const string HungerDamageMode = "HungerDamageMode";
    internal const string SleepRecoveryBalance = "SleepRecoveryBalance";
    internal const string EnableSeedEdibility = "EnableSeedEdibility";
    internal const string EnableSanitySystem = "EnableSanitySystem";
    internal const string SanityMonsterIntensity = "SanityMonsterIntensity";
    internal const string DarkHandMode = "DarkHandMode";
    internal const string EnableSanityVisualEffects = "EnableSanityVisualEffects";
    internal const string EnableLowSanityScreenDistortion =
        "EnableLowSanityScreenDistortion";
    internal const string EnableSanityVignette = "EnableSanityVignette";
    internal const string EnableNaturalDarkness = "EnableNaturalDarkness";
    internal const string SanityNaturalDrainRule = "SanityNaturalDrainRule";
    internal const string DarknessDamageMode = "DarknessDamageMode";
    internal const string EnableDontStarveLightAmbience =
        "EnableDontStarveLightAmbience";
    internal const string MonsterDifficultyProfile = "MonsterDifficultyProfile";
    internal const string EnableJunimoBlessing = "EnableJunimoBlessing";
    internal const string EnableDangerMinigameBlocking =
        "EnableDangerMinigameBlocking";
    internal const string EnableStatusFeedbackSounds = "EnableStatusFeedbackSounds";
    internal const string EnableDawnDuskMusic = "EnableDawnDuskMusic";
    internal const string EnableActivityMusic = "EnableActivityMusic";
    internal const string EnableFarmingMusic = "EnableFarmingMusic";
    internal const string EnableDontStarveHealth = "EnableDontStarveHealth";
    internal const string EnableHealthEffectBalance = "EnableHealthEffectBalance";
    internal const string EnableCombatAutoSwing = "EnableCombatAutoSwing";
    internal const string AutoSwingSword = "AutoSwingSword";
    internal const string AutoSwingClub = "AutoSwingClub";
    internal const string AutoSwingDagger = "AutoSwingDagger";
    internal const string SlingshotAutoRelease = "SlingshotAutoRelease";
    internal const string SlingshotPower = "SlingshotPower";
    internal const string AutoSpecialMode = "AutoSpecialMode";
    internal const string EnableMouseFacing = "EnableMouseFacing";

    private static readonly HashSet<string> FrozenKeys = new(
        new[]
        {
            EnableHungerSystem,
            HungerDamageMode,
            SleepRecoveryBalance,
            EnableSeedEdibility,
            EnableSanitySystem,
            SanityMonsterIntensity,
            DarkHandMode,
            EnableSanityVisualEffects,
            EnableLowSanityScreenDistortion,
            EnableSanityVignette,
            EnableNaturalDarkness,
            SanityNaturalDrainRule,
            DarknessDamageMode,
            EnableDontStarveLightAmbience,
            MonsterDifficultyProfile,
            EnableJunimoBlessing,
            EnableDangerMinigameBlocking,
            EnableStatusFeedbackSounds,
            EnableDawnDuskMusic,
            EnableActivityMusic,
            EnableFarmingMusic,
            EnableDontStarveHealth,
            EnableHealthEffectBalance,
            EnableCombatAutoSwing,
            AutoSwingSword,
            AutoSwingClub,
            AutoSwingDagger,
            SlingshotAutoRelease,
            SlingshotPower,
            AutoSpecialMode,
            EnableMouseFacing,
            // 键位绑定通道的测试锚键：仅供测试工程样例 schema 使用（键位绑定通道自
            // 智能索敌/自动战斗 2026-11-15 移除后无真实选项；shipped config-options.json
            // 不定义这两个键，GMCM 不会出现死选项）。
            "TestKeybindChannel",
            "TestKeybindChannelAlt",
        },
        StringComparer.Ordinal
    );

    internal static bool IsFrozen(string key)
    {
        return FrozenKeys.Contains(key);
    }
}

internal enum ConfigOptionType
{
    Boolean,
    Enum,
    Keybind,
    Number,
}

/// <summary>
/// Keybind 通道的唯一值约定：以 SButton 枚举名字符串存取，未绑定固定为 <see cref="Unbound"/>。
/// 纯逻辑层（含测试工程）不引用游戏程序集，无法枚举 SButton 成员；这里用保守的标识符形状
/// 校验 fail-closed 拒绝空值、空白与符号串（SButton 成员名全部是 PascalCase 标识符）。
/// 精确的 SButton 名单校验由 SMAPI 侧（ModConfigMenuRegistrar 的 Enum.TryParse + IsDefined）
/// 在字符串到 SButton 的转换边界完成，非法值回落 <see cref="Unbound"/>，绝不抛异常。
/// </summary>
internal static class ConfigKeybind
{
    internal const string Unbound = "None";

    internal static bool IsValidButtonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // SButton 成员名形状：首字符为 ASCII 字母或下划线，其余为 ASCII 字母/数字/下划线。
        // "None" 满足该形状，天然包含在合法集合内。
        if (!IsAsciiLetter(value[0]) && value[0] != '_')
            return false;

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character) && character != '_')
                return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char character)
    {
        return (character >= 'a' && character <= 'z')
            || (character >= 'A' && character <= 'Z');
    }

    private static bool IsAsciiDigit(char character)
    {
        return character >= '0' && character <= '9';
    }
}

internal sealed class ConfigValue : IEquatable<ConfigValue>
{
    private ConfigValue(
        ConfigOptionType type,
        bool booleanValue,
        string? enumValue,
        int integerValue
    )
    {
        Type = type;
        BooleanValue = booleanValue;
        EnumValue = enumValue;
        IntegerValue = integerValue;
    }

    internal ConfigOptionType Type { get; }

    internal bool BooleanValue { get; }

    internal string? EnumValue { get; }

    /// <summary>Number 通道的整数值；其它类型恒为 0，不得读取。</summary>
    internal int IntegerValue { get; }

    internal string CanonicalText =>
        Type switch
        {
            ConfigOptionType.Boolean => BooleanValue ? "true" : "false",
            // Number 的 canonical 文本即裸整数字面量（ToJson 直接落为 JSON 数字）。
            ConfigOptionType.Number => IntegerValue.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            _ => EnumValue ?? string.Empty,
        };

    internal static ConfigValue Boolean(bool value)
    {
        return new ConfigValue(ConfigOptionType.Boolean, value, null, 0);
    }

    internal static ConfigValue Enum(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Enum config values must be non-empty.", nameof(value));

        return new ConfigValue(ConfigOptionType.Enum, false, value, 0);
    }

    /// <summary>
    /// Keybind 与 Enum 并用同一 string 存储通道（值落在 <see cref="EnumValue"/> 槽位），
    /// 只是值域不同：任意合法按钮名而非封闭 allowedValues 表；"None" 表示未绑定。
    /// </summary>
    internal static ConfigValue Keybind(string value)
    {
        if (!ConfigKeybind.IsValidButtonName(value))
            throw new ArgumentException(
                "Keybind config values must be valid button names.",
                nameof(value)
            );

        return new ConfigValue(ConfigOptionType.Keybind, false, value, 0);
    }

    /// <summary>
    /// Number 与 Enum 并用独立整数通道：值域由选项定义的 Min/Max 边界约束，
    /// 边界校验在 <see cref="ConfigOptionDefinition.Accepts"/> 完成。
    /// </summary>
    internal static ConfigValue Number(int value)
    {
        return new ConfigValue(ConfigOptionType.Number, false, null, value);
    }

    public bool Equals(ConfigValue? other)
    {
        return other is not null
            && Type == other.Type
            && BooleanValue == other.BooleanValue
            && string.Equals(EnumValue, other.EnumValue, StringComparison.Ordinal)
            && IntegerValue == other.IntegerValue;
    }

    public override bool Equals(object? obj)
    {
        return obj is ConfigValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type, BooleanValue, EnumValue, IntegerValue);
    }
}

internal sealed class ConfigOptionDefinition
{
    private readonly HashSet<string> allowedValueSet;

    internal ConfigOptionDefinition(
        string key,
        ConfigOptionType type,
        ConfigValue defaultValue,
        IReadOnlyList<string> allowedValues,
        bool exposeToContentPatcher,
        bool affectsWorldState,
        string parentPageI18n,
        string pageI18n,
        string nameI18n,
        string tooltipI18n,
        int parentPageOrder,
        int pageOrder,
        int order,
        int? min = null,
        int? max = null,
        int? interval = null
    )
    {
        Key = key;
        Type = type;
        DefaultValue = defaultValue;
        var copiedAllowedValues = new string[allowedValues.Count];
        for (var index = 0; index < allowedValues.Count; index++)
            copiedAllowedValues[index] = allowedValues[index];

        AllowedValues = Array.AsReadOnly(copiedAllowedValues);
        allowedValueSet = new HashSet<string>(copiedAllowedValues, StringComparer.Ordinal);
        ExposeToContentPatcher = exposeToContentPatcher;
        AffectsWorldState = affectsWorldState;
        ParentPageI18n = parentPageI18n;
        PageI18n = pageI18n;
        NameI18n = nameI18n;
        TooltipI18n = tooltipI18n;
        ParentPageOrder = parentPageOrder;
        PageOrder = pageOrder;
        Order = order;
        Min = min;
        Max = max;
        Interval = interval;
    }

    internal string Key { get; }

    internal ConfigOptionType Type { get; }

    internal ConfigValue DefaultValue { get; }

    internal IReadOnlyList<string> AllowedValues { get; }

    internal bool ExposeToContentPatcher { get; }

    internal bool AffectsWorldState { get; }

    internal string ParentPageI18n { get; }

    internal string PageI18n { get; }

    internal string NameI18n { get; }

    internal string TooltipI18n { get; }

    internal int ParentPageOrder { get; }

    internal int PageOrder { get; }

    internal int Order { get; }

    /// <summary>
    /// Number 通道的滑轮边界与步长（GMCM AddNumberOption 的 min/max/interval）。
    /// 仅 Number 类型非空；schema 加载器对 Number fail-closed 校验边界完整性，
    /// 其它类型恒为 null。
    /// </summary>
    internal int? Min { get; }

    internal int? Max { get; }

    internal int? Interval { get; }

    internal bool Accepts(ConfigValue value)
    {
        if (value.Type != Type)
            return false;

        return Type switch
        {
            ConfigOptionType.Boolean => true,
            // Keybind 的值域是"合法按钮名"而不是封闭表，与 Enum 的 allowedValueSet 区分。
            ConfigOptionType.Keybind => value.EnumValue is not null
                && ConfigKeybind.IsValidButtonName(value.EnumValue),
            // Number 的值域是闭区间 [Min, Max]；步长由 GMCM 滑轮 UI 约束，手改 config.json
            // 的非步长值（如 27）仍是有效力度，不做步长拒绝——只挡出界。
            ConfigOptionType.Number => Min.HasValue
                && Max.HasValue
                && value.IntegerValue >= Min.Value
                && value.IntegerValue <= Max.Value,
            _ => value.EnumValue is not null && allowedValueSet.Contains(value.EnumValue),
        };
    }
}

internal sealed class ConfigRegistry
{
    private readonly ReadOnlyDictionary<string, ConfigOptionDefinition> byKey;

    internal ConfigRegistry(int schemaVersion, IReadOnlyCollection<ConfigOptionDefinition> options)
    {
        SchemaVersion = schemaVersion;
        var sorted = new List<ConfigOptionDefinition>(options);
        sorted.Sort(
            static (left, right) =>
            {
                var order = left.Order.CompareTo(right.Order);
                return order != 0
                    ? order
                    : StringComparer.Ordinal.Compare(left.Key, right.Key);
            }
        );

        Options = sorted.AsReadOnly();
        var map = new Dictionary<string, ConfigOptionDefinition>(StringComparer.Ordinal);
        foreach (var option in sorted)
            map.Add(option.Key, option);

        byKey = new ReadOnlyDictionary<string, ConfigOptionDefinition>(map);
    }

    internal int SchemaVersion { get; }

    internal IReadOnlyList<ConfigOptionDefinition> Options { get; }

    internal bool TryGet(string key, out ConfigOptionDefinition? option)
    {
        return byKey.TryGetValue(key, out option);
    }
}

internal enum ConfigSchemaStatus
{
    Available,
    Degraded,
    Unavailable,
}

internal sealed class ConfigSchemaLoadResult
{
    internal ConfigSchemaLoadResult(
        ConfigSchemaStatus status,
        string reason,
        ConfigRegistry? registry,
        IReadOnlyCollection<string>? diagnostics = null
    )
    {
        Status = status;
        Reason = reason;
        Registry = registry;
        Diagnostics = new ReadOnlyCollection<string>(
            diagnostics is null ? Array.Empty<string>() : new List<string>(diagnostics)
        );
    }

    internal ConfigSchemaStatus Status { get; }

    internal string Reason { get; }

    internal ConfigRegistry? Registry { get; }

    internal IReadOnlyList<string> Diagnostics { get; }

    internal bool IsAvailable => Registry is not null;

    internal static ConfigSchemaLoadResult Unavailable(string reason)
    {
        return new ConfigSchemaLoadResult(ConfigSchemaStatus.Unavailable, reason, null);
    }
}
