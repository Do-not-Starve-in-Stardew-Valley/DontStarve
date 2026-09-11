using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class ShadowCreatureHarmlessProjectionTests
{
    private const string OwnerA = "1001";
    private const string OwnerB = "2002";

    [Fact]
    public void CatalogFreezesOnlyTwoIdleSpeciesAndFourToSixteenRing()
    {
        var policies = ShadowCreatureHarmlessProjectionCatalog.Policies;

        Assert.Equal(2, policies.Count);
        Assert.Equal(
            new[]
            {
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
                ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId,
            },
            policies.Select(policy => policy.SpeciesId)
        );
        Assert.All(policies, policy =>
        {
            Assert.Equal(4, policy.MinimumDistanceTiles);
            Assert.Equal(16, policy.MaximumDistanceTiles);
            Assert.Equal(16, policy.CandidateAttemptLimit);
            Assert.Equal(HarmlessProjectionPlacementKind.Ground, policy.PlacementKind);
            Assert.EndsWith(".idle", policy.IdleVisualSlotId, StringComparison.Ordinal);
        });
        Assert.Equal(200, policies[0].FrameDurationMilliseconds);
        Assert.Equal(100, policies[0].SpawnFrameDurationMilliseconds);
        Assert.Equal(100, policies[0].MoveFrameDurationMilliseconds);
        Assert.Equal(200, policies[1].FrameDurationMilliseconds);
        Assert.Equal(100, policies[1].SpawnFrameDurationMilliseconds);
        Assert.Equal(100, policies[1].MoveFrameDurationMilliseconds);
    }

    [Theory]
    [InlineData(0, 75d)]
    [InlineData(1, 180d)]
    public void Harmless_shadow_wander_speed_matches_the_same_species_hostile_half_speed(
        int policyIndex,
        double expectedPixelsAfterOneSecond
    )
    {
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            $"wander-speed-{policyIndex}",
            Owner(OwnerA),
            ShadowCreatureHarmlessProjectionCatalog.Policies[policyIndex],
            new HarmlessProjectionWorldPoint(0, 0),
            spawnedAtMinute: 0
        );
        SetWanderTarget(instance, new HarmlessProjectionWorldPoint(1_000, 0));

        var behaviorEvent = instance.AdvanceBehavior(
            elapsedMilliseconds: 1_000,
            playerWorldPixel: new HarmlessProjectionWorldPoint(640, 640)
        );

        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorEvent.None,
            behaviorEvent
        );
        Assert.Equal(expectedPixelsAfterOneSecond, (double)instance.WorldPixel.X, precision: 6);
        Assert.Equal(0d, (double)instance.WorldPixel.Y, precision: 6);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Moving,
            instance.AnimationState
        );
    }

    [Theory]
    [InlineData(0, 150d)]
    [InlineData(1, 360d)]
    public void Harmless_shadow_flee_speed_matches_its_species_hostile_speed(
        int policyIndex,
        double expectedHostilePixelsPerSecond
    )
    {
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            $"flee-speed-{policyIndex}",
            Owner(OwnerA),
            ShadowCreatureHarmlessProjectionCatalog.Policies[policyIndex],
            new HarmlessProjectionWorldPoint(0, 0),
            spawnedAtMinute: 0
        );

        var startedFlee = instance.AdvanceBehavior(
            elapsedMilliseconds: 1,
            playerWorldPixel: new HarmlessProjectionWorldPoint(64, 0)
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorEvent.FleeTriggered,
            startedFlee
        );

        var moving = instance.AdvanceBehavior(
            elapsedMilliseconds: 500,
            playerWorldPixel: new HarmlessProjectionWorldPoint(64, 0)
        );

        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorEvent.None,
            moving
        );
        Assert.Equal(
            -(expectedHostilePixelsPerSecond * 0.5d),
            (double)instance.WorldPixel.X,
            precision: 6
        );
        Assert.Equal(0d, (double)instance.WorldPixel.Y, precision: 6);
    }

    [Fact]
    public void PushBox_capture_is_owner_scoped_and_excludes_binding_projection()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var ownerA = Owner(OwnerA, screenId: 0);
        var ownerB = Owner(OwnerB, screenId: 1);
        var ownerAInstance = AddDirect(index, ownerA, "projection-owner-a", x: 100d);
        var ownerBInstance = AddDirect(index, ownerB, "projection-owner-b", x: 200d);
        var binding = AddDirect(index, ownerA, "projection-binding", x: 300d);
        binding.SetBindingProjection(protectionMilliseconds: 10_000);

        index.BeginPushBoxIntentCapture(ownerA);
        index.CapturePushBoxIntents(ownerA);
        index.BeginPushBoxIntentCapture(ownerB);
        index.CapturePushBoxIntents(ownerB);

        var ownerAIntents = new List<ShadowCreaturePushBoxIntent>();
        Assert.Equal(1, index.CopyPushBoxIntents(ownerA, ownerAIntents));
        Assert.Equal(
            ownerAInstance.CorrelationId,
            Assert.Single(ownerAIntents).CorrelationId
        );

        var allIntents = new List<ShadowCreaturePushBoxIntent>();
        Assert.Equal(2, index.CopyPushBoxIntents(allIntents));
        Assert.Equal(
            new[] { ownerAInstance.CorrelationId, ownerBInstance.CorrelationId },
            allIntents.Select(intent => intent.CorrelationId)
        );
        Assert.DoesNotContain(
            allIntents,
            intent => intent.CorrelationId == binding.CorrelationId
        );
    }

    [Fact]
    public void PushBox_capture_keeps_the_deterministic_256_entry_boundary()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var owner = Owner(OwnerA);
        for (var indexValue = 0; indexValue <= HostileShadowProtocol.MaximumPushBoxEntriesPerBatch; indexValue++)
        {
            AddDirect(
                index,
                owner,
                $"projection-{indexValue:D4}",
                x: indexValue * 10d
            );
        }

        index.BeginPushBoxIntentCapture(owner);
        index.CapturePushBoxIntents(owner);
        var intents = new List<ShadowCreaturePushBoxIntent>();

        Assert.Equal(
            HostileShadowProtocol.MaximumPushBoxEntriesPerBatch,
            index.CopyPushBoxIntents(owner, intents)
        );
        Assert.Equal("projection-0000", intents[0].CorrelationId);
        Assert.Equal("projection-0255", intents[^1].CorrelationId);
        Assert.DoesNotContain(
            intents,
            intent => intent.CorrelationId == "projection-0256"
        );
    }

    [Fact]
    public void PushBox_result_accepts_a_delayed_revision_but_rejects_stale_or_future_revision()
    {
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            "push-box-result",
            Owner(OwnerA),
            ShadowCreatureHarmlessProjectionCatalog.Policies[0],
            new HarmlessProjectionWorldPoint(0d, 0d),
            spawnedAtMinute: 0
        );

        instance.BeginPushBoxIntentCapture();
        Assert.True(instance.CapturePushBoxIntent());
        Assert.True(
            instance.TryApplyHostPushBoxResult(1, 100d, 200d, out var firstReason),
            firstReason
        );

        instance.BeginPushBoxIntentCapture();
        Assert.True(instance.CapturePushBoxIntent());
        Assert.True(
            instance.TryApplyHostPushBoxResult(1, 110d, 210d, out var delayedReason),
            delayedReason
        );
        Assert.Equal(new HarmlessProjectionWorldPoint(110d, 210d), instance.WorldPixel);

        Assert.False(
            instance.TryApplyHostPushBoxResult(3, 300d, 400d, out var futureReason)
        );
        Assert.Equal("shadow-projection.push-box-result-future", futureReason);
        Assert.True(
            instance.TryApplyHostPushBoxResult(2, 120d, 220d, out var currentReason),
            currentReason
        );
        Assert.False(
            instance.TryApplyHostPushBoxResult(1, 130d, 230d, out var staleReason)
        );
        Assert.Equal("shadow-projection.push-box-result-stale", staleReason);
    }

    [Fact]
    public void Binding_projection_cannot_receive_or_emit_a_PushBox_result()
    {
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            "push-box-binding",
            Owner(OwnerA),
            ShadowCreatureHarmlessProjectionCatalog.Policies[0],
            new HarmlessProjectionWorldPoint(0d, 0d),
            spawnedAtMinute: 0
        );
        instance.SetBindingProjection(protectionMilliseconds: 10_000);
        instance.BeginPushBoxIntentCapture();
        Assert.True(instance.CapturePushBoxIntent());
        Assert.False(instance.TryGetPushBoxIntent(out _));
        Assert.False(
            instance.TryApplyHostPushBoxResult(1, 100d, 200d, out var reason)
        );
        Assert.Equal("shadow-projection.push-box-instance-not-applicable", reason);
    }

    [Fact]
    public void Harmless_catalog_registers_taunt_and_death_as_dormant_actions()
    {
        foreach (var policy in ShadowCreatureHarmlessProjectionCatalog.Policies)
        {
            Assert.Equal(
                new[]
                {
                    policy.SpawnVisualSlotId,
                    policy.MoveVisualSlotId,
                    policy.IdleVisualSlotId,
                },
                policy.AppliedVisualSlotIds
            );
            Assert.Equal(
                new[]
                {
                    policy.TauntVisualSlotId,
                    policy.DeathVisualSlotId,
                },
                policy.RegisteredDormantActionVisualSlotIds
            );
            Assert.Contains(policy.TauntVisualSlotId, policy.AllVisualSlotIds);
            Assert.Contains(policy.DeathVisualSlotId, policy.AllVisualSlotIds);
            Assert.DoesNotContain(policy.TauntVisualSlotId, policy.AppliedVisualSlotIds);
            Assert.DoesNotContain(policy.DeathVisualSlotId, policy.AppliedVisualSlotIds);
        }
    }

    [Theory]
    [InlineData(0, "sanity.animation.creeper-fear.idle", "sanity.asset.creeper-fear.sprite", 32, 48)]
    [InlineData(1, "sanity.animation.terrorbeak.idle", "sanity.asset.terrorbeak.sprite", 24, 48)]
    public void ShippedIdleSlotsUseTheExistingLoaderOwnedVisualContract(
        int policyIndex,
        string visualSlotId,
        string textureSlotId,
        int pivotX,
        int pivotY
    )
    {
        using var loader = new SanityRuntimeResourceLoader(
            ShippedModRoot,
            new FakePhysicalResourceFactory()
        );
        var policy = ShadowCreatureHarmlessProjectionCatalog.Policies[policyIndex];

        var result = loader.LoadSlot(policy.IdleVisualSlotId, frameIndex: 0);

        Assert.True(result.Success, result.Diagnostic.Reason);
        Assert.True(policy.TryValidateResource(result, out var reason), reason);
        Assert.Equal("shadow-projection.visual-contract-valid", reason);
        Assert.Equal(visualSlotId, result.VisualPreview!.RequestedSlotId);
        Assert.Equal(textureSlotId, result.VisualPreview.TextureSlotId);
        Assert.Equal(new SanityResourcePoint(pivotX, pivotY), result.VisualPreview.PivotSourcePx);
        Assert.False(result.VisualPreview.OwnerLocalOnly);
        Assert.True(result.VisualPreview.IsPlaceholder);
        Assert.True(result.VisualPreview.IsProvisional);
    }

    [Theory]
    [InlineData(MrSkittsProjectionContract.SpeciesId)]
    [InlineData(DarkHandProjectionContract.SpeciesId)]
    [InlineData(DarkWatcherProjectionContract.SpeciesId)]
    [InlineData(EyesProjectionContract.SpeciesId)]
    public void OrdinarySpeciesCannotConsumeTheSharedShadowPermit(string speciesId)
    {
        var permit = Permit(OwnerA, occupancy: 0, cap: 1, minute: 60);

        var accepted = ShadowCreatureProjectionPermitGate.TryAuthorize(
            speciesId,
            OwnerA,
            gameMinute: 60,
            occupancy: 0,
            permit,
            out var reason
        );

        Assert.False(accepted);
        Assert.Equal("shadow-permit.species-not-authorized", reason);
    }

    [Theory]
    [InlineData(ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId)]
    [InlineData(ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId)]
    public void OnlyFrozenShadowSpeciesCanConsumeAHarmlessPermit(string speciesId)
    {
        var accepted = ShadowCreatureProjectionPermitGate.TryAuthorize(
            speciesId,
            OwnerA,
            gameMinute: 60,
            occupancy: 1,
            Permit(OwnerA, occupancy: 1, cap: 2, minute: 60),
            out var reason
        );

        Assert.True(accepted, reason);
        Assert.Equal("shadow-permit.authorized", reason);
    }

    [Fact]
    public void SharedPoolAlternatesBothSpeciesAndStopsAtOneOwnerCap()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);

        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Waiting,
            harness.Update(owner, minute: 0).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, minute: 60, elapsedMilliseconds: 42_000).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, minute: 120, elapsedMilliseconds: 42_000).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, minute: 180, elapsedMilliseconds: 42_000).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, minute: 240, elapsedMilliseconds: 42_000).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, minute: 300, elapsedMilliseconds: 42_000).Status
        );
        var capped = harness.Update(owner, minute: 301);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.AtCap, capped.Status);
        Assert.Equal(5, capped.Occupancy);
        Assert.Equal(5, harness.Factory.Requests.Count);
        Assert.Contains(
            harness.Factory.Requests,
            request => request.Policy.SpeciesId
                == ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId
        );
        Assert.Contains(
            harness.Factory.Requests,
            request => request.Policy.SpeciesId
                == ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId
        );
    }

    [Fact]
    public void Debug_clear_does_not_immediately_refill_or_disable_natural_refresh()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);

        harness.Update(owner, minute: 0);
        var first = harness.Update(owner, minute: 60, elapsedMilliseconds: 42_000);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Spawned, first.Status);
        Assert.Single(harness.Factory.Requests);
        Assert.Equal(1, harness.Index.CountForOwner(OwnerA));

        var cleared = harness.Coordinator.CleanupAll(
            HarmlessProjectionCleanupReason.OwnerInvalidated,
            clearOwnerPhases: false,
            clearConversionEvidence: false
        );

        Assert.Equal(1, cleared);
        Assert.Equal(0, harness.Index.CountForOwner(OwnerA));

        var afterClear = harness.Update(owner, minute: 60, elapsedMilliseconds: 16);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Waiting, afterClear.Status);
        Assert.Equal(0, afterClear.SpawnedCount);
        Assert.Single(harness.Factory.Requests);

        var due = harness.Update(owner, minute: 60, elapsedMilliseconds: 41_984);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Spawned, due.Status);
        Assert.Equal(2, harness.Factory.Requests.Count);
        Assert.Equal(1, harness.Index.CountForOwner(OwnerA));
    }

    [Fact]
    public void InsaneCapCanHoldNineInstancesAcrossTheTwoSpecies()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Insane);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);

        harness.Update(owner, minute: 0, advanceMovement: false);
        foreach (var minute in new long[] { 30, 60, 90, 120, 150, 180, 210, 240, 270 })
        {
            Assert.Equal(
                ShadowCreatureProjectionUpdateStatus.Spawned,
                harness.Update(
                    owner,
                    minute,
                    elapsedMilliseconds: 21_000,
                    advanceMovement: false
                ).Status
            );
        }
        var capped = harness.Update(owner, minute: 301, advanceMovement: false);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.AtCap, capped.Status);
        Assert.Equal(9, harness.Index.CountForOwner(OwnerA));
        Assert.Equal(9, harness.Factory.Requests.Count);
        Assert.Contains(
            harness.Factory.Requests,
            request => request.Policy.SpeciesId
                == ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId
        );
        Assert.Contains(
            harness.Factory.Requests,
            request => request.Policy.SpeciesId
                == ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId
        );
    }

    [Fact]
    public void TwoOwnersKeepIndependentTimersOccupancyAndCaps()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var ownerA = Owner(OwnerA, screenId: 0, location: new object());
        var ownerB = Owner(OwnerB, screenId: 1, location: new object());
        harness.EnterShadow(OwnerA);
        harness.EnterShadow(OwnerB);

        harness.Update(ownerA, minute: 0);
        harness.Update(ownerB, minute: 20);
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(ownerA, minute: 60, elapsedMilliseconds: 42_000).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Waiting,
            harness.Update(ownerB, minute: 60).Status
        );
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(ownerB, minute: 80, elapsedMilliseconds: 42_000).Status
        );

        Assert.Equal(1, harness.Index.CountForOwner(OwnerA));
        Assert.Equal(1, harness.Index.CountForOwner(OwnerB));
        Assert.Equal(2, harness.Index.Count);
    }

    [Fact]
    public void OnePermitCanCreateAtMostOneLocalProjection()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var factory = new FakeSpawnFactory();
        var authority = new AlwaysPermitAuthority();
        var coordinator = CreateCoordinator(
            index,
            authority,
            new RecordingSink()
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            gameMinute: 0
        );
        var owner = Owner(OwnerA);

        var result = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 60,
            elapsedMilliseconds: 16,
            factory
        );
        var repeatedSameMinute = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 60,
            elapsedMilliseconds: 16,
            factory
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Spawned, result.Status);
        Assert.Equal(1, result.SpawnedCount);
        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Waiting, repeatedSameMinute.Status);
        Assert.Equal("shadow-projection.budget-minute-already-evaluated", repeatedSameMinute.Reason);
        Assert.Equal(1, authority.EvaluateCalls);
        Assert.Single(factory.Requests);
        Assert.Equal(1, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void OwnerApproachFlees_uses_delayed_compensation_without_immediate_vacancy_replacement()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);
        harness.Update(owner, minute: 0, advanceMovement: false);
        harness.Update(
            owner,
            minute: 60,
            elapsedMilliseconds: 42_000,
            advanceMovement: false
        );
        harness.Update(
            owner,
            minute: 120,
            elapsedMilliseconds: 42_000,
            advanceMovement: false
        );
        harness.Update(
            owner,
            minute: 180,
            elapsedMilliseconds: 42_000,
            advanceMovement: false
        );
        harness.Update(
            owner,
            minute: 240,
            elapsedMilliseconds: 42_000,
            advanceMovement: false
        );
        harness.Update(
            owner,
            minute: 300,
            elapsedMilliseconds: 42_000,
            advanceMovement: false
        );
        harness.Update(owner, minute: 301, advanceMovement: false);
        var first = harness.Factory.Instances[0];
        var playerWorldPixel = first.WorldPixel;

        var startedFlee = harness.Coordinator.UpdateOwner(
            owner,
            playerWorldPixel,
            gameMinute: 302,
            elapsedMilliseconds: 16,
            harness.Factory
        );

        Assert.Equal(0, startedFlee.ProximityCleanupCount);
        Assert.Equal(ShadowCreatureProjectionUpdateStatus.AtCap, startedFlee.Status);
        Assert.False(first.IsCleanedUp);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorState.Fleeing,
            first.BehaviorState
        );

        var replacement = harness.Coordinator.UpdateOwner(
            owner,
            playerWorldPixel,
            gameMinute: 303,
            elapsedMilliseconds: 2_000,
            harness.Factory
        );

        Assert.Equal(1, replacement.ProximityCleanupCount);
        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Waiting, replacement.Status);
        Assert.Equal(4, replacement.Occupancy);
        Assert.Equal(5, harness.Factory.Requests.Count);
        Assert.True(first.IsCleanedUp);
        Assert.Equal(HarmlessProjectionCleanupReason.OwnerApproached, first.CleanupReason);

        harness.Update(owner, minute: 303, elapsedMilliseconds: 7_000);

        Assert.Equal(
            new[]
            {
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
            },
            harness.Coordinator.ConsumePendingCompensations(OwnerA)
        );
    }

    [Fact]
    public void ExactOwnerApproachOnlyFleesAndLaterCleansItsOwnProjection()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var ownerA = Owner(OwnerA, screenId: 0, location: new object());
        var ownerB = Owner(OwnerB, screenId: 1, location: new object());
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerB, SanityTierIds.ShadowCreatures),
            0
        );
        var instanceA = AddDirect(index, ownerA, "direct-a", x: 320);
        var instanceB = AddDirect(index, ownerB, "direct-b", x: 320);

        var startedFlee = coordinator.UpdateOwner(
            ownerA,
            instanceA.SpawnWorldPixel,
            gameMinute: 1,
            elapsedMilliseconds: 16,
            new FakeSpawnFactory()
        );

        Assert.Equal(0, startedFlee.ProximityCleanupCount);
        Assert.False(instanceA.IsCleanedUp);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorState.Fleeing,
            instanceA.BehaviorState
        );
        Assert.False(instanceB.IsCleanedUp);

        var completed = coordinator.UpdateOwner(
            ownerA,
            instanceA.SpawnWorldPixel,
            gameMinute: 2,
            elapsedMilliseconds: 2_000,
            new FakeSpawnFactory()
        );

        Assert.Equal(1, completed.ProximityCleanupCount);
        Assert.True(instanceA.IsCleanedUp);
        Assert.False(instanceB.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
        Assert.Equal(1, index.CountForOwner(OwnerB));
    }

    [Fact]
    public void FiftyPercentTierExitFadesOutWithoutCompensationAndIsIdempotent()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);
        harness.Update(owner, 0);
        harness.Update(owner, 60, elapsedMilliseconds: 42_000);
        var instance = harness.Factory.Instances.Single();

        var first = harness.Publish(
            SanityStateEventKind.TierExited,
            OwnerA,
            SanityTierIds.ShadowCreatures,
            minute: 61
        );
        var repeated = harness.Publish(
            SanityStateEventKind.TierExited,
            OwnerA,
            SanityTierIds.ShadowCreatures,
            minute: 61
        );

        Assert.Equal(1, first.LocalCleanupCount);
        Assert.Equal(0, repeated.LocalCleanupCount);
        Assert.Equal(1, harness.Index.CountForOwner(OwnerA));
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionFadeOutKind.HighSan,
            instance.FadeOutKind
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorState.FadingOut,
            instance.BehaviorState
        );

        harness.Update(owner, minute: 61, elapsedMilliseconds: 1000);

        Assert.Equal(0, harness.Index.CountForOwner(OwnerA));
        Assert.True(instance.IsCleanedUp);
        Assert.Empty(harness.Coordinator.ConsumePendingCompensations(OwnerA));
    }

    [Fact]
    public void Command_projection_without_a_shadow_phase_enters_high_sanity_fade()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            sink
        );
        var owner = Owner(OwnerA);
        var instance = AddDirect(index, owner, "command-projection", x: 320);

        var result = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 16,
            new FakeSpawnFactory()
        );

        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Inactive,
            result.Status
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionFadeOutKind.HighSan,
            instance.FadeOutKind
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionBehaviorState.FadingOut,
            instance.BehaviorState
        );
        Assert.Empty(sink.Intents);
    }

    [Fact]
    public void Danger_update_converts_new_projection_before_far_fade_can_remove_it()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);

        coordinator.ApplyStateEvent(
            TierEvent(
                SanityStateEventKind.TierEntered,
                OwnerA,
                SanityTierIds.ShadowCreatures
            ),
            gameMinute: 0
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            gameMinute: 1
        );
        var instance = AddDirect(index, owner, "danger-far-projection", x: 2000);

        var result = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 2,
            elapsedMilliseconds: 1000,
            new FakeSpawnFactory()
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, result.Status);
        Assert.Single(sink.Intents);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(
            HarmlessProjectionCleanupReason.ConversionRequested,
            instance.CleanupReason
        );
        Assert.Empty(coordinator.ConsumePendingCompensations(OwnerA));
    }

    [Fact]
    public void Current_danger_snapshot_rehydrates_missing_phase_for_projection_conversion()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);

        // Models a debug projection registered after the initial tier event was missed by the
        // owner-local coordinator. The host repairs this phase from the current tier snapshot.
        coordinator.SynchronizeOwnerTierState(
            OwnerA,
            shadowTierActive: true,
            dangerTierActive: true
        );
        var instance = AddDirect(index, owner, "danger-snapshot-rehydrated", x: 320);

        var result = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 2,
            elapsedMilliseconds: 1000,
            new FakeSpawnFactory()
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, result.Status);
        Assert.Single(sink.Intents);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void Danger_spawned_projection_waits_for_spawn_animation_before_conversion()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);

        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            gameMinute: 0
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            gameMinute: 1
        );
        var instance = AddDirect(index, owner, "danger-spawn-animation", x: 320);

        var duringSpawn = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 1,
            elapsedMilliseconds: 100,
            new FakeSpawnFactory()
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, duringSpawn.Status);
        Assert.Empty(sink.Intents);
        Assert.False(instance.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Spawning,
            instance.AnimationState
        );

        var afterSpawn = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 1,
            elapsedMilliseconds: 300,
            new FakeSpawnFactory()
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, afterSpawn.Status);
        Assert.Single(sink.Intents);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void High_sanity_far_fade_never_creates_a_compensation()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var owner = Owner(OwnerA);
        var instance = AddDirect(index, owner, "high-sanity-far-projection", x: 2000);

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 1000,
            new FakeSpawnFactory()
        );
        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 7000,
            new FakeSpawnFactory()
        );

        Assert.True(instance.IsCleanedUp);
        Assert.Empty(coordinator.ConsumePendingCompensations(OwnerA));
    }

    [Fact]
    public void Two_same_species_flees_create_two_compensations_after_seven_seconds()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var owner = Owner(OwnerA);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var first = AddDirect(index, owner, "flee-compensation-a", x: 64);
        var second = AddDirect(index, owner, "flee-compensation-b", x: 64);
        CompleteSpawnAnimation(first);
        CompleteSpawnAnimation(second);

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 16,
            new FakeSpawnFactory()
        );
        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 2_000,
            new FakeSpawnFactory()
        );

        Assert.Equal(0, index.CountForOwner(OwnerA));
        Assert.Empty(coordinator.ConsumePendingCompensations(OwnerA));

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 0,
            elapsedMilliseconds: 7_000,
            new FakeSpawnFactory()
        );

        Assert.Equal(
            new[]
            {
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
                ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
            },
            coordinator.ConsumePendingCompensations(OwnerA)
        );
    }

    [Fact]
    public void Warp_compensation_snapshot_does_not_require_the_new_location_context()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var oldOwner = Owner(OwnerA, location: new object());
        var first = AddDirect(index, oldOwner, "warp-compensation-a", x: 320);
        var second = AddDirect(index, oldOwner, "warp-compensation-b", x: 640, policyIndex: 1);

        var snapshot = coordinator.SnapshotOwnerInstances(OwnerA);

        Assert.Equal(
            new[] { first.CorrelationId, second.CorrelationId },
            snapshot.Select(instance => instance.CorrelationId)
        );
        Assert.Equal(
            2,
            coordinator.CountForOwnerAtLocation(
                OwnerA,
                oldOwner.LocationNameOrUniqueName
            )
        );
        Assert.False(
            index.TryGetContextInstances(
                Owner(OwnerA, location: new object()),
                out _
            )
        );
    }

    [Fact]
    public void Natural_refresh_uses_real_elapsed_time_when_game_minute_does_not_advance()
    {
        var harness = new Harness(SanityMonsterIntensityIds.Default);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);

        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Waiting,
            harness.Update(owner, minute: 0, elapsedMilliseconds: 16).Status
        );

        var due = harness.Update(
            owner,
            minute: 0,
            elapsedMilliseconds: 42_000
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Spawned, due.Status);
        Assert.Single(harness.Factory.Instances);
    }

    [Fact]
    public void Direct_shadow_registration_requires_the_active_harmless_pool()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var owner = Owner(OwnerA);
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            "direct-registration",
            owner,
            ShadowCreatureHarmlessProjectionCatalog.Policies[0],
            new HarmlessProjectionWorldPoint(320, 0),
            spawnedAtMinute: 0
        );

        var beforeTier = coordinator.TryRegisterAuthorizedProjection(
            owner,
            instance,
            Permit(OwnerA, occupancy: 0, cap: 1, minute: 0),
            out var beforeReason
        );

        Assert.False(beforeTier);
        Assert.Equal("shadow-projection.shadow-tier-inactive", beforeReason);
        Assert.Equal(0, index.CountForOwner(OwnerA));

        coordinator.ApplyStateEvent(
            TierEvent(
                SanityStateEventKind.TierEntered,
                OwnerA,
                SanityTierIds.ShadowCreatures
            ),
            gameMinute: 0
        );

        var accepted = coordinator.TryRegisterAuthorizedProjection(
            owner,
            instance,
            Permit(OwnerA, occupancy: 0, cap: 1, minute: 0),
            out var acceptedReason
        );

        Assert.True(accepted, acceptedReason);
        Assert.Equal(1, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void Debug_shadow_registration_bypasses_natural_permit_and_phase_checks()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var owner = Owner(OwnerA);
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            "debug-registration",
            owner,
            ShadowCreatureHarmlessProjectionCatalog.Policies[0],
            new HarmlessProjectionWorldPoint(320, 0),
            spawnedAtMinute: 0
        );

        var accepted = coordinator.TryRegisterDebugProjection(
            owner,
            instance,
            out var reason
        );

        Assert.True(accepted, reason);
        Assert.Equal(1, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void Danger_entry_defers_conversion_until_generation_animation_finishes()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);
        var instance = AddDirect(index, owner, "paused-conversion", x: 320);

        coordinator.ApplyStateEvent(
            TierEvent(
                SanityStateEventKind.TierEntered,
                OwnerA,
                SanityTierIds.ShadowCreatures
            ),
            gameMinute: 0
        );
        var firstDangerEntry = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            gameMinute: 1
        );

        Assert.Equal(0, firstDangerEntry.IntentCount);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Spawning,
            instance.AnimationState
        );
        Assert.False(instance.IsCleanedUp);
        Assert.Empty(sink.Intents);
        Assert.Equal(1, index.CountForOwner(OwnerA));

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 1,
            elapsedMilliseconds: 360,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Spawning,
            instance.AnimationState
        );
        Assert.Empty(sink.Intents);
        Assert.False(instance.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 1,
            elapsedMilliseconds: 40,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Single(sink.Intents);
        Assert.Equal(instance.CorrelationId, sink.Intents[0].CorrelationId);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void DangerEntryRemovesEveryLocalProjectionBeforeRecordingUniqueIntents()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink
        {
            OnRecord = _ => Assert.Equal(0, index.CountForOwner(OwnerA)),
        };
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var owner = Owner(OwnerA);
        var first = AddDirect(index, owner, "conversion-a", x: 320, policyIndex: 0);
        var second = AddDirect(index, owner, "conversion-b", x: 640, policyIndex: 1);
        CompleteSpawnAnimation(first);
        CompleteSpawnAnimation(second);

        var result = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            gameMinute: 90
        );

        Assert.Equal(2, result.LocalCleanupCount);
        Assert.Equal(2, result.IntentCount);
        Assert.Equal(2, sink.Intents.Count);
        Assert.Equal(2, sink.Intents.Select(intent => intent.CorrelationId).Distinct().Count());
        Assert.All(sink.Intents, intent => Assert.Equal(OwnerA, intent.PlayerKey));
        Assert.Equal(HarmlessProjectionCleanupReason.ConversionRequested, first.CleanupReason);
        Assert.Equal(HarmlessProjectionCleanupReason.ConversionRequested, second.CleanupReason);
    }

    [Theory]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Confirmed)]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Unconfirmed)]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Delayed)]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Rejected)]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Failed)]
    public void ConversionSubmissionResultNeverRestoresRemovedLocalState(
        int statusValue
    )
    {
        var status = (ShadowProjectionConversionSubmissionStatus)statusValue;
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink { Status = status };
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var removed = AddDirect(index, Owner(OwnerA), "status-correlation", x: 320);
        CompleteSpawnAnimation(removed);

        var first = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            90
        );
        var repeated = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            90
        );

        var evidence = Assert.Single(coordinator.SnapshotConversionEvidence());
        Assert.Equal(status, evidence.Submission.Status);
        Assert.Equal("conversion-requested", evidence.LocalCleanupReasonId);
        var shouldRestore = status
            is ShadowProjectionConversionSubmissionStatus.Rejected
                or ShadowProjectionConversionSubmissionStatus.Failed;
        Assert.Equal(!shouldRestore, removed.IsCleanedUp);
        Assert.Equal(shouldRestore ? 1 : 0, index.Count);
        Assert.Equal(1, first.IntentCount);
        Assert.True(repeated.Duplicate);
        Assert.Equal(0, repeated.IntentCount);
        Assert.Single(sink.Intents);
    }

    [Fact]
    public void ThrowingConversionSinkIsCapturedOnceAsFailedEvidence()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink { Throw = true };
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var throwing = AddDirect(index, Owner(OwnerA), "throwing-correlation", x: 320);
        CompleteSpawnAnimation(throwing);

        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            90
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            90
        );

        var evidence = Assert.Single(coordinator.SnapshotConversionEvidence());
        Assert.Equal(ShadowProjectionConversionSubmissionStatus.Failed, evidence.Submission.Status);
        Assert.Equal("shadow-conversion.sink-threw-InvalidOperationException", evidence.Submission.Reason);
        Assert.Equal(1, sink.CallCount);
        Assert.Equal(1, index.Count);
    }

    [Theory]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Failed)]
    [InlineData((int)ShadowProjectionConversionSubmissionStatus.Rejected)]
    public void Failed_or_rejected_conversion_is_retried_on_the_next_danger_update(
        int firstStatusValue
    )
    {
        var firstStatus = (ShadowProjectionConversionSubmissionStatus)firstStatusValue;
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        sink.StatusSequence.Enqueue(firstStatus);
        sink.StatusSequence.Enqueue(ShadowProjectionConversionSubmissionStatus.Confirmed);
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var instance = AddDirect(index, owner, "retryable-conversion", x: 320);
        CompleteSpawnAnimation(instance);

        var first = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            90
        );

        Assert.Equal(1, first.IntentCount);
        Assert.False(instance.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));
        Assert.Equal(firstStatus, Assert.Single(coordinator.SnapshotConversionEvidence()).Submission.Status);

        var retry = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 90,
            elapsedMilliseconds: 16,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, retry.Status);
        Assert.Equal(2, sink.CallCount);
        Assert.Equal(2, sink.Intents.Count);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
        var evidence = Assert.Single(coordinator.SnapshotConversionEvidence());
        Assert.Equal(ShadowProjectionConversionSubmissionStatus.Confirmed, evidence.Submission.Status);
    }

    [Fact]
    public void Danger_exit_continues_pending_spawn_animation_until_danger_reentry()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var instance = AddDirect(index, owner, "danger-exit-pending-spawn", x: 320);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            1
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierExited, OwnerA, SanityTierIds.Danger),
            2
        );

        var stillSpawning = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 2,
            elapsedMilliseconds: 360,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, stillSpawning.Status);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Spawning,
            instance.AnimationState
        );
        Assert.Empty(sink.Intents);
        Assert.Equal(1, index.CountForOwner(OwnerA));

        var finished = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 2,
            elapsedMilliseconds: 40,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, finished.Status);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Idle,
            instance.AnimationState
        );
        Assert.Empty(sink.Intents);
        Assert.False(instance.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));

        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            3
        );

        Assert.Single(sink.Intents);
        Assert.True(instance.IsCleanedUp);
        Assert.Equal(0, index.CountForOwner(OwnerA));
    }

    [Theory]
    [InlineData(0, 75d)]
    [InlineData(1, 180d)]
    public void Danger_exit_keeps_existing_binding_projection_behavior_alive(
        int policyIndex,
        double expectedPixelsAfterOneSecond
    )
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var coordinator = CreateCoordinator(
            index,
            new NeverPermitAuthority(),
            new RecordingSink()
        );
        var owner = Owner(OwnerA);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            gameMinute: 0
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            gameMinute: 1
        );
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierExited, OwnerA, SanityTierIds.Danger),
            gameMinute: 2
        );

        var instance = AddDirect(
            index,
            owner,
            $"danger-exit-binding-{policyIndex}",
            x: 320,
            policyIndex: policyIndex
        );
        instance.SetBindingProjection(protectionMilliseconds: 10_000);
        SetWanderTarget(instance, new HarmlessProjectionWorldPoint(1_000, 0));

        var result = coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 2,
            elapsedMilliseconds: 1_000,
            new FakeSpawnFactory()
        );

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, result.Status);
        Assert.Equal(
            320d + expectedPixelsAfterOneSecond,
            (double)instance.WorldPixel.X,
            precision: 6
        );
        Assert.Equal(0d, (double)instance.WorldPixel.Y, precision: 6);
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Moving,
            instance.AnimationState
        );
        Assert.False(instance.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void Danger_conversion_handles_spawning_idle_and_binding_instances_independently()
    {
        var index = new ShadowCreatureHarmlessProjectionIndex();
        var sink = new RecordingSink();
        var coordinator = CreateCoordinator(index, new NeverPermitAuthority(), sink);
        var owner = Owner(OwnerA);
        coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.ShadowCreatures),
            0
        );
        var spawning = AddDirect(index, owner, "mixed-spawning", x: 320);
        var idle = AddDirect(index, owner, "mixed-idle", x: 640, policyIndex: 1);
        CompleteSpawnAnimation(idle);
        var binding = AddDirect(index, owner, "mixed-binding", x: 960);
        binding.SetBindingProjection(protectionMilliseconds: 10_000);

        var first = coordinator.ApplyStateEvent(
            TierEvent(SanityStateEventKind.TierEntered, OwnerA, SanityTierIds.Danger),
            1
        );

        Assert.Equal(1, first.IntentCount);
        Assert.Single(sink.Intents);
        Assert.False(spawning.IsCleanedUp);
        Assert.True(idle.IsCleanedUp);
        Assert.False(binding.IsCleanedUp);
        Assert.Equal(2, index.CountForOwner(OwnerA));

        coordinator.UpdateOwner(
            owner,
            new HarmlessProjectionWorldPoint(0, 0),
            gameMinute: 1,
            elapsedMilliseconds: 400,
            new FakeSpawnFactory(),
            advanceMovement: false
        );

        Assert.Equal(2, sink.Intents.Count);
        Assert.True(spawning.IsCleanedUp);
        Assert.False(binding.IsCleanedUp);
        Assert.Equal(1, index.CountForOwner(OwnerA));
    }

    [Fact]
    public void DangerExitStaysLockedUntilAFreshShadowTierEntryAndNewPermit()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);
        var oldInstance = AddDirect(
            harness.Index,
            owner,
            "old-local-instance",
            x: 320
        );
        CompleteSpawnAnimation(oldInstance);
        harness.Publish(
            SanityStateEventKind.TierEntered,
            OwnerA,
            SanityTierIds.Danger,
            90
        );
        harness.Publish(
            SanityStateEventKind.TierExited,
            OwnerA,
            SanityTierIds.Danger,
            100
        );

        var locked = harness.Update(owner, minute: 160);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.ConversionLocked, locked.Status);
        Assert.Empty(harness.Factory.Requests);
        Assert.True(oldInstance.IsCleanedUp);

        harness.Publish(
            SanityStateEventKind.TierExited,
            OwnerA,
            SanityTierIds.ShadowCreatures,
            170
        );
        harness.EnterShadow(OwnerA, minute: 200);
        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Waiting, harness.Update(owner, 200).Status);
        Assert.Equal(
            ShadowCreatureProjectionUpdateStatus.Spawned,
            harness.Update(owner, 260, elapsedMilliseconds: 42_000).Status
        );

        var fresh = Assert.Single(harness.Factory.Instances);
        Assert.NotSame(oldInstance, fresh);
        Assert.NotEqual(oldInstance.CorrelationId, fresh.CorrelationId);
    }

    [Fact]
    public void TenPercentTierCannotAuthorizeAHarmlessLocalSpawn()
    {
        var harness = new Harness(SanityMonsterIntensityIds.More);
        var owner = Owner(OwnerA);
        harness.EnterShadow(OwnerA);
        harness.Publish(
            SanityStateEventKind.TierEntered,
            OwnerA,
            SanityTierIds.Terrorbeak,
            0
        );

        harness.Update(owner, 0);
        var result = harness.Update(owner, 60, elapsedMilliseconds: 42_000);

        Assert.Equal(ShadowCreatureProjectionUpdateStatus.Unavailable, result.Status);
        Assert.Equal("shadow-permit.pool-not-harmless", result.Reason);
        Assert.Empty(harness.Factory.Requests);
        Assert.Equal(0, harness.Index.Count);
    }

    [Fact]
    public void ConversionIntentCarriesOptionalOriginAndNoVitalState()
    {
        var intent = new ShadowProjectionConversionIntent(
            "contract-correlation",
            OwnerA,
            ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
            requestedAtMinute: 90
        );
        var propertyNames = typeof(ShadowProjectionConversionIntent)
            .GetProperties(
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
            )
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AuthorityResponsibility",
                "CorrelationId",
                "PlayerKey",
                "PositionX",
                "PositionY",
                "RequestedAtMinute",
                "RequiresFreshBudgetAndLegalSpawn",
                "RestoresRemovedLocalInstance",
                "SpeciesId",
            },
            propertyNames.OrderBy(name => name, StringComparer.Ordinal)
        );
        Assert.True(intent.RequiresFreshBudgetAndLegalSpawn);
        Assert.False(intent.RestoresRemovedLocalInstance);
        Assert.Equal("future-host-authority", intent.AuthorityResponsibility);
        Assert.True(double.IsNaN(intent.PositionX));
        Assert.True(double.IsNaN(intent.PositionY));
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Health", StringComparison.OrdinalIgnoreCase)
            || name.Equals("HP", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void StageProductHasNoSharedCollectionOrCombatMutationSurface()
    {
        var contractRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "ShadowProjection"
        );
        var product = string.Join(
            "\n",
            Directory.GetFiles(contractRoot, "*.cs")
                .Select(File.ReadAllText)
        );

        foreach (
            var forbidden in new[]
            {
                "characters.Add",
                "critters?.Add",
                "temporarySprites.Add",
                "new Monster",
                ": Monster",
                "takeDamage(",
                "monsterDrop",
                "debris.Add",
            }
        )
        {
            Assert.DoesNotContain(forbidden, product, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("HostileAttackStateMachine", product, StringComparison.Ordinal);
        Assert.DoesNotContain("HostileAttackHitProcessor", product, StringComparison.Ordinal);
        Assert.DoesNotContain("ShadowAttackHitRequest", product, StringComparison.Ordinal);
    }

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static ShadowCreatureHarmlessProjectionCoordinator CreateCoordinator(
        ShadowCreatureHarmlessProjectionIndex index,
        IShadowCreatureProjectionBudgetAuthority budget,
        IShadowProjectionConversionIntentSink sink,
        IShadowProjectionCorrelationSource? correlation = null
    )
    {
        var coordinator = new ShadowCreatureHarmlessProjectionCoordinator(
            index,
            budget,
            sink,
            correlation ?? new DeterministicCorrelationSource()
        );
        foreach (var policy in ShadowCreatureHarmlessProjectionCatalog.Policies)
            Assert.True(coordinator.RegisterPolicy(policy, out var reason), reason);
        return coordinator;
    }

    private static HarmlessProjectionOwnerContext Owner(
        string playerKey,
        int screenId = 0,
        object? location = null
    )
    {
        return new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location ?? new object(),
            $"TestLocation-{playerKey}-{screenId}"
        );
    }

    private static ShadowCreatureHarmlessProjectionInstance AddDirect(
        ShadowCreatureHarmlessProjectionIndex index,
        HarmlessProjectionOwnerContext owner,
        string correlationId,
        double x,
        int policyIndex = 0
    )
    {
        var instance = new ShadowCreatureHarmlessProjectionInstance(
            correlationId,
            owner,
            ShadowCreatureHarmlessProjectionCatalog.Policies[policyIndex],
            new HarmlessProjectionWorldPoint(x, 0),
            spawnedAtMinute: 0
        );
        Assert.True(index.TryAdd(instance, out var reason), reason);
        return instance;
    }

    private static void SetWanderTarget(
        ShadowCreatureHarmlessProjectionInstance instance,
        HarmlessProjectionWorldPoint target
    )
    {
        var targetField = typeof(ShadowCreatureHarmlessProjectionInstance).GetField(
            "wanderTargetPixel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );

        Assert.NotNull(targetField);
        targetField!.SetValue(instance, target);
    }

    private static void CompleteSpawnAnimation(
        ShadowCreatureHarmlessProjectionInstance instance
    )
    {
        var policy = instance.Policy;
        Assert.True(
            instance.AdvanceFrame(
                checked(policy.FrameCount * policy.SpawnFrameDurationMilliseconds)
            )
        );
        Assert.Equal(
            ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Idle,
            instance.AnimationState
        );
    }

    private static SanityShadowSpawnPermit Permit(
        string playerKey,
        int occupancy,
        int cap,
        long minute
    )
    {
        return new SanityShadowSpawnPermit(
            playerKey,
            SanityShadowPoolTier.Harmless50,
            SanityMonsterIntensityIds.More,
            occupancy,
            cap,
            minute,
            minute + 60,
            "budget.permit.interval-elapsed"
        );
    }

    private static SanityStateEvent TierEvent(
        SanityStateEventKind kind,
        string playerKey,
        string tierId
    )
    {
        return new SanityStateEvent(
            $"test-{kind}-{playerKey}-{tierId}",
            kind,
            playerKey,
            tierId,
            Revision: 1,
            Ratio: null
        );
    }

    private sealed class Harness
    {
        private readonly GovernorAuthority authority;

        internal Harness(string intensityId)
        {
            Index = new ShadowCreatureHarmlessProjectionIndex();
            Sink = new RecordingSink();
            authority = new GovernorAuthority(intensityId);
            Coordinator = CreateCoordinator(Index, authority, Sink);
            Factory = new FakeSpawnFactory();
            Assert.True(
                authority.Apply(
                    new SanityStateEvent(
                        "test-system-enabled",
                        SanityStateEventKind.SystemEnabled,
                        string.Empty,
                        string.Empty,
                        0,
                        null
                    ),
                    out var reason
                ),
                reason
            );
        }

        internal ShadowCreatureHarmlessProjectionIndex Index { get; }

        internal ShadowCreatureHarmlessProjectionCoordinator Coordinator { get; }

        internal FakeSpawnFactory Factory { get; }

        internal RecordingSink Sink { get; }

        internal void EnterShadow(string playerKey, long minute = 0)
        {
            Publish(
                SanityStateEventKind.TierEntered,
                playerKey,
                SanityTierIds.ShadowCreatures,
                minute
            );
        }

        internal ShadowCreatureProjectionTransitionResult Publish(
            SanityStateEventKind kind,
            string playerKey,
            string tierId,
            long minute
        )
        {
            var stateEvent = TierEvent(kind, playerKey, tierId);
            Assert.True(authority.Apply(stateEvent, out var reason), reason);
            return Coordinator.ApplyStateEvent(stateEvent, minute);
        }

        internal ShadowCreatureProjectionUpdateResult Update(
            HarmlessProjectionOwnerContext owner,
            long minute,
            int elapsedMilliseconds = 16,
            bool advanceMovement = true
        )
        {
            return Coordinator.UpdateOwner(
                owner,
                new HarmlessProjectionWorldPoint(0, 0),
                minute,
                elapsedMilliseconds,
                Factory,
                advanceMovement
            );
        }
    }

    private sealed class GovernorAuthority
        : IShadowCreatureProjectionBudgetAuthority,
            ISanityShadowRealTimeBudgetAuthority
    {
        private readonly SanityShadowBudgetGovernor governor;

        internal GovernorAuthority(string intensityId)
        {
            governor = new SanityShadowBudgetGovernor(
                new FixedIntensityProvider(intensityId)
            );
        }

        internal bool Apply(SanityStateEvent stateEvent, out string reason)
        {
            return governor.ApplyStateEvent(stateEvent, out reason);
        }

        public SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
            string playerKey,
            long gameMinute,
            int occupancy
        )
        {
            return governor.Evaluate(playerKey, gameMinute, occupancy);
        }

        public SanityShadowBudgetEvaluationResult EvaluateShadowBudgetRealTime(
            string playerKey,
            long gameMinute,
            int occupancy,
            int elapsedMilliseconds,
            SanityShadowSpecies? requestedSpecies
        )
        {
            return governor.EvaluateRealTime(
                playerKey,
                gameMinute,
                occupancy,
                elapsedMilliseconds,
                requestedSpecies
            );
        }
    }

    private sealed class FixedIntensityProvider : ISanityMonsterIntensityProvider
    {
        private readonly string value;

        internal FixedIntensityProvider(string value)
        {
            this.value = value;
        }

        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(true, value, "test");
        }
    }

    private sealed class AlwaysPermitAuthority
        : IShadowCreatureProjectionBudgetAuthority
    {
        internal int EvaluateCalls { get; private set; }

        public SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
            string playerKey,
            long gameMinute,
            int occupancy
        )
        {
            EvaluateCalls++;
            var permit = Permit(playerKey, occupancy, cap: 4, gameMinute);
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.PermitGranted,
                permit.Reason,
                playerKey,
                permit.PoolTier,
                permit.IntensityId,
                occupancy,
                permit.Cap,
                intervalMinutes: 60,
                permit.NextDueMinute,
                permit
            );
        }
    }

    private sealed class NeverPermitAuthority
        : IShadowCreatureProjectionBudgetAuthority
    {
        public SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
            string playerKey,
            long gameMinute,
            int occupancy
        )
        {
            return new SanityShadowBudgetEvaluationResult(
                SanityShadowBudgetEvaluationStatus.Waiting,
                "test.waiting",
                playerKey,
                SanityShadowPoolTier.Harmless50,
                SanityMonsterIntensityIds.Default,
                occupancy,
                cap: 1,
                intervalMinutes: 60,
                nextDueMinute: gameMinute + 60
            );
        }
    }

    private sealed class FakeSpawnFactory
        : IShadowCreatureHarmlessProjectionSpawnFactory
    {
        internal List<ShadowCreatureHarmlessProjectionSpawnRequest> Requests { get; } =
            new();

        internal List<ShadowCreatureHarmlessProjectionInstance> Instances { get; } =
            new();

        public ShadowCreatureHarmlessProjectionSpawnResult TrySpawn(
            ShadowCreatureHarmlessProjectionSpawnRequest request
        )
        {
            Requests.Add(request);
            var instance = new ShadowCreatureHarmlessProjectionInstance(
                request.CorrelationId,
                request.Owner,
                request.Policy,
                new HarmlessProjectionWorldPoint(
                    request.OwnerStandingWorldPixel.X + (Requests.Count * 320d),
                    request.OwnerStandingWorldPixel.Y
                ),
                request.GameMinute
            );
            Instances.Add(instance);
            return ShadowCreatureHarmlessProjectionSpawnResult.Spawned(instance);
        }
    }

    private sealed class DeterministicCorrelationSource
        : IShadowProjectionCorrelationSource
    {
        private int next;

        public string Next(string playerKey, string speciesId)
        {
            next++;
            return $"test-shadow-correlation-{next:D4}";
        }
    }

    private sealed class RecordingSink : IShadowProjectionConversionIntentSink
    {
        internal ShadowProjectionConversionSubmissionStatus Status { get; set; } =
            ShadowProjectionConversionSubmissionStatus.Unconfirmed;

        internal bool Throw { get; set; }

        internal int CallCount { get; private set; }

        internal Queue<ShadowProjectionConversionSubmissionStatus> StatusSequence { get; } =
            new();

        internal Action<ShadowProjectionConversionIntent>? OnRecord { get; set; }

        internal List<ShadowProjectionConversionIntent> Intents { get; } = new();

        public ShadowProjectionConversionSubmissionResult Record(
            ShadowProjectionConversionIntent intent
        )
        {
            CallCount++;
            OnRecord?.Invoke(intent);
            if (Throw)
                throw new InvalidOperationException("test sink failure");
            Intents.Add(intent);
            var status = StatusSequence.Count > 0 ? StatusSequence.Dequeue() : Status;
            return new ShadowProjectionConversionSubmissionResult(
                status,
                $"test.{status.ToString().ToLowerInvariant()}"
            );
        }
    }

    private sealed class FakePhysicalResourceFactory : ISanityPhysicalResourceFactory
    {
        public SanityPhysicalResourceCreationResult CreateTexture(
            string path,
            byte[] bytes
        )
        {
            return SanityPhysicalResourceCreationResult.Created(
                new FakePhysicalResource(SanityPhysicalResourceKind.Texture, path)
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(
            string path,
            byte[] bytes
        )
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
