using DontStarve.Config;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Tests.Config;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class CreeperFearOwnerBudgetAndLifecycleTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerA = "101";
    private const string OwnerB = "202";
    private const long PeerA = 101;
    private static readonly Lazy<RuntimeProfiles> ShippedProfiles = new(
        ResolveShippedProfiles
    );

    public static IEnumerable<object[]> DensityIds =>
        SanityShadowBudgetPolicyCatalog.Policies.Select(policy =>
            new object[] { policy.IntensityId }
        );

    [Fact]
    public void Fifty_percent_creeper_projection_is_exact_owner_context_only_and_never_transport_state()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var location = new object();
        var owner = OwnerContext(OwnerA, screenId: 0, location, "Farm");
        var observer = OwnerContext(OwnerB, screenId: 0, location, "Farm");
        var otherScreen = OwnerContext(OwnerA, screenId: 1, location, "Farm");
        var otherLocation = OwnerContext(OwnerA, screenId: 0, new object(), "Mine");
        var instance = AddLocal(index, owner, "owner-local-creeper");

        Assert.True(index.TryGetContextInstances(owner, out var visible));
        Assert.Equal(instance, Assert.Single(visible!));
        Assert.False(index.TryGetContextInstances(observer, out _));
        Assert.False(index.TryGetContextInstances(otherScreen, out _));
        Assert.False(index.TryGetContextInstances(otherLocation, out _));

        var projectionHost = Contract(
            "ShadowProjection",
            "SmapiHarmlessProjectionHost.cs"
        );
        var multiplayer = Contract(
            "HostileShadowAuthority",
            "SmapiHostileShadowMultiplayerCoordinator.cs"
        );
        var modEntry = Contract("ShadowProjection", "ModEntry.cs");
        Assert.Contains(
            "TryGetCurrentOwner(out _, out _, out var owner)",
            projectionHost,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "shadowCoordinator.Index.TryGetContextInstances(",
            projectionHost,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "helper.Multiplayer.SendMessage",
            projectionHost,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "intent.PlayerKey,\n                SanityPlayerKey.FromUniqueMultiplayerId(",
            multiplayer.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
        Assert.True(
            modEntry.IndexOf(
                "_hostileShadowHost = new SmapiHostileShadowHost",
                StringComparison.Ordinal
            )
                < modEntry.IndexOf(
                    "_harmlessProjectionHost = new SmapiHarmlessProjectionHost",
                    StringComparison.Ordinal
                )
        );
        AssertPair(projectionHost, "GameLoop.UpdateTicked", "OnUpdateTicked");
        AssertPair(projectionHost, "Display.RenderedWorld", "OnRenderedWorld");
        AssertPair(projectionHost, "GameLoop.DayEnding", "OnDayEnding");

        var sharedProperties = typeof(ShadowStateSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("CorrelationId", sharedProperties);
        Assert.DoesNotContain("ScreenId", sharedProperties);
        Assert.DoesNotContain("OwnerLocal", sharedProperties);
    }

    [Theory]
    [MemberData(nameof(DensityIds))]
    public void Fifteen_percent_converts_local_creeper_when_capacity_exists_and_restores_it_when_disabled(
        string intensityId
    )
    {
        Assert.Equal(6, SanityShadowBudgetPolicyCatalog.Policies.Count);
        Assert.True(
            SanityShadowBudgetPolicyCatalog.TryGet(intensityId, out var policy)
        );
        var harness = new Harness(intensityId);
        harness.Observe(OwnerA, current: 50d, revision: 1, gameMinute: 0);
        var local = harness.AddLocal(OwnerA, "Farm", "fifty-local-creeper");

        var danger = harness.Observe(
            OwnerA,
            current: 15d,
            revision: 2,
            gameMinute: 100
        );

        Assert.Contains(danger.Events, IsDangerEnter);
        Assert.True(harness.Sink.LocalPoolWasEmptyBeforeEverySubmission);
        var spawn = Assert.Single(harness.Sink.Results);
        Assert.Equal(policy!.BaseCap, spawn.Cap);
        Assert.Equal(policy.BaseCap > 0, spawn.Spawned);
        Assert.Equal(policy.BaseCap > 0 ? 1 : 0, harness.Authority.Count);

        if (policy.BaseCap == 0)
        {
            Assert.False(local.IsCleanedUp);
            Assert.Null(local.CleanupReason);
            Assert.Equal(1, harness.Index.CountForOwner(OwnerA));
            return;
        }

        Assert.True(local.IsCleanedUp);
        Assert.Equal(
            HarmlessProjectionCleanupReason.ConversionRequested,
            local.CleanupReason
        );
        Assert.Equal(0, harness.Index.CountForOwner(OwnerA));

        var budget = harness.Governor.Evaluate(
            OwnerA,
            gameMinute: 100,
            harness.Authority.GetOwnerOccupancy(OwnerA)
        );
        Assert.Equal(policy.IntervalMinutes, budget.IntervalMinutes);
        Assert.Equal(policy.BaseCap, budget.Cap);

        var shared = Assert.Single(harness.Authority.CreateFullSnapshot().Entities);
        Assert.Equal(OwnerA, shared.OwnerPlayerKey);
        Assert.Equal("Farm", shared.LocationId);
        Assert.Equal(ShadowMonsterAssetBindingIds.CreeperFear, shared.AssetBindingId);
        Assert.Equal(Harness.HostSpawnX, shared.PositionX);
        Assert.NotEqual(local.SpawnWorldPixel.X, shared.PositionX);
    }

    [Fact]
    public void Two_owners_convert_independently_without_removing_or_reassigning_the_other_owner()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.Observe(OwnerB, 50d, revision: 1, gameMinute: 0);
        var localA = harness.AddLocal(OwnerA, "Farm", "owner-a-local");
        var localB = harness.AddLocal(OwnerB, "Mine", "owner-b-local");

        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 10);

        Assert.True(localA.IsCleanedUp);
        Assert.False(localB.IsCleanedUp);
        Assert.Equal(0, harness.Index.CountForOwner(OwnerA));
        Assert.Equal(1, harness.Index.CountForOwner(OwnerB));
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(0, harness.Authority.GetOwnerOccupancy(OwnerB));

        harness.Observe(OwnerB, 15d, revision: 2, gameMinute: 10);

        Assert.True(localB.IsCleanedUp);
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerB));
        var states = harness.Authority.CreateFullSnapshot().Entities;
        Assert.Equal(2, states.Count);
        Assert.Contains(states, state => state.OwnerPlayerKey == OwnerA && state.LocationId == "Farm");
        Assert.Contains(states, state => state.OwnerPlayerKey == OwnerB && state.LocationId == "Mine");
    }

    [Fact]
    public void Danger_hysteresis_preserves_shared_entity_above_seventeen_point_five_until_retreat()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.AddLocal(OwnerA, "Farm", "hysteresis-local");
        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 10);
        Assert.Equal(1, harness.Authority.Count);

        var exactExit = harness.Observe(
            OwnerA,
            17.5d,
            revision: 3,
            gameMinute: 20
        );
        Assert.DoesNotContain(exactExit.Events, IsDangerExit);
        Assert.Equal(1, harness.Authority.Count);

        var aboveExit = harness.Observe(
            OwnerA,
            17.5001d,
            revision: 4,
            gameMinute: 21
        );
        Assert.Contains(aboveExit.Events, IsDangerExit);
        Assert.Equal(1, harness.Authority.Count);
        var locked = harness.Coordinator.UpdateOwner(
            harness.GetOwnerContext(OwnerA),
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 80,
            elapsedMilliseconds: 16,
            NeverSpawnFactory.Instance
        );
        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, locked.Status);
        Assert.Equal(0, harness.Index.Count);
    }

    [Fact]
    public void Ten_percent_terrorbeak_consumes_the_same_owner_pool_without_a_global_cap()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.AddLocal(OwnerA, "Farm", "creeper-conversion");
        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 0);
        harness.Observe(OwnerA, 10d, revision: 3, gameMinute: 1);

        var terrorbeak = harness.Authority.TrySpawn(
            Command(
                "ten-percent-terrorbeak",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                "Farm",
                gameMinute: 1,
                ShippedProfiles.Value.Terrorbeak
            )
        );
        var sameOwnerAtCap = harness.Authority.TrySpawn(
            Command(
                "same-owner-at-cap",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                "Farm",
                gameMinute: 1,
                ShippedProfiles.Value.CreeperFear
            )
        );

        harness.Observe(OwnerB, 50d, revision: 1, gameMinute: 0);
        harness.Observe(OwnerB, 15d, revision: 2, gameMinute: 0);
        var otherOwner = harness.Authority.TrySpawn(
            Command(
                "other-owner-conversion",
                HostileShadowSpawnOrigin.OwnerProjectionConversion,
                OwnerB,
                "Mine",
                gameMinute: 0,
                ShippedProfiles.Value.CreeperFear
            )
        );

        Assert.True(terrorbeak.Spawned);
        Assert.Equal(HostileShadowSpawnStatus.AtCap, sameOwnerAtCap.Status);
        Assert.True(otherOwner.Spawned);
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerB));
        Assert.Equal(3, harness.Authority.Count);
        var ownerBindings = harness.Authority.CreateFullSnapshot().Entities
            .Where(state => state.OwnerPlayerKey == OwnerA)
            .Select(state => state.AssetBindingId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                ShadowMonsterAssetBindingIds.CreeperFear,
                ShadowMonsterAssetBindingIds.Terrorbeak,
            },
            ownerBindings
        );
    }

    [Fact]
    public void Full_cap_pauses_and_one_vacancy_grants_exactly_one_replacement()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.AddLocal(OwnerA, "Farm", "vacancy-conversion");
        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 0);
        Assert.True(
            harness.Authority.TrySpawn(
                Command(
                    "fill-cap",
                    HostileShadowSpawnOrigin.Interval,
                    OwnerA,
                    "Farm",
                    60,
                    ShippedProfiles.Value.CreeperFear
                )
            ).Spawned
        );
        Assert.Equal(
            HostileShadowSpawnStatus.AtCap,
            harness.Authority.TrySpawn(
                Command(
                    "observe-cap",
                    HostileShadowSpawnOrigin.Interval,
                    OwnerA,
                    "Farm",
                    120,
                    ShippedProfiles.Value.CreeperFear
                )
            ).Status
        );
        var originalIds = harness.Authority.CreateFullSnapshot().Entities
            .Select(state => state.EntityId)
            .OrderBy(id => id)
            .ToArray();

        Assert.True(
            harness.Authority.CleanupEntity(
                originalIds[0],
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var replacement = harness.Authority.TrySpawn(
            Command(
                "single-vacancy",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                "Farm",
                120,
                ShippedProfiles.Value.CreeperFear
            )
        );
        Assert.True(replacement.Spawned);
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerA));

        Assert.True(
            harness.Authority.CleanupEntity(
                originalIds[1],
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var sameMinute = harness.Authority.TrySpawn(
            Command(
                "same-minute-second-vacancy",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                "Farm",
                120,
                ShippedProfiles.Value.CreeperFear
            )
        );
        Assert.Equal(HostileShadowSpawnStatus.Waiting, sameMinute.Status);
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerA));
    }

    [Fact]
    public void Owner_off_map_keeps_original_location_retargets_remaining_player_or_idles_without_local_ttl()
    {
        var ownerPresent = EvaluateTargets(
            currentMinute: 30,
            Player(OwnerA, "Farm", 160, 256),
            Player(OwnerB, "Mine", 140, 256)
        );
        Assert.Equal(OwnerA, ownerPresent.TargetPlayerKey);
        Assert.Equal(HostileShadowTargetSource.Owner, ownerPresent.TargetSource);

        var observerRemains = EvaluateTargets(
            currentMinute: 30,
            Player(OwnerA, "Mine", 160, 256),
            Player(OwnerB, "Farm", 192, 256)
        );
        Assert.Equal(OwnerB, observerRemains.TargetPlayerKey);
        Assert.Equal(
            HostileShadowTargetSource.NearestPlayer,
            observerRemains.TargetSource
        );
        Assert.Equal(Harness.HostSpawnX, observerRemains.PositionX);
        Assert.Equal(Harness.HostSpawnY, observerRemains.PositionY);

        var nobodyRemains = EvaluateTargets(
            currentMinute: 119,
            Player(OwnerA, "Mine", 160, 256),
            Player(OwnerB, "Town", 192, 256)
        );
        var expired = EvaluateTargets(
            currentMinute: 120,
            Player(OwnerA, "Mine", 160, 256),
            Player(OwnerB, "Town", 192, 256)
        );
        Assert.False(nobodyRemains.NaturalTtlExpired);
        Assert.Equal(HostileShadowStateIds.Idle, nobodyRemains.StateId);
        Assert.Equal(string.Empty, nobodyRemains.TargetPlayerKey);
        Assert.False(expired.NaturalTtlExpired);
    }

    [Fact]
    public void Late_join_receives_only_current_location_shared_entities_and_no_owner_local_projection()
    {
        var harness = TwoLocationHarness();
        var scoped = Scope(
            harness.Authority,
            "Farm",
            ShadowSnapshotTrigger.Join
        );
        var store = new ShadowStateRevisionStore();
        Assert.True(
            store.BeginSubscription(
                "Farm",
                ShadowSnapshotTrigger.Join,
                out var reason
            ),
            reason
        );

        var applied = store.ApplyFull(scoped);

        Assert.Equal(ShadowRevisionApplyStatus.Applied, applied.Status);
        var shared = Assert.Single(store.GetSnapshot().Values);
        Assert.Equal(OwnerA, shared.OwnerPlayerKey);
        Assert.Equal("Farm", shared.LocationId);
        Assert.Equal(ShadowMonsterAssetBindingIds.CreeperFear, shared.AssetBindingId);
        Assert.DoesNotContain(
            store.GetSnapshot().Values,
            state => state.LocationId == "Mine"
        );
        Assert.Equal(0, harness.Index.Count);
    }

    [Fact]
    public void Warp_immediately_retires_old_mirror_and_old_revision_cannot_repopulate_it()
    {
        var harness = TwoLocationHarness();
        var farm = Scope(harness.Authority, "Farm", ShadowSnapshotTrigger.Join);
        var mine = Scope(harness.Authority, "Mine", ShadowSnapshotTrigger.Warp);
        var store = new ShadowStateRevisionStore();
        Assert.True(store.BeginSubscription("Farm", ShadowSnapshotTrigger.Join, out _));
        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyFull(farm).Status);
        var farmEntity = Assert.Single(farm.Entities);
        var mineEntity = Assert.Single(mine.Entities);

        Assert.True(store.BeginSubscription("Mine", ShadowSnapshotTrigger.Warp, out _));
        Assert.Equal(0, store.Count);
        Assert.True(store.AwaitingFullSnapshot);
        var whileWaiting = store.ApplyDelta(
            Delta(
                Session,
                baseRevision: farm.Revision,
                revision: farm.Revision + 1,
                ShadowStateDeltaKind.Removed,
                farmEntity.EntityId,
                state: null
            )
        );
        Assert.True(whileWaiting.RequiresFullSnapshot);
        Assert.Equal(0, store.Count);

        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyFull(mine).Status);
        Assert.True(store.TryGet(mineEntity.EntityId, out _));
        var stale = store.ApplyDelta(
            Delta(
                Session,
                baseRevision: farm.Revision - 1,
                revision: farm.Revision,
                ShadowStateDeltaKind.Removed,
                farmEntity.EntityId,
                state: null
            )
        );
        Assert.Equal(ShadowRevisionApplyStatus.IgnoredStale, stale.Status);
        Assert.Equal(1, store.Count);
        Assert.False(store.TryGet(farmEntity.EntityId, out _));
        Assert.True(store.TryGet(mineEntity.EntityId, out _));
    }

    [Fact]
    public void Disconnect_clears_owner_entity_subscription_nonce_and_mirror_entry_without_fallback_owner()
    {
        var harness = SingleSharedHarness();
        var lifecycle = PrepareLifecycleWindow(harness, out var store);
        var deltas = new List<ShadowStateDeltaMessage>();
        harness.Authority.DeltaProduced += deltas.Add;

        lifecycle.Disconnect(PeerA, OwnerA);
        var removed = harness.Authority.ForgetOwner(
            OwnerA,
            HostileShadowCleanupReasonIds.OwnerDisconnected
        );
        foreach (var delta in deltas)
            Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyDelta(delta).Status);

        Assert.Equal(1, removed);
        Assert.Equal(0, harness.Authority.Count);
        Assert.Equal(0, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.False(harness.Authority.TryGetConversionEpochRevision(OwnerA, out _));
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(0, lifecycle.FingerprintCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.ReceiptCount);
        Assert.Equal(0, store.Count);
        Assert.Equal(
            string.Empty,
            deltas[0].Change.State?.TargetPlayerKey ?? string.Empty
        );
    }

    [Fact]
    public void Day_ending_clears_entities_client_revision_subscriptions_nonces_and_leases()
    {
        var harness = SingleSharedHarness();
        var lifecycle = PrepareLifecycleWindow(harness, out var store);

        harness.Authority.CleanupAll(HostileShadowCleanupReasonIds.DayEnding);
        lifecycle.DayEnding();
        store.Reset();

        Assert.Equal(0, harness.Authority.Count);
        Assert.True(harness.Authority.IsHostSessionActive);
        Assert.False(lifecycle.IsWorldActive);
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(0, lifecycle.FingerprintCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.ReceiptCount);
        AssertMirrorReset(store);
    }

    [Fact]
    public void Returned_to_title_resets_host_revision_session_mirror_subscription_nonce_and_lease()
    {
        var harness = SingleSharedHarness();
        var lifecycle = PrepareLifecycleWindow(harness, out var store);

        harness.Authority.EndSession(HostileShadowCleanupReasonIds.ReturnedToTitle);
        lifecycle.ClearSession();
        store.Reset();

        Assert.Equal(0, harness.Authority.Count);
        Assert.False(harness.Authority.IsHostSessionActive);
        Assert.Equal(string.Empty, harness.Authority.SessionId);
        Assert.Equal(0, harness.Authority.Revision);
        Assert.False(lifecycle.IsSessionActive);
        Assert.Equal(string.Empty, lifecycle.SessionId);
        Assert.Equal(0, lifecycle.SubscriptionCount);
        Assert.Equal(0, lifecycle.LeaseAuthority.NonceOwnerCount);
        Assert.Equal(string.Empty, lifecycle.LeaseAuthority.SessionId);
        AssertMirrorReset(store);
    }

    private static Harness SingleSharedHarness()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.AddLocal(OwnerA, "Farm", "single-shared-local");
        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 0);
        Assert.Equal(1, harness.Authority.Count);
        return harness;
    }

    private static Harness TwoLocationHarness()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        harness.Observe(OwnerA, 50d, revision: 1, gameMinute: 0);
        harness.Observe(OwnerB, 50d, revision: 1, gameMinute: 0);
        harness.AddLocal(OwnerA, "Farm", "farm-local");
        harness.AddLocal(OwnerB, "Mine", "mine-local");
        harness.Observe(OwnerA, 15d, revision: 2, gameMinute: 0);
        harness.Observe(OwnerB, 15d, revision: 2, gameMinute: 0);
        Assert.Equal(2, harness.Authority.Count);
        return harness;
    }

    private static HostileShadowSessionLifecycleCoordinator PrepareLifecycleWindow(
        Harness harness,
        out ShadowStateRevisionStore store
    )
    {
        var lifecycle = new HostileShadowSessionLifecycleCoordinator();
        Assert.True(lifecycle.BeginSession(Session, enabled: true, out var reason), reason);
        Assert.True(
            lifecycle.TrySubscribe(
                PeerA,
                OwnerA,
                "Farm",
                ShadowSnapshotTrigger.Join,
                out reason
            ),
            reason
        );
        Assert.True(
            lifecycle.RecordFingerprint(
                PeerA,
                OwnerA,
                new HostileShadowConfigFingerprintReport
                {
                    SessionId = Session,
                    PlayerKey = OwnerA,
                    ConfigSchemaVersion = 1,
                    Hash = new string('A', 64),
                },
                out reason
            ),
            reason
        );
        lifecycle.LeaseAuthority.TryIssue(
            LeaseRequest(nonce: 1),
            OwnerA,
            nowTick: 10,
            UnavailableDarkHandLeaseTargetAuthority.Instance
        );
        Assert.Equal(1, lifecycle.LeaseAuthority.NonceOwnerCount);

        store = new ShadowStateRevisionStore();
        Assert.True(store.BeginSubscription("Farm", ShadowSnapshotTrigger.Join, out reason), reason);
        Assert.Equal(
            ShadowRevisionApplyStatus.Applied,
            store.ApplyFull(
                Scope(harness.Authority, "Farm", ShadowSnapshotTrigger.Join)
            ).Status
        );
        Assert.Equal(1, store.Count);
        return lifecycle;
    }

    private static void AssertMirrorReset(ShadowStateRevisionStore store)
    {
        Assert.Equal(0, store.Count);
        Assert.Equal(string.Empty, store.SessionId);
        Assert.Equal(string.Empty, store.SubscriptionLocationId);
        Assert.Equal(0, store.Revision);
        Assert.False(store.AwaitingFullSnapshot);
    }

    private static HostileShadowTargetingDecision EvaluateTargets(
        long currentMinute,
        params HostileShadowPlayerSample[] players
    )
    {
        var index = new HostileShadowLocationPlayerIndex();
        var rebuilt = index.Rebuild(players);
        Assert.True(rebuilt.Success, rebuilt.Reason);
        return HostileShadowTargetingEngine.Evaluate(
            new HostileShadowTargetingInput
            {
                EntityId = 1,
                OwnerPlayerKey = OwnerA,
                LocationId = "Farm",
                PositionX = Harness.HostSpawnX,
                PositionY = Harness.HostSpawnY,
                StandingX = Harness.HostSpawnX,
                StandingY = Harness.HostSpawnY,
                MovementSpeed = ShippedProfiles.Value.CreeperFear.MovementSpeed,
                DetectionRadiusPixels = ShippedProfiles.Value.CreeperFear.DetectionRadiusPixels,
                StopDistancePixels = ShippedProfiles.Value.CreeperFear.AttackRangePixels,
                SpawnGameMinute = 0,
                CurrentGameMinute = currentMinute,
                NaturalTtlMinutes = 120,
                ElapsedSeconds = 0d,
            },
            index
        );
    }

    private static HostileShadowPlayerSample Player(
        string playerKey,
        string locationId,
        double x,
        double y
    )
    {
        return new HostileShadowPlayerSample(playerKey, locationId, x, y);
    }

    private static ShadowStateSnapshotMessage Scope(
        HostileShadowAuthority authority,
        string locationId,
        ShadowSnapshotTrigger trigger
    )
    {
        Assert.True(
            HostileShadowProtocol.TryCreateScopedSnapshot(
                authority.CreateFullSnapshot(),
                locationId,
                trigger,
                out var scoped,
                out var reason
            ),
            reason
        );
        return scoped!;
    }

    private static ShadowStateDeltaMessage Delta(
        string sessionId,
        long baseRevision,
        long revision,
        ShadowStateDeltaKind kind,
        long entityId,
        ShadowStateSnapshot? state
    )
    {
        return new ShadowStateDeltaMessage
        {
            SessionId = sessionId,
            BaseRevision = baseRevision,
            Revision = revision,
            Change = new ShadowStateDelta
            {
                Kind = kind,
                EntityId = entityId,
                State = state,
                Reason = "test-lifecycle",
                SettlementEligible = false,
            },
        };
    }

    private static DarkHandInteractionLeaseRequest LeaseRequest(long nonce)
    {
        return new DarkHandInteractionLeaseRequest
        {
            SessionId = Session,
            Nonce = nonce,
            OwnerPlayerKey = OwnerA,
            LocationId = "Farm",
            TargetId = "test-target",
            OperationId = "test-operation",
            ObservedTargetRevision = 1,
        };
    }

    private static HostileShadowSpawnCommand Command(
        string requestId,
        HostileShadowSpawnOrigin origin,
        string owner,
        string locationId,
        long gameMinute,
        ShadowMonsterRuntimeProfile profile
    )
    {
        return new HostileShadowSpawnCommand(
            requestId,
            origin,
            owner,
            locationId,
            Harness.HostSpawnX,
            Harness.HostSpawnY,
            gameMinute,
            profile,
            origin == HostileShadowSpawnOrigin.OwnerProjectionConversion
                ? "hostile-shadow.spawn.owner-projection-conversion"
                : "hostile-shadow.spawn.interval"
        );
    }

    private static ShadowCreatureHarmlessProjectionInstance AddLocal(
        ShadowCreatureHarmlessProjectionIndex index,
        HarmlessProjectionOwnerContext owner,
        string correlationId
    )
    {
        var policy = ShadowCreatureHarmlessProjectionCatalog.Policies.Single(
            candidate =>
                candidate.SpeciesId
                == ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId
        );
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            correlationId,
            owner,
            policy,
            new HarmlessProjectionWorldPoint(640d, 320d),
            spawnedAtMinute: 0
        );
        Assert.True(index.TryAdd(instance, out var reason), reason);
        Assert.True(
            instance.AdvanceFrame(
                checked(policy.FrameCount * policy.SpawnFrameDurationMilliseconds)
            )
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Idle,
            instance.AnimationState
        );
        return instance;
    }

    private static HarmlessProjectionOwnerContext OwnerContext(
        string playerKey,
        int screenId,
        object location,
        string locationId
    )
    {
        return new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location,
            locationId
        );
    }

    private static bool IsDangerEnter(SanityStateEvent stateEvent)
    {
        return stateEvent.Kind == SanityStateEventKind.TierEntered
            && string.Equals(
                stateEvent.TierId,
                SanityTierIds.Danger,
                StringComparison.Ordinal
            );
    }

    private static bool IsDangerExit(SanityStateEvent stateEvent)
    {
        return stateEvent.Kind == SanityStateEventKind.TierExited
            && string.Equals(
                stateEvent.TierId,
                SanityTierIds.Danger,
                StringComparison.Ordinal
            );
    }

    private static RuntimeProfiles ResolveShippedProfiles()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
        var registry = ConfigTestData.LoadShippedRegistry();
        var access = new MemoryFlatConfigFileAccess(
            $"{{\"MonsterDifficultyProfile\":\"{ShadowMonsterDifficultyProfileIds.Compatible}\"}}"
        );
        var load = FlatConfigValueStore.Load(registry, access);
        var store = Assert.IsType<FlatConfigValueStore>(load.Store);
        var provider = new HostileShadowRuntimeProfileProvider(
            catalog,
            capability,
            new TypedConfigResolver(registry, store),
            tileSize: 64,
            unavailableReason: "test-profile-unavailable"
        );
        var creeper = provider.Resolve(ShadowMonsterAssetBindingIds.CreeperFear);
        var terrorbeak = provider.Resolve(ShadowMonsterAssetBindingIds.Terrorbeak);
        Assert.True(creeper.Success, creeper.Reason);
        Assert.True(terrorbeak.Success, terrorbeak.Reason);
        return new RuntimeProfiles(creeper.Profile!, terrorbeak.Profile!);
    }

    private static string Contract(string group, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", group, fileName)
        );
    }

    private static void AssertPair(string source, string eventName, string handler)
    {
        Assert.Contains($"{eventName} += {handler}", source, StringComparison.Ordinal);
        Assert.Contains($"{eventName} -= {handler}", source, StringComparison.Ordinal);
    }

    private sealed record RuntimeProfiles(
        ShadowMonsterRuntimeProfile CreeperFear,
        ShadowMonsterRuntimeProfile Terrorbeak
    );

    private sealed class Harness
    {
        internal const double HostSpawnX = 128d;
        internal const double HostSpawnY = 256d;
        private readonly Dictionary<string, HarmlessProjectionOwnerContext> contexts =
            new(StringComparer.Ordinal);

        internal Harness(string intensityId)
        {
            Governor = new SanityShadowBudgetGovernor(
                new FixedIntensityProvider(intensityId)
            );
            Authority = new HostileShadowAuthority(
                new GovernorBudget(Governor),
                new IncrementingIdSource(),
                new HostileShadowPhysicalEntityCapability(
                    HostileShadowPhysicalEntityCapabilityStatus.Available,
                    "hostile-shadow.physical-monster-netcollection-roundtrip-verified"
                )
            );
            Assert.True(
                Authority.BeginHostSession(Session, systemEnabled: true, out var reason),
                reason
            );
            Index = new ShadowCreatureHarmlessProjectionIndex();
            Sink = new AuthorityConversionSink(Index, Authority);
            Coordinator = new ShadowCreatureHarmlessProjectionCoordinator(
                Index,
                new GovernorProjectionBudget(Governor),
                Sink,
                new DeterministicCorrelationSource()
            );
            foreach (var policy in ShadowCreatureHarmlessProjectionCatalog.Policies)
                Assert.True(Coordinator.RegisterPolicy(policy, out reason), reason);
        }

        internal SanityTierStateMachine TierState { get; } = new();

        internal SanityShadowBudgetGovernor Governor { get; }

        internal HostileShadowAuthority Authority { get; }

        internal ShadowCreatureHarmlessProjectionIndex Index { get; }

        internal ShadowCreatureHarmlessProjectionCoordinator Coordinator { get; }

        internal AuthorityConversionSink Sink { get; }

        internal SanityTierEvaluationResult Observe(
            string owner,
            double current,
            long revision,
            long gameMinute
        )
        {
            var result = TierState.Observe(
                new SanityPlayerSnapshot
                {
                    PlayerKey = owner,
                    Current = current,
                    Maximum = 100d,
                    Revision = revision,
                },
                isSystemEnabled: true
            );
            foreach (var stateEvent in result.Events)
            {
                Assert.True(
                    Governor.ApplyStateEvent(stateEvent, out var reason),
                    reason
                );
                // Production registers the host before the owner-local projection sink. Preserve
                // that order so danger epoch authority exists before conversion intent submission.
                Authority.ObserveStateEvent(stateEvent);
                Coordinator.ApplyStateEvent(stateEvent, gameMinute);
            }
            return result;
        }

        internal ShadowCreatureHarmlessProjectionInstance AddLocal(
            string owner,
            string locationId,
            string correlationId
        )
        {
            var context = OwnerContext(owner, 0, new object(), locationId);
            contexts[owner] = context;
            Sink.SetLocation(owner, locationId);
            return CreeperFearOwnerBudgetAndLifecycleTests.AddLocal(
                Index,
                context,
                correlationId
            );
        }

        internal HarmlessProjectionOwnerContext GetOwnerContext(string owner)
        {
            return contexts[owner];
        }
    }

    private sealed class AuthorityConversionSink : IShadowProjectionConversionIntentSink
    {
        private readonly ShadowCreatureHarmlessProjectionIndex index;
        private readonly HostileShadowAuthority authority;
        private readonly Dictionary<string, string> locations = new(StringComparer.Ordinal);

        internal AuthorityConversionSink(
            ShadowCreatureHarmlessProjectionIndex index,
            HostileShadowAuthority authority
        )
        {
            this.index = index;
            this.authority = authority;
        }

        internal List<HostileShadowSpawnResult> Results { get; } = new();

        internal bool LocalPoolWasEmptyBeforeEverySubmission { get; private set; } = true;

        internal void SetLocation(string owner, string locationId)
        {
            locations[owner] = locationId;
        }

        public ShadowProjectionConversionSubmissionResult Record(
            ShadowProjectionConversionIntent intent
        )
        {
            LocalPoolWasEmptyBeforeEverySubmission &=
                index.CountForOwner(intent.PlayerKey) == 0;
            var result = authority.TrySpawn(
                Command(
                    intent.CorrelationId,
                    HostileShadowSpawnOrigin.OwnerProjectionConversion,
                    intent.PlayerKey,
                    locations[intent.PlayerKey],
                    intent.RequestedAtMinute,
                    ShippedProfiles.Value.CreeperFear
                )
            );
            Results.Add(result);
            return new ShadowProjectionConversionSubmissionResult(
                result.Spawned || result.Status == HostileShadowSpawnStatus.Duplicate
                    ? ShadowProjectionConversionSubmissionStatus.Confirmed
                    : ShadowProjectionConversionSubmissionStatus.Rejected,
                result.Reason
            );
        }
    }

    private sealed class FixedIntensityProvider : ISanityMonsterIntensityProvider
    {
        private readonly string intensityId;

        internal FixedIntensityProvider(string intensityId)
        {
            this.intensityId = intensityId;
        }

        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(true, intensityId, "test-intensity");
        }
    }

    private sealed class GovernorBudget : IHostileShadowBudgetAuthority
    {
        private readonly SanityShadowBudgetGovernor governor;

        internal GovernorBudget(SanityShadowBudgetGovernor governor)
        {
            this.governor = governor;
        }

        public SanityShadowBudgetEvaluationResult Evaluate(
            string playerKey,
            long gameMinute,
            int occupancy,
            SanityShadowSpecies requestedSpecies
        )
        {
            return governor.EvaluateHostileSpawn(
                playerKey,
                gameMinute,
                occupancy,
                requestedSpecies
            );
        }
    }

    private sealed class GovernorProjectionBudget
        : IShadowCreatureProjectionBudgetAuthority
    {
        private readonly SanityShadowBudgetGovernor governor;

        internal GovernorProjectionBudget(SanityShadowBudgetGovernor governor)
        {
            this.governor = governor;
        }

        public SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
            string playerKey,
            long gameMinute,
            int occupancy
        )
        {
            return governor.Evaluate(playerKey, gameMinute, occupancy);
        }
    }

    private sealed class IncrementingIdSource : IHostileShadowEntityIdSource
    {
        private long next = 1000;

        public long Next()
        {
            return ++next;
        }
    }

    private sealed class DeterministicCorrelationSource
        : IShadowProjectionCorrelationSource
    {
        private long next;

        public string Next(string playerKey, string speciesId)
        {
            return $"stage-08-05-{playerKey}-{++next:D4}";
        }
    }

    private sealed class NeverSpawnFactory
        : IShadowCreatureHarmlessProjectionSpawnFactory
    {
        internal static NeverSpawnFactory Instance { get; } = new();

        public ShadowCreatureHarmlessProjectionSpawnResult TrySpawn(
            ShadowCreatureHarmlessProjectionSpawnRequest request
        )
        {
            throw new InvalidOperationException(
                "Conversion lock must short-circuit before a local spawn attempt."
            );
        }
    }
}
