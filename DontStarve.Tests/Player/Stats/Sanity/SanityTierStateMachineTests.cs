using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityTierStateMachineTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerA = "123456789";
    private const string OwnerB = "223456789";

    public static TheoryData<string, double> StandardRules =>
        new()
        {
            { SanityTierIds.MrSkitts, 0.835d },
            { SanityTierIds.DarkHand, 0.75d },
            { SanityTierIds.DarkWatcher, 0.65d },
            { SanityTierIds.Eyes, 0.6d },
            { SanityTierIds.ShadowCreatures, 0.5d },
            { SanityTierIds.Whispers, 0.45d },
            { SanityTierIds.BeardRabbit, 0.4d },
            { SanityTierIds.Terrorbeak, 0.1d },
        };

    [Fact]
    public void Catalog_freezes_stable_ids_thresholds_and_event_ids()
    {
        var expected = new[]
        {
            ("mr-skitts", 0.835d, 0.835d),
            ("dark-hand", 0.75d, 0.75d),
            ("dark-watcher", 0.65d, 0.65d),
            ("eyes", 0.6d, 0.6d),
            ("shadow-creatures", 0.5d, 0.5d),
            ("whispers", 0.45d, 0.45d),
            ("beard-rabbit", 0.4d, 0.4d),
            ("danger", 0.15d, 0.175d),
            ("terrorbeak", 0.1d, 0.1d),
        };

        Assert.Equal(expected.Length, SanityTierCatalog.Rules.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = SanityTierCatalog.Rules[index];
            Assert.Equal(expected[index].Item1, actual.Id);
            Assert.Equal(expected[index].Item2, actual.EnterRatio);
            Assert.Equal(expected[index].Item3, actual.ExitRatio);
            Assert.Equal(
                $"sanity/tier/{actual.Id}/entered",
                actual.EnteredEventId
            );
            Assert.Equal(
                $"sanity/tier/{actual.Id}/exited",
                actual.ExitedEventId
            );
        }

        Assert.Equal("sanity/system/enabled", SanityStateEventIds.SystemEnabled);
        Assert.Equal("sanity/system/disabled", SanityStateEventIds.SystemDisabled);
        Assert.Equal(
            "sanity/owner/invalidated",
            SanityStateEventIds.OwnerInvalidated
        );
        Assert.Equal("sanity/world/cleanup", SanityStateEventIds.WorldCleanup);
    }

    [Theory]
    [MemberData(nameof(StandardRules))]
    public void Standard_tiers_enter_at_or_below_and_exit_only_above(
        string tierId,
        double threshold
    )
    {
        var machine = new SanityTierStateMachine();
        var maximum = 200d;

        var above = machine.Observe(
            Player(OwnerA, (threshold + 0.001d) * maximum, maximum, 0),
            true
        );
        var equal = machine.Observe(
            Player(OwnerA, threshold * maximum, maximum, 1),
            true
        );
        var below = machine.Observe(
            Player(OwnerA, (threshold - 0.001d) * maximum, maximum, 2),
            true
        );
        var exited = machine.Observe(
            Player(OwnerA, (threshold + 0.001d) * maximum, maximum, 3),
            true
        );

        Assert.DoesNotContain(
            SanityStateEventIds.TierEntered(tierId),
            EventIds(above)
        );
        Assert.Equal(
            new[] { SanityStateEventIds.TierEntered(tierId) },
            TierEventIds(equal, tierId)
        );
        Assert.Empty(TierEventIds(below, tierId));
        Assert.Equal(
            new[] { SanityStateEventIds.TierExited(tierId) },
            TierEventIds(exited, tierId)
        );
    }

    [Fact]
    public void Danger_uses_15_enter_and_strictly_above_17_5_exit_hysteresis()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 36d, 200d, 0), true);

        var entered = machine.Observe(Player(OwnerA, 30d, 200d, 1), true);
        var sixteen = machine.Observe(Player(OwnerA, 32d, 200d, 2), true);
        var exactExitLine = machine.Observe(
            Player(OwnerA, 35d, 200d, 3),
            true
        );
        var exited = machine.Observe(Player(OwnerA, 35.0001d, 200d, 4), true);
        var sixteenAfterExit = machine.Observe(
            Player(OwnerA, 32d, 200d, 5),
            true
        );
        var reentered = machine.Observe(Player(OwnerA, 29.9999d, 200d, 6), true);

        Assert.Equal(
            new[] { "sanity/tier/danger/entered" },
            TierEventIds(entered, SanityTierIds.Danger)
        );
        Assert.Empty(TierEventIds(sixteen, SanityTierIds.Danger));
        Assert.Empty(TierEventIds(exactExitLine, SanityTierIds.Danger));
        Assert.Equal(
            new[] { "sanity/tier/danger/exited" },
            TierEventIds(exited, SanityTierIds.Danger)
        );
        Assert.Empty(TierEventIds(sixteenAfterExit, SanityTierIds.Danger));
        Assert.Equal(
            new[] { "sanity/tier/danger/entered" },
            TierEventIds(reentered, SanityTierIds.Danger)
        );
    }

    [Theory]
    [InlineData(150d)]
    [InlineData(200d)]
    [InlineData(240d)]
    public void Thresholds_are_percentages_for_non_200_maximum(double maximum)
    {
        var machine = new SanityTierStateMachine();

        var result = machine.Observe(
            Player(OwnerA, maximum * 0.15d, maximum, 0),
            true
        );

        Assert.Contains("sanity/tier/danger/entered", EventIds(result));
        Assert.DoesNotContain("sanity/tier/terrorbeak/entered", EventIds(result));
        Assert.True(machine.TryGetOwnerState(OwnerA, out var state));
        Assert.Equal(maximum, state!.Maximum);
    }

    [Fact]
    public void Initial_low_snapshot_publishes_one_deterministic_nested_sequence()
    {
        var machine = new SanityTierStateMachine();

        var result = machine.Observe(Player(OwnerA, 10d, 200d, 7), true);

        var expected = new List<string> { SanityStateEventIds.SystemEnabled };
        expected.AddRange(
            SanityTierCatalog.Rules.Select(rule => rule.EnteredEventId)
        );
        Assert.Equal(expected, EventIds(result));
        Assert.All(
            result.Events.Where(e => e.Kind == SanityStateEventKind.TierEntered),
            stateEvent =>
            {
                Assert.Equal(OwnerA, stateEvent.PlayerKey);
                Assert.Equal(7, stateEvent.Revision);
                Assert.Equal(0.05d, stateEvent.Ratio);
            }
        );
    }

    [Fact]
    public void Duplicate_and_stale_revisions_do_not_repeat_events_and_conflict_fails_closed()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 20d, 200d, 4), true);

        var duplicate = machine.Observe(Player(OwnerA, 20d, 200d, 4), true);
        var stale = machine.Observe(Player(OwnerA, 200d, 200d, 3), true);
        var conflict = machine.Observe(Player(OwnerA, 200d, 200d, 4), true);

        Assert.Equal(SanityTierEvaluationStatus.IgnoredDuplicate, duplicate.Status);
        Assert.Empty(duplicate.Events);
        Assert.Equal(SanityTierEvaluationStatus.IgnoredStale, stale.Status);
        Assert.Empty(stale.Events);
        Assert.Equal(SanityTierEvaluationStatus.Unavailable, conflict.Status);
        Assert.Equal("tier.snapshot-revision-conflict", conflict.Reason);
        Assert.Equal(SanityStateEventIds.OwnerInvalidated, conflict.Events[^1].EventId);
        Assert.True(machine.TryGetOwnerState(OwnerA, out var state));
        Assert.False(state!.IsAvailable);
        Assert.Empty(state.ActiveTierIds);

        var repeatedConflict = machine.Observe(
            Player(OwnerA, 200d, 200d, 4),
            true
        );
        Assert.Equal(SanityTierEvaluationStatus.Unavailable, repeatedConflict.Status);
        Assert.Empty(repeatedConflict.Events);
    }

    [Theory]
    [InlineData(double.NaN, 200d, "tier.current-non-finite")]
    [InlineData(double.PositiveInfinity, 200d, "tier.current-non-finite")]
    [InlineData(10d, double.NaN, "tier.maximum-non-finite")]
    [InlineData(10d, double.PositiveInfinity, "tier.maximum-non-finite")]
    [InlineData(0d, 0d, "tier.maximum-not-positive")]
    [InlineData(-1d, 200d, "tier.current-out-of-range")]
    [InlineData(201d, 200d, "tier.current-out-of-range")]
    public void Invalid_numeric_input_is_unavailable_and_never_activates(
        double current,
        double maximum,
        string expectedReason
    )
    {
        var machine = new SanityTierStateMachine();

        var result = machine.Observe(
            Player(OwnerA, current, maximum, 0),
            true
        );

        Assert.Equal(SanityTierEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.DoesNotContain(
            result.Events,
            stateEvent => stateEvent.Kind == SanityStateEventKind.TierEntered
        );
        Assert.True(machine.TryGetOwnerState(OwnerA, out var state));
        Assert.False(state!.IsAvailable);
        Assert.Empty(state.ActiveTierIds);
    }

    [Fact]
    public void Invalid_input_safely_exits_old_state_and_deduplicates_invalidation()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 10d, 200d, 0), true);

        var invalid = machine.Observe(Player(OwnerA, 10d, 0d, 1), true);
        var duplicate = machine.Observe(Player(OwnerA, 10d, 0d, 1), true);

        var expectedExitIds = SanityTierCatalog.Rules
            .Reverse()
            .Select(rule => rule.ExitedEventId)
            .Append(SanityStateEventIds.OwnerInvalidated)
            .ToArray();
        Assert.Equal(expectedExitIds, EventIds(invalid));
        Assert.Equal(SanityTierEvaluationStatus.Unavailable, duplicate.Status);
        Assert.Empty(duplicate.Events);
    }

    [Fact]
    public void Owners_are_isolated_and_only_the_changed_owner_transitions()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 20d, 200d, 0), true);
        machine.Observe(Player(OwnerB, 200d, 200d, 0), true);

        var ownerBEntered = machine.Observe(Player(OwnerB, 100d, 200d, 1), true);

        Assert.All(
            ownerBEntered.Events.Where(IsTierEvent),
            stateEvent => Assert.Equal(OwnerB, stateEvent.PlayerKey)
        );
        Assert.True(machine.TryGetOwnerState(OwnerA, out var ownerA));
        Assert.True(machine.TryGetOwnerState(OwnerB, out var ownerB));
        Assert.Contains(SanityTierIds.Danger, ownerA!.ActiveTierIds);
        Assert.DoesNotContain(SanityTierIds.Danger, ownerB!.ActiveTierIds);
        Assert.Contains(SanityTierIds.ShadowCreatures, ownerB.ActiveTierIds);
    }

    [Fact]
    public void Disabled_input_exits_once_tracks_latest_snapshot_and_reenters_deterministically()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 20d, 200d, 0), true);

        var disabled = machine.SetSystemEnabled(false);
        var disabledAgain = machine.SetSystemEnabled(false);
        var whileDisabled = machine.Observe(Player(OwnerA, 10d, 200d, 1), false);
        var enabled = machine.SetSystemEnabled(true);

        var expectedDisabled = SanityTierCatalog.Rules
            .Reverse()
            .Select(rule => rule.ExitedEventId)
            .Append(SanityStateEventIds.SystemDisabled)
            .ToArray();
        Assert.Equal(expectedDisabled, EventIds(disabled));
        Assert.Empty(disabledAgain.Events);
        Assert.Equal(SanityTierEvaluationStatus.SystemDisabled, whileDisabled.Status);
        Assert.Empty(whileDisabled.Events);

        var expectedEnabled = new List<string> { SanityStateEventIds.SystemEnabled };
        expectedEnabled.AddRange(
            SanityTierCatalog.Rules.Select(rule => rule.EnteredEventId)
        );
        Assert.Equal(expectedEnabled, EventIds(enabled));
    }

    [Fact]
    public void Owner_invalidation_and_world_cleanup_are_idempotent_lifecycle_events()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(OwnerA, 20d, 200d, 0), true);
        machine.Observe(Player(OwnerB, 100d, 200d, 0), true);

        var invalidated = machine.InvalidateOwner(OwnerA);
        var invalidatedAgain = machine.InvalidateOwner(OwnerA);
        var cleanup = machine.CleanupWorld();
        var cleanupAgain = machine.CleanupWorld();

        Assert.Equal(SanityStateEventIds.OwnerInvalidated, invalidated.Events[^1].EventId);
        Assert.Empty(invalidatedAgain.Events);
        Assert.Equal(SanityStateEventIds.WorldCleanup, cleanup.Events[^1].EventId);
        Assert.All(
            cleanup.Events.Where(IsTierEvent),
            stateEvent => Assert.Equal(OwnerB, stateEvent.PlayerKey)
        );
        Assert.Empty(cleanupAgain.Events);
    }

    [Fact]
    public void Host_service_wiring_publishes_initial_change_and_cleanup_sequences_once()
    {
        var data = SanitySaveDataCodec.NewData(OwnerA, 20d, 200d);
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test",
            20d,
            data
        );
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;

        Assert.True(
            service.BeginHostSession(Session, persistence, OwnerA, out var reason),
            reason
        );
        var initialCount = published.Count;
        service.GetCurrent(OwnerA);
        var restored = service.Set(
            OwnerA,
            200d,
            SanityChangeSource.Administration
        );
        var afterRestore = published.Count;
        service.Set(OwnerA, 200d, SanityChangeSource.Administration);
        service.ClearSession();

        Assert.Equal(9, published.Take(initialCount).Count(IsTierEvent));
        Assert.Equal(initialCount, published.Take(initialCount).Count());
        Assert.Equal(SanityChangeStatus.Applied, restored.Status);
        Assert.Equal(9, published.Skip(initialCount).Take(afterRestore - initialCount).Count(IsTierEvent));
        Assert.Equal(afterRestore + 1, published.Count);
        Assert.Equal(SanityStateEventIds.WorldCleanup, published[^1].EventId);
    }

    [Fact]
    public void Host_service_repeated_reads_are_event_free_and_peer_forget_invalidates_once()
    {
        var data = SanitySaveDataCodec.NewData(OwnerA, 20d, 200d);
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test",
            20d,
            data
        );
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;
        Assert.True(
            service.BeginHostSession(Session, persistence, OwnerA, out var reason),
            reason
        );
        var afterLoad = published.Count;

        service.GetCurrent(OwnerA);
        service.GetMaximum(OwnerA);
        var afterReads = published.Count;
        service.ForgetPeer(OwnerA);
        var afterForget = published.Count;
        service.ForgetPeer(OwnerA);
        var beforeReconnect = published.Count;
        Assert.True(
            service.TryEnsureHostPlayer(OwnerA, out _, out var reconnectReason),
            reconnectReason
        );

        Assert.Equal(afterLoad, afterReads);
        var expectedForget = SanityTierCatalog.Rules
            .Reverse()
            .Select(rule => rule.ExitedEventId)
            .Append(SanityStateEventIds.OwnerInvalidated)
            .ToArray();
        Assert.Equal(
            expectedForget,
            published
                .Skip(afterReads)
                .Take(afterForget - afterReads)
                .Select(stateEvent => stateEvent.EventId)
        );
        Assert.Equal(afterForget, beforeReconnect);
        Assert.Equal(
            SanityTierCatalog.Rules.Select(rule => rule.EnteredEventId),
            published
                .Skip(beforeReconnect)
                .Select(stateEvent => stateEvent.EventId)
        );
        Assert.True(service.TryGetTierState(OwnerA, out _));
    }

    [Fact]
    public void Client_service_wiring_only_publishes_for_applied_authoritative_snapshots()
    {
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;
        Assert.True(service.BeginClientSession(OwnerA, out var reason), reason);

        var low = Full(Player(OwnerA, 20d, 200d, 4));
        var applied = service.ApplyClientSnapshot(low);
        var afterApplied = published.Count;
        var duplicate = service.ApplyClientSnapshot(low);
        var restored = service.ApplyClientSnapshot(
            Delta(Player(OwnerA, 200d, 200d, 5))
        );

        Assert.Equal(SanitySnapshotApplyStatus.AppliedFull, applied.Status);
        Assert.Equal(SanitySnapshotApplyStatus.AppliedFull, duplicate.Status);
        Assert.Equal(afterApplied, published.Take(afterApplied).Count());
        Assert.Equal(9, published.Take(afterApplied).Count(IsTierEvent));
        Assert.Equal(SanitySnapshotApplyStatus.AppliedDelta, restored.Status);
        Assert.Equal(9, published.Skip(afterApplied).Count(IsTierEvent));
    }

    private static bool IsTierEvent(SanityStateEvent stateEvent)
    {
        return stateEvent.Kind
            is SanityStateEventKind.TierEntered or SanityStateEventKind.TierExited;
    }

    private static string[] EventIds(SanityTierEvaluationResult result)
    {
        return result.Events.Select(stateEvent => stateEvent.EventId).ToArray();
    }

    private static string[] TierEventIds(
        SanityTierEvaluationResult result,
        string tierId
    )
    {
        return result.Events
            .Where(stateEvent => string.Equals(stateEvent.TierId, tierId))
            .Select(stateEvent => stateEvent.EventId)
            .ToArray();
    }

    private static SanityPlayerSnapshot Player(
        string playerKey,
        double current,
        double maximum,
        long revision
    )
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = playerKey,
            Current = current,
            Maximum = maximum,
            Revision = revision,
        };
    }

    private static SanitySnapshotMessage Full(
        params SanityPlayerSnapshot[] players
    )
    {
        return new SanitySnapshotMessage
        {
            SessionId = Session,
            IsFull = true,
            Players = players.ToList(),
        };
    }

    private static SanitySnapshotMessage Delta(SanityPlayerSnapshot player)
    {
        return new SanitySnapshotMessage
        {
            SessionId = Session,
            IsFull = false,
            Players = new List<SanityPlayerSnapshot> { player },
        };
    }
}
