#nullable enable

using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// 编排 Sanity 的磁盘事务：legacy 必须先得到可确认的备份，正式 key 才能写成 v2。
/// 任一读写失败都会把整个会话降为只读，避免安全默认值覆盖可恢复数据。
/// </summary>
internal sealed class SanityPersistenceStore
{
    internal const string SaveKey = "DontStarve.Sanity";
    internal const string LegacyBackupKey = "DontStarve.Sanity.LegacyBackup";

    internal SanityPersistenceResult Load(
        ISanitySaveDataAccess dataAccess,
        string playerKey,
        double currentMax
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            return Error(
                "player-key-is-not-canonical-invariant-decimal",
                currentMax
            );
        }
        if (!double.IsFinite(currentMax) || currentMax <= 0)
            return Error("current-max-must-be-positive-and-finite", currentMax);

        var read = dataAccess.Read(SaveKey);
        if (read.Status == SanityRawReadStatus.Error)
            return Error($"primary-read-failed:{read.Reason}", currentMax);

        if (read.Status == SanityRawReadStatus.Missing)
        {
            var orphanedBackup = dataAccess.Read(LegacyBackupKey);
            if (orphanedBackup.Status == SanityRawReadStatus.Error)
            {
                return Error(
                    $"legacy-backup-read-failed:{orphanedBackup.Reason}",
                    currentMax
                );
            }
            if (orphanedBackup.Status == SanityRawReadStatus.Success)
            {
                return Error(
                    "primary-missing-while-legacy-backup-exists",
                    currentMax
                );
            }

            var initialized = SanitySaveDataCodec.NewData(
                playerKey,
                currentMax,
                currentMax
            );
            return new SanityPersistenceResult(
                SanityPersistenceStatus.InitializedNew,
                SanityPersistenceCapability.ReadWrite,
                "no-existing-sanity-data",
                currentMax,
                initialized
            );
        }

        var decoded = SanitySaveDataCodec.Decode(read.Json, playerKey, currentMax);
        if (!decoded.Success || decoded.Data is null)
            return Error($"primary-data-invalid:{decoded.Reason}", currentMax);

        if (decoded.Format == SanityDecodedFormat.V2)
        {
            return new SanityPersistenceResult(
                SanityPersistenceStatus.LoadedV2,
                SanityPersistenceCapability.ReadWrite,
                decoded.Reason,
                decoded.Current,
                decoded.Data
            );
        }

        return MigrateLegacy(
            dataAccess,
            read,
            decoded.Data,
            decoded.Current,
            currentMax
        );
    }

    internal SanityPersistenceResult Save(
        ISanitySaveDataAccess dataAccess,
        SanityPersistenceResult session,
        string playerKey,
        double current,
        double currentMax
    )
    {
        return SavePlayers(
            dataAccess,
            session,
            new[] { new SanityPlayerSaveInput(playerKey, current, currentMax) },
            playerKey
        );
    }

    internal SanityPersistenceResult SavePlayers(
        ISanitySaveDataAccess dataAccess,
        SanityPersistenceResult session,
        IReadOnlyCollection<SanityPlayerSaveInput> players,
        string resultPlayerKey
    )
    {
        // 只读结果是会话级熔断器；Saving 事件不得重新尝试或静默回写。
        if (!session.CanSave || session.Data is null)
            return session;

        if (players is null || players.Count == 0)
            return Error("v2-save-batch-is-empty", session.Current);
        if (!SanityPlayerKey.IsCanonical(resultPlayerKey))
        {
            return Error(
                "player-key-is-not-canonical-invariant-decimal",
                session.Current
            );
        }

        var seenPlayerKeys = new HashSet<string>(System.StringComparer.Ordinal);
        var preparedData = session.Data;
        var resultCurrent = session.Current;
        var resultCurrentMax = SanitySaveData.CurrentDefaultMaxSanity;
        var foundResultPlayer = false;

        foreach (var player in players)
        {
            if (!seenPlayerKeys.Add(player.PlayerKey))
            {
                return Error(
                    "v2-save-batch-player-is-duplicated",
                    resultCurrentMax
                );
            }

            var encoded = SanitySaveDataCodec.PrepareForSave(
                preparedData,
                player.PlayerKey,
                player.Current,
                player.CurrentMax
            );
            if (!encoded.Success || encoded.Data is null)
            {
                return Error(
                    $"v2-save-validation-failed:{encoded.Reason}",
                    player.CurrentMax
                );
            }

            preparedData = encoded.Data;
            if (string.Equals(player.PlayerKey, resultPlayerKey, System.StringComparison.Ordinal))
            {
                foundResultPlayer = true;
                resultCurrent = encoded.Current;
                resultCurrentMax = player.CurrentMax;
            }
        }

        if (!foundResultPlayer)
            return Error("v2-save-result-player-is-missing", resultCurrentMax);

        // 所有在线/本会话玩家先在内存中合并，最后只写一次正式 key；
        // preparedData 仍以阶段 02 的原字典为基底，因此离线 v2 记录不会丢失。
        var write = dataAccess.WriteV2(SaveKey, preparedData);
        if (!write.Success)
            return Error($"primary-write-failed:{write.Reason}", resultCurrentMax);

        return new SanityPersistenceResult(
            SanityPersistenceStatus.SavedV2,
            SanityPersistenceCapability.ReadWrite,
            "v2-data-saved",
            resultCurrent,
            preparedData
        );
    }

    private static SanityPersistenceResult MigrateLegacy(
        ISanitySaveDataAccess dataAccess,
        SanityRawReadResult original,
        SanitySaveData migrated,
        double migratedCurrent,
        double currentMax
    )
    {
        var backup = dataAccess.Read(LegacyBackupKey);
        if (backup.Status == SanityRawReadStatus.Error)
            return Error($"legacy-backup-read-failed:{backup.Reason}", currentMax);

        if (backup.Status == SanityRawReadStatus.Missing)
        {
            var backupWrite = dataAccess.Copy(LegacyBackupKey, original);
            if (!backupWrite.Success)
            {
                return Error(
                    $"legacy-backup-write-failed:{backupWrite.Reason}",
                    currentMax
                );
            }
        }
        else if (
            !SanitySaveDataCodec.AreEquivalentJsonValues(backup.Json, original.Json)
        )
        {
            // 已有不同备份时绝不覆盖它，也绝不覆盖仍可恢复的正式 legacy 值。
            return Error("legacy-backup-does-not-match-current-data", currentMax);
        }

        var encoded = SanitySaveDataCodec.Encode(migrated);
        if (!encoded.Success)
            return Error($"legacy-migration-encode-failed:{encoded.Reason}", currentMax);

        var primaryWrite = dataAccess.WriteV2(SaveKey, migrated);
        if (!primaryWrite.Success)
            return Error($"legacy-v2-write-failed:{primaryWrite.Reason}", currentMax);

        return new SanityPersistenceResult(
            SanityPersistenceStatus.MigratedLegacy,
            SanityPersistenceCapability.ReadWrite,
            "legacy-backed-up-and-migrated-to-v2",
            migratedCurrent,
            migrated
        );
    }

    private static SanityPersistenceResult Error(string reason, double currentMax)
    {
        return SanityPersistenceResult.ReadOnlyDefault(
            SanityPersistenceStatus.ReadOnlyError,
            reason,
            currentMax
        );
    }
}

internal readonly record struct SanityPlayerSaveInput(
    string PlayerKey,
    double Current,
    double CurrentMax
);

internal interface ISanitySaveDataAccess
{
    SanityRawReadResult Read(string key);

    SanityRawWriteResult Copy(string key, SanityRawReadResult source);

    SanityRawWriteResult WriteV2(string key, SanitySaveData data);
}

internal enum SanityRawReadStatus
{
    Missing,
    Success,
    Error,
}

internal sealed class SanityRawReadResult
{
    private SanityRawReadResult(
        SanityRawReadStatus status,
        string json,
        string reason,
        object? nativeValue
    )
    {
        Status = status;
        Json = json;
        Reason = reason;
        NativeValue = nativeValue;
    }

    internal SanityRawReadStatus Status { get; }
    internal string Json { get; }
    internal string Reason { get; }
    internal object? NativeValue { get; }

    internal static SanityRawReadResult Missing()
    {
        return new SanityRawReadResult(
            SanityRawReadStatus.Missing,
            string.Empty,
            "save-entry-is-missing",
            null
        );
    }

    internal static SanityRawReadResult Success(
        string json,
        object? nativeValue = null
    )
    {
        return new SanityRawReadResult(
            SanityRawReadStatus.Success,
            json,
            "save-entry-read",
            nativeValue
        );
    }

    internal static SanityRawReadResult Error(string reason)
    {
        return new SanityRawReadResult(
            SanityRawReadStatus.Error,
            string.Empty,
            reason,
            null
        );
    }
}

internal sealed class SanityRawWriteResult
{
    private SanityRawWriteResult(bool success, string reason)
    {
        Success = success;
        Reason = reason;
    }

    internal bool Success { get; }
    internal string Reason { get; }

    internal static SanityRawWriteResult Succeeded()
    {
        return new SanityRawWriteResult(true, "save-entry-written");
    }

    internal static SanityRawWriteResult Error(string reason)
    {
        return new SanityRawWriteResult(false, reason);
    }
}
