using DontStarve.Time;
using Xunit;

namespace DontStarve.Tests.Time;

public sealed class MinuteTimeTimelineTests
{
    [Fact]
    public void PositiveSyncPublishesExclusiveInclusiveMinutesBeforeSingleSync()
    {
        var timeline = new MinuteTimeTimeline();
        var events = new List<string>();
        timeline.OnLoad.Add(time => events.Add($"load:{time}:{timeline.Time}"));
        timeline.OnUpdate.Add(time => events.Add($"update:{time}:{timeline.Time}"));
        timeline.OnSync.Add(
            (time, delta) => events.Add($"sync:{time}:{delta}:{timeline.Time}")
        );

        timeline.Load(100);
        timeline.Synchronize(105);

        Assert.Equal(
            new[]
            {
                "load:100:100",
                "update:101:101",
                "update:102:102",
                "update:103:103",
                "update:104:104",
                "update:105:105",
                "sync:105:5:105",
            },
            events
        );
    }

    [Theory]
    [InlineData(105, 105, 0)]
    [InlineData(105, 102, -3)]
    public void EqualOrBackwardSyncDoesNotPublishForwardUpdates(
        long oldTime,
        long syncTime,
        long expectedDelta
    )
    {
        var timeline = new MinuteTimeTimeline();
        var updates = new List<long>();
        var syncs = new List<(long Time, long Delta)>();
        timeline.OnUpdate.Add(updates.Add);
        timeline.OnSync.Add((time, delta) => syncs.Add((time, delta)));

        timeline.Load(oldTime);
        timeline.Synchronize(syncTime);

        Assert.Empty(updates);
        Assert.Equal(new[] { (syncTime, expectedDelta) }, syncs);
        Assert.Equal(syncTime, timeline.Time);
    }

    [Fact]
    public void PauseAndRepeatedUpdateTickDoNotDuplicateMinute()
    {
        var timeline = new MinuteTimeTimeline();
        var updates = new List<long>();
        timeline.OnUpdate.Add(updates.Add);
        timeline.Load(600);

        Assert.False(timeline.TryAdvanceWithinBlock(false, 5));
        Assert.True(timeline.TryAdvanceWithinBlock(true, 1));
        Assert.False(timeline.TryAdvanceWithinBlock(true, 1));
        Assert.True(timeline.TryAdvanceWithinBlock(true, 2));
        for (var minute = 3; minute <= 9; minute++)
            Assert.True(timeline.TryAdvanceWithinBlock(true, 10));
        Assert.False(timeline.TryAdvanceWithinBlock(true, 10));
        timeline.Synchronize(610);

        Assert.Equal(Enumerable.Range(601, 10).Select(value => (long)value), updates);
        Assert.Equal(610, timeline.Time);
    }

    [Theory]
    [InlineData(608, 612)]
    [InlineData(1438, 1442)]
    public void PositiveSyncCrossesBlockAndDayBoundaries(long oldTime, long syncTime)
    {
        var timeline = new MinuteTimeTimeline();
        var updates = new List<long>();
        timeline.OnUpdate.Add(updates.Add);
        timeline.Load(oldTime);

        timeline.Synchronize(syncTime);

        Assert.Equal(
            Enumerable.Range(1, checked((int)(syncTime - oldTime)))
                .Select(offset => oldTime + offset),
            updates
        );
    }

    [Fact]
    public void RepresentativeConsumersReceiveOneCallbackPerMinuteWithoutDoubleDrain()
    {
        var timeline = new MinuteTimeTimeline();
        var hunger = 150d;
        var sanity = 200d;
        var hungerUpdates = 0;
        var sanityUpdates = 0;
        var buffUpdates = 0;
        var displayUpdates = 0;

        timeline.OnUpdate.Add(_ =>
        {
            hungerUpdates++;
            hunger -= 0.052d;
        });
        timeline.OnUpdate.Add(_ =>
        {
            sanityUpdates++;
            sanity -= 0.0588d;
        });
        timeline.OnUpdate.Add(_ => buffUpdates++);
        timeline.OnUpdate.Add(_ => displayUpdates++);

        timeline.Load(100);
        timeline.Synchronize(110);

        Assert.Equal(10, hungerUpdates);
        Assert.Equal(10, sanityUpdates);
        Assert.Equal(10, buffUpdates);
        Assert.Equal(10, displayUpdates);
        Assert.Equal(149.48d, hunger, 10);
        Assert.Equal(199.412d, sanity, 10);
    }

    [Fact]
    public void RewindAwareConsumerKeepsPersistedWaitAndDoesNotReplayEffects()
    {
        var timeline = new MinuteTimeTimeline();
        var consumer = new RewindAwareConsumer(wait: 2);
        timeline.OnUpdate.Add(consumer.Update);
        timeline.OnSync.Add(consumer.Sync);
        timeline.Load(100);

        timeline.Synchronize(103);
        Assert.Equal(0, consumer.Wait);
        Assert.Equal(1, consumer.AppliedUpdates);

        timeline.Synchronize(100);
        Assert.Equal(3, consumer.Wait);

        timeline.Synchronize(103);
        Assert.Equal(0, consumer.Wait);
        Assert.Equal(1, consumer.AppliedUpdates);
    }

    [Fact]
    public void CallbackFailureIsDiagnosedOnceAndNeverRetriesTheMinute()
    {
        var diagnostics = new List<string>();
        var timeline = new MinuteTimeTimeline(diagnostics.Add);
        var failingCalls = 0;
        var healthyCalls = 0;
        var syncCalls = 0;
        timeline.OnUpdate.Add(_ =>
        {
            failingCalls++;
            throw new InvalidOperationException("synthetic failure");
        });
        timeline.OnUpdate.Add(_ => healthyCalls++);
        timeline.OnSync.Add((_, _) => syncCalls++);
        timeline.Load(10);

        timeline.Synchronize(12);

        Assert.Equal(2, failingCalls);
        Assert.Equal(2, healthyCalls);
        Assert.Equal(1, syncCalls);
        Assert.Single(diagnostics);
        Assert.Contains("callback failed and was not retried", diagnostics[0]);
        Assert.Equal(12, timeline.Time);
    }

    [Fact]
    public void LargeDeltaIsDiagnosedWithoutDiscardingMinutes()
    {
        var diagnostics = new List<string>();
        var timeline = new MinuteTimeTimeline(diagnostics.Add);
        var updates = 0;
        timeline.OnUpdate.Add(_ => updates++);
        timeline.Load(0);

        timeline.Synchronize(1441);

        Assert.Equal(1441, updates);
        Assert.Equal(1441, timeline.Time);
        Assert.Single(diagnostics);
        Assert.Contains("no minutes were discarded", diagnostics[0]);
    }

    private sealed class RewindAwareConsumer
    {
        internal RewindAwareConsumer(long wait)
        {
            Wait = wait;
        }

        internal long Wait { get; private set; }

        internal int AppliedUpdates { get; private set; }

        internal void Update(long _)
        {
            if (Wait > 0)
            {
                Wait--;
                return;
            }

            AppliedUpdates++;
        }

        internal void Sync(long _, long delta)
        {
            if (delta < 0)
                Wait += -delta;
        }
    }
}
