using System;
using System.Collections.Generic;
using System.Text.Json;
using DontStarve.Player.Stats.Health;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

public sealed class HealthPersistenceStoreTests
{
    /// <summary>内存假存档：模拟磁盘上的一页 JSON，供失败策略单测（镜像 hunger store 测试纪律）。</summary>
    private sealed class FakeSaveAccess : IHealthSaveDataAccess
    {
        internal string? StoredJson;
        internal bool FailRead;
        internal bool FailWrite;
        internal HealthSaveData? LastWritten;

        public HealthRawReadResult Read(string key)
        {
            if (FailRead)
                return HealthRawReadResult.Error("simulated-read-exception");
            return StoredJson is null
                ? HealthRawReadResult.Missing()
                : HealthRawReadResult.Success(StoredJson);
        }

        public HealthRawWriteResult Write(string key, HealthSaveData data)
        {
            if (FailWrite)
                return HealthRawWriteResult.Error("simulated-write-exception");

            LastWritten = data;
            StoredJson = JsonSerializer.Serialize(data);
            return HealthRawWriteResult.Succeeded();
        }
    }

    private static HealthSaveData NewData(params (string Key, double Value)[] entries)
    {
        var data = new HealthSaveData();
        foreach (var (key, value) in entries)
            data.Remainders[key] = HealthLedgerRules.FormatRemainder(value);
        return data;
    }

    [Fact]
    public void Save_then_load_round_trips_the_remainder_dictionary()
    {
        // 决议 6：save 级抽屉往返必须无损失、无文化漂移（InvariantCulture "R" 往返）。
        var access = new FakeSaveAccess();
        var store = new HealthPersistenceStore();
        var session = store.Load(access);

        Assert.Equal(HealthPersistenceStatus.InitializedNew, session.Status);
        Assert.True(session.CanSave);

        var saved = store.Save(
            access,
            session,
            NewData(
                ("123", -0.09999999999999964),
                ("456", 0.25),
                ("789", -0.5)
            )
        );

        Assert.Equal(HealthPersistenceStatus.Saved, saved.Status);
        Assert.NotNull(access.LastWritten);

        var reloaded = store.Load(access);
        Assert.Equal(HealthPersistenceStatus.Loaded, reloaded.Status);
        Assert.True(reloaded.CanSave);
        Assert.NotNull(reloaded.Remainders);
        Assert.Equal(3, reloaded.Remainders!.Count);
        Assert.Equal(-0.09999999999999964, reloaded.Remainders["123"], precision: 15);
        Assert.Equal(0.25d, reloaded.Remainders["456"], precision: 15);
        Assert.Equal(-0.5d, reloaded.Remainders["789"], precision: 15);
        Assert.Empty(reloaded.DroppedEntryKeys);
    }

    [Fact]
    public void Missing_data_initializes_new_and_stays_writable()
    {
        // 决议 6：Missing（新档）→ 空字典可写，不熔断——余数天然可从 0 播种、无旧数据可丢
        //（与 hunger 熔断语义的差异及原因见 HealthPersistenceStore 注释）。
        var access = new FakeSaveAccess();
        var result = new HealthPersistenceStore().Load(access);

        Assert.Equal(HealthPersistenceStatus.InitializedNew, result.Status);
        Assert.Equal(HealthPersistenceCapability.ReadWrite, result.Capability);
        Assert.NotNull(result.Remainders);
        Assert.Empty(result.Remainders!);
    }

    [Fact]
    public void Unreadable_read_breaks_the_session_to_read_only_and_never_writes()
    {
        // hunger 同款纪律：读失败 → 本次会话只读熔断，绝不用默认值覆盖旧档。
        var access = new FakeSaveAccess { FailRead = true, StoredJson = "{\"SchemaVersion\":1}" };
        var store = new HealthPersistenceStore();
        var session = store.Load(access);

        Assert.Equal(HealthPersistenceCapability.ReadOnly, session.Capability);
        Assert.Null(session.Remainders);

        var saved = store.Save(access, session, NewData(("123", 0.5)));
        Assert.Same(session, saved);
        Assert.Null(access.LastWritten);
    }

    [Fact]
    public void Malformed_json_breaks_the_session_to_read_only()
    {
        var access = new FakeSaveAccess { StoredJson = "{not-json" };
        var result = new HealthPersistenceStore().Load(access);

        Assert.Equal(HealthPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal("save-json-is-malformed", result.Reason);
        Assert.False(result.CanSave);
    }

    [Fact]
    public void Unsupported_schema_version_breaks_the_session_to_read_only()
    {
        var access = new FakeSaveAccess
        {
            StoredJson = "{\"SchemaVersion\":99,\"Remainders\":{\"123\":\"0.5\"}}",
        };
        var result = new HealthPersistenceStore().Load(access);

        Assert.Equal(HealthPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal("save-schema-version-is-unsupported", result.Reason);
    }

    [Fact]
    public void Unknown_root_field_breaks_the_session_to_read_only()
    {
        // 严格 schema（镜像 hunger）：未知字段拒绝接管，防半结构化脏档被默认值静默覆盖。
        var access = new FakeSaveAccess
        {
            StoredJson = "{\"SchemaVersion\":1,\"Remainders\":{},\"Players\":{}}",
        };
        var result = new HealthPersistenceStore().Load(access);

        Assert.Equal(HealthPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal("save-root-has-unknown-fields", result.Reason);
    }

    [Fact]
    public void Single_bad_entry_is_dropped_without_breaking_the_dictionary()
    {
        // 决议 6：单条解析失败 → 该条丢弃为缺省 0，不熔断整个字典（单条代价 ≤ 1 点血，
        // 整档熔断会让所有玩家零头进度回退，代价不成比例）。
        var access = new FakeSaveAccess
        {
            StoredJson =
                "{\"SchemaVersion\":1,\"Remainders\":{\"123\":\"abc\",\"456\":\"-0.5\",\"789\":1E400,\"12\":\"0.25\"}}",
        };
        var result = new HealthPersistenceStore().Load(access);

        Assert.True(result.CanSave);
        Assert.NotNull(result.Remainders);
        Assert.Equal(2, result.Remainders!.Count);
        Assert.Equal(-0.5d, result.Remainders["456"], precision: 15);
        Assert.Equal(0.25d, result.Remainders["12"], precision: 15);
        Assert.Equal(2, result.DroppedEntryKeys.Count);
        Assert.Contains("123", result.DroppedEntryKeys);
        Assert.Contains("789", result.DroppedEntryKeys);
    }

    [Fact]
    public void Non_canonical_or_missing_keys_are_rejected_on_decode()
    {
        // key 必须是 UniqueMultiplayerID 的规范十进制串（可无损还原），其余丢弃。
        var access = new FakeSaveAccess
        {
            StoredJson =
                "{\"SchemaVersion\":1,\"Remainders\":{\"0123\":\"0.5\",\"abc\":\"0.5\",\"124\":\"-0.25\"}}",
        };
        var result = new HealthPersistenceStore().Load(access);

        Assert.True(result.CanSave);
        Assert.NotNull(result.Remainders);
        Assert.Single(result.Remainders!);
        Assert.Equal(-0.25d, result.Remainders!["124"], precision: 15);
        Assert.Equal(2, result.DroppedEntryKeys.Count);
    }

    [Fact]
    public void Encode_filters_non_finite_values_and_non_canonical_keys()
    {
        // 写前净化：非有限余数与非法 key 一律剔除，绝不写进存档。
        var data = new HealthSaveData();
        data.Remainders["123"] = HealthLedgerRules.FormatRemainder(0.5);
        data.Remainders["456"] = "NaN";
        data.Remainders["789"] = "1E400";
        data.Remainders["012"] = "0.25";
        data.Remainders["124"] = "-0.75";

        var (success, encoded, _) = HealthPersistenceStore.Encode(data);

        Assert.True(success);
        Assert.NotNull(encoded);
        Assert.Equal(2, encoded!.Remainders.Count);
        Assert.Equal(HealthLedgerRules.FormatRemainder(0.5), encoded.Remainders["123"]);
        Assert.Equal(HealthLedgerRules.FormatRemainder(-0.75), encoded.Remainders["124"]);
    }

    [Fact]
    public void Write_failure_breaks_the_session_to_read_only()
    {
        var access = new FakeSaveAccess();
        var store = new HealthPersistenceStore();
        var session = store.Load(access);
        Assert.True(session.CanSave);

        access.FailWrite = true;
        var saved = store.Save(access, session, NewData(("123", 0.5)));

        Assert.Equal(HealthPersistenceCapability.ReadOnly, saved.Capability);
        Assert.Equal("primary-write-failed:simulated-write-exception", saved.Reason);
        Assert.Null(access.LastWritten);
    }
}
