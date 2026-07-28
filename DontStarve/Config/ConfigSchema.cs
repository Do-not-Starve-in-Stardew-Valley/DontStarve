#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Config;

internal static class ConfigKeys
{
    internal const string EnableSanitySystem = "EnableSanitySystem";
    internal const string SanityMonsterIntensity = "SanityMonsterIntensity";
    internal const string DarkHandMode = "DarkHandMode";
    internal const string EnableSanityVisualEffects = "EnableSanityVisualEffects";
    internal const string DarknessDamageMode = "DarknessDamageMode";
    internal const string MonsterDifficultyProfile = "MonsterDifficultyProfile";
    internal const string EnableJunimoBlessing = "EnableJunimoBlessing";
    internal const string EnableDawnDuskMusic = "EnableDawnDuskMusic";

    private static readonly HashSet<string> FrozenKeys = new(
        new[]
        {
            EnableSanitySystem,
            SanityMonsterIntensity,
            DarkHandMode,
            EnableSanityVisualEffects,
            DarknessDamageMode,
            MonsterDifficultyProfile,
            EnableJunimoBlessing,
            EnableDawnDuskMusic,
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
}

internal sealed class ConfigValue : IEquatable<ConfigValue>
{
    private ConfigValue(ConfigOptionType type, bool booleanValue, string? enumValue)
    {
        Type = type;
        BooleanValue = booleanValue;
        EnumValue = enumValue;
    }

    internal ConfigOptionType Type { get; }

    internal bool BooleanValue { get; }

    internal string? EnumValue { get; }

    internal string CanonicalText =>
        Type == ConfigOptionType.Boolean
            ? (BooleanValue ? "true" : "false")
            : EnumValue ?? string.Empty;

    internal static ConfigValue Boolean(bool value)
    {
        return new ConfigValue(ConfigOptionType.Boolean, value, null);
    }

    internal static ConfigValue Enum(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Enum config values must be non-empty.", nameof(value));

        return new ConfigValue(ConfigOptionType.Enum, false, value);
    }

    public bool Equals(ConfigValue? other)
    {
        return other is not null
            && Type == other.Type
            && BooleanValue == other.BooleanValue
            && string.Equals(EnumValue, other.EnumValue, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is ConfigValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type, BooleanValue, EnumValue);
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
        string sectionI18n,
        string nameI18n,
        string tooltipI18n,
        int order
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
        SectionI18n = sectionI18n;
        NameI18n = nameI18n;
        TooltipI18n = tooltipI18n;
        Order = order;
    }

    internal string Key { get; }

    internal ConfigOptionType Type { get; }

    internal ConfigValue DefaultValue { get; }

    internal IReadOnlyList<string> AllowedValues { get; }

    internal bool ExposeToContentPatcher { get; }

    internal bool AffectsWorldState { get; }

    internal string SectionI18n { get; }

    internal string NameI18n { get; }

    internal string TooltipI18n { get; }

    internal int Order { get; }

    internal bool Accepts(ConfigValue value)
    {
        if (value.Type != Type)
            return false;

        return Type == ConfigOptionType.Boolean
            || (value.EnumValue is not null && allowedValueSet.Contains(value.EnumValue));
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
