using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class HarmlessProjectionFoundationTests
{
    [Fact]
    public void IndexUsesPlayerScreenAndLocationReferenceAndSpectatorCannotAffectOwner()
    {
        var index = new HarmlessProjectionIndex();
        var location = new object();
        var owner = Owner("1", 0, location, "Farm");
        var instance = Instance(owner, Policy("species-a", "tier-a"), 100);

        Assert.True(index.TryAdd(instance, out var addReason), addReason);
        Assert.True(index.TryGet(owner, "species-a", out var found));
        Assert.Same(instance, found);
        Assert.False(
            index.TryGet(Owner("1", 1, location, "Farm"), "species-a", out _)
        );
        Assert.False(
            index.TryGet(Owner("2", 0, location, "Farm"), "species-a", out _)
        );
        Assert.False(
            index.TryGet(Owner("1", 0, new object(), "Farm"), "species-a", out _)
        );

        Assert.Equal(
            0,
            index.CleanupOwner("2", HarmlessProjectionCleanupReason.OwnerInvalidated)
        );
        Assert.True(index.TryGet(owner, "species-a", out _));
        Assert.Equal(
            1,
            index.CleanupOwner("1", HarmlessProjectionCleanupReason.OwnerInvalidated)
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.OwnerInvalidated,
            instance.CleanupReason
        );
        Assert.Equal("owner-invalidated", instance.CleanupReasonId);
        Assert.Equal(
            0,
            index.CleanupOwner("1", HarmlessProjectionCleanupReason.OwnerInvalidated)
        );
    }

    [Fact]
    public void IndexEnforcesPerOwnerSpeciesCapButKeepsOwnersAndSpeciesIndependent()
    {
        var index = new HarmlessProjectionIndex();
        var sharedLocation = new object();
        var policyA = Policy("species-a", "tier-a");
        var policyB = Policy("species-b", "tier-b");
        var ownerOne = Owner("1", 0, sharedLocation, "Farm");
        var ownerTwo = Owner("2", 1, sharedLocation, "Farm");

        Assert.True(index.TryAdd(Instance(ownerOne, policyA, 100), out _));
        Assert.True(index.TryAdd(Instance(ownerOne, policyB, 100), out _));
        Assert.True(index.TryAdd(Instance(ownerTwo, policyA, 100), out _));
        Assert.False(
            index.TryAdd(
                Instance(Owner("1", 2, new object(), "Town"), policyA, 100),
                out var reason
            )
        );

        Assert.Equal("index.active-cap-reached", reason);
        Assert.Equal(3, index.Count);
        Assert.Equal(1, index.CountForOwnerSpecies("1", "species-a"));
        Assert.Equal(1, index.CountForOwnerSpecies("1", "species-b"));
        Assert.Equal(1, index.CountForOwnerSpecies("2", "species-a"));
    }

    [Fact]
    public void ContextCleanupDistinguishesLocationIdentityAndInvalidScreens()
    {
        var index = new HarmlessProjectionIndex();
        var oldLocation = new object();
        var otherScreenLocation = new object();
        var policyA = Policy("species-a", "tier-a");
        var policyB = Policy("species-b", "tier-b");
        var old = Instance(Owner("1", 0, oldLocation, "Farm"), policyA, 100);
        var otherScreen = Instance(
            Owner("2", 4, otherScreenLocation, "Town"),
            policyB,
            100
        );
        Assert.True(index.TryAdd(old, out _));
        Assert.True(index.TryAdd(otherScreen, out _));

        var current = Owner("1", 0, new object(), "Farm");
        Assert.Equal(
            1,
            index.CleanupMismatchedLocation(
                current,
                HarmlessProjectionCleanupReason.LocationInvalid
            )
        );
        Assert.Equal(HarmlessProjectionCleanupReason.LocationInvalid, old.CleanupReason);
        Assert.Equal(
            1,
            index.CleanupInvalidScreens(
                screenId => screenId != 4,
                HarmlessProjectionCleanupReason.ScreenInvalid
            )
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.ScreenInvalid,
            otherScreen.CleanupReason
        );
    }

    [Fact]
    public void FourOrdinaryPoliciesUseIndependentCadenceCapAndTtl()
    {
        var (scheduler, _) = SchedulerWithFrozenPolicies();
        var factory = new RecordingSpawnFactory();
        var owner = Owner("1", 0, new object(), "Farm");
        foreach (var tierId in new[] { "mr", "hand", "watcher", "eyes" })
            Assert.Equal(1, scheduler.SetTierActive("1", tierId, true));

        var first = scheduler.UpdateOwner(owner, Point(640, 640), 100, factory);
        Assert.Equal(4, first.AttemptCount);
        Assert.Equal(4, first.SpawnedCount);
        Assert.Equal(4, scheduler.Index.Count);

        var beforeDue = scheduler.UpdateOwner(owner, Point(640, 640), 119, factory);
        Assert.Equal(0, beforeDue.AttemptCount);
        Assert.Equal(HarmlessProjectionSchedulerStatus.AtCap, beforeDue.Status);

        var firstTtl = scheduler.UpdateOwner(owner, Point(640, 640), 120, factory);
        Assert.Equal(3, firstTtl.ExpiredCount);
        Assert.Equal(3, firstTtl.AttemptCount);
        Assert.Equal(3, firstTtl.SpawnedCount);
        Assert.Equal(7, factory.Requests.Count);

        var secondTtl = scheduler.UpdateOwner(owner, Point(640, 640), 140, factory);
        Assert.Equal(4, secondTtl.ExpiredCount);
        Assert.Equal(4, secondTtl.AttemptCount);
        Assert.Equal(4, secondTtl.SpawnedCount);
        Assert.Equal(11, factory.Requests.Count);
    }

    [Fact]
    public void SuccessAndFailureAdvanceSameCadenceWhileAtCapDoesNotAttempt()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        var policy = Policy("species-a", "tier-a", ttl: 40);
        Assert.True(scheduler.RegisterPolicy(policy, out _));
        Assert.Equal(1, scheduler.SetTierActive("1", "tier-a", true));
        var factory = new RecordingSpawnFactory(false, true, true);
        var owner = Owner("1", 0, new object(), "Farm");

        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(640, 640), 100, factory).AttemptCount
        );
        Assert.Equal(
            0,
            scheduler.UpdateOwner(owner, Point(640, 640), 119, factory).AttemptCount
        );
        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(640, 640), 120, factory).AttemptCount
        );
        Assert.Equal(
            0,
            scheduler.UpdateOwner(owner, Point(640, 640), 140, factory).AttemptCount
        );
        Assert.True(scheduler.TryGetSchedule("1", "species-a", out var atCap));
        Assert.Equal(140, atCap.NextAttemptMinute);
        Assert.Equal(2, factory.Requests.Count);

        var ttl = scheduler.UpdateOwner(owner, Point(640, 640), 160, factory);
        Assert.Equal(1, ttl.ExpiredCount);
        Assert.Equal(1, ttl.AttemptCount);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public void ResourceFailureIsARealAttemptAndWaitsTheSameCadence()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        Assert.True(scheduler.RegisterPolicy(Policy("species-a", "tier-a"), out _));
        scheduler.SetTierActive("1", "tier-a", true);
        var factory = new RecordingSpawnFactory(false, false);
        var owner = Owner("1", 0, new object(), "Farm");

        var failed = scheduler.UpdateOwner(owner, Point(640, 640), 100, factory);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(0, failed.SpawnedCount);
        Assert.True(scheduler.TryGetSchedule("1", "species-a", out var schedule));
        Assert.Equal(120, schedule.NextAttemptMinute);
        Assert.Equal(
            0,
            scheduler.UpdateOwner(owner, Point(640, 640), 119, factory).AttemptCount
        );
        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(640, 640), 120, factory).AttemptCount
        );
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public void NegativeTimeSyncPreservesRemainingCadenceAndTtl()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        var policy = Policy("species-a", "tier-a", ttl: 40);
        scheduler.RegisterPolicy(policy, out _);
        scheduler.SetTierActive("1", "tier-a", true);
        var factory = new RecordingSpawnFactory();
        var owner = Owner("1", 0, new object(), "Farm");

        scheduler.UpdateOwner(owner, Point(640, 640), 100, factory);
        scheduler.UpdateOwner(owner, Point(640, 640), 110, factory);
        scheduler.UpdateOwner(owner, Point(640, 640), 90, factory);

        Assert.True(scheduler.TryGetSchedule("1", "species-a", out var rebased));
        Assert.Equal(100, rebased.NextAttemptMinute);
        Assert.True(index.TryGet(owner, "species-a", out var active));
        Assert.NotNull(active);
        Assert.Equal(80, active!.SpawnedAtMinute);
        Assert.Equal(120, active.ExpiresAtMinute);
        Assert.Equal(
            0,
            scheduler.UpdateOwner(owner, Point(640, 640), 100, factory).AttemptCount
        );

        var expires = scheduler.UpdateOwner(owner, Point(640, 640), 120, factory);
        Assert.Equal(1, expires.ExpiredCount);
        Assert.Equal(1, expires.AttemptCount);
    }

    [Fact]
    public void TtlRebaseOverflowFailsAtomically()
    {
        var instance = Instance(
            Owner("1", 0, new object(), "Farm"),
            Policy("species-a", "tier-a"),
            0
        );

        Assert.False(instance.TryRebaseTime(long.MaxValue));
        Assert.Equal(0, instance.SpawnedAtMinute);
        Assert.Equal(20, instance.ExpiresAtMinute);
    }

    [Fact]
    public void TierExitCleansNormalProjectionButRetainsDeclaredSpecialProjectionUntilTtl()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        var normal = Policy("normal", "normal-tier", ttl: 20, clearOnTierExit: true);
        var special = Policy("special", "special-tier", ttl: 40, clearOnTierExit: false);
        scheduler.RegisterPolicy(normal, out _);
        scheduler.RegisterPolicy(special, out _);
        scheduler.SetTierActive("1", "normal-tier", true);
        scheduler.SetTierActive("1", "special-tier", true);
        var owner = Owner("1", 0, new object(), "Farm");
        scheduler.UpdateOwner(owner, Point(640, 640), 100, new RecordingSpawnFactory());
        Assert.True(index.TryGet(owner, "normal", out var normalInstance));
        Assert.True(index.TryGet(owner, "special", out var specialInstance));

        scheduler.SetTierActive("1", "normal-tier", false);
        scheduler.SetTierActive("1", "special-tier", false);
        scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            101,
            new RecordingSpawnFactory()
        );

        Assert.False(index.TryGet(owner, "normal", out _));
        Assert.Equal(
            HarmlessProjectionCleanupReason.TierExited,
            normalInstance!.CleanupReason
        );
        Assert.True(index.TryGet(owner, "special", out _));
        var expiry = scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            140,
            new RecordingSpawnFactory()
        );
        Assert.Equal(1, expiry.ExpiredCount);
        Assert.Equal(
            HarmlessProjectionCleanupReason.HardTtlExpired,
            specialInstance!.CleanupReason
        );
    }

    [Fact]
    public void SpeciesExitHookCanRetainForTransitionBeforeFinalCleanup()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        var policy = Policy("special", "tier", ttl: 40, clearOnTierExit: false);
        scheduler.RegisterPolicy(policy, out _);
        scheduler.SetTierActive("1", "tier", true);
        var owner = Owner("1", 0, new object(), "Farm");
        scheduler.UpdateOwner(owner, Point(640, 640), 100, new RecordingSpawnFactory());

        Assert.False(
            scheduler.RequestSoftExit(
                owner,
                "special",
                HarmlessProjectionCleanupReason.OwnerApproached,
                new FixedExitHook(HarmlessProjectionExitResolution.RetainForSpeciesTransition),
                out var deferred
            )
        );
        Assert.Equal("cleanup.deferred-to-species-hook", deferred);
        Assert.True(index.TryGet(owner, "special", out _));

        Assert.True(
            scheduler.RequestSoftExit(
                owner,
                "special",
                HarmlessProjectionCleanupReason.DarkHandReturned,
                new FixedExitHook(HarmlessProjectionExitResolution.Cleanup),
                out var cleaned
            )
        );
        Assert.Equal("dark-hand-returned", cleaned);
        Assert.False(index.TryGet(owner, "special", out _));
    }

    [Fact]
    public void LifecycleCleanupIsIdempotentAndDoesNotLeakScheduleAcrossOwners()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        scheduler.RegisterPolicy(Policy("species-a", "tier-a"), out _);
        scheduler.SetTierActive("1", "tier-a", true);
        scheduler.SetTierActive("2", "tier-a", true);
        var location = new object();
        scheduler.UpdateOwner(
            Owner("1", 0, location, "Farm"),
            Point(640, 640),
            100,
            new RecordingSpawnFactory()
        );
        scheduler.UpdateOwner(
            Owner("2", 1, location, "Farm"),
            Point(640, 640),
            100,
            new RecordingSpawnFactory()
        );

        Assert.Equal(
            1,
            scheduler.CleanupOwner("1", HarmlessProjectionCleanupReason.OwnerWarped)
        );
        Assert.Equal(
            0,
            scheduler.CleanupOwner("1", HarmlessProjectionCleanupReason.OwnerWarped)
        );
        Assert.False(scheduler.TryGetSchedule("1", "species-a", out _));
        Assert.True(scheduler.TryGetSchedule("2", "species-a", out _));
        Assert.Equal(
            1,
            scheduler.CleanupAll(HarmlessProjectionCleanupReason.DayEnding)
        );
        Assert.Equal(
            0,
            scheduler.CleanupAll(HarmlessProjectionCleanupReason.ReturnedTitle)
        );
        Assert.False(scheduler.TryGetSchedule("2", "species-a", out _));
    }

    [Fact]
    public void ResourceInvalidationCleansBorrowedInstancesAndRetriesActiveTiersOnce()
    {
        var index = new HarmlessProjectionIndex();
        var scheduler = new HarmlessProjectionScheduler(index);
        scheduler.RegisterPolicy(Policy("species-a", "tier-a"), out _);
        scheduler.SetTierActive("1", "tier-a", true);
        var owner = Owner("1", 0, new object(), "Farm");
        var factory = new RecordingSpawnFactory();
        scheduler.UpdateOwner(owner, Point(640, 640), 100, factory);
        Assert.True(index.TryGet(owner, "species-a", out var old));

        Assert.Equal(1, scheduler.InvalidateResources());
        Assert.Equal(
            HarmlessProjectionCleanupReason.ResourceInvalidated,
            old!.CleanupReason
        );
        Assert.True(scheduler.TryGetSchedule("1", "species-a", out var pending));
        Assert.True(pending.ImmediateAttemptPending);

        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(640, 640), 101, factory).AttemptCount
        );
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public void ContextRecoveryRetriesOnlyTheAffectedOwnerPolicies()
    {
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(Policy("species-a", "tier-a"), out _);
        scheduler.SetTierActive("1", "tier-a", true);
        scheduler.SetTierActive("2", "tier-a", true);
        var ownerOne = Owner("1", 0, new object(), "Farm");
        var ownerTwo = Owner("2", 1, new object(), "Town");
        var factory = new RecordingSpawnFactory(false, false, false);
        scheduler.UpdateOwner(ownerOne, Point(640, 640), 100, factory);
        scheduler.UpdateOwner(ownerTwo, Point(640, 640), 100, factory);

        scheduler.RetryActivePolicies("1");

        Assert.Equal(
            1,
            scheduler.UpdateOwner(ownerOne, Point(640, 640), 101, factory).AttemptCount
        );
        Assert.Equal(
            0,
            scheduler.UpdateOwner(ownerTwo, Point(640, 640), 101, factory).AttemptCount
        );
        Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public void GroundSelectorChecksBoundsBeforeOpenAndPassable()
    {
        var selector = new HarmlessProjectionSpawnPointSelector();
        var map = new RecordingMap();
        var result = selector.Select(
            Point(640, 640),
            Policy("species-a", "tier-a", min: 5, max: 10),
            map,
            new SequenceRandom(0d, 0d)
        );

        Assert.True(result.Success, result.Reason);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(new[] { "bounds", "anchor", "open", "passable" }, map.Calls);
        Assert.Equal(Point(960, 640), result.WorldPixel);
    }

    [Fact]
    public void AnchorOnlySelectorSkipsGroundCollisionQueries()
    {
        var selector = new HarmlessProjectionSpawnPointSelector();
        var map = new RecordingMap();
        var result = selector.Select(
            Point(640, 640),
            Policy(
                "species-a",
                "tier-a",
                min: 5,
                max: 10,
                placement: HarmlessProjectionPlacementKind.AnchorOnly
            ),
            map,
            new SequenceRandom(0d, 0d)
        );

        Assert.True(result.Success, result.Reason);
        Assert.Equal(new[] { "bounds", "anchor" }, map.Calls);
    }

    [Fact]
    public void SelectorStopsAfterSixteenCandidatesWithoutScanningWorld()
    {
        var selector = new HarmlessProjectionSpawnPointSelector();
        var map = new RecordingMap { TileOnMap = false };
        var randomValues = Enumerable.Repeat(0d, 32).ToArray();
        var result = selector.Select(
            Point(640, 640),
            Policy("species-a", "tier-a", attempts: 16),
            map,
            new SequenceRandom(randomValues)
        );

        Assert.False(result.Success);
        Assert.Equal("spawn.no-legal-point", result.Reason);
        Assert.Equal(16, result.Attempts);
        Assert.Equal(16, map.Calls.Count(value => value == "bounds"));
        Assert.DoesNotContain("open", map.Calls);
        Assert.DoesNotContain("passable", map.Calls);
    }

    [Fact]
    public void SelectorFailsClosedOnInvalidRandomWithoutRetryLoop()
    {
        var result = new HarmlessProjectionSpawnPointSelector().Select(
            Point(640, 640),
            Policy("species-a", "tier-a"),
            new RecordingMap(),
            new SequenceRandom(double.NaN, 0d)
        );

        Assert.False(result.Success);
        Assert.Equal("spawn.random-out-of-range", result.Reason);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void OrdinaryContractsContainNoShadowBudgetOrPermitSurface()
    {
        Assert.Equal(
            new[] { HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies },
            Enum.GetValues<HarmlessProjectionBudgetLane>()
        );
        foreach (
            var type in new[]
            {
                typeof(HarmlessProjectionPolicy),
                typeof(HarmlessProjectionSpawnRequest),
            }
        )
        {
            var names = type
                .GetProperties()
                .Select(property => property.Name)
                .Concat(type.GetFields().Select(field => field.Name))
                .ToArray();
            Assert.DoesNotContain(
                names,
                name => name.Contains("Intensity", StringComparison.OrdinalIgnoreCase)
            );
            Assert.DoesNotContain(
                names,
                name => name.Contains("Permit", StringComparison.OrdinalIgnoreCase)
            );
            Assert.DoesNotContain(
                names,
                name => name.Contains("ShadowBudget", StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    [Fact]
    public void CleanupReasonIdsMatchTheFrozenContractExactly()
    {
        Assert.Equal(
            new[]
            {
                "config-disabled",
                "conversion-requested",
                "dark-hand-returned",
                "day-ending",
                "day-started-recovery",
                "event-override",
                "hard-ttl-expired",
                "light-restored",
                "location-invalid",
                "owner-approached",
                "owner-invalidated",
                "owner-warped",
                "resource-invalidated",
                "returned-title",
                "screen-invalid",
                "tier-exited",
                "world-cleanup",
            },
            HarmlessProjectionCleanupReasonIds.All.OrderBy(value => value, StringComparer.Ordinal)
        );
    }

    private static (
        HarmlessProjectionScheduler Scheduler,
        IReadOnlyList<HarmlessProjectionPolicy> Policies
    ) SchedulerWithFrozenPolicies()
    {
        var policies = new[]
        {
            Policy("mr-species", "mr", min: 5, max: 10, ttl: 20),
            Policy("hand-species", "hand", min: 10, max: 20, ttl: 40, clearOnTierExit: false),
            Policy("watcher-species", "watcher", min: 5, max: 10, ttl: 20),
            Policy("eyes-species", "eyes", min: 5, max: 15, ttl: 20),
        };
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        foreach (var policy in policies)
            Assert.True(scheduler.RegisterPolicy(policy, out _));
        return (scheduler, policies);
    }

    private static HarmlessProjectionPolicy Policy(
        string species,
        string tier,
        int min = 5,
        int max = 10,
        int ttl = 20,
        int attempts = 16,
        bool clearOnTierExit = true,
        HarmlessProjectionPlacementKind placement = HarmlessProjectionPlacementKind.Ground
    )
    {
        return new HarmlessProjectionPolicy(
            species,
            tier,
            $"sanity.animation.{species}.idle",
            $"sanity.projection.state.{species}.active",
            min,
            max,
            attemptIntervalMinutes: 20,
            activeCap: 1,
            hardTtlMinutes: ttl,
            candidateAttemptLimit: attempts,
            placement,
            clearOnTierExit
        );
    }

    private static HarmlessProjectionOwnerContext Owner(
        string playerKey,
        int screenId,
        object location,
        string locationName
    )
    {
        return new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location,
            locationName
        );
    }

    private static HarmlessProjectionInstance Instance(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionPolicy policy,
        long minute
    )
    {
        return new HarmlessProjectionInstance(
            owner,
            policy,
            Point(960, 640),
            minute,
            minute + policy.HardTtlMinutes,
            policy.InitialStateId
        );
    }

    private static HarmlessProjectionWorldPoint Point(double x, double y)
    {
        return new HarmlessProjectionWorldPoint(x, y);
    }

    private sealed class RecordingSpawnFactory : IHarmlessProjectionSpawnFactory
    {
        private readonly Queue<bool> outcomes;

        internal RecordingSpawnFactory(params bool[] outcomes)
        {
            this.outcomes = new Queue<bool>(outcomes);
        }

        internal List<HarmlessProjectionSpawnRequest> Requests { get; } = new();

        public HarmlessProjectionSpawnResult TrySpawn(HarmlessProjectionSpawnRequest request)
        {
            Requests.Add(request);
            var succeeds = outcomes.Count == 0 || outcomes.Dequeue();
            return succeeds
                ? HarmlessProjectionSpawnResult.Spawned(
                    new HarmlessProjectionInstance(
                        request.Owner,
                        request.Policy,
                        Point(
                            request.OwnerStandingWorldPixel.X + 64,
                            request.OwnerStandingWorldPixel.Y
                        ),
                        request.GameMinute,
                        request.GameMinute + request.Policy.HardTtlMinutes,
                        request.Policy.InitialStateId
                    )
                )
                : HarmlessProjectionSpawnResult.Failed(
                    "sanity.resource.consumer-facade-unavailable"
                );
        }
    }

    private sealed class FixedExitHook : IHarmlessProjectionExitHook
    {
        private readonly HarmlessProjectionExitResolution resolution;

        internal FixedExitHook(HarmlessProjectionExitResolution resolution)
        {
            this.resolution = resolution;
        }

        public HarmlessProjectionExitResolution Resolve(
            HarmlessProjectionInstance instance,
            HarmlessProjectionCleanupReason requestedReason
        )
        {
            return resolution;
        }
    }

    private sealed class SequenceRandom : IHarmlessProjectionRandom
    {
        private readonly Queue<double> values;

        internal SequenceRandom(params double[] values)
        {
            this.values = new Queue<double>(values);
        }

        public double NextUnitDouble()
        {
            return values.Count > 0 ? values.Dequeue() : 0d;
        }
    }

    private sealed class RecordingMap : IHarmlessProjectionMapCapability
    {
        internal List<string> Calls { get; } = new();

        internal bool TileOnMap { get; init; } = true;

        public bool IsTileOnMap(int tileX, int tileY)
        {
            Calls.Add("bounds");
            return TileOnMap;
        }

        public bool IsTileLocationOpen(int tileX, int tileY)
        {
            Calls.Add("open");
            return true;
        }

        public bool IsTilePassable(int tileX, int tileY)
        {
            Calls.Add("passable");
            return true;
        }

        public bool IsAnchorVisible(HarmlessProjectionWorldPoint worldPixel)
        {
            Calls.Add("anchor");
            return true;
        }
    }
}
