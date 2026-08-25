using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanitySystemLifecycleCoordinatorTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";
    private const string OwnerA = "1";
    private const string OwnerB = "2";

    [Fact]
    public void Startup_disabled_initializes_event_chain_before_loading_frozen_value()
    {
        var service = Service();
        var lifecycle = new SanitySystemLifecycleCoordinator(service);
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;

        lifecycle.Initialize();
        lifecycle.ApplyConfiguredState(false);

        Assert.Equal(
            new[]
            {
                SanityStateEventIds.SystemEnabled,
                SanityStateEventIds.SystemDisabled,
            },
            published.Select(stateEvent => stateEvent.EventId)
        );
        BeginHost(service, Persistence((OwnerA, 40d, 200d)));

        Assert.False(lifecycle.IsEnabled);
        Assert.Equal(40d, service.GetCurrent(OwnerA));
        Assert.True(service.TryGetTierState(OwnerA, out var tier));
        Assert.Empty(Assert.IsType<SanityTierOwnerStateSnapshot>(tier).ActiveTierIds);
        var budget = service.EvaluateShadowBudget(OwnerA, 0, 0);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Inactive, budget.Status);
        Assert.Equal("budget.owner-untracked", budget.Reason);
        Assert.Null(budget.Permit);
    }

    [Fact]
    public void Warp_notifies_projection_boundary_without_resetting_tier_state_machine()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 20d, 200d)));
        var sequence = new List<string>();
        lifecycle.WorldBoundaryStarting += boundary =>
            sequence.Add($"world:{boundary}");
        lifecycle.StateEventPublished += stateEvent =>
            sequence.Add($"state:{stateEvent.Kind}");

        lifecycle.HandleWorldBoundary(SanityWorldBoundary.Warp);

        Assert.Equal(new[] { "world:Warp" }, sequence);
    }

    [Fact]
    public void Projection_event_owner_hook_runs_before_owner_tier_invalidation()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 20d, 200d)));
        var sequence = new List<string>();
        lifecycle.EventOwnerCoverageChanged += change =>
            sequence.Add(
                $"owner:{change.Key.PlayerKey}/{change.Key.ScreenId}:{change.Active}"
            );
        lifecycle.StateEventPublished += stateEvent =>
            sequence.Add($"state:{stateEvent.Kind}");

        Assert.True(
            lifecycle.TrySetEventCoverage(
                Coverage(OwnerA),
                true,
                out var reason
            ),
            reason
        );

        Assert.Equal("owner:1/0:True", sequence[0]);
        Assert.Contains(
            sequence.Skip(1),
            value => value == "state:OwnerInvalidated"
        );
    }

    [Fact]
    public void Event_owner_hook_observes_post_edge_player_coverage()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 20d, 200d)));
        var observed = new List<(bool Edge, bool Coverage)>();
        lifecycle.EventOwnerCoverageChanged += change =>
            observed.Add(
                (
                    change.Active,
                    lifecycle.IsEventCoverageActiveForPlayer(change.Key.PlayerKey)
                )
            );
        var coverage = Coverage(OwnerA);

        Assert.True(lifecycle.TrySetEventCoverage(coverage, true, out _));
        Assert.True(lifecycle.TrySetEventCoverage(coverage, false, out _));

        Assert.Equal(new[] { (true, true), (false, false) }, observed);
    }

    [Fact]
    public void Projection_session_hook_preserves_returned_title_before_clear()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 20d, 200d)));
        var sequence = new List<string>();
        lifecycle.SessionClearing += boundary =>
            sequence.Add($"session:{boundary}");
        lifecycle.StateEventPublished += stateEvent =>
            sequence.Add($"state:{stateEvent.Kind}");

        lifecycle.ClearSession(SanitySessionBoundary.ReturnedToTitle);

        Assert.Equal("session:ReturnedToTitle", sequence[0]);
        Assert.Contains(sequence.Skip(1), value => value == "state:WorldCleanup");
    }

    [Fact]
    public void Tier_observation_publishes_higher_revision_without_requiring_a_tier_edge()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 120d, 200d)));
        var observed = new List<SanityTierOwnerStateSnapshot>();
        var tierEvents = new List<SanityStateEvent>();
        lifecycle.TierStateObserved += observed.Add;
        lifecycle.StateEventPublished += tierEvents.Add;

        var result = service.Change(OwnerA, -1d, SanityChangeSource.Night);

        Assert.Equal(SanityChangeStatus.Applied, result.Status);
        var snapshot = Assert.Single(observed);
        Assert.Equal(119d, snapshot.Current);
        Assert.Equal(1, snapshot.Revision);
        Assert.Empty(tierEvents);
    }

    [Fact]
    public void Disabled_host_freezes_every_change_source_and_administrative_set()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 80d, 200d)));
        var hostChanges = new List<SanityStateChanged>();
        service.HostStateChanged += hostChanges.Add;
        lifecycle.ApplyConfiguredState(false);
        Assert.True(service.TryGetSnapshot(OwnerA, out var before));

        foreach (
            var source in Enum.GetValues<SanityChangeSource>()
                .Where(source => source != SanityChangeSource.Unknown)
        )
        {
            var result = service.Change(OwnerA, 10d, source, "food-16");
            Assert.Equal(SanityChangeStatus.NoChange, result.Status);
            Assert.Equal("sanity-system-disabled", result.Reason);
            Assert.Equal(before.Current, Assert.IsType<SanityPlayerSnapshot>(result.Snapshot).Current);
        }

        var set = service.Set(OwnerA, 5d, SanityChangeSource.Administration);
        Assert.Equal(SanityChangeStatus.NoChange, set.Status);
        Assert.Equal("sanity-system-disabled", set.Reason);
        Assert.True(service.TryGetSnapshot(OwnerA, out var after));
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Empty(hostChanges);
    }

    [Fact]
    public void Disabled_client_food_keeps_snapshot_and_queues_no_request()
    {
        var service = Service();
        var lifecycle = new SanitySystemLifecycleCoordinator(service);
        lifecycle.Initialize();
        lifecycle.ApplyConfiguredState(false);
        Assert.True(service.BeginClientSession(OwnerA, out var reason), reason);
        Assert.Equal(
            SanitySnapshotApplyStatus.AppliedFull,
            service.ApplyClientSnapshot(
                Full(Player(OwnerA, 65d, 200d, 7))
            ).Status
        );
        var requests = new List<SanityChangeRequest>();
        service.ClientRequestCreated += requests.Add;

        var result = service.Change(
            OwnerA,
            30d,
            SanityChangeSource.Food,
            "16"
        );

        Assert.Equal(SanityChangeStatus.NoChange, result.Status);
        Assert.Equal("sanity-system-disabled", result.Reason);
        Assert.Equal(65d, service.GetCurrent(OwnerA));
        Assert.Empty(requests);
    }

    [Fact]
    public void Reenable_rebuilds_tiers_and_budget_from_preserved_value_without_refill()
    {
        var service = Service();
        var lifecycle = new SanitySystemLifecycleCoordinator(service);
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;
        lifecycle.Initialize();
        lifecycle.ApplyConfiguredState(false);
        BeginHost(service, Persistence((OwnerA, 20d, 200d)));
        Assert.True(service.TryGetSnapshot(OwnerA, out var before));
        published.Clear();

        lifecycle.ApplyConfiguredState(true);

        Assert.True(service.TryGetSnapshot(OwnerA, out var after));
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(SanityStateEventIds.SystemEnabled, published[0].EventId);
        Assert.Equal(
            SanityTierCatalog.Rules.Select(rule => rule.EnteredEventId),
            published.Skip(1).Select(stateEvent => stateEvent.EventId)
        );
        var budget = service.EvaluateShadowBudget(OwnerA, 0, 0);
        Assert.NotEqual(SanityShadowBudgetEvaluationStatus.SystemDisabled, budget.Status);
        Assert.Equal(SanityShadowPoolTier.Hostile10, budget.PoolTier);
        Assert.Equal(2, budget.Cap);
    }

    [Fact]
    public void Runtime_toggle_is_idempotent_and_preserves_value_revision()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 20d, 200d)));
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;
        Assert.True(service.TryGetSnapshot(OwnerA, out var before));

        lifecycle.ApplyConfiguredState(false);
        lifecycle.ApplyConfiguredState(false);
        lifecycle.ApplyConfiguredState(true);
        lifecycle.ApplyConfiguredState(true);

        Assert.True(service.TryGetSnapshot(OwnerA, out var after));
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Single(
            published.Where(stateEvent =>
                stateEvent.EventId == SanityStateEventIds.SystemDisabled
            )
        );
        Assert.Single(
            published.Where(stateEvent =>
                stateEvent.EventId == SanityStateEventIds.SystemEnabled
            )
        );
    }

    [Fact]
    public void Warp_cleans_and_rebuilds_each_owner_without_changing_values()
    {
        AssertWorldBoundary(SanityWorldBoundary.Warp);
    }

    [Fact]
    public void Day_start_cleans_and_rebuilds_each_owner_without_changing_values()
    {
        AssertWorldBoundary(SanityWorldBoundary.DayStarted);
    }

    private static void AssertWorldBoundary(SanityWorldBoundary boundary)
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(
            service,
            Persistence((OwnerA, 20d, 200d), (OwnerB, 90d, 200d))
        );
        Assert.True(service.TryEnsureHostPlayer(OwnerB, out _, out var reason), reason);
        Assert.True(service.TryGetSnapshot(OwnerA, out var beforeA));
        Assert.True(service.TryGetSnapshot(OwnerB, out var beforeB));
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;

        lifecycle.HandleWorldBoundary(boundary);

        Assert.True(service.TryGetSnapshot(OwnerA, out var afterA));
        Assert.True(service.TryGetSnapshot(OwnerB, out var afterB));
        Assert.Equal((beforeA.Current, beforeA.Revision), (afterA.Current, afterA.Revision));
        Assert.Equal((beforeB.Current, beforeB.Revision), (afterB.Current, afterB.Revision));
        if (boundary == SanityWorldBoundary.Warp)
        {
            Assert.Empty(published);
            Assert.True(service.TryGetShadowBudgetState(OwnerA, out _));
            Assert.True(service.TryGetShadowBudgetState(OwnerB, out _));
            return;
        }
        Assert.Single(
            published.Where(stateEvent =>
                stateEvent.EventId == SanityStateEventIds.WorldCleanup
            )
        );
        Assert.Single(
            published.Where(stateEvent =>
                stateEvent.EventId == SanityStateEventIds.SystemEnabled
            )
        );
        Assert.Contains(published, stateEvent => stateEvent.PlayerKey == OwnerA);
        Assert.Contains(published, stateEvent => stateEvent.PlayerKey == OwnerB);
        Assert.True(service.TryGetShadowBudgetState(OwnerA, out _));
        Assert.True(service.TryGetShadowBudgetState(OwnerB, out _));
    }

    [Fact]
    public void Event_coverage_freezes_base_and_rebuilds_owner_from_preserved_value()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 100d, 200d)));
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;

        var coverage = Coverage(OwnerA);
        Assert.True(
            lifecycle.TrySetEventCoverage(coverage, true, out var startReason),
            startReason
        );
        Assert.False(
            lifecycle.TrySetEventCoverage(coverage, true, out var duplicateStart)
        );
        Assert.Equal("event-coverage-already-active", duplicateStart);
        var afterCleanup = published.Count;
        var changed = service.Set(OwnerA, 20d, SanityChangeSource.Administration);

        Assert.Equal(SanityChangeStatus.NoChange, changed.Status);
        Assert.Equal("sanity-change-frozen-by-effective-overlay", changed.Reason);
        Assert.Equal(afterCleanup, published.Count);
        Assert.Equal(100d, service.GetCurrent(OwnerA));
        Assert.Equal(
            100d,
            Assert.Single(service.CaptureSaveInputs()).Current
        );
        Assert.True(
            lifecycle.TrySetEventCoverage(coverage, false, out var endReason),
            endReason
        );
        Assert.False(
            lifecycle.TrySetEventCoverage(coverage, false, out var duplicateEnd)
        );
        Assert.Equal("event-coverage-already-inactive", duplicateEnd);
        Assert.Equal(100d, service.GetCurrent(OwnerA));
        Assert.True(service.TryGetTierState(OwnerA, out _));
        Assert.Contains(
            published.Skip(afterCleanup),
            stateEvent => stateEvent.PlayerKey == OwnerA
        );
    }

    [Fact]
    public void Clear_session_emits_cleanup_and_allows_next_save_to_load_its_own_value()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 25d, 200d)));
        var published = new List<SanityStateEvent>();
        service.TierStateEventPublished += published.Add;

        lifecycle.ClearSession();

        Assert.False(service.HasActiveSession);
        Assert.Contains(
            published,
            stateEvent => stateEvent.EventId == SanityStateEventIds.WorldCleanup
        );
        Assert.False(service.TryGetTierState(OwnerA, out _));
        Assert.False(service.TryGetShadowBudgetState(OwnerA, out _));
        BeginHost(service, Persistence((OwnerA, 175d, 200d)));
        Assert.Equal(175d, service.GetCurrent(OwnerA));
    }

    [Fact]
    public void Split_screen_owners_remain_distinct_across_disabled_round_trip()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(
            service,
            Persistence((OwnerA, 20d, 200d), (OwnerB, 180d, 200d))
        );
        Assert.True(service.TryEnsureHostPlayer(OwnerB, out _, out var reason), reason);

        lifecycle.ApplyConfiguredState(false);
        service.Change(OwnerA, 50d, SanityChangeSource.Food);
        service.Change(OwnerB, -50d, SanityChangeSource.Night);
        lifecycle.ApplyConfiguredState(true);

        Assert.Equal(20d, service.GetCurrent(OwnerA));
        Assert.Equal(180d, service.GetCurrent(OwnerB));
        Assert.True(service.TryGetTierState(OwnerA, out var tierA));
        Assert.True(service.TryGetTierState(OwnerB, out var tierB));
        Assert.NotEqual(
            Assert.IsType<SanityTierOwnerStateSnapshot>(tierA).ActiveTierIds.Count,
            Assert.IsType<SanityTierOwnerStateSnapshot>(tierB).ActiveTierIds.Count
        );
    }

    [Fact]
    public void Invalid_numeric_input_remains_rejected_while_disabled()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 80d, 200d)));
        lifecycle.ApplyConfiguredState(false);

        var change = service.Change(
            OwnerA,
            double.NaN,
            SanityChangeSource.Food
        );
        var set = service.Set(
            OwnerA,
            double.PositiveInfinity,
            SanityChangeSource.Administration
        );

        Assert.Equal(SanityChangeStatus.Rejected, change.Status);
        Assert.Equal("sanity-delta-must-be-finite", change.Reason);
        Assert.Equal(SanityChangeStatus.Rejected, set.Status);
        Assert.Equal("requested-sanity-value-must-be-finite", set.Reason);
        Assert.Equal(80d, service.GetCurrent(OwnerA));
    }

    [Fact]
    public void In_flight_host_food_request_is_validated_but_applies_zero_when_disabled()
    {
        var service = Service();
        var lifecycle = ReadyLifecycle(service, Persistence((OwnerA, 80d, 200d)));
        lifecycle.ApplyConfiguredState(false);
        Assert.True(service.TryGetSnapshot(OwnerA, out var snapshot));
        var request = new SanityChangeRequest
        {
            SessionId = Session,
            PlayerKey = OwnerA,
            Source = SanityChangeSource.Food,
            InteractionId = "16",
            Nonce = 1,
            ExpectedRevision = snapshot.Revision,
        };

        var result = service.HandleHostRequest(
            request,
            1,
            new AcceptedTruthSource(30d),
            1000
        );

        Assert.True(result.Accepted);
        Assert.False(result.NeedsSnapshot);
        Assert.Equal("sanity-system-disabled", result.Reason);
        Assert.Equal(80d, Assert.IsType<SanityPlayerSnapshot>(result.Snapshot).Current);
        Assert.Equal(80d, service.GetCurrent(OwnerA));
    }

    private static SanityChangeService Service()
    {
        return new SanityChangeService(
            new DefaultSanityMaximumProvider(),
            new DefaultIntensityProvider()
        );
    }

    private static SanityEventCoverageKey Coverage(
        string owner,
        int screenId = 0
    )
    {
        return new SanityEventCoverageKey(owner, screenId, Session);
    }

    private static SanitySystemLifecycleCoordinator ReadyLifecycle(
        SanityChangeService service,
        SanityPersistenceResult persistence
    )
    {
        var lifecycle = new SanitySystemLifecycleCoordinator(service);
        lifecycle.Initialize();
        BeginHost(service, persistence);
        return lifecycle;
    }

    private static void BeginHost(
        SanityChangeService service,
        SanityPersistenceResult persistence
    )
    {
        Assert.True(
            service.BeginHostSession(Session, persistence, OwnerA, out var reason),
            reason
        );
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

        return new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test-persistence",
            data.Players[OwnerA].Current,
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

    private static SanitySnapshotMessage Full(params SanityPlayerSnapshot[] players)
    {
        return new SanitySnapshotMessage
        {
            SessionId = Session,
            IsFull = true,
            Players = players.ToList(),
        };
    }

    private sealed class DefaultIntensityProvider : ISanityMonsterIntensityProvider
    {
        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(
                true,
                SanityMonsterIntensityIds.Default,
                "test-intensity"
            );
        }
    }

    private sealed class AcceptedTruthSource : ISanityRequestTruthSource
    {
        private readonly double delta;

        internal AcceptedTruthSource(double delta)
        {
            this.delta = delta;
        }

        public SanityRequestTruth Resolve(
            SanityChangeRequest request,
            long senderPlayerId
        )
        {
            return SanityRequestTruth.Accepted(delta);
        }
    }
}
