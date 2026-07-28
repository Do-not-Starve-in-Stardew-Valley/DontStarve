using System.Text.Json;
using DontStarve.Config;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Config;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarkHandProjectionTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    [Fact]
    public void PolicyUsesFrozenTierRingCadenceCapTtlAndFiveStateSlots()
    {
        var policy = DarkHandProjectionContract.CreatePolicy();

        Assert.Equal("sanity.projection.dark-hand", policy.SpeciesId);
        Assert.Equal(SanityTierIds.DarkHand, policy.TierId);
        Assert.Equal(10, policy.MinimumDistanceTiles);
        Assert.Equal(20, policy.MaximumDistanceTiles);
        Assert.Equal(20, policy.AttemptIntervalMinutes);
        Assert.Equal(1, policy.ActiveCap);
        Assert.Equal(40, policy.HardTtlMinutes);
        Assert.Equal(16, policy.CandidateAttemptLimit);
        Assert.False(policy.ClearOnTierExit);
        Assert.Equal(HarmlessProjectionPlacementKind.Ground, policy.PlacementKind);
        Assert.Equal(
            DontStarve.Player.Stats.Sanity.Illusions.Projection.HarmlessProjectionBudgetLane
                .OrdinaryPerOwnerPerSpecies,
            policy.BudgetLane
        );
        Assert.Equal(DarkHandProjectionContract.AnimationProfileId, policy.VisualProfileId);
        Assert.Equal(DarkHandProjectionContract.AppearStateId, policy.InitialStateId);
        Assert.Equal(
            new[]
            {
                DarkHandProjectionContract.AppearStateId,
                DarkHandProjectionContract.ApproachStateId,
                DarkHandProjectionContract.RetreatStateId,
                DarkHandProjectionContract.ReturnStateId,
                DarkHandProjectionContract.DisappearStateId,
            },
            policy.VisualStates.Select(state => state.StateId)
        );

        var names = typeof(DarkHandProjectionBehavior)
            .GetMembers()
            .Select(member => member.Name)
            .Concat(typeof(HarmlessProjectionPolicy).GetProperties().Select(property => property.Name))
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Intensity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Permit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("ShadowBudget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShippedProfileMatchesAllFivePlaceholderProvisionalRows()
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
                == DarkHandProjectionContract.AnimationProfileId
            );

        Assert.Equal(DarkHandProjectionContract.TextureSlotId, profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal(192, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(176, profile.GetProperty("FrameHeight").GetInt32());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        AssertState(states, DarkHandProjectionContract.AppearStateId, 0, 275, loop: false);
        AssertState(states, DarkHandProjectionContract.ApproachStateId, 1, 300, loop: true);
        AssertState(states, DarkHandProjectionContract.ReturnStateId, 2, 175, loop: false);
        AssertState(states, DarkHandProjectionContract.RetreatStateId, 3, 275, loop: false);
        AssertState(states, DarkHandProjectionContract.DisappearStateId, 4, 90, loop: false);
    }

    [Fact]
    public void ResourceFacadeLoadsFiveStatesFromOneLoaderOwnedTexture()
    {
        var factory = new FakeResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var policy = DarkHandProjectionContract.CreatePolicy();
        var resources = policy.VisualStates
            .Select(state => (State: state, Resource: loader.LoadSlot(state.VisualSlotId, 0)))
            .ToArray();

        Assert.All(
            resources,
            pair =>
            {
                Assert.True(
                    pair.State.TryValidateResource(pair.Resource, out var reason),
                    reason
                );
                Assert.True(pair.Resource.VisualPreview!.IsPlaceholder);
                Assert.True(pair.Resource.VisualPreview.IsProvisional);
            }
        );
        Assert.All(resources, pair => Assert.Same(resources[0].Resource.PhysicalResource, pair.Resource.PhysicalResource));
        Assert.Equal(1, factory.TextureCreateCount);
    }

    [Fact]
    public void ExactSeventyFiveEntersAndOnlyAboveSeventyFiveExits()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(151d, 200d, 0), true);

        var exact = machine.Observe(Player(150d, 200d, 1), true);
        var stillExact = machine.Observe(Player(150d, 200d, 2), true);
        var above = machine.Observe(Player(150.0001d, 200d, 3), true);

        Assert.Contains(
            SanityStateEventIds.TierEntered(SanityTierIds.DarkHand),
            exact.Events.Select(stateEvent => stateEvent.EventId)
        );
        Assert.DoesNotContain(
            stillExact.Events,
            stateEvent => stateEvent.TierId == SanityTierIds.DarkHand
        );
        Assert.Contains(
            SanityStateEventIds.TierExited(SanityTierIds.DarkHand),
            above.Events.Select(stateEvent => stateEvent.EventId)
        );
    }

    [Fact]
    public void ModeOffRejectsSpawnAndSafelyCleansAnActiveProjection()
    {
        var mode = new MutableModeResolver(DarkHandProjectionMode.Off);
        var behavior = Behavior(mode: mode);
        var request = Request();

        Assert.False(behavior.CanSpawn(request, out var reason));
        Assert.Equal("dark-hand.mode-off", reason);

        mode.Mode = DarkHandProjectionMode.FireThief;
        Assert.True(behavior.CanSpawn(request, out reason), reason);
        var owner = request.Owner;
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);
        behavior.Update(instance, owner, Point(0, 0), 16);
        mode.Mode = DarkHandProjectionMode.Off;

        var disabled = behavior.Update(instance, owner, Point(0, 0), 16);
        Assert.True(disabled.ShouldCleanup);
        Assert.Equal(HarmlessProjectionCleanupReason.ConfigDisabled, disabled.CleanupReason);
        Assert.Equal("Off", instance.BehaviorLabel);
    }

    [Theory]
    [InlineData("Off", "Off", false)]
    [InlineData("FireThief", "Fire Thief", true)]
    [InlineData("Harassment", "Harassment", true)]
    [InlineData("Thief", "Thief", true)]
    public void TypedResolverConsumesTheCurrentDarkHandModeEnumExactly(
        string configuredValue,
        string expectedLabel,
        bool allowsVisual
    )
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var file = new MemoryFlatConfigFileAccess(
            $"{{\"DarkHandMode\":\"{configuredValue}\"}}"
        );
        var store = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(registry, file).Store
        );
        var mode = new TypedConfigDarkHandModeResolver(
            new TypedConfigResolver(registry, store)
        ).Resolve();

        Assert.True(mode.IsAvailable, mode.Reason);
        Assert.Equal(configuredValue, mode.Mode.ToString());
        Assert.Equal(expectedLabel, mode.BehaviorLabel);
        Assert.Equal(allowsVisual, mode.AllowsVisual);
        Assert.Equal(0, file.WriteCount);
    }

    [Theory]
    [InlineData("FireThief", "Fire Thief")]
    [InlineData("Harassment", "Harassment")]
    [InlineData("Thief", "Thief")]
    public void NonOffModesOnlyChangeTheDisplayableVisualLabel(
        string configuredModeId,
        string expectedLabel
    )
    {
        var configuredMode = Enum.Parse<DarkHandProjectionMode>(configuredModeId);
        var mode = new MutableModeResolver(configuredMode);
        var target = new RecordingTargetObserver(
            DarkHandLocalTargetObservation.Unavailable("test.no-target")
        );
        var behavior = Behavior(mode, target: target);
        var owner = Owner();
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);
        var before = instance.SpawnWorldPixel;

        var result = behavior.Update(instance, owner, Point(0, 0), 100);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, result.Status);
        Assert.Equal(expectedLabel, instance.BehaviorLabel);
        Assert.True(instance.SpawnWorldPixel.X < before.X);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(configuredMode, snapshot.Mode);
        Assert.Equal(expectedLabel, snapshot.BehaviorLabel);
    }

    [Fact]
    public void SuccessFailureCadenceCapAndHardTtlUseTheDarkHandPolicy()
    {
        var behavior = Behavior();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        var policy = DarkHandProjectionContract.CreatePolicy();
        Assert.True(scheduler.RegisterPolicy(policy, behavior, out _));
        scheduler.SetTierActive("1", SanityTierIds.DarkHand, true);
        var owner = Owner();
        var failedFactory = new RecordingSpawnFactory(success: false);

        Assert.Equal(1, scheduler.UpdateOwner(owner, Point(0, 0), 100, failedFactory).AttemptCount);
        Assert.Equal(0, scheduler.UpdateOwner(owner, Point(0, 0), 119, failedFactory).AttemptCount);
        Assert.Equal(1, scheduler.UpdateOwner(owner, Point(0, 0), 120, failedFactory).AttemptCount);

        var schedulerWithActive = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        schedulerWithActive.RegisterPolicy(policy, behavior, out _);
        schedulerWithActive.SetTierActive("1", SanityTierIds.DarkHand, true);
        var successFactory = new RecordingSpawnFactory(success: true);
        schedulerWithActive.UpdateOwner(owner, Point(0, 0), 100, successFactory);
        Assert.Equal(0, schedulerWithActive.UpdateOwner(owner, Point(0, 0), 139, successFactory).AttemptCount);
        var expiry = schedulerWithActive.UpdateOwner(owner, Point(0, 0), 140, successFactory);
        Assert.Equal(1, expiry.ExpiredCount);
        Assert.Equal(1, expiry.AttemptCount);
    }

    [Fact]
    public void OwnerApproachRetreatsReturnsToOriginThenDisappears()
    {
        var behavior = Behavior();
        var owner = Owner();
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);
        var origin = instance.OriginWorldPixel;

        var approached = behavior.Update(instance, owner, origin, 16);
        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Transitioning, approached.Status);
        Assert.Equal(DarkHandProjectionContract.RetreatStateId, instance.StateId);
        Assert.Equal(HarmlessProjectionCleanupReason.OwnerApproached, instance.PendingExitReason);

        behavior.Update(instance, owner, origin, 1100);
        Assert.Equal(DarkHandProjectionContract.ReturnStateId, instance.StateId);
        Assert.NotEqual(origin, instance.SpawnWorldPixel);
        behavior.Update(instance, owner, origin, 1100);
        Assert.Equal(DarkHandProjectionContract.DisappearStateId, instance.StateId);
        Assert.Equal(origin, instance.SpawnWorldPixel);
        var completed = behavior.Update(instance, owner, origin, 360);
        Assert.True(completed.ShouldCleanup);
        Assert.Equal(HarmlessProjectionCleanupReason.DarkHandReturned, completed.CleanupReason);
    }

    [Fact]
    public void ConfirmedLightStartsRetreatWhileFallbackDimDoesNot()
    {
        var owner = Owner();
        var light = new MutableLightProbe(EnvironmentLightResult.Fallback("test.dim", 100));
        var behavior = Behavior(light: light);
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);

        var dim = behavior.Update(instance, owner, Point(0, 0), 16);
        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, dim.Status);
        Assert.Equal(DarkHandProjectionContract.ApproachStateId, instance.StateId);

        light.Result = EnvironmentLightResult.Confirmed(
            EnvironmentLightLevel.Lit,
            EnvironmentLightReasonIds.BaseWhiteConfirmed,
            101
        );
        var lit = behavior.Update(instance, owner, Point(0, 0), 16);
        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Transitioning, lit.Status);
        Assert.Equal(DarkHandProjectionContract.RetreatStateId, instance.StateId);
        Assert.Equal(HarmlessProjectionCleanupReason.LightRestored, instance.PendingExitReason);
    }

    [Fact]
    public void TierRecoveryStopsNewSpawnsButDoesNotForceDeleteTheActiveInstance()
    {
        var behavior = Behavior();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        var policy = DarkHandProjectionContract.CreatePolicy();
        scheduler.RegisterPolicy(policy, behavior, out _);
        scheduler.SetTierActive("1", SanityTierIds.DarkHand, true);
        var owner = Owner();
        var factory = new RecordingSpawnFactory(success: true);
        scheduler.UpdateOwner(owner, Point(0, 0), 100, factory);
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var active));

        scheduler.SetTierActive("1", SanityTierIds.DarkHand, false);
        var inactive = scheduler.UpdateOwner(owner, Point(0, 0), 101, factory);
        Assert.Equal(0, inactive.ExpiredCount);
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var retained));
        Assert.Same(active, retained);
        Assert.Null(active!.CleanupReason);
        Assert.Null(active.PendingExitReason);
        Assert.Equal(
            HarmlessProjectionExitResolution.RetainForSpeciesTransition,
            behavior.Resolve(active, HarmlessProjectionCleanupReason.TierExited)
        );
        Assert.Equal(DarkHandProjectionContract.AppearStateId, active.StateId);

        scheduler.SetTierActive("1", SanityTierIds.DarkHand, true);
        Assert.Equal(0, scheduler.UpdateOwner(owner, Point(0, 0), 102, factory).AttemptCount);
        Assert.Equal(1, factory.AttemptCount);
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
    public void GenericLifecycleReasonsRemainHardCleanup(int reasonValue)
    {
        var reason = (HarmlessProjectionCleanupReason)reasonValue;
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        var policy = DarkHandProjectionContract.CreatePolicy();
        scheduler.RegisterPolicy(policy, Behavior(), out _);
        scheduler.SetTierActive("1", SanityTierIds.DarkHand, true);
        var owner = Owner();
        scheduler.UpdateOwner(owner, Point(0, 0), 100, new RecordingSpawnFactory(true));
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var active));

        scheduler.CleanupAll(reason);

        Assert.False(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(reason, active!.CleanupReason);
    }

    [Fact]
    public void SpectatorsCannotAdvanceMoveProbeOrExitTheOwnerProjection()
    {
        var mode = new MutableModeResolver(DarkHandProjectionMode.FireThief);
        var light = new MutableLightProbe(EnvironmentLightResult.Fallback("test.dim", 100));
        var target = new RecordingTargetObserver(
            DarkHandLocalTargetObservation.Unavailable("test.no-target")
        );
        var behavior = Behavior(mode, light, target);
        var location = new object();
        var owner = Owner("1", 0, location);
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);
        var before = instance.SpawnWorldPixel;

        var results = new[]
        {
            behavior.Update(instance, Owner("2", 0, location), before, 1000),
            behavior.Update(instance, Owner("1", 1, location), before, 1000),
            behavior.Update(instance, Owner("1", 0, new object()), before, 1000),
        };

        Assert.All(results, result => Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.IgnoredObserver, result.Status));
        Assert.Equal(before, instance.SpawnWorldPixel);
        Assert.Equal(0, instance.CurrentFrameIndex);
        Assert.Null(instance.PendingExitReason);
        Assert.Equal(0, mode.ResolveCount);
        Assert.Equal(0, light.ObserveCount);
        Assert.Equal(0, target.ObserveCount);
    }

    [Fact]
    public void FrozenTargetSummaryIsCadenceBoundedAndOnlyChangesLocalDirection()
    {
        var target = new RecordingTargetObserver(
            DarkHandLocalTargetObservation.FrozenExplainable(
                "test.frozen-summary",
                Point(128, 0),
                "test.frozen-explainable"
            )
        );
        var behavior = Behavior(target: target);
        var owner = Owner();
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);

        behavior.Update(instance, owner, Point(0, 0), 100);
        var afterFirst = instance.SpawnWorldPixel;
        behavior.Update(instance, owner, Point(0, 0), 899);
        Assert.Equal(1, target.ObserveCount);
        behavior.Update(instance, owner, Point(0, 0), 101);

        Assert.Equal(2, target.ObserveCount);
        Assert.True(afterFirst.X < instance.OriginWorldPixel.X);
        Assert.Equal(-1, instance.FacingX);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(DarkHandLocalTargetStatus.FrozenExplainable, snapshot.TargetStatus);
        Assert.Equal(Point(128, 0), snapshot.TargetWorldPixel);
    }

    [Fact]
    public void ReachingBoundTargetSubmitsOneOwnerLocalCommitAndShowsPrivateFeedback()
    {
        var target = new RecordingTargetObserver(
            DarkHandLocalTargetObservation.FrozenExplainable(
                "dark-hand.target.fixture",
                DarkHandFireOperationIds.Extinguish,
                7,
                Point(640, 0),
                "dark-hand.target-private-lease-requested"
            ),
            new DarkHandLocalActionFeedback(
                Submitted: true,
                Applied: true,
                "Applied",
                DarkHandFireReasonIds.CommitApplied
            )
        );
        var behavior = Behavior(target: target);
        var owner = Owner();
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);

        var result = behavior.Update(instance, owner, Point(0, 0), 100);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Transitioning, result.Status);
        Assert.Equal(1, target.CommitCount);
        Assert.Equal("dark-hand.target.fixture", target.LastTargetId);
        Assert.Equal(DarkHandFireOperationIds.Extinguish, target.LastOperationId);
        Assert.Equal(7, target.LastRevision);
        Assert.Equal("Fire Thief · Applied", instance.BehaviorLabel);
        Assert.Equal(DarkHandFireReasonIds.CommitApplied, result.Reason);
    }

    [Fact]
    public void UnknownOrInvalidTargetIsRejectedAndUsesOwnerDirectedVisualFallback()
    {
        var target = new RecordingTargetObserver(
            DarkHandLocalTargetObservation.Rejected("dark-hand.target-unknown-machine")
        );
        var behavior = Behavior(target: target);
        var owner = Owner();
        var instance = Instance(owner, DarkHandProjectionContract.ApproachStateId);

        behavior.Update(instance, owner, Point(0, 0), 100);

        Assert.True(instance.SpawnWorldPixel.X < instance.OriginWorldPixel.X);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(DarkHandLocalTargetStatus.Rejected, snapshot.TargetStatus);
        Assert.Null(snapshot.TargetWorldPixel);
        Assert.Equal("dark-hand.target-unknown-machine", snapshot.TargetReason);
    }

    [Fact]
    public void DarkHandSourcesExposeNoWorldMutationLeaseOrLegacyCritterPath()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Contracts", "DarkHandProjection");
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
                "location.Objects",
                "heldObject",
                "MinutesUntilReady",
                "LeaseRequest",
                "nonce",
                "critterTexture",
                "SanityMonsterIntensity",
                "SanityShadowSpawnPermit",
                "SanityShadowBudgetGovernor",
            }
        )
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }

        var targetProperties = typeof(DarkHandLocalTargetObservation)
            .GetProperties()
            .Select(property => property.PropertyType.FullName ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(targetProperties, type => type.Contains("StardewValley", StringComparison.Ordinal));
        Assert.DoesNotContain(targetProperties, type => type.Contains("Object", StringComparison.Ordinal));
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
        var state = states.Single(value => value.GetProperty("AnimationId").GetString() == animationId);
        Assert.Equal(row, state.GetProperty("Row").GetInt32());
        Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
        Assert.Equal(duration, state.GetProperty("FrameDurationMs").GetInt32());
        Assert.Equal(loop, state.GetProperty("Loop").GetBoolean());
        Assert.Equal(96, state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32());
        Assert.Equal(172, state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32());
        Assert.Equal(1d, state.GetProperty("DrawScale").GetDouble());
        Assert.True(state.GetProperty("IsProvisional").GetBoolean());
    }

    private static DarkHandProjectionBehavior Behavior(
        MutableModeResolver? mode = null,
        MutableLightProbe? light = null,
        IDarkHandLocalTargetObserver? target = null
    )
    {
        return new DarkHandProjectionBehavior(
            mode ?? new MutableModeResolver(DarkHandProjectionMode.FireThief),
            light ?? new MutableLightProbe(EnvironmentLightResult.Fallback("test.dim", 100)),
            target ?? new RecordingTargetObserver(
                DarkHandLocalTargetObservation.Unavailable("test.no-target")
            )
        );
    }

    private static HarmlessProjectionSpawnRequest Request()
    {
        return new HarmlessProjectionSpawnRequest(
            Owner(),
            DarkHandProjectionContract.CreatePolicy(),
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
        var policy = DarkHandProjectionContract.CreatePolicy();
        return new HarmlessProjectionInstance(
            owner,
            policy,
            Point(640, 0),
            100,
            140,
            stateId
        );
    }

    private static HarmlessProjectionWorldPoint Point(double x, double y)
    {
        return new HarmlessProjectionWorldPoint(x, y);
    }

    private static SanityPlayerSnapshot Player(double current, double maximum, long revision)
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = "1",
            Current = current,
            Maximum = maximum,
            Revision = revision,
        };
    }

    private sealed class MutableModeResolver : IDarkHandModeResolver
    {
        internal MutableModeResolver(DarkHandProjectionMode mode)
        {
            Mode = mode;
        }

        internal DarkHandProjectionMode Mode { get; set; }

        internal int ResolveCount { get; private set; }

        public DarkHandModeResolution Resolve()
        {
            ResolveCount++;
            var label = Mode switch
            {
                DarkHandProjectionMode.Off => "Off",
                DarkHandProjectionMode.FireThief => "Fire Thief",
                DarkHandProjectionMode.Harassment => "Harassment",
                DarkHandProjectionMode.Thief => "Thief",
                _ => "Off",
            };
            return new DarkHandModeResolution(true, Mode, label, "test.mode");
        }
    }

    private sealed class MutableLightProbe : IDarkHandEnvironmentLightProbe
    {
        internal MutableLightProbe(EnvironmentLightResult result)
        {
            Result = result;
        }

        internal EnvironmentLightResult Result { get; set; }

        internal int ObserveCount { get; private set; }

        public EnvironmentLightResult Observe(HarmlessProjectionOwnerContext owner)
        {
            ObserveCount++;
            return Result;
        }
    }

    private sealed class RecordingTargetObserver : IDarkHandLocalTargetObserver
    {
        private readonly DarkHandLocalTargetObservation observation;
        private readonly DarkHandLocalActionFeedback feedback;

        internal RecordingTargetObserver(
            DarkHandLocalTargetObservation observation,
            DarkHandLocalActionFeedback? feedback = null
        )
        {
            this.observation = observation;
            this.feedback = feedback
                ?? new DarkHandLocalActionFeedback(
                    Submitted: false,
                    Applied: false,
                    "No action",
                    "dark-hand.test-no-operation"
                );
        }

        internal int ObserveCount { get; private set; }
        internal int CommitCount { get; private set; }
        internal string LastTargetId { get; private set; } = string.Empty;
        internal string LastOperationId { get; private set; } = string.Empty;
        internal long LastRevision { get; private set; }

        public DarkHandLocalTargetObservation Observe(
            HarmlessProjectionOwnerContext owner,
            HarmlessProjectionWorldPoint ownerStandingWorldPixel,
            DarkHandProjectionMode mode
        )
        {
            ObserveCount++;
            return observation;
        }

        public DarkHandLocalActionFeedback Commit(
            HarmlessProjectionOwnerContext owner,
            string targetId,
            string operationId,
            long targetRevision
        )
        {
            CommitCount++;
            LastTargetId = targetId;
            LastOperationId = operationId;
            LastRevision = targetRevision;
            return feedback;
        }

        public bool TryTakeFeedback(
            HarmlessProjectionOwnerContext owner,
            string targetId,
            string operationId,
            out DarkHandLocalActionFeedback feedback
        )
        {
            feedback = default;
            return false;
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

        public HarmlessProjectionSpawnResult TrySpawn(HarmlessProjectionSpawnRequest request)
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

        public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
        {
            TextureCreateCount++;
            return SanityPhysicalResourceCreationResult.Created(
                new FakePhysicalResource(SanityPhysicalResourceKind.Texture, path)
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
        {
            return SanityPhysicalResourceCreationResult.Created(
                new FakePhysicalResource(SanityPhysicalResourceKind.SoundEffect, path)
            );
        }
    }

    private sealed class FakePhysicalResource : ISanityPhysicalResource
    {
        internal FakePhysicalResource(SanityPhysicalResourceKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }

        public SanityPhysicalResourceKind Kind { get; }

        public string Path { get; }

        public void Dispose() { }
    }
}
