using System.Collections.Generic;
using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerPersistenceStoreTests
{
    [Fact]
    public void Legacy_single_value_is_mapped_to_master_and_scaled_from_legacy_maximum()
    {
        var access = new FakeAccess();
        access.SetPrimary("{\"Hunger\":75}");

        var result = new HungerPersistenceStore().Load(access, "123", 300d);

        Assert.Equal(HungerPersistenceStatus.MigratedLegacy, result.Status);
        Assert.True(result.CanSave);
        Assert.Equal(150d, result.Data!.Players["123"].Current);
        Assert.Equal(300d, result.Data.Players["123"].MaxAtSave);
        Assert.Equal("{\"Hunger\":75}", access.Values[HungerPersistenceStore.LegacyBackupKey]);
    }

    [Fact]
    public void V2_player_value_uses_max_at_save_ratio_for_current_maximum()
    {
        var json = "{\"SchemaVersion\":2,\"Players\":{\"123\":{\"Current\":50,\"MaxAtSave\":100}}}";
        var result = HungerSaveDataCodec.Decode(json, "123", 200d);

        Assert.True(result.Success, result.Reason);
        Assert.True(HungerSaveDataCodec.TryResolvePlayerCurrent(result.Data!.Players["123"], 200d, out var current));
        Assert.Equal(100d, current);
    }

    [Fact]
    public void Malformed_primary_data_enters_read_only_mode_and_is_not_written()
    {
        var access = new FakeAccess();
        access.SetPrimary("not-json");

        var result = new HungerPersistenceStore().Load(access, "123", 150d);

        Assert.Equal(HungerPersistenceCapability.ReadOnly, result.Capability);
        Assert.False(result.CanSave);
        Assert.Equal(0, access.WriteCount);
    }

    [Fact]
    public void Unsupported_schema_enters_read_only_mode()
    {
        var access = new FakeAccess();
        access.SetPrimary("{\"SchemaVersion\":99,\"Players\":{}}" );

        var result = new HungerPersistenceStore().Load(access, "123", 150d);

        Assert.Equal(HungerPersistenceStatus.ReadOnlyError, result.Status);
        Assert.False(result.CanSave);
    }

    [Fact]
    public void Unknown_v2_root_fields_are_treated_as_bad_data()
    {
        var result = HungerSaveDataCodec.Decode(
            "{\"SchemaVersion\":2,\"Players\":{},\"Unexpected\":true}",
            "123",
            150d
        );

        Assert.False(result.Success);
        Assert.Equal("v2-root-has-unknown-fields", result.Reason);
    }

    private sealed class FakeAccess : IHungerSaveDataAccess
    {
        internal Dictionary<string, string> Values { get; } = new();
        internal int WriteCount { get; private set; }

        public HungerRawReadResult Read(string key)
        {
            return Values.TryGetValue(key, out var json)
                ? HungerRawReadResult.Success(json, json)
                : HungerRawReadResult.Missing();
        }

        public HungerRawWriteResult Copy(string key, HungerRawReadResult source)
        {
            Values[key] = source.Json;
            return HungerRawWriteResult.Succeeded();
        }

        public HungerRawWriteResult WriteV2(string key, HungerSaveData data)
        {
            WriteCount++;
            Values[key] = "written-v2";
            return HungerRawWriteResult.Succeeded();
        }

        internal void SetPrimary(string json) => Values[HungerPersistenceStore.SaveKey] = json;
    }
}
