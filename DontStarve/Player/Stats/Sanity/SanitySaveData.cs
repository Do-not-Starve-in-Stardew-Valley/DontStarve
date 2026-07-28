#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>磁盘 v2 根对象；每个玩家只持久化可迁移的数值与保存时上限。</summary>
internal sealed class SanitySaveData
{
    internal const int CurrentSchemaVersion = 2;
    internal const double CurrentDefaultMaxSanity = 200d;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, SanityPlayerSaveData> Players { get; set; } =
        new Dictionary<string, SanityPlayerSaveData>();
}

internal sealed class SanityPlayerSaveData
{
    public double Current { get; set; }

    public double MaxAtSave { get; set; }
}

/// <summary>把 Stardew 的 long ID 固定成无区域差异的十进制磁盘 key。</summary>
internal static class SanityPlayerKey
{
    internal static string FromUniqueMultiplayerId(long uniqueMultiplayerId)
    {
        return uniqueMultiplayerId.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool IsCanonical(string playerKey)
    {
        return long.TryParse(
                playerKey,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            && parsed >= 0
            && string.Equals(
                playerKey,
                parsed.ToString(CultureInfo.InvariantCulture),
                System.StringComparison.Ordinal
            );
    }
}

internal enum SanityPersistenceStatus
{
    InitializedNew,
    LoadedV2,
    MigratedLegacy,
    SavedV2,
    ReadOnlyClient,
    ReadOnlyError,
}

internal enum SanityPersistenceCapability
{
    ReadWrite,
    ReadOnly,
}

/// <summary>
/// 一次加载/保存后的完整会话边界。错误态始终携带安全满值且禁止后续写回。
/// </summary>
internal sealed class SanityPersistenceResult
{
    internal SanityPersistenceResult(
        SanityPersistenceStatus status,
        SanityPersistenceCapability capability,
        string reason,
        double current,
        SanitySaveData? data
    )
    {
        Status = status;
        Capability = capability;
        Reason = reason;
        Current = current;
        Data = data;
    }

    internal SanityPersistenceStatus Status { get; }

    internal SanityPersistenceCapability Capability { get; }

    internal string Reason { get; }

    internal double Current { get; }

    internal SanitySaveData? Data { get; }

    internal bool CanSave =>
        Capability == SanityPersistenceCapability.ReadWrite && Data is not null;

    internal static SanityPersistenceResult ReadOnlyDefault(
        SanityPersistenceStatus status,
        string reason,
        double currentMax
    )
    {
        var safeCurrent =
            double.IsFinite(currentMax) && currentMax > 0
                ? currentMax
                : SanitySaveData.CurrentDefaultMaxSanity;
        return new SanityPersistenceResult(
            status,
            SanityPersistenceCapability.ReadOnly,
            reason,
            safeCurrent,
            null
        );
    }
}
