#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Hunger 磁盘数据的唯一 codec/store。坏数据会让本次会话只读，避免用安全默认值覆盖旧档。
/// </summary>
internal sealed class HungerPersistenceStore
{
    internal const string SaveKey = "DontStarve.Hunger";
    internal const string LegacyBackupKey = "DontStarve.Hunger.LegacyBackup";

    internal HungerPersistenceResult Load(
        IHungerSaveDataAccess dataAccess,
        string masterPlayerKey,
        double currentMax
    )
    {
        if (!HungerProtocol.IsValidPlayerKey(masterPlayerKey))
            return Error("master-player-key-is-invalid", currentMax);
        if (!IsValidMaximum(currentMax))
            return Error("current-max-must-be-positive-and-finite", currentMax);

        var read = dataAccess.Read(SaveKey);
        if (read.Status == HungerRawReadStatus.Error)
            return Error($"primary-read-failed:{read.Reason}", currentMax);

        if (read.Status == HungerRawReadStatus.Missing)
        {
            return new HungerPersistenceResult(
                HungerPersistenceStatus.InitializedNew,
                HungerPersistenceCapability.ReadWrite,
                "no-existing-hunger-data",
                HungerSaveDataCodec.NewData(masterPlayerKey, currentMax),
                currentMax
            );
        }

        var decoded = HungerSaveDataCodec.Decode(read.Json, masterPlayerKey, currentMax);
        if (!decoded.Success || decoded.Data is null)
            return Error($"primary-data-invalid:{decoded.Reason}", currentMax);

        if (decoded.Format == HungerDecodedFormat.V2)
        {
            return new HungerPersistenceResult(
                HungerPersistenceStatus.LoadedV2,
                HungerPersistenceCapability.ReadWrite,
                decoded.Reason,
                decoded.Data,
                decoded.Current
            );
        }

        return MigrateLegacy(dataAccess, read, decoded.Data, decoded.Current, currentMax);
    }

    internal HungerPersistenceResult Save(
        IHungerSaveDataAccess dataAccess,
        HungerPersistenceResult session,
        HungerSaveData data
    )
    {
        if (!session.CanSave)
            return session;
        if (data is null)
            return Error("hunger-save-data-is-missing", session.Current);

        var encoded = HungerSaveDataCodec.Encode(data);
        if (!encoded.Success || encoded.Data is null)
            return Error($"v2-save-validation-failed:{encoded.Reason}", session.Current);

        var write = dataAccess.WriteV2(SaveKey, encoded.Data);
        if (!write.Success)
            return Error($"primary-write-failed:{write.Reason}", session.Current);

        return new HungerPersistenceResult(
            HungerPersistenceStatus.SavedV2,
            HungerPersistenceCapability.ReadWrite,
            "v2-data-saved",
            encoded.Data,
            session.Current
        );
    }

    private static HungerPersistenceResult MigrateLegacy(
        IHungerSaveDataAccess dataAccess,
        HungerRawReadResult original,
        HungerSaveData migrated,
        double migratedCurrent,
        double currentMax
    )
    {
        var backup = dataAccess.Read(LegacyBackupKey);
        if (backup.Status == HungerRawReadStatus.Error)
            return Error($"legacy-backup-read-failed:{backup.Reason}", currentMax);

        if (backup.Status == HungerRawReadStatus.Missing)
        {
            var backupWrite = dataAccess.Copy(LegacyBackupKey, original);
            if (!backupWrite.Success)
                return Error($"legacy-backup-write-failed:{backupWrite.Reason}", currentMax);
        }
        else if (!HungerSaveDataCodec.AreEquivalentJsonValues(backup.Json, original.Json))
        {
            return Error("legacy-backup-does-not-match-current-data", currentMax);
        }

        var encoded = HungerSaveDataCodec.Encode(migrated);
        if (!encoded.Success || encoded.Data is null)
            return Error($"legacy-migration-encode-failed:{encoded.Reason}", currentMax);

        var primaryWrite = dataAccess.WriteV2(SaveKey, encoded.Data);
        if (!primaryWrite.Success)
            return Error($"legacy-v2-write-failed:{primaryWrite.Reason}", currentMax);

        return new HungerPersistenceResult(
            HungerPersistenceStatus.MigratedLegacy,
            HungerPersistenceCapability.ReadWrite,
            "legacy-backed-up-and-migrated-to-v2",
            encoded.Data,
            migratedCurrent
        );
    }

    private static HungerPersistenceResult Error(string reason, double current)
    {
        return new HungerPersistenceResult(
            HungerPersistenceStatus.ReadOnlyError,
            HungerPersistenceCapability.ReadOnly,
            reason,
            null,
            IsValidMaximum(current) ? current : HungerRules.DefaultMaximum
        );
    }

    private static bool IsValidMaximum(double value) =>
        double.IsFinite(value) && value > 0d;
}

internal static class HungerSaveDataCodec
{
    internal const double LegacyMaximum = HungerRules.DefaultMaximum;

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = false };

    internal static HungerSaveData NewData(string masterPlayerKey, double currentMax)
    {
        var data = new HungerSaveData();
        data.Players[masterPlayerKey] = new HungerPlayerSaveData
        {
            Current = currentMax,
            MaxAtSave = currentMax,
        };
        return data;
    }

    internal static HungerDecodeResult Decode(
        string json,
        string masterPlayerKey,
        double currentMax
    )
    {
        if (!HungerProtocol.IsValidPlayerKey(masterPlayerKey))
            return HungerDecodeResult.Error("master-player-key-is-invalid");
        if (!double.IsFinite(currentMax) || currentMax <= 0d)
            return HungerDecodeResult.Error("current-max-must-be-positive-and-finite");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return HungerDecodeResult.Error("save-json-is-malformed");
        }
        catch (ArgumentException)
        {
            return HungerDecodeResult.Error("save-json-is-malformed");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return HungerDecodeResult.Error("save-root-must-be-an-object");

            var schemaCount = CountProperty(root, "SchemaVersion");
            if (schemaCount > 1)
                return HungerDecodeResult.Error("v2-schema-version-is-duplicated");
            if (schemaCount == 1)
                return DecodeV2(root, masterPlayerKey, currentMax);

            if (CountProperty(root, "Players") > 0)
                return HungerDecodeResult.Error("v2-schema-version-is-missing");

            return DecodeLegacy(root, masterPlayerKey, currentMax);
        }
    }

    internal static HungerEncodeResult Encode(HungerSaveData data)
    {
        var reason = ValidateForWrite(data);
        if (reason is not null)
            return HungerEncodeResult.Error(reason);

        try
        {
            return HungerEncodeResult.Succeeded(
                JsonSerializer.Serialize(data, SerializerOptions),
                data
            );
        }
        catch (JsonException)
        {
            return HungerEncodeResult.Error("v2-json-serialization-failed");
        }
        catch (NotSupportedException)
        {
            return HungerEncodeResult.Error("v2-json-serialization-failed");
        }
    }

    internal static bool TryResolvePlayerCurrent(
        HungerPlayerSaveData saved,
        double currentMaximum,
        out double current
    )
    {
        current = 0d;
        if (
            saved is null
            || !double.IsFinite(saved.Current)
            || !double.IsFinite(saved.MaxAtSave)
            || saved.MaxAtSave <= 0d
            || !double.IsFinite(currentMaximum)
            || currentMaximum <= 0d
        )
            return false;

        current = Clamp(saved.Current / saved.MaxAtSave * currentMaximum, currentMaximum);
        return true;
    }

    internal static bool AreEquivalentJsonValues(string left, string right)
    {
        return TryNormalize(left, out var normalizedLeft)
            && TryNormalize(right, out var normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static HungerDecodeResult DecodeV2(
        JsonElement root,
        string masterPlayerKey,
        double currentMax
    )
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("SchemaVersion" or "Players"))
                return HungerDecodeResult.Error("v2-root-has-unknown-fields");
        }
        if (!TryGetInt32(root, "SchemaVersion", out var schemaVersion))
            return HungerDecodeResult.Error("v2-schema-version-must-be-an-integer");
        if (schemaVersion != HungerSaveData.CurrentSchemaVersion)
            return HungerDecodeResult.Error("save-schema-version-is-unsupported");
        if (CountProperty(root, "Players") != 1)
            return HungerDecodeResult.Error("v2-players-field-is-missing-or-duplicated");
        if (!root.TryGetProperty("Players", out var playersElement) || playersElement.ValueKind != JsonValueKind.Object)
            return HungerDecodeResult.Error("v2-players-must-be-an-object");

        var players = new Dictionary<string, HungerPlayerSaveData>(StringComparer.Ordinal);
        foreach (var property in playersElement.EnumerateObject())
        {
            if (!HungerProtocol.IsValidPlayerKey(property.Name))
                return HungerDecodeResult.Error("v2-player-key-is-not-canonical");
            if (!DecodePlayer(property.Value, out var player, out var reason))
                return HungerDecodeResult.Error(reason);
            if (!players.TryAdd(property.Name, player!))
                return HungerDecodeResult.Error("v2-player-key-is-duplicated");
        }

        var data = new HungerSaveData { SchemaVersion = HungerSaveData.CurrentSchemaVersion, Players = players };
        if (ValidateForWrite(data) is { } validationReason)
            return HungerDecodeResult.Error(validationReason);

        var current = currentMax;
        if (
            players.TryGetValue(masterPlayerKey, out var master)
            && TryResolvePlayerCurrent(master, currentMax, out var migratedCurrent)
        )
            current = migratedCurrent;
        return HungerDecodeResult.Succeeded(HungerDecodedFormat.V2, current, data, "v2-data-is-valid");
    }

    private static HungerDecodeResult DecodeLegacy(
        JsonElement root,
        string masterPlayerKey,
        double currentMax
    )
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not "Hunger")
                return HungerDecodeResult.Error("legacy-root-has-unknown-fields");
        }
        if (CountProperty(root, "Hunger") != 1)
            return HungerDecodeResult.Error("legacy-hunger-field-is-missing-or-duplicated");
        if (!root.TryGetProperty("Hunger", out var hunger) || !hunger.TryGetDouble(out var legacyCurrent) || !double.IsFinite(legacyCurrent))
            return HungerDecodeResult.Error("legacy-hunger-value-is-not-finite");

        var data = NewData(masterPlayerKey, currentMax);
        data.Players[masterPlayerKey] = new HungerPlayerSaveData
        {
            Current = Clamp(legacyCurrent / LegacyMaximum * currentMax, currentMax),
            MaxAtSave = currentMax,
        };
        return HungerDecodeResult.Succeeded(
            HungerDecodedFormat.Legacy,
            data.Players[masterPlayerKey].Current,
            data,
            "legacy-value-ready-for-backup-first-migration"
        );
    }

    private static bool DecodePlayer(
        JsonElement element,
        out HungerPlayerSaveData? player,
        out string reason
    )
    {
        player = null;
        reason = "v2-player-record-is-invalid";
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "v2-player-record-must-be-an-object";
            return false;
        }
        if (CountProperty(element, "Current") != 1 || CountProperty(element, "MaxAtSave") != 1)
        {
            reason = "v2-player-fields-are-missing-or-duplicated";
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("Current" or "MaxAtSave"))
            {
                reason = "v2-player-record-has-unknown-fields";
                return false;
            }
        }

        if (!element.GetProperty("Current").TryGetDouble(out var current) || !double.IsFinite(current))
        {
            reason = "v2-current-must-be-finite";
            return false;
        }
        if (!element.GetProperty("MaxAtSave").TryGetDouble(out var maxAtSave) || !double.IsFinite(maxAtSave) || maxAtSave <= 0d)
        {
            reason = "v2-max-at-save-must-be-positive-and-finite";
            return false;
        }

        player = new HungerPlayerSaveData { Current = current, MaxAtSave = maxAtSave };
        reason = "v2-player-record-is-valid";
        return true;
    }

    private static string? ValidateForWrite(HungerSaveData? data)
    {
        if (data is null || data.SchemaVersion != HungerSaveData.CurrentSchemaVersion || data.Players is null)
            return "v2-save-root-is-invalid";
        foreach (var pair in data.Players)
        {
            if (!HungerProtocol.IsValidPlayerKey(pair.Key))
                return "v2-player-key-is-not-canonical";
            if (pair.Value is null || !double.IsFinite(pair.Value.Current) || !double.IsFinite(pair.Value.MaxAtSave) || pair.Value.MaxAtSave <= 0d)
                return "v2-player-value-is-invalid";
        }
        return null;
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

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryNormalize(string json, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            normalized = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static double Clamp(double value, double maximum)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 0d;
        return value >= maximum ? maximum : value;
    }
}

internal enum HungerPersistenceStatus
{
    InitializedNew,
    LoadedV2,
    MigratedLegacy,
    SavedV2,
    ReadOnlyClient,
    ReadOnlyError,
}

internal enum HungerPersistenceCapability
{
    ReadWrite,
    ReadOnly,
}

internal sealed class HungerPersistenceResult
{
    internal HungerPersistenceResult(
        HungerPersistenceStatus status,
        HungerPersistenceCapability capability,
        string reason,
        HungerSaveData? data,
        double current
    )
    {
        Status = status;
        Capability = capability;
        Reason = reason;
        Data = data;
        Current = current;
    }

    internal HungerPersistenceStatus Status { get; }
    internal HungerPersistenceCapability Capability { get; }
    internal string Reason { get; }
    internal HungerSaveData? Data { get; }
    internal double Current { get; }
    internal bool CanSave => Capability == HungerPersistenceCapability.ReadWrite && Data is not null;
}

internal enum HungerDecodedFormat
{
    V2,
    Legacy,
    Error,
}

internal sealed class HungerDecodeResult
{
    private HungerDecodeResult(bool success, HungerDecodedFormat format, double current, HungerSaveData? data, string reason)
    {
        Success = success;
        Format = format;
        Current = current;
        Data = data;
        Reason = reason;
    }

    internal bool Success { get; }
    internal HungerDecodedFormat Format { get; }
    internal double Current { get; }
    internal HungerSaveData? Data { get; }
    internal string Reason { get; }

    internal static HungerDecodeResult Succeeded(HungerDecodedFormat format, double current, HungerSaveData data, string reason) =>
        new(true, format, current, data, reason);

    internal static HungerDecodeResult Error(string reason) =>
        new(false, HungerDecodedFormat.Error, 0d, null, reason);
}

internal sealed class HungerEncodeResult
{
    private HungerEncodeResult(bool success, string json, HungerSaveData? data, string reason)
    {
        Success = success;
        Json = json;
        Data = data;
        Reason = reason;
    }

    internal bool Success { get; }
    internal string Json { get; }
    internal HungerSaveData? Data { get; }
    internal string Reason { get; }

    internal static HungerEncodeResult Succeeded(string json, HungerSaveData data) =>
        new(true, json, data, "hunger-data-encoded");

    internal static HungerEncodeResult Error(string reason) =>
        new(false, string.Empty, null, reason);
}

internal interface IHungerSaveDataAccess
{
    HungerRawReadResult Read(string key);
    HungerRawWriteResult Copy(string key, HungerRawReadResult source);
    HungerRawWriteResult WriteV2(string key, HungerSaveData data);
}

internal enum HungerRawReadStatus
{
    Missing,
    Success,
    Error,
}

internal sealed class HungerRawReadResult
{
    private HungerRawReadResult(HungerRawReadStatus status, string json, string reason, object? nativeValue)
    {
        Status = status;
        Json = json;
        Reason = reason;
        NativeValue = nativeValue;
    }

    internal HungerRawReadStatus Status { get; }
    internal string Json { get; }
    internal string Reason { get; }
    internal object? NativeValue { get; }

    internal static HungerRawReadResult Missing() => new(HungerRawReadStatus.Missing, string.Empty, "save-entry-is-missing", null);
    internal static HungerRawReadResult Success(string json, object? nativeValue = null) => new(HungerRawReadStatus.Success, json, "save-entry-read", nativeValue);
    internal static HungerRawReadResult Error(string reason) => new(HungerRawReadStatus.Error, string.Empty, reason, null);
}

internal sealed class HungerRawWriteResult
{
    private HungerRawWriteResult(bool success, string reason)
    {
        Success = success;
        Reason = reason;
    }

    internal bool Success { get; }
    internal string Reason { get; }
    internal static HungerRawWriteResult Succeeded() => new(true, "save-entry-written");
    internal static HungerRawWriteResult Error(string reason) => new(false, reason);
}
