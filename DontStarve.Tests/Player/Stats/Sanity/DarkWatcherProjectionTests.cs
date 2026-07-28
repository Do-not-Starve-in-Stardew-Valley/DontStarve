using System.Text.Json;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarkWatcherProjectionTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    [Fact]
    public void PolicyUsesFrozenTierRingCadenceCapTtlAndThreeStates()
    {
        var policy = DarkWatcherProjectionContract.CreatePolicy();

        Assert.Equal("sanity.projection.dark-watcher", policy.SpeciesId);
        Assert.Equal(SanityTierIds.DarkWatcher, policy.TierId);
        Assert.Equal(5, policy.MinimumDistanceTiles);
        Assert.Equal(10, policy.MaximumDistanceTiles);
        Assert.Equal(20, policy.AttemptIntervalMinutes);
        Assert.Equal(1, policy.ActiveCap);
        Assert.Equal(20, policy.HardTtlMinutes);
        Assert.Equal(16, policy.CandidateAttemptLimit);
        Assert.True(policy.ClearOnTierExit);
        Assert.Equal(HarmlessProjectionPlacementKind.Ground, policy.PlacementKind);
        Assert.Equal(
            DontStarve.Player.Stats.Sanity.Illusions.Projection
                .HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
            policy.BudgetLane
        );
        Assert.Equal(
            DarkWatcherProjectionContract.AnimationProfileId,
            policy.VisualProfileId
        );
        Assert.Equal(
            new[]
            {
                DarkWatcherProjectionContract.AppearStateId,
                DarkWatcherProjectionContract.IdleStateId,
                DarkWatcherProjectionContract.DisappearStateId,
            },
            policy.VisualStates.Select(state => state.StateId)
        );

        var names = typeof(DarkWatcherProjectionBehavior)
            .GetMembers()
            .Select(member => member.Name)
            .Concat(
                typeof(HarmlessProjectionPolicy)
                    .GetProperties()
                    .Select(property => property.Name)
            )
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

    [Fact]
    public void ShippedProfileMatchesAppearIdleDisappearPlaceholderMetadata()
    {
        var path = Path.Combine(
            ShippedModRoot,
            "Asset",
            "Sanity",
            "Data",
            "animations.json"
        );
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var profile = document.RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .Single(value =>
                value.GetProperty("AnimationProfileId").GetString()
                == DarkWatcherProjectionContract.AnimationProfileId
            );

        Assert.Equal(
            DarkWatcherProjectionContract.TextureSlotId,
            profile.GetProperty("TextureSlotId").GetString()
        );
        Assert.Equal(904, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(128, profile.GetProperty("FrameHeight").GetInt32());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        AssertState(
            states,
            DarkWatcherProjectionContract.AppearStateId,
            row: 0,
            duration: 510,
            loop: false
        );
        AssertState(
            states,
            DarkWatcherProjectionContract.IdleStateId,
            row: 1,
            duration: 510,
            loop: true
        );
        AssertState(
            states,
            DarkWatcherProjectionContract.DisappearStateId,
            row: 2,
            duration: 150,
            loop: false
        );
    }

    [Fact]
    public void ResourceFacadeLoadsThreeStatesFromOneRuntimeSheet()
    {
        var factory = new FakeResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(
            ShippedModRoot,
            factory
        );
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        var resources = policy.VisualStates
            .Select(state =>
                (
                    State: state,
                    Resource: loader.LoadSlot(state.VisualSlotId, frameIndex: 0)
                )
            )
            .ToArray();

        Assert.All(
            resources,
            pair =>
            {
                Assert.True(
                    pair.State.TryValidateResource(
                        pair.Resource,
                        out var reason
                    ),
                    reason
                );
                Assert.True(pair.Resource.VisualPreview!.IsPlaceholder);
                Assert.True(pair.Resource.VisualPreview.IsProvisional);
                Assert.Equal(
                    new SanityResourceRectangle(
                        0,
                        Array.IndexOf(resources, pair) * 128,
                        904,
                        128
                    ),
                    pair.Resource.VisualPreview.SourceRectangle
                );
            }
        );
        Assert.All(
            resources,
            pair =>
                Assert.Same(
                    resources[0].Resource.PhysicalResource,
                    pair.Resource.PhysicalResource
                )
        );
        Assert.Equal(1, factory.TextureCreateCount);

        var sheet = PngRgbaImage.Decode(
            Path.Combine(
                ShippedModRoot,
                "Asset",
                "Sanity",
                "Sprites",
                "Illusions",
                "dark-watcher.png"
            )
        );
        Assert.Equal(904 * 4, sheet.Width);
        Assert.Equal(128 * 3, sheet.Height);
    }

    [Fact]
    public void ExactSixtyFiveEntersAndOnlyAboveSixtyFiveExits()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(131d, 200d, 0), true);

        var exact = machine.Observe(Player(130d, 200d, 1), true);
        var stillExact = machine.Observe(Player(130d, 200d, 2), true);
        var above = machine.Observe(Player(130.0001d, 200d, 3), true);

        Assert.Contains(
            SanityStateEventIds.TierEntered(SanityTierIds.DarkWatcher),
            exact.Events.Select(stateEvent => stateEvent.EventId)
        );
        Assert.DoesNotContain(
            stillExact.Events,
            stateEvent => stateEvent.TierId == SanityTierIds.DarkWatcher
        );
        Assert.Contains(
            SanityStateEventIds.TierExited(SanityTierIds.DarkWatcher),
            above.Events.Select(stateEvent => stateEvent.EventId)
        );
    }

    [Fact]
    public void OnlyConfirmedPitchBlackAllowsSpawn()
    {
        var probe = new MutableLightProbe(
            Observation(ConfirmedBlack(), direction: Point(0, 0))
        );
        var behavior = Behavior(probe);

        Assert.True(behavior.CanSpawn(Request(), out var reason), reason);
        Assert.Equal("dark-watcher.spawn-confirmed-pitch-black", reason);

        foreach (
            var result in new[]
            {
                EnvironmentLightResult.Fallback("test.dim-fallback", 100),
                EnvironmentLightResult.Confirmed(
                    EnvironmentLightLevel.Dim,
                    "test.dim-confirmed",
                    100
                ),
                ConfirmedLit(),
            }
        )
        {
            probe.Observation = Observation(result);
            Assert.False(behavior.CanSpawn(Request(), out reason));
            Assert.Equal("dark-watcher.spawn-light-not-confirmed", reason);
        }
    }

    [Fact]
    public void ExplainableDirectionChangesOnlyPrivateFacing()
    {
        var probe = new MutableLightProbe(
            Observation(
                ConfirmedBlack(),
                direction: Point(0, 0),
                directionReason: "test.nearest-explainable-light"
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );
        var before = instance.SpawnWorldPixel;

        var updated = behavior.Update(instance, owner, Point(1280, 0), 16);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, updated.Status);
        Assert.Equal(before, instance.SpawnWorldPixel);
        Assert.Equal(-1, instance.FacingX);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(
            DarkWatcherFacingTarget.ExplainableLight,
            snapshot.FacingTarget
        );
        Assert.Equal(Point(0, 0), snapshot.DirectionWorldPixel);
        Assert.Equal("test.nearest-explainable-light", snapshot.DirectionReason);
    }

    [Fact]
    public void MissingDirectionUsesOwnerFacingFallbackWithStableDiagnostic()
    {
        var probe = new MutableLightProbe(
            Observation(
                ConfirmedBlack(),
                directionReason:
                    "dark-watcher.light-direction-position-unavailable-owner-fallback"
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );
        var before = instance.SpawnWorldPixel;

        behavior.Update(instance, owner, Point(0, 0), 16);

        Assert.Equal(before, instance.SpawnWorldPixel);
        Assert.Equal(-1, instance.FacingX);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(
            DarkWatcherFacingTarget.OwnerFallback,
            snapshot.FacingTarget
        );
        Assert.Null(snapshot.DirectionWorldPixel);
        Assert.Equal(
            "dark-watcher.light-direction-position-unavailable-owner-fallback",
            snapshot.DirectionReason
        );
    }

    [Fact]
    public void InvalidDirectionCannotMasqueradeAsExplainableLight()
    {
        var probe = new MutableLightProbe(
            Observation(
                ConfirmedBlack(),
                direction: Point(double.NaN, 0),
                directionReason: "test.claimed-explainable"
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );

        behavior.Update(instance, owner, Point(0, 0), 16);

        Assert.Equal(-1, instance.FacingX);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(
            DarkWatcherFacingTarget.OwnerFallback,
            snapshot.FacingTarget
        );
        Assert.Null(snapshot.DirectionWorldPixel);
        Assert.Equal(
            "dark-watcher.light-direction-invalid-owner-fallback",
            snapshot.DirectionReason
        );
    }

    [Fact]
    public void FallbackLightNeitherDirectsNorExitsAnActiveWatcher()
    {
        var probe = new MutableLightProbe(
            Observation(
                EnvironmentLightResult.Fallback("test.dim-fallback", 100),
                direction: Point(0, 0),
                directionReason: "test.untrusted-direction"
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );

        var updated = behavior.Update(instance, owner, Point(1280, 0), 510);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, updated.Status);
        Assert.Equal(1, instance.FacingX);
        Assert.Null(instance.PendingExitReason);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(DarkWatcherFacingTarget.Resting, snapshot.FacingTarget);
        Assert.Null(snapshot.DirectionWorldPixel);
        Assert.Equal(
            "dark-watcher.light-not-confirmed-resting",
            snapshot.DirectionReason
        );
    }

    [Theory]
    [InlineData(EnvironmentLightReasonIds.BaseWhiteConfirmed)]
    [InlineData(EnvironmentLightReasonIds.NightVisionActiveConfirmed)]
    public void ConfirmedLightOrNightVisionRunsDisappear(string lightReason)
    {
        var probe = new MutableLightProbe(
            Observation(
                EnvironmentLightResult.Confirmed(
                    EnvironmentLightLevel.Lit,
                    lightReason,
                    101
                )
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );

        var lit = behavior.Update(instance, owner, Point(1280, 0), 16);
        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.Transitioning,
            lit.Status
        );
        Assert.Equal(
            DarkWatcherProjectionContract.DisappearStateId,
            instance.StateId
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.LightRestored,
            instance.PendingExitReason
        );

        var completed = behavior.Update(instance, owner, Point(1280, 0), 600);
        Assert.True(completed.ShouldCleanup);
        Assert.Equal(
            HarmlessProjectionCleanupReason.LightRestored,
            completed.CleanupReason
        );
        Assert.Equal(1, probe.ObserveCount);
    }

    [Fact]
    public void ExactOwnerProximityRunsDisappear()
    {
        var behavior = Behavior();
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );
        var exact = Point(
            instance.SpawnWorldPixel.X
                + DarkWatcherProjectionContract.OwnerProximityPixels,
            instance.SpawnWorldPixel.Y
        );

        var approached = behavior.Update(instance, owner, exact, 16);

        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.Transitioning,
            approached.Status
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.OwnerApproached,
            instance.PendingExitReason
        );
        Assert.Equal(
            DarkWatcherProjectionContract.DisappearStateId,
            instance.StateId
        );
    }

    [Fact]
    public void AppearIdleDisappearUseFrozenNonLoopAndLoopTimings()
    {
        var probe = new MutableLightProbe(Observation(ConfirmedBlack()));
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.AppearStateId
        );

        var appearing = behavior.Update(instance, owner, Point(1280, 0), 2039);
        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.Transitioning,
            appearing.Status
        );
        Assert.Equal(
            DarkWatcherProjectionContract.AppearStateId,
            instance.StateId
        );
        Assert.Equal(3, instance.CurrentFrameIndex);

        behavior.Update(instance, owner, Point(1280, 0), 1);
        Assert.Equal(
            DarkWatcherProjectionContract.IdleStateId,
            instance.StateId
        );
        Assert.Equal(0, instance.CurrentFrameIndex);
        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.Active,
            behavior.Update(instance, owner, Point(1280, 0), 510).Status
        );
        Assert.Equal(1, instance.CurrentFrameIndex);

        behavior.Resolve(instance, HarmlessProjectionCleanupReason.TierExited);
        Assert.False(
            behavior.Update(instance, owner, Point(1280, 0), 599).ShouldCleanup
        );
        var completed = behavior.Update(instance, owner, Point(1280, 0), 1);
        Assert.True(completed.ShouldCleanup);
        Assert.Equal(
            HarmlessProjectionCleanupReason.TierExited,
            completed.CleanupReason
        );
    }

    [Fact]
    public void TierEnteredAttemptsImmediatelyThenCapAndHardTtlUseTwentyMinutes()
    {
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        var behavior = Behavior();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        Assert.True(scheduler.RegisterPolicy(policy, behavior, out _));
        Assert.Equal(
            1,
            scheduler.SetTierActive("1", SanityTierIds.DarkWatcher, true)
        );
        var owner = Owner();
        var factory = new RecordingSpawnFactory(success: true);

        var entered = scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            factory
        );
        Assert.Equal(1, entered.AttemptCount);
        Assert.Equal(1, entered.SpawnedCount);
        Assert.True(
            scheduler.TryGetSchedule("1", policy.SpeciesId, out var schedule)
        );
        Assert.Equal(120, schedule.NextAttemptMinute);
        Assert.Equal(
            HarmlessProjectionSchedulerStatus.AtCap,
            scheduler.UpdateOwner(owner, Point(0, 0), 119, factory).Status
        );
        Assert.True(
            scheduler.Index.TryGet(owner, policy.SpeciesId, out var first)
        );

        var expiry = scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            120,
            factory
        );
        Assert.Equal(1, expiry.ExpiredCount);
        Assert.Equal(1, expiry.AttemptCount);
        Assert.Equal(1, expiry.SpawnedCount);
        Assert.Equal(
            HarmlessProjectionCleanupReason.HardTtlExpired,
            first!.CleanupReason
        );
    }

    [Fact]
    public void FailedSpawnAlsoWaitsTwentyInternalMinutes()
    {
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        Assert.True(scheduler.RegisterPolicy(policy, Behavior(), out _));
        scheduler.SetTierActive("1", SanityTierIds.DarkWatcher, true);
        var owner = Owner();
        var factory = new RecordingSpawnFactory(success: false);

        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(0, 0), 100, factory).AttemptCount
        );
        Assert.Equal(
            0,
            scheduler.UpdateOwner(owner, Point(0, 0), 119, factory).AttemptCount
        );
        Assert.Equal(
            1,
            scheduler.UpdateOwner(owner, Point(0, 0), 120, factory).AttemptCount
        );
        Assert.Equal(2, factory.AttemptCount);
    }

    [Fact]
    public void TierExitUsesDisappearButCannotExtendHardTtl()
    {
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        var behavior = Behavior();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(policy, behavior, out _);
        scheduler.SetTierActive("1", SanityTierIds.DarkWatcher, true);
        var owner = Owner();
        scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(
            scheduler.Index.TryGet(owner, policy.SpeciesId, out var instance)
        );

        scheduler.SetTierActive("1", SanityTierIds.DarkWatcher, false);
        var exited = scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            101,
            new RecordingSpawnFactory(success: true)
        );
        Assert.Equal("cleanup.deferred-to-species-hook", exited.Reason);
        Assert.Equal(
            HarmlessProjectionCleanupReason.TierExited,
            instance!.PendingExitReason
        );

        var expiryScheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        expiryScheduler.RegisterPolicy(policy, behavior, out _);
        expiryScheduler.SetTierActive("1", SanityTierIds.DarkWatcher, true);
        expiryScheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(
            expiryScheduler.Index.TryGet(
                owner,
                policy.SpeciesId,
                out var expiring
            )
        );
        expiryScheduler.SetTierActive("1", SanityTierIds.DarkWatcher, false);
        var expired = expiryScheduler.UpdateOwner(
            owner,
            Point(0, 0),
            120,
            new RecordingSpawnFactory(success: true)
        );
        Assert.Equal(1, expired.ExpiredCount);
        Assert.Equal(
            HarmlessProjectionCleanupReason.HardTtlExpired,
            expiring!.CleanupReason
        );
        Assert.False(
            expiryScheduler.Index.TryGet(owner, policy.SpeciesId, out _)
        );
    }

    [Theory]
    [InlineData((int)HarmlessProjectionCleanupReason.ConfigDisabled)]
    [InlineData((int)HarmlessProjectionCleanupReason.EventOverride)]
    [InlineData((int)HarmlessProjectionCleanupReason.OwnerWarped)]
    [InlineData((int)HarmlessProjectionCleanupReason.DayEnding)]
    [InlineData((int)HarmlessProjectionCleanupReason.DayStartedRecovery)]
    [InlineData((int)HarmlessProjectionCleanupReason.ReturnedTitle)]
    [InlineData((int)HarmlessProjectionCleanupReason.ScreenInvalid)]
    [InlineData((int)HarmlessProjectionCleanupReason.LocationInvalid)]
    [InlineData((int)HarmlessProjectionCleanupReason.ResourceInvalidated)]
    [InlineData((int)HarmlessProjectionCleanupReason.WorldCleanup)]
    public void GenericLifecycleReasonsRemainImmediateHardCleanup(int reasonValue)
    {
        var reason = (HarmlessProjectionCleanupReason)reasonValue;
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(policy, Behavior(), out _);
        scheduler.SetTierActive("1", SanityTierIds.DarkWatcher, true);
        var owner = Owner();
        scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(
            scheduler.Index.TryGet(owner, policy.SpeciesId, out var active)
        );

        scheduler.CleanupAll(reason);

        Assert.False(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(reason, active!.CleanupReason);
    }

    [Fact]
    public void SpectatorsCannotSampleAdvanceFaceOrExitTheOwnerProjection()
    {
        var probe = new MutableLightProbe(
            Observation(ConfirmedLit(), direction: Point(0, 0))
        );
        var behavior = Behavior(probe);
        var location = new object();
        var owner = Owner("1", 0, location);
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );
        var before = instance.SpawnWorldPixel;

        var results = new[]
        {
            behavior.Update(
                instance,
                Owner("2", 0, location),
                before,
                510
            ),
            behavior.Update(
                instance,
                Owner("1", 1, location),
                before,
                510
            ),
            behavior.Update(
                instance,
                Owner("1", 0, new object()),
                before,
                510
            ),
        };

        Assert.All(
            results,
            result =>
                Assert.Equal(
                    HarmlessProjectionSpeciesUpdateStatus.IgnoredObserver,
                    result.Status
                )
        );
        Assert.Equal(0, probe.ObserveCount);
        Assert.Equal(0, instance.CurrentFrameIndex);
        Assert.Equal(1, instance.FacingX);
        Assert.Null(instance.PendingExitReason);
    }

    [Fact]
    public void FacingHintNeverMovesTheProjectionAnchor()
    {
        var owner = Owner();
        var instance = Instance(
            owner,
            DarkWatcherProjectionContract.IdleStateId
        );
        var before = instance.SpawnWorldPixel;

        Assert.True(instance.TryFaceToward(Point(0, 0)));
        Assert.Equal(-1, instance.FacingX);
        Assert.Equal(before, instance.SpawnWorldPixel);
        Assert.True(instance.TryFaceToward(Point(1280, 0)));
        Assert.Equal(1, instance.FacingX);
        Assert.Equal(before, instance.SpawnWorldPixel);
    }

    [Fact]
    public void ProductConsumesTheExistingLightServiceCacheWithoutASecondScan()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkWatcherProjection"
        );
        var runtime = File.ReadAllText(
            Path.Combine(root, "SmapiDarkWatcherProjectionRuntime.cs")
        );

        Assert.Equal(15, EnvironmentLightCache.SampleCadenceTicks);
        Assert.Equal(1, Count(runtime, "lightService.Evaluate("));
        Assert.Equal(1, Count(runtime, "lightService.TryGetDiagnostic("));
        Assert.Contains("EnvironmentLightService", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new EnvironmentLightService", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentLightClassifier", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotProvider", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLightSources", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedLights", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherSourcesExposeNoDamageWorldMutationOrLegacyFallbackPath()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkWatcherProjection"
        );
        var sources = Directory.GetFiles(root, "*.cs")
            .Select(File.ReadAllText)
            .ToArray();
        Assert.Equal(3, sources.Length);
        var combined = string.Join("\n", sources);
        foreach (
            var forbidden in new[]
            {
                "location.critters",
                "location.characters",
                "temporarySprites",
                "takeDamage",
                "damageFarmer",
                "changeSanity",
                "critterTexture",
                "SpawnDarkWatcher",
                "SanityMonsterIntensity",
                "SanityShadowSpawnPermit",
                "SanityShadowBudgetGovernor",
                "references/",
                "TestPackage/",
                "SoundEffect",
            }
        )
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }

        var properties = typeof(DarkWatcherLightObservation)
            .GetProperties()
            .Select(property => property.PropertyType.FullName ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(
            properties,
            type => type.Contains("StardewValley", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            properties,
            type => type.Contains("GameLocation", StringComparison.Ordinal)
        );
        Assert.Contains("EnvironmentLightService", combined, StringComparison.Ordinal);
    }

    private static void AssertState(
        IReadOnlyList<JsonElement> states,
        string animationId,
        int row,
        int duration,
        bool loop
    )
    {
        var state = states.Single(value =>
            value.GetProperty("AnimationId").GetString() == animationId
        );
        Assert.Equal(row, state.GetProperty("Row").GetInt32());
        Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
        Assert.Equal(duration, state.GetProperty("FrameDurationMs").GetInt32());
        Assert.Equal(loop, state.GetProperty("Loop").GetBoolean());
        Assert.Equal(
            452,
            state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32()
        );
        Assert.Equal(
            124,
            state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32()
        );
        Assert.Equal(1d, state.GetProperty("DrawScale").GetDouble());
        Assert.True(state.GetProperty("IsProvisional").GetBoolean());
    }

    private static DarkWatcherProjectionBehavior Behavior(
        MutableLightProbe? probe = null
    )
    {
        return new DarkWatcherProjectionBehavior(
            probe
                ?? new MutableLightProbe(
                    Observation(
                        ConfirmedBlack(),
                        directionReason: "test.no-direction"
                    )
                )
        );
    }

    private static HarmlessProjectionSpawnRequest Request()
    {
        return new HarmlessProjectionSpawnRequest(
            Owner(),
            DarkWatcherProjectionContract.CreatePolicy(),
            Point(0, 0),
            100
        );
    }

    private static HarmlessProjectionOwnerContext Owner(
        string playerKey = "1",
        int screenId = 0,
        object? location = null
    )
    {
        return new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location ?? new object(),
            "Farm"
        );
    }

    private static HarmlessProjectionInstance Instance(
        HarmlessProjectionOwnerContext owner,
        string stateId
    )
    {
        var policy = DarkWatcherProjectionContract.CreatePolicy();
        return new HarmlessProjectionInstance(
            owner,
            policy,
            Point(640, 0),
            100,
            120,
            stateId
        );
    }

    private static DarkWatcherLightObservation Observation(
        EnvironmentLightResult result,
        HarmlessProjectionWorldPoint? direction = null,
        string directionReason = "test.direction-unavailable"
    )
    {
        return direction.HasValue
            ? DarkWatcherLightObservation.WithExplainableDirection(
                result,
                direction.Value,
                directionReason
            )
            : DarkWatcherLightObservation.WithoutDirection(
                result,
                directionReason
            );
    }

    private static EnvironmentLightResult ConfirmedBlack()
    {
        // This synthetic result proves only the Watcher consumer contract. The current production
        // classifier still has no rule that can manufacture confirmed pitch black.
        return EnvironmentLightResult.Confirmed(
            EnvironmentLightLevel.PitchBlack,
            "test.synthetic-confirmed-pitch-black",
            100
        );
    }

    private static EnvironmentLightResult ConfirmedLit()
    {
        return EnvironmentLightResult.Confirmed(
            EnvironmentLightLevel.Lit,
            EnvironmentLightReasonIds.BaseWhiteConfirmed,
            100
        );
    }

    private static HarmlessProjectionWorldPoint Point(double x, double y)
    {
        return new HarmlessProjectionWorldPoint(x, y);
    }

    private static SanityPlayerSnapshot Player(
        double current,
        double maximum,
        long revision
    )
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = "1",
            Current = current,
            Maximum = maximum,
            Revision = revision,
        };
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }

    private sealed class MutableLightProbe : IDarkWatcherEnvironmentLightProbe
    {
        internal MutableLightProbe(DarkWatcherLightObservation observation)
        {
            Observation = observation;
        }

        internal DarkWatcherLightObservation Observation { get; set; }

        internal int ObserveCount { get; private set; }

        public DarkWatcherLightObservation Observe(
            HarmlessProjectionOwnerContext owner
        )
        {
            ObserveCount++;
            return Observation;
        }
    }

    private sealed class RecordingSpawnFactory : IHarmlessProjectionSpawnFactory
    {
        private readonly bool success;

        internal RecordingSpawnFactory(bool success)
        {
            this.success = success;
        }

        internal int AttemptCount { get; private set; }

        public HarmlessProjectionSpawnResult TrySpawn(
            HarmlessProjectionSpawnRequest request
        )
        {
            AttemptCount++;
            if (!success)
                return HarmlessProjectionSpawnResult.Failed("test.spawn-failed");
            return HarmlessProjectionSpawnResult.Spawned(
                new HarmlessProjectionInstance(
                    request.Owner,
                    request.Policy,
                    Point(640, 0),
                    request.GameMinute,
                    request.GameMinute + request.Policy.HardTtlMinutes,
                    request.Policy.InitialStateId
                )
            );
        }
    }

    private sealed class FakeResourceFactory : ISanityPhysicalResourceFactory
    {
        internal int TextureCreateCount { get; private set; }

        public SanityPhysicalResourceCreationResult CreateTexture(
            string path,
            byte[] bytes
        )
        {
            TextureCreateCount++;
            return SanityPhysicalResourceCreationResult.Created(
                new FakePhysicalResource(
                    SanityPhysicalResourceKind.Texture,
                    path
                )
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(
            string path,
            byte[] bytes
        )
        {
            return SanityPhysicalResourceCreationResult.Created(
                new FakePhysicalResource(
                    SanityPhysicalResourceKind.SoundEffect,
                    path
                )
            );
        }
    }

    private sealed class FakePhysicalResource : ISanityPhysicalResource
    {
        internal FakePhysicalResource(
            SanityPhysicalResourceKind kind,
            string path
        )
        {
            Kind = kind;
            Path = path;
        }

        public SanityPhysicalResourceKind Kind { get; }

        public string Path { get; }

        public void Dispose() { }
    }
}
