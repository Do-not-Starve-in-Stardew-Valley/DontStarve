using DontStarve.Interface;
using DontStarve.Player.Stats;
using DontStarve.Time;
using Xunit;

namespace DontStarve.Tests.Time;

public sealed class SurvivalStateTimeOwnerTests
{
    [Fact]
    public void Positive_sync_dispatches_each_ordinary_minute_once()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(0);

        harness.TimeApi.Synchronize(1441);
        harness.TimeApi.Synchronize(1441);

        Assert.Equal(
            Enumerable.Range(1, 1441).Select(minute => ("host", (long)minute, SurvivalStateTimePhase.Ordinary)),
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Realtime_and_blackout_intervals_are_not_replayed_by_later_catch_up()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(10);
        harness.TimeApi.Synchronize(12);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));

        harness.TimeApi.Synchronize(15);
        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 12, 15,
            SurvivalStateTimePhase.RealtimeSleep, out var realtimeMinutes));
        Assert.Equal(3, realtimeMinutes);
        Assert.True(harness.Owner.SetSleepPhase(
            "host", "sleep-a", SurvivalStateTimePhase.BlackoutSleep));
        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 15, 18,
            SurvivalStateTimePhase.BlackoutSleep, out var blackoutMinutes));
        Assert.Equal(3, blackoutMinutes);
        harness.Owner.EndAllSleepContexts();
        Assert.False(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 15, 18,
            SurvivalStateTimePhase.BlackoutSleep, out _));

        harness.TimeApi.Synchronize(20);

        Assert.Equal(
            new[]
            {
                ("host", 11L, SurvivalStateTimePhase.Ordinary),
                ("host", 12L, SurvivalStateTimePhase.Ordinary),
                ("host", 13L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 14L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 15L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 16L, SurvivalStateTimePhase.BlackoutSleep),
                ("host", 17L, SurvivalStateTimePhase.BlackoutSleep),
                ("host", 18L, SurvivalStateTimePhase.BlackoutSleep),
                ("host", 19L, SurvivalStateTimePhase.Ordinary),
                ("host", 20L, SurvivalStateTimePhase.Ordinary),
            },
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Duplicate_sleep_calls_are_idempotent_and_post_target_minutes_continue()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(100);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));

        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 100, 102,
            SurvivalStateTimePhase.RealtimeSleep, out var firstCount));
        Assert.Equal(2, firstCount);
        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 100, 102,
            SurvivalStateTimePhase.RealtimeSleep, out var duplicateCount));
        Assert.Equal(0, duplicateCount);
        Assert.True(harness.Owner.EndSleep("host", "sleep-a"));

        harness.TimeApi.RaiseUpdate(103);
        harness.TimeApi.RaiseUpdate(104);

        Assert.Equal(
            new[]
            {
                ("host", 101L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 102L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 103L, SurvivalStateTimePhase.Ordinary),
                ("host", 104L, SurvivalStateTimePhase.Ordinary),
            },
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Sleep_interval_can_be_preflighted_without_consuming_minutes()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(100);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));
        Assert.True(harness.Owner.SetSleepPhase(
            "host", "sleep-a", SurvivalStateTimePhase.BlackoutSleep));

        bool CommitSleepInterval(bool allStatusWritesAccepted, out long count)
        {
            count = 0;
            if (!harness.Owner.CanAdvanceSleepInterval(
                "host", host, "sleep-a", 100, 102,
                SurvivalStateTimePhase.BlackoutSleep
            ) || !allStatusWritesAccepted)
                return false;

            return harness.Owner.AdvanceSleepInterval(
                "host", host, "sleep-a", 100, 102,
                SurvivalStateTimePhase.BlackoutSleep, out count);
        }

        Assert.False(CommitSleepInterval(allStatusWritesAccepted: false, out _));
        Assert.Empty(harness.Consumer.Received);
        Assert.True(CommitSleepInterval(allStatusWritesAccepted: true, out var count));
        Assert.Equal(2, count);
        Assert.Equal(2, harness.Consumer.Received.Count);
    }

    [Fact]
    public void Rewind_and_zero_delta_do_not_replay_consumed_minutes()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(100);
        harness.TimeApi.Synchronize(105);
        harness.TimeApi.Synchronize(105);
        harness.TimeApi.Synchronize(102);
        harness.TimeApi.Synchronize(105);
        harness.TimeApi.Synchronize(107);

        Assert.Equal(
            Enumerable.Range(101, 7).Select(minute => ("host", (long)minute, SurvivalStateTimePhase.Ordinary)),
            harness.Consumer.Received
        );
    }

    [Fact]
    public void One_registered_synthetic_consumer_needs_no_sleep_deduplication_code()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(0);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));

        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 0, 4,
            SurvivalStateTimePhase.RealtimeSleep, out _));
        harness.TimeApi.RaiseUpdate(1);
        harness.TimeApi.RaiseUpdate(2);
        harness.TimeApi.RaiseUpdate(3);
        harness.TimeApi.RaiseUpdate(4);

        Assert.Equal(
            Enumerable.Range(1, 4).Select(minute => ("host", (long)minute, SurvivalStateTimePhase.RealtimeSleep)),
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Players_have_independent_minute_watermarks()
    {
        var host = new TestPlayer("host");
        var farmhand = new TestPlayer("farmhand");
        var harness = Create(host, farmhand);
        harness.TimeApi.Load(0);

        harness.TimeApi.RaiseUpdate(1);
        harness.TimeApi.RaiseUpdate(1);

        Assert.Equal(
            new[]
            {
                ("host", 1L, SurvivalStateTimePhase.Ordinary),
                ("farmhand", 1L, SurvivalStateTimePhase.Ordinary),
            },
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Cancelling_sleep_clears_phase_but_preserves_completed_minutes()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(10);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));
        Assert.True(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 10, 12,
            SurvivalStateTimePhase.RealtimeSleep, out _));

        Assert.True(harness.Owner.EndSleep("host", "sleep-a"));
        Assert.False(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 10, 12,
            SurvivalStateTimePhase.RealtimeSleep, out _));
        harness.TimeApi.RaiseUpdate(11);
        harness.TimeApi.RaiseUpdate(12);
        harness.TimeApi.RaiseUpdate(13);

        Assert.Equal(
            new[]
            {
                ("host", 11L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 12L, SurvivalStateTimePhase.RealtimeSleep),
                ("host", 13L, SurvivalStateTimePhase.Ordinary),
            },
            harness.Consumer.Received
        );
    }

    [Fact]
    public void Session_load_and_title_cleanup_allow_a_fresh_minute_cursor()
    {
        var host = new TestPlayer("host");
        var harness = Create(host);
        harness.TimeApi.Load(10);
        harness.TimeApi.RaiseUpdate(11);
        harness.TimeApi.Load(10);
        harness.TimeApi.RaiseUpdate(11);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));
        harness.Owner.ClearSession();
        Assert.False(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 10, 12,
            SurvivalStateTimePhase.RealtimeSleep, out _));
        harness.TimeApi.RaiseUpdate(11);

        Assert.Equal(3, harness.Consumer.Received.Count);
        Assert.All(harness.Consumer.Received, minute => Assert.Equal(11, minute.Minute));
    }

    [Fact]
    public void Disconnect_clears_only_that_players_sleep_and_cursor_state()
    {
        var host = new TestPlayer("host");
        var farmhand = new TestPlayer("farmhand");
        var harness = Create(host, farmhand);
        harness.TimeApi.Load(10);
        Assert.True(harness.Owner.BeginSleep("host", "sleep-a"));
        Assert.True(harness.Owner.BeginSleep("farmhand", "sleep-b"));

        Assert.True(harness.Owner.ForgetPlayer("host"));
        Assert.False(harness.Owner.AdvanceSleepInterval(
            "host", host, "sleep-a", 10, 12,
            SurvivalStateTimePhase.RealtimeSleep, out _));
        Assert.True(harness.Owner.AdvanceSleepInterval(
            "farmhand", farmhand, "sleep-b", 10, 12,
            SurvivalStateTimePhase.RealtimeSleep, out var count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Runtime_player_ledger_is_bounded()
    {
        var players = Enumerable.Range(0, SurvivalStateTimeOwner<TestPlayer>.MaxTrackedPlayers + 1)
            .Select(index => new TestPlayer(index.ToString()))
            .ToArray();
        var harness = Create(players);
        harness.TimeApi.Load(0);
        harness.TimeApi.RaiseUpdate(1);

        Assert.Equal(SurvivalStateTimeOwner<TestPlayer>.MaxTrackedPlayers, harness.Consumer.Received.Count);
    }

    private static Harness Create(params TestPlayer[] players)
    {
        var timeApi = new TestTimeApi();
        var owner = new SurvivalStateTimeOwner<TestPlayer>(
            timeApi,
            () => players,
            player => player.Key
        );
        var consumer = new RecordingConsumer();
        owner.Register(consumer);
        return new Harness(timeApi, owner, consumer);
    }

    private sealed record TestPlayer(string Key);

    private sealed class RecordingConsumer : ISurvivalStateTimeBehavior<TestPlayer>
    {
        internal List<(string PlayerKey, long Minute, SurvivalStateTimePhase Phase)> Received { get; } = new();

        public void OnMinute(
            TestPlayer player,
            string playerKey,
            long minute,
            SurvivalStateTimePhase phase
        ) => Received.Add((playerKey, minute, phase));
    }

    private sealed class TestTimeApi : ITimeAPI
    {
        private readonly MinuteTimeTimeline timeline = new();

        public long Time => timeline.Time;
        public List<Action<long>> OnLoad => timeline.OnLoad;
        public List<Action<long>> OnUpdate => timeline.OnUpdate;
        public List<Action<long, long>> OnSync => timeline.OnSync;

        internal void Load(long minute) => timeline.Load(minute);

        internal void Synchronize(long minute) => timeline.Synchronize(minute);

        internal void RaiseUpdate(long minute)
        {
            foreach (var callback in OnUpdate.ToArray())
                callback(minute);
        }
    }

    private sealed record Harness(
        TestTimeApi TimeApi,
        SurvivalStateTimeOwner<TestPlayer> Owner,
        RecordingConsumer Consumer
    );
}
