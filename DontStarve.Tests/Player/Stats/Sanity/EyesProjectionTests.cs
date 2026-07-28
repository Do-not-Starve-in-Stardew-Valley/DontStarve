using System.Text.Json;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class EyesProjectionTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    [Fact]
    public void PolicyUsesFrozenEyesTierFiveToFifteenRingAndOrdinaryLimits()
    {
        var policy = EyesProjectionContract.CreatePolicy();

        Assert.Equal("sanity.projection.eyes", policy.SpeciesId);
        Assert.Equal(SanityTierIds.Eyes, policy.TierId);
        Assert.Equal(5, policy.MinimumDistanceTiles);
        Assert.Equal(15, policy.MaximumDistanceTiles);
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
        Assert.Equal(EyesProjectionContract.AnimationProfileId, policy.VisualProfileId);
        Assert.Equal(EyesProjectionContract.BlinkStateId, policy.InitialStateId);
        Assert.Equal(EyesProjectionContract.BlinkStateId, policy.VisualSlotId);
        Assert.Collection(
            policy.VisualStates,
            state => Assert.Equal(EyesProjectionContract.BlinkStateId, state.StateId)
        );

        var names = typeof(EyesProjectionBehavior)
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
    public void ShippedBlinkMetadataIsTheSingleFourFramePlaceholderContract()
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
                == EyesProjectionContract.AnimationProfileId
            );
        var state = profile.GetProperty("States").EnumerateArray().Single();
        var policyState = EyesProjectionContract.CreatePolicy().VisualStates.Single();

        Assert.Equal(
            EyesProjectionContract.TextureSlotId,
            profile.GetProperty("TextureSlotId").GetString()
        );
        Assert.Equal(64, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(32, profile.GetProperty("FrameHeight").GetInt32());
        Assert.Equal("None", profile.GetProperty("DirectionMode").GetString());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.Equal(
            EyesProjectionContract.BlinkStateId,
            state.GetProperty("AnimationId").GetString()
        );
        Assert.Equal(0, state.GetProperty("Row").GetInt32());
        Assert.Equal(
            state.GetProperty("FrameCount").GetInt32(),
            policyState.FrameCount
        );
        Assert.Equal(
            state.GetProperty("FrameDurationMs").GetInt32(),
            policyState.FrameDurationMilliseconds
        );
        Assert.Equal(state.GetProperty("Loop").GetBoolean(), policyState.Loop);
        Assert.Equal(4, policyState.FrameCount);
        Assert.Equal(200, policyState.FrameDurationMilliseconds);
        Assert.True(policyState.Loop);
        Assert.Equal(
            new SanityResourcePoint(32, 16),
            policyState.ExpectedPivotSourcePx
        );
        Assert.Equal(1d, policyState.ExpectedDrawScale);
        Assert.Equal("None", state.GetProperty("Mirror").GetString());
        Assert.Equal("Screen", state.GetProperty("SortLayer").GetString());
        Assert.Empty(state.GetProperty("AllowedEmptyFrames").EnumerateArray());
        Assert.True(state.GetProperty("IsProvisional").GetBoolean());
        Assert.Contains(
            "DEV placeholder",
            state.GetProperty("ProvisionalReason").GetString(),
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void ResourceFacadeLoadsBlinkFramesFromOneLoaderOwnedSheet()
    {
        var factory = new FakeResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(
            ShippedModRoot,
            factory
        );
        var state = EyesProjectionContract.CreatePolicy().VisualStates.Single();
        var resources = Enumerable
            .Range(0, state.FrameCount)
            .Select(frame => loader.LoadSlot(state.VisualSlotId, frame))
            .ToArray();

        Assert.All(resources, resource => Assert.True(resource.Success));
        Assert.True(state.TryValidateResource(resources[0], out var reason), reason);
        Assert.All(
            resources,
            resource =>
            {
                Assert.Same(resources[0].PhysicalResource, resource.PhysicalResource);
                Assert.True(resource.VisualPreview!.IsPlaceholder);
                Assert.True(resource.VisualPreview.IsProvisional);
                Assert.Equal(4, resource.VisualPreview.FrameCount);
            }
        );
        Assert.Equal(
            new[] { 0, 64, 128, 192 },
            resources.Select(resource => resource.VisualPreview!.SourceRectangle.X)
        );
        Assert.All(
            resources,
            resource =>
                Assert.Equal(0, resource.VisualPreview!.SourceRectangle.Y)
        );
        Assert.Equal(1, factory.TextureCreateCount);

        var sheet = PngRgbaImage.Decode(
            Path.Combine(
                ShippedModRoot,
                "Asset",
                "Sanity",
                "Sprites",
                "Illusions",
                "eyes.png"
            )
        );
        Assert.Equal(256, sheet.Width);
        Assert.Equal(32, sheet.Height);
    }

    [Fact]
    public void PlaceholderIdentityReachesRuntimeStatusAndBehaviorLabel()
    {
        var factory = new FakeResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(
            ShippedModRoot,
            factory
        );
        var policy = EyesProjectionContract.CreatePolicy();
        var resource = loader.LoadSlot(EyesProjectionContract.BlinkStateId);
        var owner = Owner();
        var instance = new HarmlessProjectionInstance(
            owner,
            policy,
            Point(640, 0),
            100,
            120,
            EyesProjectionContract.BlinkStateId,
            visualStateResources: new Dictionary<string, SanitySlotResourceResult>
            {
                [EyesProjectionContract.BlinkStateId] = resource,
            }
        );
        var behavior = Behavior();

        var updated = behavior.Update(instance, owner, Point(0, 0), 1);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, updated.Status);
        Assert.Equal("eyes.blink.placeholder", instance.BehaviorLabel);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.True(snapshot.IsPlaceholder);
        Assert.True(snapshot.IsProvisional);
        Assert.Equal(
            EnvironmentLightEvidenceStatus.Confirmed,
            snapshot.EvidenceStatus
        );
    }

    [Fact]
    public void ExactSixtyEntersAndOnlyAboveSixtyExits()
    {
        var machine = new SanityTierStateMachine();
        machine.Observe(Player(121d, 200d, 0), true);

        var exact = machine.Observe(Player(120d, 200d, 1), true);
        var stillExact = machine.Observe(Player(120d, 200d, 2), true);
        var above = machine.Observe(Player(120.0001d, 200d, 3), true);

        Assert.Contains(
            SanityStateEventIds.TierEntered(SanityTierIds.Eyes),
            exact.Events.Select(stateEvent => stateEvent.EventId)
        );
        Assert.DoesNotContain(
            stillExact.Events,
            stateEvent => stateEvent.TierId == SanityTierIds.Eyes
        );
        Assert.Contains(
            SanityStateEventIds.TierExited(SanityTierIds.Eyes),
            above.Events.Select(stateEvent => stateEvent.EventId)
        );
    }

    [Fact]
    public void OnlySyntheticConfirmedPitchBlackProvesTheSpawnConsumer()
    {
        var probe = new MutableLightProbe(ConfirmedBlack());
        var behavior = Behavior(probe);

        Assert.True(behavior.CanSpawn(Request(), out var reason), reason);
        Assert.Equal("eyes.spawn-confirmed-pitch-black", reason);

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
            probe.Result = result;
            Assert.False(behavior.CanSpawn(Request(), out reason));
            Assert.Equal("eyes.spawn-light-not-confirmed", reason);
        }
    }

    [Fact]
    public void DimFallbackCannotExitOrMasqueradeAsPitchBlack()
    {
        var probe = new MutableLightProbe(
            EnvironmentLightResult.Fallback("test.dim-fallback", 100)
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(owner);

        var updated = behavior.Update(instance, owner, Point(0, 0), 200);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, updated.Status);
        Assert.Equal("eyes.blink-light-unconfirmed", updated.Reason);
        Assert.Null(updated.CleanupReason);
        Assert.Equal(-1, instance.FacingX);
        Assert.Equal(1, instance.CurrentFrameIndex);
        Assert.True(behavior.TryGetRuntimeSnapshot(instance, out var snapshot));
        Assert.Equal(EnvironmentLightLevel.Dim, snapshot.LightLevel);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, snapshot.EvidenceStatus);
    }

    [Fact]
    public void BlinkAlwaysFacesTheExactOwnerWithoutMovingItsAnchor()
    {
        var behavior = Behavior();
        var owner = Owner();
        var instance = Instance(owner);
        var before = instance.SpawnWorldPixel;

        behavior.Update(instance, owner, Point(0, 0), 0);
        Assert.Equal(-1, instance.FacingX);
        Assert.Equal(before, instance.SpawnWorldPixel);

        behavior.Update(instance, owner, Point(1280, 0), 0);
        Assert.Equal(1, instance.FacingX);
        Assert.Equal(before, instance.SpawnWorldPixel);
        Assert.Equal(before, instance.OriginWorldPixel);
    }

    [Theory]
    [InlineData(EnvironmentLightReasonIds.BaseWhiteConfirmed)]
    [InlineData(EnvironmentLightReasonIds.NightVisionActiveConfirmed)]
    public void ConfirmedLightOrNightVisionRequestsImmediateCleanup(string lightReason)
    {
        var probe = new MutableLightProbe(
            EnvironmentLightResult.Confirmed(
                EnvironmentLightLevel.Lit,
                lightReason,
                100
            )
        );
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(owner);

        var updated = behavior.Update(instance, owner, Point(0, 0), 200);

        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
            updated.Status
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.LightRestored,
            updated.CleanupReason
        );
        Assert.Equal(0, instance.CurrentFrameIndex);
    }

    [Fact]
    public void ExactOneTileOwnerProximityRequestsCleanupBeforeSamplingLight()
    {
        var probe = new MutableLightProbe(ConfirmedBlack());
        var behavior = Behavior(probe);
        var owner = Owner();
        var instance = Instance(owner);

        var updated = behavior.Update(instance, owner, Point(576, 0), 200);

        Assert.Equal(
            HarmlessProjectionSpeciesUpdateStatus.CleanupRequested,
            updated.Status
        );
        Assert.Equal(
            HarmlessProjectionCleanupReason.OwnerApproached,
            updated.CleanupReason
        );
        Assert.Equal(0, probe.ObserveCount);
    }

    [Fact]
    public void FourFramesAdvanceAtMetadataCadenceAndLoop()
    {
        var behavior = Behavior();
        var owner = Owner();
        var instance = Instance(owner);

        behavior.Update(instance, owner, Point(0, 0), 199);
        Assert.Equal(0, instance.CurrentFrameIndex);
        behavior.Update(instance, owner, Point(0, 0), 1);
        Assert.Equal(1, instance.CurrentFrameIndex);
        behavior.Update(instance, owner, Point(0, 0), 200);
        Assert.Equal(2, instance.CurrentFrameIndex);
        behavior.Update(instance, owner, Point(0, 0), 200);
        Assert.Equal(3, instance.CurrentFrameIndex);
        behavior.Update(instance, owner, Point(0, 0), 200);
        Assert.Equal(0, instance.CurrentFrameIndex);
    }

    [Fact]
    public void TierEnteredAttemptsImmediatelyThenCapAndHardTtlUseTwentyMinutes()
    {
        var policy = EyesProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        Assert.True(scheduler.RegisterPolicy(policy, Behavior(), out _));
        scheduler.SetTierActive("1", SanityTierIds.Eyes, true);
        var owner = Owner();
        var factory = new RecordingSpawnFactory(success: true);

        var entered = scheduler.UpdateOwner(owner, Point(0, 0), 100, factory);
        var capped = scheduler.UpdateOwner(owner, Point(0, 0), 119, factory);
        var expired = scheduler.UpdateOwner(owner, Point(0, 0), 120, factory);

        Assert.Equal(1, entered.AttemptCount);
        Assert.Equal(0, capped.AttemptCount);
        Assert.Equal(1, expired.ExpiredCount);
        Assert.Equal(1, expired.AttemptCount);
        Assert.Equal(2, factory.AttemptCount);
        Assert.Equal(
            HarmlessProjectionCleanupReason.HardTtlExpired,
            factory.SpawnedInstances[0].CleanupReason
        );
        Assert.Equal(1, scheduler.Index.Count);
    }

    [Fact]
    public void FailedSpawnAlsoWaitsTwentyInternalMinutes()
    {
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(
            EyesProjectionContract.CreatePolicy(),
            Behavior(),
            out _
        );
        scheduler.SetTierActive("1", SanityTierIds.Eyes, true);
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
    public void TierExitClearsImmediatelyAndCannotLeaveTheOwnerSlotOccupied()
    {
        var policy = EyesProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(policy, Behavior(), out _);
        scheduler.SetTierActive("1", SanityTierIds.Eyes, true);
        var owner = Owner();
        scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var active));

        scheduler.SetTierActive("1", SanityTierIds.Eyes, false);
        var exited = scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            101,
            new RecordingSpawnFactory(success: true)
        );

        Assert.Equal(HarmlessProjectionCleanupReasonIds.TierExited, exited.Reason);
        Assert.False(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(HarmlessProjectionCleanupReason.TierExited, active!.CleanupReason);
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
    [InlineData((int)HarmlessProjectionCleanupReason.OwnerInvalidated)]
    [InlineData((int)HarmlessProjectionCleanupReason.WorldCleanup)]
    public void GenericLifecycleReasonsRemainImmediateHardCleanup(int reasonValue)
    {
        var reason = (HarmlessProjectionCleanupReason)reasonValue;
        var policy = EyesProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(
            new HarmlessProjectionIndex()
        );
        scheduler.RegisterPolicy(policy, Behavior(), out _);
        scheduler.SetTierActive("1", SanityTierIds.Eyes, true);
        var owner = Owner();
        scheduler.UpdateOwner(
            owner,
            Point(0, 0),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var active));

        scheduler.CleanupAll(reason);

        Assert.False(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(reason, active!.CleanupReason);
    }

    [Fact]
    public void SpectatorsCannotSampleAdvanceFaceOrExitTheOwnerProjection()
    {
        var probe = new MutableLightProbe(ConfirmedLit());
        var behavior = Behavior(probe);
        var location = new object();
        var owner = Owner("1", 0, location);
        var instance = Instance(owner);
        var before = instance.SpawnWorldPixel;

        var results = new[]
        {
            behavior.Update(instance, Owner("2", 0, location), before, 200),
            behavior.Update(instance, Owner("1", 1, location), before, 200),
            behavior.Update(instance, Owner("1", 0, new object()), before, 200),
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
        Assert.Equal(before, instance.SpawnWorldPixel);
    }

    [Fact]
    public void ProductConsumesTheExistingLightServiceCacheWithoutASecondScan()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "EyesProjection"
        );
        var runtime = File.ReadAllText(
            Path.Combine(root, "SmapiEyesProjectionRuntime.cs")
        );

        Assert.Equal(15, EnvironmentLightCache.SampleCadenceTicks);
        Assert.Equal(1, Count(runtime, "lightService.Evaluate("));
        Assert.Contains("EnvironmentLightService", runtime, StringComparison.Ordinal);
        Assert.Contains("Final lightmap evidence and PitchBlack authorization remain upstream", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new EnvironmentLightService", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentLightClassifier", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotProvider", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLightSources", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedLights", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetDiagnostic", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void EyesSourcesExposeNoSharedWorldWriteOrOutOfScopeFeaturePath()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "EyesProjection"
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
                "SpawnEye",
                "SanityMonsterIntensity",
                "SanityShadowSpawnPermit",
                "SanityShadowBudgetGovernor",
                "references/",
                "TestPackage/",
                "SoundEffect",
                "shake",
            }
        )
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("eyes.png", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Asset/Sanity", combined, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightService", combined, StringComparison.Ordinal);
        Assert.Contains("IHarmlessProjectionWorldRenderer", combined, StringComparison.Ordinal);
    }

    private static EyesProjectionBehavior Behavior(
        MutableLightProbe? probe = null
    )
    {
        return new EyesProjectionBehavior(
            probe ?? new MutableLightProbe(ConfirmedBlack())
        );
    }

    private static HarmlessProjectionSpawnRequest Request()
    {
        return new HarmlessProjectionSpawnRequest(
            Owner(),
            EyesProjectionContract.CreatePolicy(),
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
        HarmlessProjectionOwnerContext owner
    )
    {
        var policy = EyesProjectionContract.CreatePolicy();
        return new HarmlessProjectionInstance(
            owner,
            policy,
            Point(640, 0),
            100,
            120,
            EyesProjectionContract.BlinkStateId
        );
    }

    private static EnvironmentLightResult ConfirmedBlack()
    {
        // Synthetic confirmed darkness proves this consumer only. It is not a claim that the
        // current location classifier can produce this result from real game evidence.
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

    private sealed class MutableLightProbe : IEyesEnvironmentLightProbe
    {
        internal MutableLightProbe(EnvironmentLightResult result)
        {
            Result = result;
        }

        internal EnvironmentLightResult Result { get; set; }

        internal int ObserveCount { get; private set; }

        public EnvironmentLightResult Observe(
            HarmlessProjectionOwnerContext owner
        )
        {
            ObserveCount++;
            return Result;
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

        internal List<HarmlessProjectionInstance> SpawnedInstances { get; } =
            new();

        public HarmlessProjectionSpawnResult TrySpawn(
            HarmlessProjectionSpawnRequest request
        )
        {
            AttemptCount++;
            if (!success)
                return HarmlessProjectionSpawnResult.Failed("test.spawn-failed");
            var instance = new HarmlessProjectionInstance(
                request.Owner,
                request.Policy,
                Point(640, 0),
                request.GameMinute,
                request.GameMinute + request.Policy.HardTtlMinutes,
                request.Policy.InitialStateId
            );
            SpawnedInstances.Add(instance);
            return HarmlessProjectionSpawnResult.Spawned(instance);
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
