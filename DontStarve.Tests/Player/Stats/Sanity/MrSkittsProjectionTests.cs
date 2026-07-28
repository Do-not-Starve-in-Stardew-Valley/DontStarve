using System.Text.Json;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MrSkittsProjectionTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    [Fact]
    public void PolicyUsesFrozenOwnerOnlyTierRingCadenceCapAndTtl()
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();

        Assert.Equal("sanity.projection.mr-skitts", policy.SpeciesId);
        Assert.Equal(SanityTierIds.MrSkitts, policy.TierId);
        Assert.Equal(5, policy.MinimumDistanceTiles);
        Assert.Equal(10, policy.MaximumDistanceTiles);
        Assert.Equal(20, policy.AttemptIntervalMinutes);
        Assert.Equal(1, policy.ActiveCap);
        Assert.Equal(20, policy.HardTtlMinutes);
        Assert.Equal(16, policy.CandidateAttemptLimit);
        Assert.True(policy.ClearOnTierExit);
        Assert.Equal(HarmlessProjectionPlacementKind.Ground, policy.PlacementKind);
        Assert.Equal(
            DontStarve.Player.Stats.Sanity.Illusions.Projection.HarmlessProjectionBudgetLane
                .OrdinaryPerOwnerPerSpecies,
            policy.BudgetLane
        );
        Assert.Equal(
            MrSkittsProjectionContract.AnimationProfileId,
            policy.VisualProfileId
        );
        Assert.Equal(
            MrSkittsProjectionContract.IdleAnimationId,
            policy.InitialStateId
        );
        Assert.Equal(2, policy.VisualStates.Count);

        var memberNames = typeof(MrSkittsProjectionBehavior)
            .GetMembers()
            .Select(member => member.Name)
            .Concat(typeof(HarmlessProjectionPolicy).GetProperties().Select(value => value.Name))
            .ToArray();
        Assert.DoesNotContain(memberNames, value => value.Contains("Intensity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, value => value.Contains("Permit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, value => value.Contains("ShadowBudget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, value => value.Contains("EnvironmentLight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShippedProfileBindsExactIdleDisappearMetadataAndKeepsPlaceholderVisible()
    {
        var path = Path.Combine(
            ShippedModRoot,
            "Asset",
            "Sanity",
            "Data",
            "animations.json"
        );
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var profile = document
            .RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .Single(value => value.GetProperty("AnimationProfileId").GetString() == MrSkittsProjectionContract.AnimationProfileId);

        Assert.Equal(
            MrSkittsProjectionContract.TextureSlotId,
            profile.GetProperty("TextureSlotId").GetString()
        );
        Assert.Equal(184, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(112, profile.GetProperty("FrameHeight").GetInt32());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        AssertState(states, MrSkittsProjectionContract.IdleAnimationId, 0, 420, true);
        AssertState(states, MrSkittsProjectionContract.DisappearAnimationId, 1, 100, false);
    }

    [Fact]
    public void ResourceFacadeResultsShareTextureAndMatchBothStateContracts()
    {
        var factory = new FakeResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var idle = loader.LoadSlot(MrSkittsProjectionContract.IdleAnimationId, frameIndex: 0);
        var disappear = loader.LoadSlot(
            MrSkittsProjectionContract.DisappearAnimationId,
            frameIndex: 0
        );

        Assert.True(policy.TryGetVisualState(MrSkittsProjectionContract.IdleAnimationId, out var idleState));
        Assert.True(idleState!.TryValidateResource(idle, out var idleReason), idleReason);
        Assert.True(policy.TryGetVisualState(MrSkittsProjectionContract.DisappearAnimationId, out var disappearState));
        Assert.True(disappearState!.TryValidateResource(disappear, out var disappearReason), disappearReason);
        Assert.Same(idle.PhysicalResource, disappear.PhysicalResource);
        Assert.Equal(1, factory.TextureCreateCount);
        Assert.True(idle.VisualPreview!.IsPlaceholder);
        Assert.True(idle.VisualPreview.IsProvisional);
        Assert.True(disappear.VisualPreview!.IsPlaceholder);
        Assert.True(disappear.VisualPreview.IsProvisional);
        Assert.Equal(new SanityResourceRectangle(0, 0, 184, 112), idle.VisualPreview.SourceRectangle);
        Assert.Equal(new SanityResourceRectangle(0, 112, 184, 112), disappear.VisualPreview.SourceRectangle);
    }

    [Fact]
    public void TierEnteredAttemptsImmediatelyThenCapAndHardTtlUseTwentyMinuteCadence()
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var behavior = new MrSkittsProjectionBehavior();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        Assert.True(scheduler.RegisterPolicy(policy, behavior, out _));
        Assert.Equal(1, scheduler.SetTierActive("1", SanityTierIds.MrSkitts, true));
        var owner = Owner("1", 0, new object(), "Farm");
        var factory = new RecordingSpawnFactory(success: true);

        var entered = scheduler.UpdateOwner(owner, Point(640, 640), 100, factory);
        Assert.Equal(HarmlessProjectionSchedulerStatus.Attempted, entered.Status);
        Assert.Equal(1, entered.AttemptCount);
        Assert.Equal(1, entered.SpawnedCount);
        Assert.True(scheduler.TryGetSchedule("1", policy.SpeciesId, out var schedule));
        Assert.Equal(120, schedule.NextAttemptMinute);

        var capped = scheduler.UpdateOwner(owner, Point(640, 640), 119, factory);
        Assert.Equal(HarmlessProjectionSchedulerStatus.AtCap, capped.Status);
        Assert.Equal(1, factory.AttemptCount);
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var first));

        var expiry = scheduler.UpdateOwner(owner, Point(640, 640), 120, factory);
        Assert.Equal(1, expiry.ExpiredCount);
        Assert.Equal(1, expiry.AttemptCount);
        Assert.Equal(1, expiry.SpawnedCount);
        Assert.Equal(2, factory.AttemptCount);
        Assert.Equal(HarmlessProjectionCleanupReason.HardTtlExpired, first!.CleanupReason);
    }

    [Fact]
    public void FailedSpawnAlsoWaitsTwentyInternalMinutes()
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        Assert.True(scheduler.RegisterPolicy(policy, new MrSkittsProjectionBehavior(), out _));
        scheduler.SetTierActive("1", SanityTierIds.MrSkitts, true);
        var owner = Owner("1", 0, new object(), "Farm");
        var factory = new RecordingSpawnFactory(success: false);

        Assert.Equal(1, scheduler.UpdateOwner(owner, Point(640, 640), 100, factory).AttemptCount);
        Assert.Equal(0, scheduler.UpdateOwner(owner, Point(640, 640), 119, factory).AttemptCount);
        Assert.Equal(1, scheduler.UpdateOwner(owner, Point(640, 640), 120, factory).AttemptCount);
        Assert.Equal(2, factory.AttemptCount);
    }

    [Fact]
    public void SelectorUsesOnlyBoundedFiveToTenTileCandidates()
    {
        var selector = new HarmlessProjectionSpawnPointSelector();
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var origin = Point(640, 640);
        var found = selector.Select(
            origin,
            policy,
            new FixedMapCapability(accept: true),
            new SequenceRandom(0d, 0d)
        );

        Assert.True(found.Success, found.Reason);
        Assert.Equal(1, found.Attempts);
        Assert.NotNull(found.WorldPixel);
        Assert.Equal(5d, DistanceTiles(origin, found.WorldPixel!.Value), precision: 8);

        var rejected = selector.Select(
            origin,
            policy,
            new FixedMapCapability(accept: false),
            new SequenceRandom(Enumerable.Repeat(0d, 32).ToArray())
        );
        Assert.False(rejected.Success);
        Assert.Equal(16, rejected.Attempts);
        Assert.Equal("spawn.no-legal-point", rejected.Reason);
    }

    [Fact]
    public void ExactOwnerProximityRunsDisappearBeforeCleanup()
    {
        var owner = Owner("1", 0, new object(), "Farm");
        var instance = Instance(owner, minute: 100);
        var behavior = new MrSkittsProjectionBehavior();
        var exactThreshold = Point(
            instance.SpawnWorldPixel.X + MrSkittsProjectionContract.OwnerProximityPixels,
            instance.SpawnWorldPixel.Y
        );

        var entered = behavior.Update(instance, owner, exactThreshold, elapsedMilliseconds: 16);
        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Transitioning, entered.Status);
        Assert.Equal(MrSkittsProjectionContract.DisappearAnimationId, instance.StateId);
        Assert.Equal(HarmlessProjectionCleanupReason.OwnerApproached, instance.PendingExitReason);
        Assert.False(behavior.Update(instance, owner, exactThreshold, 399).ShouldCleanup);
        Assert.Equal(3, instance.CurrentFrameIndex);
        var completed = behavior.Update(instance, owner, exactThreshold, 1);
        Assert.True(completed.ShouldCleanup);
        Assert.Equal(HarmlessProjectionCleanupReason.OwnerApproached, completed.CleanupReason);
    }

    [Fact]
    public void SpectatorsAtSameCoordinateCannotTriggerOrAdvanceTheProjection()
    {
        var location = new object();
        var owner = Owner("1", 0, location, "Farm");
        var instance = Instance(owner, minute: 100);
        var behavior = new MrSkittsProjectionBehavior();
        var sameCoordinate = instance.SpawnWorldPixel;

        var otherPlayer = behavior.Update(
            instance,
            Owner("2", 0, location, "Farm"),
            sameCoordinate,
            420
        );
        var otherScreen = behavior.Update(
            instance,
            Owner("1", 1, location, "Farm"),
            sameCoordinate,
            420
        );
        var otherLocation = behavior.Update(
            instance,
            Owner("1", 0, new object(), "Farm"),
            sameCoordinate,
            420
        );

        Assert.All(
            new[] { otherPlayer, otherScreen, otherLocation },
            result => Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.IgnoredObserver, result.Status)
        );
        Assert.Equal(MrSkittsProjectionContract.IdleAnimationId, instance.StateId);
        Assert.Equal(0, instance.CurrentFrameIndex);
        Assert.Null(instance.PendingExitReason);

        var inputSurface = typeof(IHarmlessProjectionSpeciesBehavior)
            .GetMethod(nameof(IHarmlessProjectionSpeciesBehavior.Update))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToArray();
        Assert.DoesNotContain(inputSurface, value => value.Contains("Attack", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inputSurface, value => value.Contains("Damage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inputSurface, value => value.Contains("Farmer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inputSurface, value => value.Contains("Collection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TierExitUsesDisappearHookAndRetainsFirstCleanupReason()
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var behavior = new MrSkittsProjectionBehavior();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        scheduler.RegisterPolicy(policy, behavior, out _);
        scheduler.SetTierActive("1", SanityTierIds.MrSkitts, true);
        var owner = Owner("1", 0, new object(), "Farm");
        scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var instance));

        scheduler.SetTierActive("1", SanityTierIds.MrSkitts, false);
        var exited = scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            101,
            new RecordingSpawnFactory(success: true)
        );
        Assert.Equal("cleanup.deferred-to-species-hook", exited.Reason);
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(MrSkittsProjectionContract.DisappearAnimationId, instance!.StateId);
        Assert.Equal(HarmlessProjectionCleanupReason.TierExited, instance.PendingExitReason);

        Assert.Equal(
            HarmlessProjectionExitResolution.RetainForSpeciesTransition,
            behavior.Resolve(instance, HarmlessProjectionCleanupReason.OwnerApproached)
        );
        Assert.Equal(HarmlessProjectionCleanupReason.TierExited, instance.PendingExitReason);
        var completed = behavior.Update(instance, owner, Point(640, 640), 400);
        Assert.True(completed.ShouldCleanup);
        Assert.True(
            scheduler.RequestSoftExit(
                owner,
                policy.SpeciesId,
                completed.CleanupReason!.Value,
                null,
                out var cleanupReason
            )
        );
        Assert.Equal(HarmlessProjectionCleanupReasonIds.TierExited, cleanupReason);
        Assert.Equal(HarmlessProjectionCleanupReason.TierExited, instance.CleanupReason);
    }

    [Fact]
    public void TierExitAtExpiryCannotExtendTheHardTtlForDisappear()
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();
        var behavior = new MrSkittsProjectionBehavior();
        var scheduler = new HarmlessProjectionScheduler(new HarmlessProjectionIndex());
        scheduler.RegisterPolicy(policy, behavior, out _);
        scheduler.SetTierActive("1", SanityTierIds.MrSkitts, true);
        var owner = Owner("1", 0, new object(), "Farm");
        scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            100,
            new RecordingSpawnFactory(success: true)
        );
        Assert.True(scheduler.Index.TryGet(owner, policy.SpeciesId, out var instance));

        scheduler.SetTierActive("1", SanityTierIds.MrSkitts, false);
        var expired = scheduler.UpdateOwner(
            owner,
            Point(640, 640),
            120,
            new RecordingSpawnFactory(success: true)
        );

        Assert.Equal(1, expired.ExpiredCount);
        Assert.Equal(HarmlessProjectionCleanupReasonIds.HardTtlExpired, expired.Reason);
        Assert.False(scheduler.Index.TryGet(owner, policy.SpeciesId, out _));
        Assert.Equal(HarmlessProjectionCleanupReason.HardTtlExpired, instance!.CleanupReason);
        Assert.Null(instance.PendingExitReason);
    }

    [Fact]
    public void IdleAnimationLoopsWithoutResourceOrWorldInput()
    {
        var owner = Owner("1", 0, new object(), "Farm");
        var instance = Instance(owner, minute: 100);
        var behavior = new MrSkittsProjectionBehavior();
        var farPoint = Point(instance.SpawnWorldPixel.X + 128, instance.SpawnWorldPixel.Y);

        Assert.Equal(HarmlessProjectionSpeciesUpdateStatus.Active, behavior.Update(instance, owner, farPoint, 420).Status);
        Assert.Equal(1, instance.CurrentFrameIndex);
        behavior.Update(instance, owner, farPoint, 1260);
        Assert.Equal(0, instance.CurrentFrameIndex);
        Assert.Equal(MrSkittsProjectionContract.IdleAnimationId, instance.StateId);
    }

    private static void AssertState(
        IReadOnlyList<JsonElement> states,
        string animationId,
        int row,
        int duration,
        bool loop
    )
    {
        var state = states.Single(
            value => value.GetProperty("AnimationId").GetString() == animationId
        );
        Assert.Equal(row, state.GetProperty("Row").GetInt32());
        Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
        Assert.Equal(duration, state.GetProperty("FrameDurationMs").GetInt32());
        Assert.Equal(loop, state.GetProperty("Loop").GetBoolean());
        Assert.Equal(92, state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32());
        Assert.Equal(108, state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32());
        Assert.Equal(1d, state.GetProperty("DrawScale").GetDouble());
        Assert.Equal("None", state.GetProperty("Mirror").GetString());
        Assert.Equal("World", state.GetProperty("SortLayer").GetString());
        Assert.True(state.GetProperty("IsProvisional").GetBoolean());
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
        long minute
    )
    {
        var policy = MrSkittsProjectionContract.CreatePolicy();
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

    private static double DistanceTiles(
        HarmlessProjectionWorldPoint left,
        HarmlessProjectionWorldPoint right
    )
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return Math.Sqrt((x * x) + (y * y)) / HarmlessProjectionSpawnPointSelector.TileSize;
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
                    Point(request.OwnerStandingWorldPixel.X + 320, request.OwnerStandingWorldPixel.Y),
                    request.GameMinute,
                    request.GameMinute + request.Policy.HardTtlMinutes,
                    request.Policy.InitialStateId
                )
            );
        }
    }

    private sealed class FixedMapCapability : IHarmlessProjectionMapCapability
    {
        private readonly bool accept;

        internal FixedMapCapability(bool accept)
        {
            this.accept = accept;
        }

        public bool IsTileOnMap(int tileX, int tileY) => accept;

        public bool IsTileLocationOpen(int tileX, int tileY) => accept;

        public bool IsTilePassable(int tileX, int tileY) => accept;

        public bool IsAnchorVisible(HarmlessProjectionWorldPoint worldPixel) => accept;
    }

    private sealed class SequenceRandom : IHarmlessProjectionRandom
    {
        private readonly double[] values;
        private int index;

        internal SequenceRandom(params double[] values)
        {
            this.values = values;
        }

        public double NextUnitDouble()
        {
            return values[index++];
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
