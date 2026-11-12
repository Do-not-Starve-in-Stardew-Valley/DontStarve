#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// Health 存档载荷（save 级抽屉 DontStarve.Health，决议 6 主机权威）。
/// 载荷 = 所有玩家的余数字典（key = UniqueMultiplayerID 的 InvariantCulture 规范十进制串）。
/// 余数以 HealthLedgerRules.FormatRemainder 的 InvariantCulture "R" 字符串逐条存储：
/// 手写脏档的单条损坏可以按条丢弃（解析失败代价 ≤ 1 点血），不会连累整个字典。
/// </summary>
internal sealed class HealthSaveData
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, string> Remainders { get; set; } = new(StringComparer.Ordinal);
}

internal enum HealthRawReadStatus
{
    Missing,
    Success,
    Error,
}

internal sealed class HealthRawReadResult
{
    private HealthRawReadResult(HealthRawReadStatus status, string json, string reason)
    {
        Status = status;
        Json = json;
        Reason = reason;
    }

    internal HealthRawReadStatus Status { get; }

    internal string Json { get; }

    internal string Reason { get; }

    internal static HealthRawReadResult Missing() =>
        new(HealthRawReadStatus.Missing, string.Empty, "save-entry-is-missing");

    internal static HealthRawReadResult Success(string json) =>
        new(HealthRawReadStatus.Success, json, "save-entry-read");

    internal static HealthRawReadResult Error(string reason) =>
        new(HealthRawReadStatus.Error, string.Empty, reason);
}

internal sealed class HealthRawWriteResult
{
    private HealthRawWriteResult(bool success, string reason)
    {
        Success = success;
        Reason = reason;
    }

    internal bool Success { get; }

    internal string Reason { get; }

    internal static HealthRawWriteResult Succeeded() => new(true, "save-entry-written");

    internal static HealthRawWriteResult Error(string reason) => new(false, reason);
}

/// <summary>
/// 存档 API 的抽象边界：让 HealthPersistenceStore 的失败策略可做纯规则单测；
/// SMAPI 适配在 SmapiHealthSaveDataAccess（不进测试编译）。
/// </summary>
internal interface IHealthSaveDataAccess
{
    HealthRawReadResult Read(string key);

    HealthRawWriteResult Write(string key, HealthSaveData data);
}

internal enum HealthPersistenceStatus
{
    InitializedNew,
    Loaded,
    Saved,
    ReadOnlyError,
}

internal enum HealthPersistenceCapability
{
    ReadWrite,
    ReadOnly,
}

internal sealed class HealthPersistenceResult
{
    internal HealthPersistenceResult(
        HealthPersistenceStatus status,
        HealthPersistenceCapability capability,
        string reason,
        IReadOnlyDictionary<string, double>? remainders,
        IReadOnlyList<string> droppedEntryKeys
    )
    {
        Status = status;
        Capability = capability;
        Reason = reason;
        Remainders = remainders;
        DroppedEntryKeys = droppedEntryKeys;
    }

    internal HealthPersistenceStatus Status { get; }

    internal HealthPersistenceCapability Capability { get; }

    internal string Reason { get; }

    /// <summary>解码后的权威余数字典；只读错误态为 null（宿主保持空字典从 0 播种）。</summary>
    internal IReadOnlyDictionary<string, double>? Remainders { get; }

    internal IReadOnlyList<string> DroppedEntryKeys { get; }

    internal bool CanSave => Capability == HealthPersistenceCapability.ReadWrite;
}

/// <summary>
/// Health 余数存取与失败策略（决议 6：save 级抽屉，镜像 HungerPersistenceStore 的纪律，
/// 但状态极简——只存所有玩家的余数字典）。本文件不引用 SMAPI/Stardew，便于纯规则单测。
///
/// 失败策略与 hunger 的同异：
/// - 读失败 / 解码失败 → 本次会话只读熔断（CanSave=false，Saving 绝不写回），避免用
///   默认值覆盖旧档——与 hunger 同款。
/// - Missing → 视为新档空字典，不熔断：余数天然可从 0 播种、无旧数据可丢，这是与
///   hunger 语义的唯一差异（hunger 的 Missing 也走 InitializedNew 可写，此处一致）。
/// - 单个条目解析失败 → 该条丢弃为缺省 0，不熔断整个字典：单条代价 ≤ 1 点血，
///   而整档熔断会让所有玩家的零头进度回退，代价不成比例。
/// </summary>
internal sealed class HealthPersistenceStore
{
    /// <summary>save 级抽屉 key（决议 6）。无 LegacyBackupKey：本系统首发即此 schema，无历史包袱。</summary>
    internal const string SaveKey = "DontStarve.Health";

    /// <summary>
    /// 旧版 modData 余数 key（HealthRemainderStore 时代）。仅迁移读取用，本文件没有任何
    /// 写入路径；旧余数并入权威字典后由宿主移除主机自己的 key（见 Health.MigrateLegacy...）。
    /// </summary>
    internal const string LegacyModDataKey = "DontStarve.Health.Remainder";

    private static readonly IReadOnlyList<string> NoDroppedEntries = Array.Empty<string>();

    internal HealthPersistenceResult Load(IHealthSaveDataAccess dataAccess)
    {
        var read = dataAccess.Read(SaveKey);
        if (read.Status == HealthRawReadStatus.Error)
            return ReadOnly($"primary-read-failed:{read.Reason}");

        if (read.Status == HealthRawReadStatus.Missing)
        {
            // 新档：余数从 0 播种即可，无旧数据可丢，不熔断。
            return new HealthPersistenceResult(
                HealthPersistenceStatus.InitializedNew,
                HealthPersistenceCapability.ReadWrite,
                "no-existing-health-data",
                new Dictionary<string, double>(StringComparer.Ordinal),
                NoDroppedEntries
            );
        }

        return Decode(read.Json);
    }

    internal HealthPersistenceResult Save(
        IHealthSaveDataAccess dataAccess,
        HealthPersistenceResult session,
        HealthSaveData data
    )
    {
        // 只读熔断的会话原样返回：绝不写回，防止坏档被默认值覆盖（hunger 同款纪律）。
        if (!session.CanSave)
            return session;
        if (data is null)
            return ReadOnly("health-save-data-is-missing");

        var encoded = Encode(data);
        if (!encoded.Success)
            return ReadOnly($"save-validation-failed:{encoded.Reason}");

        var write = dataAccess.Write(SaveKey, encoded.Data!);
        if (!write.Success)
            return ReadOnly($"primary-write-failed:{write.Reason}");

        return new HealthPersistenceResult(
            HealthPersistenceStatus.Saved,
            HealthPersistenceCapability.ReadWrite,
            "health-data-saved",
            session.Remainders,
            session.DroppedEntryKeys
        );
    }

    /// <summary>手工逐条解码：schema 严格性整档把关，条目级损坏按条丢弃。</summary>
    internal static HealthPersistenceResult Decode(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ReadOnly("save-json-is-malformed");
        }
        catch (ArgumentException)
        {
            return ReadOnly("save-json-is-malformed");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ReadOnly("save-root-must-be-an-object");

            // 未知字段一律拒绝（镜像 hunger 严格 schema）：半结构化脏档宁可熔断也不静默接管。
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("SchemaVersion" or "Remainders"))
                    return ReadOnly("save-root-has-unknown-fields");
            }

            if (
                !root.TryGetProperty("SchemaVersion", out var schemaElement)
                || !schemaElement.TryGetInt32(out var schemaVersion)
            )
                return ReadOnly("save-schema-version-must-be-an-integer");
            if (schemaVersion != HealthSaveData.CurrentSchemaVersion)
                return ReadOnly("save-schema-version-is-unsupported");
            if (
                !root.TryGetProperty("Remainders", out var remaindersElement)
                || remaindersElement.ValueKind != JsonValueKind.Object
            )
                return ReadOnly("save-remainders-must-be-an-object");

            var remainders = new Dictionary<string, double>(StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var property in remaindersElement.EnumerateObject())
            {
                // key 必须是规范玩家 id；value 必须可按 InvariantCulture 解析且有限。
                // 任一不满足 → 该条丢弃为缺省 0（单条代价 ≤ 1 点血），不熔断整个字典。
                if (
                    !HealthMultiplayerProtocol.TryParsePlayerId(property.Name, out _)
                    || !TryDecodeRemainderValue(property.Value, out var value)
                )
                {
                    dropped.Add(property.Name);
                    continue;
                }

                remainders[property.Name] = value;
            }

            return new HealthPersistenceResult(
                HealthPersistenceStatus.Loaded,
                HealthPersistenceCapability.ReadWrite,
                dropped.Count > 0 ? "loaded-with-dropped-entries" : "save-data-is-valid",
                remainders,
                dropped
            );
        }
    }

    /// <summary>
    /// 写前净化：非有限余数与非法 key 一律剔除（宁可丢零头，不把脏数据写进存档），
    /// 再交给存档 API 序列化。返回净化后的载荷供 access 写入。
    /// </summary>
    internal static (bool Success, HealthSaveData? Data, string Reason) Encode(HealthSaveData? data)
    {
        if (data is null || data.Remainders is null)
            return (false, null, "health-save-data-is-missing");

        var cleaned = new HealthSaveData
        {
            SchemaVersion = HealthSaveData.CurrentSchemaVersion,
            Remainders = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        foreach (var pair in data.Remainders)
        {
            if (
                !HealthMultiplayerProtocol.TryParsePlayerId(pair.Key, out _)
                || !HealthLedgerRules.TryParseRemainder(pair.Value, out var value)
                || !double.IsFinite(value)
            )
            {
                continue;
            }

            // 重新 Format：写盘形态只由 FormatRemainder 决定（InvariantCulture "R" 往返）。
            cleaned.Remainders[pair.Key] = HealthLedgerRules.FormatRemainder(value);
        }

        return (true, cleaned, "health-save-data-encoded");
    }

    private static bool TryDecodeRemainderValue(JsonElement element, out double value)
    {
        value = 0d;
        if (element.ValueKind == JsonValueKind.Number)
        {
            // JSON 数值按 InvariantCulture 语义解析；1e400 这类溢出会得到 Infinity，被 IsFinite 拒绝。
            if (!element.TryGetDouble(out value))
                return false;
            return double.IsFinite(value);
        }

        if (element.ValueKind == JsonValueKind.String)
            return HealthLedgerRules.TryParseRemainder(element.GetString(), out value)
                && double.IsFinite(value);

        return false;
    }

    private static HealthPersistenceResult ReadOnly(string reason)
    {
        return new HealthPersistenceResult(
            HealthPersistenceStatus.ReadOnlyError,
            HealthPersistenceCapability.ReadOnly,
            reason,
            null,
            NoDroppedEntries
        );
    }
}
