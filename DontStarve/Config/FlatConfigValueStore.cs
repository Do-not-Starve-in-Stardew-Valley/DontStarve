#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DontStarve.Config;

internal enum ConfigTextReadStatus
{
    Available,
    Missing,
    Error,
}

internal readonly record struct ConfigTextReadResult(
    ConfigTextReadStatus Status,
    string Content,
    string Reason
)
{
    internal static ConfigTextReadResult Available(string content)
    {
        return new ConfigTextReadResult(
            ConfigTextReadStatus.Available,
            content,
            "config.file-available"
        );
    }

    internal static ConfigTextReadResult Missing()
    {
        return new ConfigTextReadResult(
            ConfigTextReadStatus.Missing,
            string.Empty,
            "config.missing-use-schema-defaults"
        );
    }

    internal static ConfigTextReadResult Error(string reason)
    {
        return new ConfigTextReadResult(ConfigTextReadStatus.Error, string.Empty, reason);
    }
}

internal readonly record struct ConfigTextWriteResult(bool Success, string Reason)
{
    internal static ConfigTextWriteResult Written()
    {
        return new ConfigTextWriteResult(true, "config.write-succeeded");
    }

    internal static ConfigTextWriteResult Error(string reason)
    {
        return new ConfigTextWriteResult(false, reason);
    }
}

internal interface IFlatConfigFileAccess
{
    ConfigTextReadResult Read();

    ConfigTextWriteResult WriteAtomically(string content);
}

internal sealed class PhysicalFlatConfigFileAccess : IFlatConfigFileAccess
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string path;

    internal PhysicalFlatConfigFileAccess(string path)
    {
        this.path = path;
    }

    public ConfigTextReadResult Read()
    {
        if (!File.Exists(path))
            return ConfigTextReadResult.Missing();

        try
        {
            return ConfigTextReadResult.Available(File.ReadAllText(path, StrictUtf8));
        }
        catch (DecoderFallbackException)
        {
            return ConfigTextReadResult.Error("config.invalid-json");
        }
        catch (IOException)
        {
            return ConfigTextReadResult.Error("config.read-failed");
        }
        catch (UnauthorizedAccessException)
        {
            return ConfigTextReadResult.Error("config.read-failed");
        }
    }

    public ConfigTextWriteResult WriteAtomically(string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return ConfigTextWriteResult.Error("config.write-failed");

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        ConfigTextWriteResult result;
        try
        {
            File.WriteAllText(temporaryPath, content, Utf8WithoutBom);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null, true);
            else
                File.Move(temporaryPath, path);

            result = ConfigTextWriteResult.Written();
        }
        catch (IOException)
        {
            result = ConfigTextWriteResult.Error("config.write-failed");
        }
        catch (UnauthorizedAccessException)
        {
            result = ConfigTextWriteResult.Error("config.write-failed");
        }

        try
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            return result.Success
                ? new ConfigTextWriteResult(
                    true,
                    "config.write-succeeded-temp-cleanup-failed"
                )
                : ConfigTextWriteResult.Error("config.write-failed-temp-cleanup-failed");
        }
        catch (UnauthorizedAccessException)
        {
            return result.Success
                ? new ConfigTextWriteResult(
                    true,
                    "config.write-succeeded-temp-cleanup-failed"
                )
                : ConfigTextWriteResult.Error("config.write-failed-temp-cleanup-failed");
        }

        return result;
    }
}

internal sealed class FlatConfigSnapshot
{
    private readonly ReadOnlyDictionary<string, string> rawValues;

    internal FlatConfigSnapshot(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> propertyOrder
    )
    {
        rawValues = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.Ordinal)
        );
        var copiedOrder = new string[propertyOrder.Count];
        for (var index = 0; index < propertyOrder.Count; index++)
            copiedOrder[index] = propertyOrder[index];

        PropertyOrder = Array.AsReadOnly(copiedOrder);
    }

    internal IReadOnlyDictionary<string, string> RawValues => rawValues;

    internal IReadOnlyList<string> PropertyOrder { get; }

    internal bool TryGetRaw(string key, out string? rawValue)
    {
        return rawValues.TryGetValue(key, out rawValue);
    }
}

internal enum FlatConfigLoadStatus
{
    Available,
    AvailableWithDefaults,
    Unavailable,
}

internal sealed class FlatConfigLoadResult
{
    internal FlatConfigLoadResult(
        FlatConfigLoadStatus status,
        string reason,
        FlatConfigValueStore? store
    )
    {
        Status = status;
        Reason = reason;
        Store = store;
    }

    internal FlatConfigLoadStatus Status { get; }

    internal string Reason { get; }

    internal FlatConfigValueStore? Store { get; }

    internal bool IsAvailable => Store is not null;
}

internal readonly record struct FlatConfigSaveResult(bool Success, string Reason)
{
    internal static FlatConfigSaveResult Saved(string reason = "config.values-saved")
    {
        return new FlatConfigSaveResult(true, reason);
    }

    internal static FlatConfigSaveResult Error(string reason)
    {
        return new FlatConfigSaveResult(false, reason);
    }
}

internal sealed class FlatConfigValueStore
{
    private readonly ConfigRegistry registry;
    private readonly IFlatConfigFileAccess fileAccess;
    private FlatConfigSnapshot snapshot;

    private FlatConfigValueStore(
        ConfigRegistry registry,
        IFlatConfigFileAccess fileAccess,
        FlatConfigSnapshot snapshot
    )
    {
        this.registry = registry;
        this.fileAccess = fileAccess;
        this.snapshot = snapshot;
    }

    internal static FlatConfigLoadResult Load(
        ConfigRegistry registry,
        IFlatConfigFileAccess fileAccess
    )
    {
        var read = fileAccess.Read();
        if (read.Status == ConfigTextReadStatus.Error)
            return new FlatConfigLoadResult(FlatConfigLoadStatus.Unavailable, read.Reason, null);

        if (read.Status == ConfigTextReadStatus.Missing)
        {
            var empty = new FlatConfigSnapshot(
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>()
            );
            return new FlatConfigLoadResult(
                FlatConfigLoadStatus.AvailableWithDefaults,
                read.Reason,
                new FlatConfigValueStore(registry, fileAccess, empty)
            );
        }

        if (!TryParseRoot(read.Content, out var parsedSnapshot))
        {
            return new FlatConfigLoadResult(
                FlatConfigLoadStatus.Unavailable,
                "config.invalid-json",
                null
            );
        }

        return new FlatConfigLoadResult(
            FlatConfigLoadStatus.Available,
            "config.values-loaded",
            new FlatConfigValueStore(registry, fileAccess, parsedSnapshot!)
        );
    }

    internal FlatConfigSnapshot GetSnapshot()
    {
        return Volatile.Read(ref snapshot);
    }

    internal FlatConfigSaveResult TrySave(IReadOnlyDictionary<string, ConfigValue> updates)
    {
        var current = GetSnapshot();
        var proposedValues = new Dictionary<string, string>(
            current.RawValues,
            StringComparer.Ordinal
        );
        var proposedOrder = new List<string>(current.PropertyOrder);

        foreach (var pair in updates)
        {
            if (
                !registry.TryGet(pair.Key, out var option)
                || option is null
                || !option.Accepts(pair.Value)
            )
            {
                return FlatConfigSaveResult.Error($"config.invalid-update:{pair.Key}");
            }

            if (!proposedValues.ContainsKey(pair.Key))
                proposedOrder.Add(pair.Key);

            proposedValues[pair.Key] = ToJson(pair.Value);
        }

        // 只有受控保存才补齐缺失的已知 key；未知 key 与已有非法 raw 值保持原样。
        foreach (var option in registry.Options)
        {
            if (proposedValues.ContainsKey(option.Key))
                continue;

            proposedValues.Add(option.Key, ToJson(option.DefaultValue));
            proposedOrder.Add(option.Key);
        }

        string serialized;
        try
        {
            serialized = Serialize(proposedValues, proposedOrder);
        }
        catch (JsonException)
        {
            return FlatConfigSaveResult.Error("config.write-plan-invalid");
        }

        var write = fileAccess.WriteAtomically(serialized);
        if (!write.Success)
            return FlatConfigSaveResult.Error(write.Reason);

        Volatile.Write(ref snapshot, new FlatConfigSnapshot(proposedValues, proposedOrder));
        return FlatConfigSaveResult.Saved(write.Reason);
    }

    private static bool TryParseRoot(string json, out FlatConfigSnapshot? parsedSnapshot)
    {
        parsedSnapshot = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, property.Value.GetRawText()))
                    return false;

                order.Add(property.Name);
            }

            parsedSnapshot = new FlatConfigSnapshot(values, order);
            return true;
        }
    }

    private static string ToJson(ConfigValue value)
    {
        return value.Type == ConfigOptionType.Boolean
            ? value.CanonicalText
            : JsonSerializer.Serialize(value.EnumValue);
    }

    private static string Serialize(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> propertyOrder
    )
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions { Indented = true }
            )
        )
        {
            writer.WriteStartObject();
            foreach (var key in propertyOrder)
            {
                writer.WritePropertyName(key);
                using var valueDocument = JsonDocument.Parse(values[key]);
                valueDocument.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }
}
