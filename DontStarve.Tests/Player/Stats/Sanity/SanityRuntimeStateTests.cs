using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityRuntimeStateTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MasterKey = "123456789";
    private const string FarmhandKey = "223456789";
    private const string SplitScreenKey = "323456789";

    [Fact]
    public void Host_session_initializes_the_master_at_200_with_revision_zero()
    {
        var service = BeginHost(DefaultPersistence());

        Assert.Equal(200d, service.GetCurrent(MasterKey));
        Assert.Equal(200d, service.GetMaximum(MasterKey));
        Assert.True(service.TryGetSnapshot(MasterKey, out var snapshot));
        Assert.Equal(0, snapshot.Revision);
    }

    [Fact]
    public void New_farmhand_and_split_screen_players_use_their_own_provider_maximum()
    {
        var provider = new DictionaryMaximumProvider(
            (FarmhandKey, 180d),
            (SplitScreenKey, 240d)
        );
        var service = BeginHost(DefaultPersistence(), provider);

        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out var farmhand, out _));
        Assert.True(service.TryEnsureHostPlayer(SplitScreenKey, out var split, out _));

        Assert.Equal((180d, 180d, 0L), (farmhand.Current, farmhand.Maximum, farmhand.Revision));
        Assert.Equal((240d, 240d, 0L), (split.Current, split.Maximum, split.Revision));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_player_provider_maximum_falls_back_to_200(double invalidMaximum)
    {
        var service = BeginHost(
            DefaultPersistence(),
            new DictionaryMaximumProvider((FarmhandKey, invalidMaximum))
        );

        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out var player, out var reason));

        Assert.Equal(200d, player.Current);
        Assert.Equal(200d, player.Maximum);
        Assert.Equal("player-maximum-provider-unavailable-use-safe-default", reason);
    }

    [Fact]
    public void Master_maximum_remains_200_even_if_a_replacement_provider_disagrees()
    {
        var service = BeginHost(
            DefaultPersistence(),
            new DictionaryMaximumProvider((MasterKey, 999d))
        );

        Assert.Equal(200d, service.GetMaximum(MasterKey));
    }

    [Fact]
    public void Existing_v2_farmhand_record_uses_the_stage02_MaxAtSave_ratio()
    {
        var persistence = Persistence(
            (MasterKey, 200d, 200d),
            (FarmhandKey, 75d, 150d)
        );
        var service = BeginHost(
            persistence,
            new DictionaryMaximumProvider((FarmhandKey, 300d))
        );

        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out var player, out _));

        Assert.Equal(150d, player.Current);
        Assert.Equal(300d, player.Maximum);
    }

    [Fact]
    public void Legacy_master_record_is_not_copied_to_a_new_farmhand()
    {
        var persistence = Persistence((MasterKey, 100d, 200d));
        var service = BeginHost(
            persistence,
            new DictionaryMaximumProvider((FarmhandKey, 180d))
        );

        Assert.Equal(100d, service.GetCurrent(MasterKey));
        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out var farmhand, out _));
        Assert.Equal(180d, farmhand.Current);
    }

    [Fact]
    public void Two_players_change_independently_with_independent_revisions()
    {
        var service = BeginHost(DefaultPersistence());
        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out _, out _));

        var master = service.Change(MasterKey, -25d, SanityChangeSource.Night);
        var farmhand = service.Change(FarmhandKey, -10d, SanityChangeSource.Monster);

        Assert.Equal(SanityChangeStatus.Applied, master.Status);
        Assert.Equal(SanityChangeStatus.Applied, farmhand.Status);
        Assert.Equal(SanityChangeSource.Night, master.Source);
        Assert.Equal(SanityChangeSource.Monster, farmhand.Source);
        Assert.Equal(175d, service.GetCurrent(MasterKey));
        Assert.Equal(190d, service.GetCurrent(FarmhandKey));
        Assert.Equal(1, master.Snapshot!.Revision);
        Assert.Equal(1, farmhand.Snapshot!.Revision);
    }

    [Fact]
    public void Host_changes_clamp_and_noop_does_not_spam_revision()
    {
        var service = BeginHost(DefaultPersistence());

        var below = service.Change(MasterKey, -500d, SanityChangeSource.Sleep);
        var repeatedBelow = service.Change(MasterKey, -1d, SanityChangeSource.Sleep);
        var above = service.Change(MasterKey, 999d, SanityChangeSource.Buff);

        Assert.Equal(0d, below.Snapshot!.Current);
        Assert.Equal(1, below.Snapshot.Revision);
        Assert.Equal(SanityChangeStatus.NoChange, repeatedBelow.Status);
        Assert.Equal(1, repeatedBelow.Snapshot!.Revision);
        Assert.Equal(200d, above.Snapshot!.Current);
        Assert.Equal(2, above.Snapshot.Revision);
    }

    [Fact]
    public void Unified_host_set_clamps_and_rejects_non_finite_values()
    {
        var service = BeginHost(DefaultPersistence());

        var clamped = service.Set(
            MasterKey,
            500d,
            SanityChangeSource.Administration
        );
        var rejected = service.Set(
            MasterKey,
            double.NaN,
            SanityChangeSource.Administration
        );

        Assert.Equal(SanityChangeStatus.NoChange, clamped.Status);
        Assert.Equal(200d, service.GetCurrent(MasterKey));
        Assert.Equal(SanityChangeStatus.Rejected, rejected.Status);
        Assert.Equal("requested-sanity-value-must-be-finite", rejected.Reason);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_host_delta_is_rejected_without_revision(double delta)
    {
        var service = BeginHost(DefaultPersistence());

        var result = service.Change(MasterKey, delta, SanityChangeSource.Food, "16");

        Assert.Equal(SanityChangeStatus.Rejected, result.Status);
        Assert.Equal(200d, service.GetCurrent(MasterKey));
        Assert.True(service.TryGetSnapshot(MasterKey, out var snapshot));
        Assert.Equal(0, snapshot.Revision);
    }

    [Fact]
    public void Unknown_host_player_is_safely_initialized_before_its_first_change()
    {
        var service = BeginHost(DefaultPersistence());

        var result = service.Change(FarmhandKey, -5d, SanityChangeSource.Food, "16");

        Assert.Equal(SanityChangeStatus.Applied, result.Status);
        Assert.Equal(195d, service.GetCurrent(FarmhandKey));
        Assert.Equal(1, result.Snapshot!.Revision);
    }

    [Fact]
    public void Only_host_sessions_capture_player_values_for_disk_save()
    {
        var host = BeginHost(DefaultPersistence());
        Assert.True(host.TryEnsureHostPlayer(FarmhandKey, out _, out _));
        var hostValues = host.CaptureSaveInputs();

        var client = new SanityChangeService(new DefaultSanityMaximumProvider());
        Assert.True(client.BeginClientSession(FarmhandKey, out _));

        Assert.Equal(2, hostValues.Count);
        Assert.Empty(client.CaptureSaveInputs());
    }

    [Fact]
    public void Returned_to_title_style_clear_prevents_two_save_sessions_from_leaking()
    {
        var service = BeginHost(DefaultPersistence());
        service.Change(MasterKey, -80d, SanityChangeSource.Night);

        service.ClearSession();
        Assert.False(service.HasActiveSession);
        Assert.True(
            service.BeginHostSession(
                SessionB,
                Persistence((MasterKey, 40d, 200d)),
                MasterKey,
                out _
            )
        );

        Assert.Equal(40d, service.GetCurrent(MasterKey));
        Assert.True(service.TryGetSnapshot(MasterKey, out var snapshot));
        Assert.Equal(0, snapshot.Revision);
    }

    [Fact]
    public void Read_only_bad_data_session_can_run_in_memory_but_stays_non_saveable()
    {
        var readOnly = SanityPersistenceResult.ReadOnlyDefault(
            SanityPersistenceStatus.ReadOnlyError,
            "injected-bad-data",
            200d
        );
        var service = BeginHost(readOnly);

        service.Change(MasterKey, -10d, SanityChangeSource.Night);

        Assert.Equal(190d, service.GetCurrent(MasterKey));
        Assert.False(readOnly.CanSave);
    }

    [Fact]
    public void Client_full_snapshot_establishes_the_authority_session()
    {
        var client = BeginClient(FarmhandKey);

        var result = client.ApplyClientSnapshot(
            Full(SessionA, Player(FarmhandKey, 90d, 180d, 4))
        );

        Assert.Equal(SanitySnapshotApplyStatus.AppliedFull, result.Status);
        Assert.Equal(SessionA, client.SessionId);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
        Assert.Equal(180d, client.GetMaximum(FarmhandKey));
    }

    [Fact]
    public void Client_accepts_only_the_next_delta_revision()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        var result = client.ApplyClientSnapshot(
            Delta(SessionA, Player(FarmhandKey, 80d, 180d, 5))
        );

        Assert.Equal(SanitySnapshotApplyStatus.AppliedDelta, result.Status);
        Assert.Equal(80d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_ignores_stale_and_identical_duplicate_deltas()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        var stale = client.ApplyClientSnapshot(
            Delta(SessionA, Player(FarmhandKey, 100d, 180d, 3))
        );
        var duplicate = client.ApplyClientSnapshot(
            Delta(SessionA, Player(FarmhandKey, 90d, 180d, 4))
        );

        Assert.Equal(SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate, stale.Status);
        Assert.Equal(SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate, duplicate.Status);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_requests_full_snapshot_for_revision_gap_or_unknown_player()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        var gap = client.ApplyClientSnapshot(
            Delta(SessionA, Player(FarmhandKey, 70d, 180d, 6))
        );
        var unknown = client.ApplyClientSnapshot(
            Delta(SessionA, Player(SplitScreenKey, 100d, 200d, 1))
        );

        Assert.True(gap.NeedsFullSnapshot);
        Assert.True(unknown.NeedsFullSnapshot);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_rejects_other_session_updates_until_session_cleanup()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        var delta = client.ApplyClientSnapshot(
            Delta(SessionB, Player(FarmhandKey, 80d, 180d, 5))
        );
        var full = client.ApplyClientSnapshot(
            Full(SessionB, Player(FarmhandKey, 80d, 180d, 5))
        );

        Assert.True(delta.NeedsFullSnapshot);
        Assert.Equal(SanitySnapshotApplyStatus.Rejected, full.Status);
        Assert.Equal(SessionA, client.SessionId);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_full_snapshot_cannot_regress_or_conflict_at_same_revision()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        var stale = client.ApplyClientSnapshot(
            Full(SessionA, Player(FarmhandKey, 100d, 180d, 3))
        );
        var conflict = client.ApplyClientSnapshot(
            Full(SessionA, Player(FarmhandKey, 80d, 180d, 4))
        );

        Assert.Equal(SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate, stale.Status);
        Assert.True(conflict.NeedsFullSnapshot);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_cleanup_allows_a_new_host_session_full_snapshot()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));

        client.ClearSession();
        Assert.True(client.BeginClientSession(FarmhandKey, out _));
        var result = client.ApplyClientSnapshot(
            Full(SessionB, Player(FarmhandKey, 160d, 180d, 0))
        );

        Assert.Equal(SanitySnapshotApplyStatus.AppliedFull, result.Status);
        Assert.Equal(SessionB, client.SessionId);
        Assert.Equal(160d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_food_change_queues_request_without_optimistic_state_write()
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));
        SanityChangeRequest? emitted = null;
        client.ClientRequestCreated += request => emitted = request;

        var result = client.Change(
            FarmhandKey,
            999d,
            SanityChangeSource.Food,
            "16"
        );

        Assert.Equal(SanityChangeStatus.RequestQueued, result.Status);
        Assert.NotNull(emitted);
        Assert.Equal(4, emitted!.ExpectedRevision);
        Assert.Equal(1, emitted.Nonce);
        Assert.Equal("16", emitted.InteractionId);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
        Assert.Null(typeof(SanityChangeRequest).GetProperty("Delta"));
        Assert.Null(typeof(SanityChangeRequest).GetProperty("Value"));
    }

    [Theory]
    [InlineData((int)SanityChangeSource.Equipment)]
    [InlineData((int)SanityChangeSource.Npc)]
    [InlineData((int)SanityChangeSource.Junimo)]
    [InlineData((int)SanityChangeSource.Monster)]
    [InlineData((int)SanityChangeSource.Night)]
    [InlineData((int)SanityChangeSource.Mine)]
    [InlineData((int)SanityChangeSource.Sleep)]
    [InlineData((int)SanityChangeSource.Buff)]
    [InlineData((int)SanityChangeSource.Migration)]
    [InlineData((int)SanityChangeSource.Administration)]
    [InlineData((int)SanityChangeSource.HostileShadowKill)]
    [InlineData((int)SanityChangeSource.VoluntarySleep)]
    [InlineData((int)SanityChangeSource.TimeLimitPassOut)]
    [InlineData((int)SanityChangeSource.ExhaustionPassOut)]
    [InlineData((int)SanityChangeSource.HealthDeath)]
    [InlineData((int)SanityChangeSource.SanityDarknessSpecialDeath)]
    public void Client_cannot_submit_host_observable_or_host_owned_sources(
        int sourceValue
    )
    {
        var client = ClientWithSnapshot(Player(FarmhandKey, 90d, 180d, 4));
        var source = (SanityChangeSource)sourceValue;

        var result = client.Change(FarmhandKey, 1d, source, "forged");

        Assert.Equal(SanityChangeStatus.Rejected, result.Status);
        Assert.Equal("change-source-is-host-only", result.Reason);
        Assert.Equal(90d, client.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Client_cannot_request_food_before_host_session_snapshot()
    {
        var client = BeginClient(FarmhandKey);

        var result = client.Change(
            FarmhandKey,
            10d,
            SanityChangeSource.Food,
            "16"
        );

        Assert.Equal(SanityChangeStatus.Rejected, result.Status);
        Assert.Equal("client-authority-session-is-not-ready", result.Reason);
    }

    private static SanityChangeService BeginHost(
        SanityPersistenceResult persistence,
        ISanityMaximumProvider? provider = null
    )
    {
        var service = new SanityChangeService(
            provider ?? new DefaultSanityMaximumProvider()
        );
        Assert.True(
            service.BeginHostSession(
                SessionA,
                persistence,
                MasterKey,
                out var reason
            ),
            reason
        );
        return service;
    }

    private static SanityChangeService BeginClient(string playerKey)
    {
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        Assert.True(service.BeginClientSession(playerKey, out var reason), reason);
        return service;
    }

    private static SanityChangeService ClientWithSnapshot(
        SanityPlayerSnapshot snapshot
    )
    {
        var service = BeginClient(snapshot.PlayerKey);
        var result = service.ApplyClientSnapshot(Full(SessionA, snapshot));
        Assert.Equal(SanitySnapshotApplyStatus.AppliedFull, result.Status);
        return service;
    }

    private static SanityPersistenceResult DefaultPersistence()
    {
        return Persistence((MasterKey, 200d, 200d));
    }

    private static SanityPersistenceResult Persistence(
        params (string Key, double Current, double Maximum)[] players
    )
    {
        var data = new SanitySaveData();
        foreach (var player in players)
        {
            data.Players[player.Key] = new SanityPlayerSaveData
            {
                Current = player.Current,
                MaxAtSave = player.Maximum,
            };
        }

        var masterCurrent = data.Players.TryGetValue(MasterKey, out var master)
            ? master.Current
            : 200d;
        return new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test-persistence",
            masterCurrent,
            data
        );
    }

    private static SanityPlayerSnapshot Player(
        string key,
        double current,
        double maximum,
        long revision
    )
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = key,
            Current = current,
            Maximum = maximum,
            Revision = revision,
        };
    }

    private static SanitySnapshotMessage Full(
        string sessionId,
        params SanityPlayerSnapshot[] players
    )
    {
        return new SanitySnapshotMessage
        {
            SessionId = sessionId,
            IsFull = true,
            Players = players.ToList(),
        };
    }

    private static SanitySnapshotMessage Delta(
        string sessionId,
        SanityPlayerSnapshot player
    )
    {
        return new SanitySnapshotMessage
        {
            SessionId = sessionId,
            IsFull = false,
            Players = new List<SanityPlayerSnapshot> { player },
        };
    }

    private sealed class DictionaryMaximumProvider : ISanityMaximumProvider
    {
        private readonly Dictionary<string, double> values;

        internal DictionaryMaximumProvider(params (string Key, double Value)[] values)
        {
            this.values = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public double GetMaximum(string playerKey)
        {
            return values.TryGetValue(playerKey, out var value) ? value : 200d;
        }
    }
}
