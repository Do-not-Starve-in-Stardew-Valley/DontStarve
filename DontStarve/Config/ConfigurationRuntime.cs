#nullable enable

using System.Collections.Generic;

namespace DontStarve.Config;

internal sealed class ConfigurationRuntimeLoadResult
{
    internal ConfigurationRuntimeLoadResult(
        ConfigSchemaLoadResult schema,
        FlatConfigLoadResult? values,
        ConfigurationRuntime? runtime,
        string reason
    )
    {
        Schema = schema;
        Values = values;
        Runtime = runtime;
        Reason = reason;
    }

    internal ConfigSchemaLoadResult Schema { get; }

    internal FlatConfigLoadResult? Values { get; }

    internal ConfigurationRuntime? Runtime { get; }

    internal string Reason { get; }

    internal bool IsAvailable => Runtime is not null;
}

internal sealed class ConfigurationRuntime
{
    private readonly ConfigRegistry registry;
    private readonly FlatConfigValueStore store;

    private ConfigurationRuntime(ConfigRegistry registry, FlatConfigValueStore store)
    {
        this.registry = registry;
        this.store = store;
        Resolver = new TypedConfigResolver(registry, store);
        Fingerprint = GameplayConfigFingerprint.Create(registry, Resolver);
    }

    internal TypedConfigResolver Resolver { get; }

    internal ConfigRegistry Registry => registry;

    internal GameplayConfigFingerprint Fingerprint { get; private set; }

    internal static ConfigurationRuntimeLoadResult Load(
        string schemaPath,
        string configPath
    )
    {
        var schema = ConfigSchemaLoader.LoadFromFile(schemaPath);
        if (!schema.IsAvailable || schema.Registry is null)
        {
            return new ConfigurationRuntimeLoadResult(
                schema,
                null,
                null,
                schema.Reason
            );
        }

        var values = FlatConfigValueStore.Load(
            schema.Registry,
            new PhysicalFlatConfigFileAccess(configPath)
        );
        if (!values.IsAvailable || values.Store is null)
        {
            return new ConfigurationRuntimeLoadResult(
                schema,
                values,
                null,
                values.Reason
            );
        }

        return new ConfigurationRuntimeLoadResult(
            schema,
            values,
            new ConfigurationRuntime(schema.Registry, values.Store),
            "config.runtime-available"
        );
    }

    internal FlatConfigSaveResult TrySave(
        IReadOnlyDictionary<string, ConfigValue> updates
    )
    {
        var result = store.TrySave(updates);
        if (!result.Success)
            return result;

        // 磁盘原子替换成功后才一次刷新 typed cache 与诊断 fingerprint。
        Resolver.Refresh();
        Fingerprint = GameplayConfigFingerprint.Create(registry, Resolver);
        return result;
    }
}
