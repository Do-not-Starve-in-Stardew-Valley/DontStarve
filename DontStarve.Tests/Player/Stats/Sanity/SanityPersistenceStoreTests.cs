using System.Security.Cryptography;
using System.Text;
using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityPersistenceStoreTests
{
    private const string MasterKey = "123456789";
    private const string FarmhandKey = "223456789";
    private const string SplitScreenKey = "323456789";
    private const double CurrentMax = 200d;

    private readonly SanityPersistenceStore store = new();

    [Fact]
    public void Empty_save_initializes_master_at_200_without_writing_during_load()
    {
        var access = new FakeSanitySaveDataAccess();

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.InitializedNew, result.Status);
        Assert.Equal(SanityPersistenceCapability.ReadWrite, result.Capability);
        Assert.Equal(200d, result.Current);
        AssertPlayer(result, MasterKey, 200d, 200d);
        Assert.Empty(access.WriteAttempts);
    }

    [Fact]
    public void Empty_save_is_written_as_v2_on_the_next_save_event()
    {
        var access = new FakeSanitySaveDataAccess();
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var saved = store.Save(access, loaded, MasterKey, 125d, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.SavedV2, saved.Status);
        Assert.Equal(125d, saved.Current);
        AssertPlayer(saved, MasterKey, 125d, 200d);
        Assert.True(access.Entries.ContainsKey(SanityPersistenceStore.SaveKey));
        var reloaded = store.Load(access, MasterKey, CurrentMax);
        Assert.Equal(SanityPersistenceStatus.LoadedV2, reloaded.Status);
        Assert.Equal(125d, reloaded.Current);
    }

    [Fact]
    public void Missing_primary_with_existing_legacy_backup_fails_closed()
    {
        var backup = ReadFixture("half.json");
        var access = new FakeSanitySaveDataAccess();
        access.Entries[SanityPersistenceStore.LegacyBackupKey] = backup;

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(
            result,
            "primary-missing-while-legacy-backup-exists"
        );
        Assert.Empty(access.WriteAttempts);
        Assert.False(access.Entries.ContainsKey(SanityPersistenceStore.SaveKey));
        Assert.Equal(backup, access.Entries[SanityPersistenceStore.LegacyBackupKey]);
    }

    [Theory]
    [InlineData("zero.json", 0d)]
    [InlineData("half.json", 100d)]
    [InlineData("max.json", 200d)]
    [InlineData("negative.json", 0d)]
    [InlineData("over-max.json", 200d)]
    public void Legacy_values_migrate_once_for_the_master_player(
        string fixtureName,
        double expected
    )
    {
        var original = ReadFixture(fixtureName);
        var access = FakeSanitySaveDataAccess.WithPrimary(original);

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.MigratedLegacy, result.Status);
        Assert.Equal(expected, result.Current);
        AssertPlayer(result, MasterKey, expected, 200d);
        Assert.True(
            SanitySaveDataCodec.AreEquivalentJsonValues(
                original,
                access.Entries[SanityPersistenceStore.LegacyBackupKey]
            )
        );
        Assert.Equal(
            new[]
            {
                SanityPersistenceStore.LegacyBackupKey,
                SanityPersistenceStore.SaveKey,
            },
            access.WriteAttempts
        );
    }

    [Fact]
    public void Legacy_value_is_not_copied_to_farmhand_or_split_screen_players()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(ReadFixture("half.json"));

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.NotNull(result.Data);
        Assert.Single(result.Data!.Players);
        Assert.True(result.Data.Players.ContainsKey(MasterKey));
        Assert.False(result.Data.Players.ContainsKey(FarmhandKey));
        Assert.False(result.Data.Players.ContainsKey(SplitScreenKey));
    }

    [Fact]
    public void Migration_copies_the_complete_legacy_object_before_primary_write()
    {
        const string original =
            "{\"Sanity\":75,\"FutureField\":{\"Keep\":true},\"Label\":\"legacy\"}";
        var access = FakeSanitySaveDataAccess.WithPrimary(original);

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.MigratedLegacy, result.Status);
        Assert.Equal(
            new[]
            {
                SanityPersistenceStore.LegacyBackupKey,
                SanityPersistenceStore.SaveKey,
            },
            access.WriteAttempts
        );
        Assert.True(
            SanitySaveDataCodec.AreEquivalentJsonValues(
                original,
                access.Entries[SanityPersistenceStore.LegacyBackupKey]
            )
        );
    }

    [Fact]
    public void Existing_matching_backup_is_not_overwritten()
    {
        var original = ReadFixture("half.json");
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        access.Entries[SanityPersistenceStore.LegacyBackupKey] = original;

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.MigratedLegacy, result.Status);
        Assert.Equal(
            new[] { SanityPersistenceStore.SaveKey },
            access.WriteAttempts
        );
        Assert.Equal(original, access.Entries[SanityPersistenceStore.LegacyBackupKey]);
    }

    [Fact]
    public void Existing_different_backup_fails_closed_without_overwriting_either_key()
    {
        var original = ReadFixture("half.json");
        var existingBackup = ReadFixture("max.json");
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        access.Entries[SanityPersistenceStore.LegacyBackupKey] = existingBackup;

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, "legacy-backup-does-not-match-current-data");
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
        Assert.Equal(existingBackup, access.Entries[SanityPersistenceStore.LegacyBackupKey]);
    }

    [Fact]
    public void Reload_after_migration_reads_v2_without_a_second_migration_or_backup()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(ReadFixture("half.json"));
        var first = store.Load(access, MasterKey, CurrentMax);
        var writeCountAfterMigration = access.WriteAttempts.Count;

        var second = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.MigratedLegacy, first.Status);
        Assert.Equal(SanityPersistenceStatus.LoadedV2, second.Status);
        Assert.Equal(100d, second.Current);
        Assert.Equal(writeCountAfterMigration, access.WriteAttempts.Count);
    }

    [Fact]
    public void V2_current_value_loads_without_migration()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, 80d, 200d))
        );

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.LoadedV2, result.Status);
        Assert.Equal(80d, result.Current);
        Assert.Empty(access.WriteAttempts);
        Assert.False(access.Entries.ContainsKey(SanityPersistenceStore.LegacyBackupKey));
    }

    [Fact]
    public void MaxAtSave_150_scales_proportionally_to_current_max_200()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, 75d, 150d))
        );

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(100d, result.Current);
        AssertPlayer(result, MasterKey, 75d, 150d);
    }

    [Fact]
    public void Saving_updates_MaxAtSave_and_a_later_max_change_preserves_ratio()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, 100d, 200d))
        );
        var at300 = store.Load(access, MasterKey, 300d);
        Assert.Equal(150d, at300.Current);

        var savedAt300 = store.Save(access, at300, MasterKey, at300.Current, 300d);
        AssertPlayer(savedAt300, MasterKey, 150d, 300d);

        var at400 = store.Load(access, MasterKey, 400d);
        Assert.Equal(200d, at400.Current);
    }

    [Fact]
    public void Saving_preserves_offline_player_records_as_the_dictionary_base()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, 100d, 200d), (FarmhandKey, 45d, 150d))
        );
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var saved = store.Save(access, loaded, MasterKey, 110d, CurrentMax);

        AssertPlayer(saved, MasterKey, 110d, 200d);
        AssertPlayer(saved, FarmhandKey, 45d, 150d);
        var reloaded = store.Load(access, MasterKey, CurrentMax);
        AssertPlayer(reloaded, FarmhandKey, 45d, 150d);
    }

    [Fact]
    public void Saving_multiple_runtime_players_writes_primary_once_and_preserves_offline_records()
    {
        const string OfflineKey = "423456789";
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, 100d, 200d), (OfflineKey, 45d, 150d))
        );
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var saved = store.SavePlayers(
            access,
            loaded,
            new[]
            {
                new SanityPlayerSaveInput(MasterKey, 110d, 200d),
                new SanityPlayerSaveInput(SplitScreenKey, 175d, 200d),
            },
            MasterKey
        );

        Assert.Equal(new[] { SanityPersistenceStore.SaveKey }, access.WriteAttempts);
        AssertPlayer(saved, MasterKey, 110d, 200d);
        AssertPlayer(saved, SplitScreenKey, 175d, 200d);
        AssertPlayer(saved, OfflineKey, 45d, 150d);
    }

    [Fact]
    public void Invalid_runtime_player_in_batch_fails_before_the_single_primary_write()
    {
        var original = V2((MasterKey, 100d, 200d));
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var result = store.SavePlayers(
            access,
            loaded,
            new[]
            {
                new SanityPlayerSaveInput(MasterKey, 110d, 200d),
                new SanityPlayerSaveInput(SplitScreenKey, double.NaN, 200d),
            },
            MasterKey
        );

        AssertReadOnlyError(result, "current-sanity-must-be-finite");
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Fact]
    public void Duplicate_runtime_player_in_batch_is_rejected_without_writing()
    {
        var original = V2((MasterKey, 100d, 200d));
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var result = store.SavePlayers(
            access,
            loaded,
            new[]
            {
                new SanityPlayerSaveInput(MasterKey, 110d, 200d),
                new SanityPlayerSaveInput(MasterKey, 120d, 200d),
            },
            MasterKey
        );

        AssertReadOnlyError(result, "v2-save-batch-player-is-duplicated");
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Fact]
    public void Missing_target_player_in_valid_v2_gets_only_its_own_safe_default()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((FarmhandKey, 45d, 150d))
        );

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(200d, result.Current);
        AssertPlayer(result, MasterKey, 200d, 200d);
        AssertPlayer(result, FarmhandKey, 45d, 150d);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":3,\"Players\":{}}", "save-schema-version-is-unsupported")]
    [InlineData("{\"Players\":{}}", "v2-schema-version-is-missing")]
    [InlineData("{\"SchemaVersion\":2}", "v2-players-field-is-missing-or-duplicated")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":null}", "v2-players-must-be-an-object")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":{\"123456789\":{\"Current\":10}}}", "v2-player-fields-are-missing-or-duplicated")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":{\"123456789\":{\"Current\":10,\"MaxAtSave\":0}}}", "v2-max-at-save-must-be-positive-and-finite")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":{\"123456789\":{\"Current\":\"NaN\",\"MaxAtSave\":200}}}", "v2-current-must-be-finite")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":{\"123456789\":{\"Current\":10,\"MaxAtSave\":\"Infinity\"}}}", "v2-max-at-save-must-be-positive-and-finite")]
    [InlineData("{\"SchemaVersion\":2,\"Players\":{},\"Extra\":true}", "v2-root-has-unknown-fields")]
    public void Invalid_v2_data_uses_safe_memory_and_makes_the_session_read_only(
        string json,
        string expectedReason
    )
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(json);

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, expectedReason);
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(json, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Theory]
    [InlineData("missing-key.json")]
    [InlineData("null.json")]
    [InlineData("nan-string.json")]
    [InlineData("positive-infinity-string.json")]
    [InlineData("negative-infinity-string.json")]
    [InlineData("non-finite-overflow.json")]
    [InlineData("bad-field.json")]
    [InlineData("invalid-root.json")]
    [InlineData("bad-json.json")]
    [InlineData("nan-token.json")]
    public void Invalid_legacy_or_json_never_creates_backup_or_overwrites_primary(
        string fixtureName
    )
    {
        var original = ReadFixture(fixtureName);
        var access = FakeSanitySaveDataAccess.WithPrimary(original);

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal(SanityPersistenceCapability.ReadOnly, result.Capability);
        Assert.Equal(200d, result.Current);
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
        Assert.False(access.Entries.ContainsKey(SanityPersistenceStore.LegacyBackupKey));
    }

    [Fact]
    public void Primary_read_failure_uses_safe_memory_and_never_writes()
    {
        var access = new FakeSanitySaveDataAccess();
        access.FailReads.Add(SanityPersistenceStore.SaveKey);

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, "primary-read-failed");
        Assert.Empty(access.WriteAttempts);
    }

    [Fact]
    public void Backup_read_failure_keeps_primary_legacy_and_never_writes()
    {
        var original = ReadFixture("half.json");
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        access.FailReads.Add(SanityPersistenceStore.LegacyBackupKey);

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, "legacy-backup-read-failed");
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Theory]
    [InlineData(-10d, 0d)]
    [InlineData(500d, 200d)]
    public void Finite_v2_current_is_clamped_after_ratio_scaling(
        double storedCurrent,
        double expected
    )
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(
            V2((MasterKey, storedCurrent, 200d))
        );

        var result = store.Load(access, MasterKey, CurrentMax);

        Assert.Equal(SanityPersistenceStatus.LoadedV2, result.Status);
        Assert.Equal(expected, result.Current);
    }

    [Fact]
    public void Backup_write_failure_keeps_primary_legacy_and_fails_closed()
    {
        var original = ReadFixture("half.json");
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        access.FailWrites.Add(SanityPersistenceStore.LegacyBackupKey);

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, "legacy-backup-write-failed");
        Assert.Equal(
            new[] { SanityPersistenceStore.LegacyBackupKey },
            access.WriteAttempts
        );
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
        Assert.False(access.Entries.ContainsKey(SanityPersistenceStore.LegacyBackupKey));
    }

    [Fact]
    public void Primary_write_failure_after_backup_keeps_legacy_and_fails_closed()
    {
        var original = ReadFixture("half.json");
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        access.FailWrites.Add(SanityPersistenceStore.SaveKey);

        var result = store.Load(access, MasterKey, CurrentMax);

        AssertReadOnlyError(result, "legacy-v2-write-failed");
        Assert.Equal(
            new[]
            {
                SanityPersistenceStore.LegacyBackupKey,
                SanityPersistenceStore.SaveKey,
            },
            access.WriteAttempts
        );
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
        Assert.True(access.Entries.ContainsKey(SanityPersistenceStore.LegacyBackupKey));
    }

    [Fact]
    public void Read_only_session_does_not_retry_writes_during_saving()
    {
        var access = FakeSanitySaveDataAccess.WithPrimary(ReadFixture("bad-json.json"));
        var loaded = store.Load(access, MasterKey, CurrentMax);
        var writesBeforeSave = access.WriteAttempts.Count;

        var afterSave = store.Save(access, loaded, MasterKey, 10d, CurrentMax);

        Assert.Same(loaded, afterSave);
        Assert.Equal(writesBeforeSave, access.WriteAttempts.Count);
    }

    [Fact]
    public void V2_save_failure_makes_the_session_read_only_without_changing_primary()
    {
        var original = V2((MasterKey, 80d, 200d));
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        var loaded = store.Load(access, MasterKey, CurrentMax);
        access.FailWrites.Add(SanityPersistenceStore.SaveKey);

        var result = store.Save(access, loaded, MasterKey, 90d, CurrentMax);

        AssertReadOnlyError(result, "primary-write-failed");
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Fact]
    public void Non_finite_runtime_value_is_not_saved()
    {
        var original = V2((MasterKey, 80d, 200d));
        var access = FakeSanitySaveDataAccess.WithPrimary(original);
        var loaded = store.Load(access, MasterKey, CurrentMax);

        var result = store.Save(access, loaded, MasterKey, double.NaN, CurrentMax);

        AssertReadOnlyError(result, "current-sanity-must-be-finite");
        Assert.Empty(access.WriteAttempts);
        Assert.Equal(original, access.Entries[SanityPersistenceStore.SaveKey]);
    }

    [Fact]
    public void Invalid_player_key_fails_closed_before_any_read_or_write()
    {
        var access = new FakeSanitySaveDataAccess();

        var result = store.Load(access, "00123", CurrentMax);

        AssertReadOnlyError(result, "player-key-is-not-canonical-invariant-decimal");
        Assert.Empty(access.ReadAttempts);
        Assert.Empty(access.WriteAttempts);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_current_max_fails_closed_before_any_read_or_write(
        double currentMax
    )
    {
        var access = new FakeSanitySaveDataAccess();

        var result = store.Load(access, MasterKey, currentMax);

        Assert.Equal(SanityPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal(200d, result.Current);
        Assert.Contains("current-max-must-be-positive-and-finite", result.Reason);
        Assert.Empty(access.ReadAttempts);
        Assert.Empty(access.WriteAttempts);
    }

    [Fact]
    public void Player_key_format_is_invariant_decimal()
    {
        Assert.Equal("123456789", SanityPlayerKey.FromUniqueMultiplayerId(123456789));
        Assert.True(SanityPlayerKey.IsCanonical("0"));
        Assert.True(SanityPlayerKey.IsCanonical("123456789"));
        Assert.False(SanityPlayerKey.IsCanonical("001"));
        Assert.False(SanityPlayerKey.IsCanonical("-1"));
        Assert.False(SanityPlayerKey.IsCanonical("1,000"));
    }

    [Fact]
    public void Stage01_fixture_set_remains_the_single_16_json_authority()
    {
        var fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "LegacySanity"
        );
        var stage01Order = new[]
        {
            "bad-field.json",
            "bad-json.json",
            "half.json",
            "invalid-root.json",
            "max.json",
            "missing-key.json",
            "nan-string.json",
            "nan-token.json",
            "negative.json",
            "negative-infinity-string.json",
            "non-finite-overflow.json",
            "null.json",
            "over-max.json",
            "positive-infinity-string.json",
            "wrong-case-key.json",
            "zero.json",
        };
        var discoveredFiles = Directory.GetFiles(fixtureDirectory, "*.json");
        var files = stage01Order
            .Select(name => Path.Combine(fixtureDirectory, name))
            .ToArray();
        var lines = files.Select(
            path =>
                $"{Path.GetFileName(path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
        );
        var payload = string.Join("\n", lines);
        var collectionHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))
        );

        Assert.Equal(16, discoveredFiles.Length);
        Assert.All(files, path => Assert.True(File.Exists(path), path));
        Assert.Equal(
            "AF7632F512A19CBC630510C313D7EFB63771C72FF374099755CA60A7A97DD278",
            collectionHash
        );
    }

    private static void AssertPlayer(
        SanityPersistenceResult result,
        string playerKey,
        double current,
        double maxAtSave
    )
    {
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Players.TryGetValue(playerKey, out var player));
        Assert.NotNull(player);
        Assert.Equal(current, player!.Current);
        Assert.Equal(maxAtSave, player.MaxAtSave);
    }

    private static void AssertReadOnlyError(
        SanityPersistenceResult result,
        string reasonFragment
    )
    {
        Assert.Equal(SanityPersistenceStatus.ReadOnlyError, result.Status);
        Assert.Equal(SanityPersistenceCapability.ReadOnly, result.Capability);
        Assert.False(result.CanSave);
        Assert.Equal(200d, result.Current);
        Assert.Contains(reasonFragment, result.Reason, StringComparison.Ordinal);
    }

    private static string V2(params (string Key, double Current, double Max)[] players)
    {
        var body = string.Join(
            ",",
            players.Select(
                player =>
                    $"\"{player.Key}\":{{\"Current\":{player.Current.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"MaxAtSave\":{player.Max.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            )
        );
        return $"{{\"SchemaVersion\":2,\"Players\":{{{body}}}}}";
    }

    private static string ReadFixture(string fixtureName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "LegacySanity",
            fixtureName
        );
        return File.ReadAllText(path);
    }

    private sealed class FakeSanitySaveDataAccess : ISanitySaveDataAccess
    {
        internal Dictionary<string, string> Entries { get; } =
            new(StringComparer.Ordinal);
        internal HashSet<string> FailReads { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> FailWrites { get; } = new(StringComparer.Ordinal);
        internal List<string> ReadAttempts { get; } = new();
        internal List<string> WriteAttempts { get; } = new();

        internal static FakeSanitySaveDataAccess WithPrimary(string json)
        {
            var access = new FakeSanitySaveDataAccess();
            access.Entries[SanityPersistenceStore.SaveKey] = json;
            return access;
        }

        public SanityRawReadResult Read(string key)
        {
            ReadAttempts.Add(key);
            if (FailReads.Contains(key))
                return SanityRawReadResult.Error("injected-read-failure");
            return Entries.TryGetValue(key, out var json)
                ? SanityRawReadResult.Success(json)
                : SanityRawReadResult.Missing();
        }

        public SanityRawWriteResult Copy(string key, SanityRawReadResult source)
        {
            WriteAttempts.Add(key);
            if (FailWrites.Contains(key))
                return SanityRawWriteResult.Error("injected-write-failure");

            Entries[key] = source.Json;
            return SanityRawWriteResult.Succeeded();
        }

        public SanityRawWriteResult WriteV2(string key, SanitySaveData data)
        {
            WriteAttempts.Add(key);
            if (FailWrites.Contains(key))
                return SanityRawWriteResult.Error("injected-write-failure");

            var encoded = SanitySaveDataCodec.Encode(data);
            if (!encoded.Success)
                return SanityRawWriteResult.Error(encoded.Reason);

            Entries[key] = encoded.Json;
            return SanityRawWriteResult.Succeeded();
        }
    }
}
