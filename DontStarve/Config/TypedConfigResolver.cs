#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;

namespace DontStarve.Config;

internal enum ConfigValueStatus
{
    Available,
    AvailableWithDefault,
    UnavailableForKey,
}

internal readonly record struct BooleanConfigResolution(
    bool HasValue,
    bool Value,
    ConfigValueStatus Status,
    string Reason
);

internal readonly record struct EnumConfigResolution(
    bool HasValue,
    string Value,
    ConfigValueStatus Status,
    string Reason
);

internal sealed class ResolvedConfigEntry
{
    internal ResolvedConfigEntry(
        bool hasValue,
        ConfigValue? value,
        ConfigValueStatus status,
        string reason
    )
    {
        HasValue = hasValue;
        Value = value;
        Status = status;
        Reason = reason;
    }

    internal bool HasValue { get; }

    internal ConfigValue? Value { get; }

    internal ConfigValueStatus Status { get; }

    internal string Reason { get; }
}

internal sealed class TypedConfigSnapshot
{
    private readonly ReadOnlyDictionary<string, ResolvedConfigEntry> entries;

    internal TypedConfigSnapshot(IReadOnlyDictionary<string, ResolvedConfigEntry> entries)
    {
        this.entries = new ReadOnlyDictionary<string, ResolvedConfigEntry>(
            new Dictionary<string, ResolvedConfigEntry>(entries, StringComparer.Ordinal)
        );
    }

    internal bool TryGet(string key, out ResolvedConfigEntry? entry)
    {
        return entries.TryGetValue(key, out entry);
    }
}

internal sealed class TypedConfigResolver
{
    private readonly ConfigRegistry registry;
    private readonly FlatConfigValueStore store;
    private TypedConfigSnapshot snapshot;

    internal TypedConfigResolver(ConfigRegistry registry, FlatConfigValueStore store)
    {
        this.registry = registry;
        this.store = store;
        snapshot = BuildSnapshot(registry, store.GetSnapshot());
    }

    internal BooleanConfigResolution GetBoolean(string key)
    {
        var entry = Resolve(key, ConfigOptionType.Boolean);
        if (
            !entry.HasValue
            || entry.Value is null
            || entry.Value.Type != ConfigOptionType.Boolean
        )
        {
            return new BooleanConfigResolution(false, false, entry.Status, entry.Reason);
        }

        return new BooleanConfigResolution(
            true,
            entry.Value.BooleanValue,
            entry.Status,
            entry.Reason
        );
    }

    internal EnumConfigResolution GetEnum(string key)
    {
        var entry = Resolve(key, ConfigOptionType.Enum);
        if (
            !entry.HasValue
            || entry.Value is null
            || entry.Value.Type != ConfigOptionType.Enum
            || entry.Value.EnumValue is null
        )
        {
            return new EnumConfigResolution(false, string.Empty, entry.Status, entry.Reason);
        }

        return new EnumConfigResolution(
            true,
            entry.Value.EnumValue,
            entry.Status,
            entry.Reason
        );
    }

    internal void Refresh()
    {
        var refreshed = BuildSnapshot(registry, store.GetSnapshot());
        Volatile.Write(ref snapshot, refreshed);
    }

    internal bool TryGetCanonical(string key, out string canonical, out string reason)
    {
        canonical = string.Empty;
        var current = Volatile.Read(ref snapshot);
        if (
            !current.TryGet(key, out var entry)
            || entry is null
            || !entry.HasValue
            || entry.Value is null
        )
        {
            reason = entry?.Reason ?? $"config.key-unavailable:{key}";
            return false;
        }

        canonical = entry.Value.CanonicalText;
        reason = entry.Reason;
        return true;
    }

    private ResolvedConfigEntry Resolve(string key, ConfigOptionType expectedType)
    {
        if (
            !registry.TryGet(key, out var option)
            || option is null
            || option.Type != expectedType
        )
        {
            return new ResolvedConfigEntry(
                false,
                null,
                ConfigValueStatus.UnavailableForKey,
                $"config.key-unavailable:{key}"
            );
        }

        var current = Volatile.Read(ref snapshot);
        if (current.TryGet(key, out var entry) && entry is not null)
            return entry;

        return new ResolvedConfigEntry(
            false,
            null,
            ConfigValueStatus.UnavailableForKey,
            $"config.key-unavailable:{key}"
        );
    }

    private static TypedConfigSnapshot BuildSnapshot(
        ConfigRegistry registry,
        FlatConfigSnapshot values
    )
    {
        var entries = new Dictionary<string, ResolvedConfigEntry>(StringComparer.Ordinal);
        foreach (var option in registry.Options)
        {
            if (!values.TryGetRaw(option.Key, out var rawValue) || rawValue is null)
            {
                entries.Add(
                    option.Key,
                    new ResolvedConfigEntry(
                        true,
                        option.DefaultValue,
                        ConfigValueStatus.AvailableWithDefault,
                        $"config.key-missing:{option.Key}"
                    )
                );
                continue;
            }

            if (TryResolveRaw(option, rawValue, out var resolvedValue))
            {
                entries.Add(
                    option.Key,
                    new ResolvedConfigEntry(
                        true,
                        resolvedValue,
                        ConfigValueStatus.Available,
                        $"config.value-available:{option.Key}"
                    )
                );
            }
            else
            {
                entries.Add(
                    option.Key,
                    new ResolvedConfigEntry(
                        false,
                        null,
                        ConfigValueStatus.UnavailableForKey,
                        $"config.invalid-value:{option.Key}"
                    )
                );
            }
        }

        return new TypedConfigSnapshot(entries);
    }

    private static bool TryResolveRaw(
        ConfigOptionDefinition option,
        string rawValue,
        out ConfigValue? value
    )
    {
        value = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawValue);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var element = document.RootElement;
            if (option.Type == ConfigOptionType.Boolean)
            {
                if (
                    element.ValueKind != JsonValueKind.True
                    && element.ValueKind != JsonValueKind.False
                )
                {
                    return false;
                }

                value = ConfigValue.Boolean(element.GetBoolean());
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
                return false;

            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var enumValue = ConfigValue.Enum(text);
            if (!option.Accepts(enumValue))
                return false;

            value = enumValue;
            return true;
        }
    }
}
