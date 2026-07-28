#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// Sanity 磁盘 JSON 的唯一纯逻辑 codec。legacy 数值分类始终委托给
/// <see cref="LegacySanityDataInspector"/>，这里不建立第二套 v1 parser。
/// </summary>
internal static class SanitySaveDataCodec
{
    internal const double LegacyMaxSanity = 150d;

    private static readonly JsonSerializerOptions SerializerOptions =
        new JsonSerializerOptions { WriteIndented = false };

    internal static SanityDecodeResult Decode(
        string json,
        string playerKey,
        double currentMax
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return SanityDecodeResult.Error("player-key-is-not-canonical-invariant-decimal");
        if (!IsValidMax(currentMax))
            return SanityDecodeResult.Error("current-max-must-be-positive-and-finite");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return SanityDecodeResult.Error("save-json-is-malformed");
        }
        catch (ArgumentException)
        {
            return SanityDecodeResult.Error("save-json-is-malformed");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SanityDecodeResult.Error("save-root-must-be-an-object");

            var schemaCount = CountProperty(root, "SchemaVersion");
            if (schemaCount > 1)
                return SanityDecodeResult.Error("v2-schema-version-is-duplicated");
            if (schemaCount == 1)
                return DecodeV2(root, playerKey, currentMax);

            // Players without a discriminator is a damaged v2 root, not a legacy object.
            if (CountProperty(root, "Players") > 0)
                return SanityDecodeResult.Error("v2-schema-version-is-missing");
        }

        var legacy = LegacySanityDataInspector.Inspect(json);
        if (
            legacy.Status != LegacySanityInspectionStatus.FiniteSanityValue
            || !legacy.Value.HasValue
        )
        {
            return SanityDecodeResult.Error(legacy.Reason);
        }

        var migratedCurrent = ScaleLegacy(legacy.Value.Value, currentMax);
        var migrated = NewData(playerKey, migratedCurrent, currentMax);
        return SanityDecodeResult.Succeeded(
            SanityDecodedFormat.Legacy,
            migratedCurrent,
            migrated,
            "legacy-value-ready-for-backup-first-migration"
        );
    }

    internal static SanityEncodeResult PrepareForSave(
        SanitySaveData source,
        string playerKey,
        double current,
        double currentMax
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return SanityEncodeResult.Error("player-key-is-not-canonical-invariant-decimal");
        if (!IsValidMax(currentMax))
            return SanityEncodeResult.Error("current-max-must-be-positive-and-finite");
        if (!double.IsFinite(current))
            return SanityEncodeResult.Error("current-sanity-must-be-finite");

        var cloned = Clone(source);
        var clampedCurrent = Clamp(current, currentMax);
        cloned.SchemaVersion = SanitySaveData.CurrentSchemaVersion;
        cloned.Players[playerKey] = new SanityPlayerSaveData
        {
            Current = clampedCurrent,
            MaxAtSave = currentMax,
        };

        var validationReason = ValidateForWrite(cloned);
        if (validationReason is not null)
            return SanityEncodeResult.Error(validationReason);

        try
        {
            return SanityEncodeResult.Succeeded(
                JsonSerializer.Serialize(cloned, SerializerOptions),
                cloned,
                clampedCurrent
            );
        }
        catch (JsonException)
        {
            return SanityEncodeResult.Error("v2-json-serialization-failed");
        }
        catch (NotSupportedException)
        {
            return SanityEncodeResult.Error("v2-json-serialization-failed");
        }
    }

    internal static SanityEncodeResult Encode(SanitySaveData data)
    {
        var validationReason = ValidateForWrite(data);
        if (validationReason is not null)
            return SanityEncodeResult.Error(validationReason);

        try
        {
            return SanityEncodeResult.Succeeded(
                JsonSerializer.Serialize(data, SerializerOptions),
                data,
                0d
            );
        }
        catch (JsonException)
        {
            return SanityEncodeResult.Error("v2-json-serialization-failed");
        }
        catch (NotSupportedException)
        {
            return SanityEncodeResult.Error("v2-json-serialization-failed");
        }
    }

    internal static bool AreEquivalentJsonValues(string left, string right)
    {
        return TryNormalize(left, out var normalizedLeft)
            && TryNormalize(right, out var normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    internal static SanitySaveData NewData(
        string playerKey,
        double current,
        double currentMax
    )
    {
        var data = new SanitySaveData();
        data.Players[playerKey] = new SanityPlayerSaveData
        {
            Current = Clamp(current, currentMax),
            MaxAtSave = currentMax,
        };
        return data;
    }

    /// <summary>
    /// 把已通过 v2 严格校验的玩家记录换算成当前上限下的运行值。
    /// 运行态必须复用这里的 MaxAtSave 比例规则，不能另写第二套存档换算权威。
    /// </summary>
    internal static bool TryResolvePlayerCurrent(
        SanitySaveData data,
        string playerKey,
        double currentMax,
        out double current,
        out string reason
    )
    {
        current = IsValidMax(currentMax)
            ? currentMax
            : SanitySaveData.CurrentDefaultMaxSanity;

        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "player-key-is-not-canonical-invariant-decimal";
            return false;
        }
        if (!IsValidMax(currentMax))
        {
            reason = "current-max-must-be-positive-and-finite";
            return false;
        }
        if (data is null || data.Players is null)
        {
            reason = "v2-players-must-be-an-object";
            return false;
        }
        if (!data.Players.TryGetValue(playerKey, out var playerData))
        {
            reason = "v2-player-record-is-missing-use-current-max";
            return true;
        }
        if (playerData is null || !double.IsFinite(playerData.Current))
        {
            reason = "v2-current-must-be-finite";
            return false;
        }
        if (!IsValidMax(playerData.MaxAtSave))
        {
            reason = "v2-max-at-save-must-be-positive-and-finite";
            return false;
        }

        current = ScaleSavedValue(playerData.Current, playerData.MaxAtSave, currentMax);
        reason = "v2-player-record-resolved-for-current-max";
        return true;
    }

    private static SanityDecodeResult DecodeV2(
        JsonElement root,
        string playerKey,
        double currentMax
    )
    {
        JsonElement schemaElement = default;
        JsonElement playersElement = default;
        var schemaCount = 0;
        var playersCount = 0;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "SchemaVersion":
                    schemaCount++;
                    schemaElement = property.Value;
                    break;
                case "Players":
                    playersCount++;
                    playersElement = property.Value;
                    break;
                default:
                    return SanityDecodeResult.Error("v2-root-has-unknown-fields");
            }
        }

        if (schemaCount != 1)
            return SanityDecodeResult.Error("v2-schema-version-is-missing-or-duplicated");
        if (
            schemaElement.ValueKind != JsonValueKind.Number
            || !schemaElement.TryGetInt32(out var schemaVersion)
        )
        {
            return SanityDecodeResult.Error("v2-schema-version-must-be-an-integer");
        }
        if (schemaVersion != SanitySaveData.CurrentSchemaVersion)
            return SanityDecodeResult.Error("save-schema-version-is-unsupported");
        if (playersCount != 1)
            return SanityDecodeResult.Error("v2-players-field-is-missing-or-duplicated");
        if (playersElement.ValueKind != JsonValueKind.Object)
            return SanityDecodeResult.Error("v2-players-must-be-an-object");

        var players = new Dictionary<string, SanityPlayerSaveData>(StringComparer.Ordinal);
        foreach (var playerProperty in playersElement.EnumerateObject())
        {
            if (!SanityPlayerKey.IsCanonical(playerProperty.Name))
                return SanityDecodeResult.Error("v2-player-key-is-not-canonical");
            if (players.ContainsKey(playerProperty.Name))
                return SanityDecodeResult.Error("v2-player-key-is-duplicated");

            var playerResult = DecodePlayer(playerProperty.Value);
            if (!playerResult.Success || playerResult.Player is null)
                return SanityDecodeResult.Error(playerResult.Reason);
            players.Add(playerProperty.Name, playerResult.Player);
        }

        var data = new SanitySaveData { Players = players };
        if (!players.TryGetValue(playerKey, out var playerData))
        {
            playerData = new SanityPlayerSaveData
            {
                Current = currentMax,
                MaxAtSave = currentMax,
            };
            data.Players.Add(playerKey, playerData);
        }

        var scaledCurrent = ScaleSavedValue(
            playerData.Current,
            playerData.MaxAtSave,
            currentMax
        );
        return SanityDecodeResult.Succeeded(
            SanityDecodedFormat.V2,
            scaledCurrent,
            data,
            "v2-data-loaded"
        );
    }

    private static SanityPlayerDecodeResult DecodePlayer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return SanityPlayerDecodeResult.Error("v2-player-record-must-be-an-object");

        JsonElement currentElement = default;
        JsonElement maxElement = default;
        var currentCount = 0;
        var maxCount = 0;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "Current":
                    currentCount++;
                    currentElement = property.Value;
                    break;
                case "MaxAtSave":
                    maxCount++;
                    maxElement = property.Value;
                    break;
                default:
                    return SanityPlayerDecodeResult.Error(
                        "v2-player-record-has-unknown-fields"
                    );
            }
        }

        if (currentCount != 1 || maxCount != 1)
            return SanityPlayerDecodeResult.Error("v2-player-fields-are-missing-or-duplicated");
        if (!TryGetFiniteDouble(currentElement, out var current))
            return SanityPlayerDecodeResult.Error("v2-current-must-be-finite");
        if (!TryGetFiniteDouble(maxElement, out var maxAtSave) || maxAtSave <= 0)
            return SanityPlayerDecodeResult.Error("v2-max-at-save-must-be-positive-and-finite");

        return SanityPlayerDecodeResult.SuccessResult(
            new SanityPlayerSaveData { Current = current, MaxAtSave = maxAtSave }
        );
    }

    private static string? ValidateForWrite(SanitySaveData data)
    {
        if (data.SchemaVersion != SanitySaveData.CurrentSchemaVersion)
            return "save-schema-version-is-unsupported";
        if (data.Players is null)
            return "v2-players-must-be-an-object";

        foreach (var pair in data.Players)
        {
            if (!SanityPlayerKey.IsCanonical(pair.Key))
                return "v2-player-key-is-not-canonical";
            if (pair.Value is null)
                return "v2-player-record-must-be-an-object";
            if (!double.IsFinite(pair.Value.Current))
                return "v2-current-must-be-finite";
            if (!IsValidMax(pair.Value.MaxAtSave))
                return "v2-max-at-save-must-be-positive-and-finite";
        }

        return null;
    }

    private static SanitySaveData Clone(SanitySaveData source)
    {
        var clone = new SanitySaveData { SchemaVersion = source.SchemaVersion };
        if (source.Players is null)
        {
            clone.Players = null!;
            return clone;
        }

        foreach (var pair in source.Players)
        {
            clone.Players[pair.Key] =
                pair.Value is null
                    ? null!
                    : new SanityPlayerSaveData
                    {
                        Current = pair.Value.Current,
                        MaxAtSave = pair.Value.MaxAtSave,
                    };
        }

        return clone;
    }

    private static int CountProperty(JsonElement element, string name)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static bool TryGetFiniteDouble(JsonElement element, out double value)
    {
        value = 0d;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        try
        {
            value = element.GetDouble();
            return double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static double ScaleLegacy(double legacyCurrent, double currentMax)
    {
        if (legacyCurrent <= 0)
            return 0d;
        if (legacyCurrent >= LegacyMaxSanity)
            return currentMax;
        return legacyCurrent / LegacyMaxSanity * currentMax;
    }

    private static double ScaleSavedValue(
        double current,
        double maxAtSave,
        double currentMax
    )
    {
        if (current <= 0)
            return 0d;
        if (current >= maxAtSave)
            return currentMax;
        return current / maxAtSave * currentMax;
    }

    private static double Clamp(double current, double currentMax)
    {
        if (current <= 0)
            return 0d;
        if (current >= currentMax)
            return currentMax;
        return current;
    }

    private static bool IsValidMax(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private static bool TryNormalize(string json, out string normalized)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            normalized = JsonSerializer.Serialize(document.RootElement, SerializerOptions);
            return true;
        }
        catch (JsonException)
        {
            normalized = string.Empty;
            return false;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}

internal enum SanityDecodedFormat
{
    Legacy,
    V2,
    Error,
}

internal sealed class SanityDecodeResult
{
    private SanityDecodeResult(
        bool success,
        SanityDecodedFormat format,
        double current,
        SanitySaveData? data,
        string reason
    )
    {
        Success = success;
        Format = format;
        Current = current;
        Data = data;
        Reason = reason;
    }

    internal bool Success { get; }
    internal SanityDecodedFormat Format { get; }
    internal double Current { get; }
    internal SanitySaveData? Data { get; }
    internal string Reason { get; }

    internal static SanityDecodeResult Succeeded(
        SanityDecodedFormat format,
        double current,
        SanitySaveData data,
        string reason
    )
    {
        return new SanityDecodeResult(true, format, current, data, reason);
    }

    internal static SanityDecodeResult Error(string reason)
    {
        return new SanityDecodeResult(false, SanityDecodedFormat.Error, 0d, null, reason);
    }
}

internal sealed class SanityEncodeResult
{
    private SanityEncodeResult(
        bool success,
        string json,
        SanitySaveData? data,
        double current,
        string reason
    )
    {
        Success = success;
        Json = json;
        Data = data;
        Current = current;
        Reason = reason;
    }

    internal bool Success { get; }
    internal string Json { get; }
    internal SanitySaveData? Data { get; }
    internal double Current { get; }
    internal string Reason { get; }

    internal static SanityEncodeResult Succeeded(
        string json,
        SanitySaveData data,
        double current
    )
    {
        return new SanityEncodeResult(true, json, data, current, "v2-json-ready");
    }

    internal static SanityEncodeResult Error(string reason)
    {
        return new SanityEncodeResult(false, string.Empty, null, 0d, reason);
    }
}

internal sealed class SanityPlayerDecodeResult
{
    private SanityPlayerDecodeResult(
        bool success,
        SanityPlayerSaveData? player,
        string reason
    )
    {
        Success = success;
        Player = player;
        Reason = reason;
    }

    internal bool Success { get; }
    internal SanityPlayerSaveData? Player { get; }
    internal string Reason { get; }

    internal static SanityPlayerDecodeResult SuccessResult(SanityPlayerSaveData player)
    {
        return new SanityPlayerDecodeResult(true, player, "v2-player-record-is-valid");
    }

    internal static SanityPlayerDecodeResult Error(string reason)
    {
        return new SanityPlayerDecodeResult(false, null, reason);
    }
}
