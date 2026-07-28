using System.Reflection;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Damage;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.Damage;

public sealed class NonLethalDamageApplyDamageUpToFloorContractTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";
    private const string CorrelationA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CorrelationB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(99, 20)]
    [InlineData(100, 20)]
    [InlineData(101, 21)]
    [InlineData(150, 30)]
    [InlineData(int.MaxValue, 429496730)]
    public void Floor_is_exact_integer_ceiling_of_twenty_percent(
        int maximumHealth,
        int expectedFloor
    )
    {
        Assert.True(
            NonLethalDamageCalculator.TryCalculateFloor(
                maximumHealth,
                out var floor,
                out var reason
            )
        );
        Assert.Equal(expectedFloor, floor);
        Assert.Equal("nonlethal.floor-calculated", reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_maximum_has_explicit_fail_closed_calculation(
        int maximumHealth
    )
    {
        Assert.False(
            NonLethalDamageCalculator.TryCalculateFloor(
                maximumHealth,
                out var floor,
                out var reason
            )
        );
        Assert.Equal(0, floor);
        Assert.Equal("nonlethal.maximum-health-invalid", reason);
    }

    [Fact]
    public void Interface_freezes_two_explicit_operations_and_distinct_request_types()
    {
        var methods = typeof(INonLethalDamageService).GetMethods();
        var apply = Assert.Single(
            methods.Where(method => method.Name == "ApplyDamageUpToFloor")
        );
        var reduce = Assert.Single(methods.Where(method => method.Name == "ReduceToFloor"));

        Assert.Equal(
            typeof(ApplyDamageUpToFloorRequest),
            Assert.Single(apply.GetParameters()).ParameterType
        );
        Assert.Equal(
            typeof(ReduceToFloorRequest),
            Assert.Single(reduce.GetParameters()).ParameterType
        );
        Assert.DoesNotContain(methods, method => method.Name == "Apply");
        Assert.DoesNotContain(
            apply.GetParameters().Concat(reduce.GetParameters()),
            parameter => parameter.ParameterType == typeof(bool)
        );
    }

    [Fact]
    public void Stage03_capabilities_are_explicitly_unavailable_and_never_claim_mutation()
    {
        var service = StartedService();

        var apply = service.GetCapability(
            NonLethalDamageOperation.ApplyDamageUpToFloor
        );
        var reduce = service.GetCapability(NonLethalDamageOperation.ReduceToFloor);

        Assert.Equal(NonLethalDamageCapabilityStatus.Unavailable, apply.Status);
        Assert.Equal(
            "nonlethal.apply-damage-up-to-floor.physical-seam-unavailable",
            apply.Reason
        );
        Assert.Equal(NonLethalDamageCapabilityStatus.Unavailable, reduce.Status);
        Assert.Equal(
            "nonlethal.reduce-to-floor.health-mutation-seam-deferred",
            reduce.Reason
        );
    }

    [Fact]
    public void Configured_apply_executor_exposes_only_the_stage04_capability()
    {
        var executor = new RecordingApplyExecutor(ApplyWithNoDefense);
        var service = StartedService(executor);

        var apply = service.GetCapability(
            NonLethalDamageOperation.ApplyDamageUpToFloor
        );
        var reduce = service.GetCapability(NonLethalDamageOperation.ReduceToFloor);

        Assert.Equal(NonLethalDamageCapabilityStatus.Available, apply.Status);
        Assert.Equal(
            "nonlethal.apply-damage-up-to-floor.controlled-defense-floor-available",
            apply.Reason
        );
        Assert.Equal(NonLethalDamageCapabilityStatus.Unavailable, reduce.Status);
        Assert.Equal(
            "nonlethal.reduce-to-floor.health-mutation-seam-deferred",
            reduce.Reason
        );
        AssertUnavailable(
            service.ReduceToFloor(
                Reduce(CorrelationA, before: 100, maximum: 100)
            ),
            NonLethalDamageOperation.ReduceToFloor
        );
        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 10, 100, 90)]
    [InlineData(15, 10, 15, 5)]
    [InlineData(100, 999, 100, 1)]
    [InlineData(100, 0, 20, 20)]
    public void Controlled_damage_applies_defense_before_the_floor_cap(
        int requestedDamage,
        int defense,
        int maximumAllowedDamage,
        int expectedAppliedDamage
    )
    {
        Assert.True(
            ControlledPhysicalDamageCalculator.TryCalculateAppliedDamage(
                requestedDamage,
                defense,
                maximumAllowedDamage,
                out var appliedDamage,
                out var reason
            )
        );
        Assert.Equal(expectedAppliedDamage, appliedDamage);
        Assert.Equal("nonlethal.controlled-damage.calculated", reason);
    }

    [Fact]
    public void Controlled_damage_rejects_invalid_inputs_without_fabricating_damage()
    {
        Assert.False(
            ControlledPhysicalDamageCalculator.TryCalculateAppliedDamage(
                requestedDamage: 100,
                defense: -1,
                maximumAllowedDamage: 100,
                out var appliedDamage,
                out var reason
            )
        );
        Assert.Equal(0, appliedDamage);
        Assert.Equal("nonlethal.controlled-damage.defense-invalid", reason);

        Assert.False(
            ControlledPhysicalDamageCalculator.TryCalculateAppliedDamage(
                requestedDamage: 10,
                defense: 0,
                maximumAllowedDamage: 11,
                out appliedDamage,
                out reason
            )
        );
        Assert.Equal(0, appliedDamage);
        Assert.Equal("nonlethal.controlled-damage.maximum-allowed-invalid", reason);
    }

    [Fact]
    public void Two_darkness_hits_stop_exactly_at_the_twenty_percent_floor()
    {
        var executor = new RecordingApplyExecutor(ApplyWithNoDefense);
        var service = StartedService(executor);

        var first = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 150, maximum: 150, requested: 100)
        );
        var second = service.ApplyDamageUpToFloor(
            Apply(CorrelationB, before: 50, maximum: 150, requested: 100)
        );

        AssertApplied(first, expectedAppliedDamage: 100, expectedAfterHealth: 50);
        Assert.Equal(30, first.Receipt!.FloorHealth);
        Assert.Equal(100, first.Receipt.MaximumAllowedDamage);

        AssertApplied(second, expectedAppliedDamage: 20, expectedAfterHealth: 30);
        Assert.Equal(30, second.Receipt!.FloorHealth);
        Assert.Equal(20, second.Receipt.MaximumAllowedDamage);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public void Defense_changes_actual_damage_without_changing_requested_evidence()
    {
        var executor = new RecordingApplyExecutor(
            request => ApplyWithDefense(request, defense: 10)
        );
        var service = StartedService(executor);

        var result = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 150, maximum: 150, requested: 100)
        );

        AssertApplied(result, expectedAppliedDamage: 90, expectedAfterHealth: 60);
        Assert.Equal(100, result.Receipt!.RequestedDamage);
        Assert.Equal(100, result.Receipt.MaximumAllowedDamage);
    }

    [Fact]
    public void Executor_rejection_records_zero_actual_damage_and_is_idempotent()
    {
        var executor = new RecordingApplyExecutor(
            request => ApplyDamageUpToFloorExecutionResult.Unavailable(
                request.BeforeHealth,
                "nonlethal.apply-damage-up-to-floor.health-snapshot-drift"
            )
        );
        var service = StartedService(executor);
        var request = Apply(CorrelationA, before: 150, maximum: 150, requested: 100);

        var original = service.ApplyDamageUpToFloor(request);
        var duplicate = service.ApplyDamageUpToFloor(request);

        Assert.Equal(NonLethalDamageResultStatus.Unavailable, original.Status);
        Assert.Equal(NonLethalDamageCapabilityStatus.Available, original.Capability.Status);
        Assert.Equal(0, original.Receipt!.AppliedDamage);
        Assert.Equal(150, original.Receipt.AfterHealth);
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void Successful_duplicate_never_executes_or_mutates_twice()
    {
        var executor = new RecordingApplyExecutor(ApplyWithNoDefense);
        var service = StartedService(executor);
        var request = Apply(CorrelationA, before: 150, maximum: 150, requested: 100);

        var original = service.ApplyDamageUpToFloor(request);
        var duplicate = service.ApplyDamageUpToFloor(request);

        AssertApplied(original, expectedAppliedDamage: 100, expectedAfterHealth: 50);
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void Full_receipt_window_rejects_before_calling_the_mutation_executor()
    {
        var executor = new RecordingApplyExecutor(ApplyWithNoDefense);
        var service = StartedService(executor);
        for (var index = 1; index <= NonLethalDamageReceiptRegistry.MaximumReceipts; index++)
        {
            var result = service.ApplyDamageUpToFloor(
                Apply(index.ToString("x32"), before: 100, maximum: 100, requested: 1)
            );
            Assert.Equal(NonLethalDamageResultStatus.Applied, result.Status);
        }

        var overflow = service.ApplyDamageUpToFloor(
            Apply("ffffffffffffffffffffffffffffffff", before: 100, maximum: 100, requested: 1)
        );

        Assert.Equal(NonLethalDamageResultStatus.CapacityExceeded, overflow.Status);
        Assert.Null(overflow.Receipt);
        Assert.Equal(NonLethalDamageReceiptRegistry.MaximumReceipts, executor.CallCount);
    }

    [Fact]
    public void Physical_intent_preserves_requested_cap_and_actual_applied_as_separate_fields()
    {
        var service = StartedService();

        var small = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 100, maximum: 100, requested: 7)
        );
        var large = service.ApplyDamageUpToFloor(
            Apply(CorrelationB, before: 100, maximum: 100, requested: 1000)
        );

        AssertUnavailable(small, NonLethalDamageOperation.ApplyDamageUpToFloor);
        Assert.Equal(7, small.Receipt!.RequestedDamage);
        Assert.Null(small.Receipt.TargetHealth);
        Assert.Equal(20, small.Receipt.FloorHealth);
        Assert.Equal(7, small.Receipt.MaximumAllowedDamage);
        Assert.Equal(0, small.Receipt.AppliedDamage);
        Assert.Equal(100, small.Receipt.AfterHealth);

        AssertUnavailable(large, NonLethalDamageOperation.ApplyDamageUpToFloor);
        Assert.Equal(80, large.Receipt!.MaximumAllowedDamage);
        Assert.Equal(0, large.Receipt.AppliedDamage);
        Assert.False(large.MutationApplied);
    }

    [Fact]
    public void Reduce_to_floor_has_a_target_and_no_physical_damage_request()
    {
        var service = StartedService();

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: 100, maximum: 101)
        );

        AssertUnavailable(result, NonLethalDamageOperation.ReduceToFloor);
        Assert.Null(result.Receipt!.RequestedDamage);
        Assert.Equal(21, result.Receipt.TargetHealth);
        Assert.Equal(21, result.Receipt.FloorHealth);
        Assert.Equal(79, result.Receipt.MaximumAllowedDamage);
        Assert.Equal(0, result.Receipt.AppliedDamage);
        Assert.Equal(100, result.Receipt.AfterHealth);
    }

    [Fact]
    public void Equal_or_below_floor_is_no_change_and_never_heals()
    {
        var service = StartedService();

        var equal = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 20, maximum: 100, requested: 100)
        );
        var below = service.ReduceToFloor(
            Reduce(CorrelationB, before: 7, maximum: 100)
        );

        Assert.Equal(NonLethalDamageResultStatus.NoChange, equal.Status);
        Assert.Equal(NonLethalDamageReceiptOutcome.NoChange, equal.Receipt!.Outcome);
        Assert.Equal((20, 20, 0), (
            equal.Receipt.BeforeHealth,
            equal.Receipt.AfterHealth,
            equal.Receipt.AppliedDamage
        ));

        Assert.Equal(NonLethalDamageResultStatus.NoChange, below.Status);
        Assert.Equal(20, below.Receipt!.FloorHealth);
        Assert.Equal((7, 7, 0), (
            below.Receipt.BeforeHealth,
            below.Receipt.AfterHealth,
            below.Receipt.AppliedDamage
        ));
        Assert.True(below.Receipt.IsInvariantSatisfied(out _));
    }

    [Fact]
    public void Invalid_identity_health_revision_and_purpose_have_explicit_outcomes()
    {
        var service = StartedService();

        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply("not-a-guid", before: 100, maximum: 100, requested: 1)
        ), "nonlethal.correlation-id-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply("00000000000000000000000000000000", before: 100, maximum: 100, requested: 1)
        ), "nonlethal.correlation-id-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(CorrelationA, playerKey: "01", before: 100, maximum: 100, requested: 1)
        ), "nonlethal.player-key-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 0, maximum: 0, requested: 1)
        ), "nonlethal.maximum-health-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 101, maximum: 100, requested: 1)
        ), "nonlethal.before-health-out-of-range");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 100, maximum: 100, requested: 0)
        ), "nonlethal.requested-damage-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(CorrelationA, revision: -1, before: 100, maximum: 100, requested: 1)
        ), "nonlethal.authority-revision-invalid");
        AssertInvalid(service.ApplyDamageUpToFloor(
            Apply(
                CorrelationA,
                purpose: NonLethalDamagePurpose.SanityDarknessSpecialDeath,
                before: 100,
                maximum: 100,
                requested: 1
            )
        ), "nonlethal.purpose-operation-mismatch");
        Assert.Equal(0, service.ReceiptCount);
    }

    [Fact]
    public void Client_can_only_request_host_work_and_cannot_reserve_a_receipt()
    {
        var service = StartedService();

        var result = service.ApplyDamageUpToFloor(
            Apply(
                CorrelationA,
                authority: SanityAuthorityRole.Client,
                before: 100,
                maximum: 100,
                requested: 10
            )
        );

        Assert.Equal(NonLethalDamageResultStatus.RequiresHostAuthority, result.Status);
        Assert.Equal("nonlethal.host-mutating-authority-required", result.Reason);
        Assert.Null(result.Receipt);
        Assert.Equal(0, service.ReceiptCount);
    }

    [Fact]
    public void Exact_duplicate_returns_the_original_receipt_without_second_execution()
    {
        var service = StartedService();
        var request = Apply(CorrelationA, before: 100, maximum: 100, requested: 10);

        var original = service.ApplyDamageUpToFloor(request);
        var duplicate = service.ApplyDamageUpToFloor(request);

        Assert.Equal(NonLethalDamageResultStatus.Unavailable, original.Status);
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Equal("nonlethal.correlation-duplicate", duplicate.Reason);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(1, service.ReceiptCount);
        Assert.False(duplicate.MutationApplied);
    }

    [Fact]
    public void Same_session_correlation_with_changed_player_operation_or_revision_conflicts()
    {
        var service = StartedService();
        var original = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, playerKey: "1", revision: 3, before: 100, maximum: 100, requested: 10)
        );

        var changedPlayer = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, playerKey: "2", revision: 3, before: 100, maximum: 100, requested: 10)
        );
        var changedOperation = service.ReduceToFloor(
            Reduce(CorrelationA, playerKey: "1", revision: 3, before: 100, maximum: 100)
        );
        var changedRevision = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, playerKey: "1", revision: 4, before: 100, maximum: 100, requested: 10)
        );

        foreach (var conflict in new[] { changedPlayer, changedOperation, changedRevision })
        {
            Assert.Equal(NonLethalDamageResultStatus.CorrelationConflict, conflict.Status);
            Assert.Equal("nonlethal.correlation-conflict", conflict.Reason);
            Assert.Same(original.Receipt, conflict.Receipt);
        }
        Assert.Equal(1, service.ReceiptCount);
    }

    [Fact]
    public void Player_isolation_and_session_reset_keep_the_receipt_window_bounded_to_one_session()
    {
        var service = StartedService();
        var playerOne = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, playerKey: "1", before: 100, maximum: 100, requested: 10)
        );
        var playerTwo = service.ApplyDamageUpToFloor(
            Apply(CorrelationB, playerKey: "2", before: 100, maximum: 100, requested: 10)
        );
        Assert.Equal(2, service.ReceiptCount);
        Assert.NotSame(playerOne.Receipt, playerTwo.Receipt);

        Assert.Equal(
            "nonlethal.session-already-active",
            service.BeginSession(SessionA).Reason
        );
        Assert.Equal(2, service.ReceiptCount);

        Assert.Equal("nonlethal.session-replaced", service.BeginSession(SessionB).Reason);
        Assert.Equal(0, service.ReceiptCount);
        var reused = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, sessionId: SessionB, before: 100, maximum: 100, requested: 10)
        );
        Assert.Equal(NonLethalDamageResultStatus.Unavailable, reused.Status);
        Assert.NotSame(playerOne.Receipt, reused.Receipt);

        service.ClearSession();
        service.ClearSession();
        Assert.Equal(string.Empty, service.ActiveSessionId);
        Assert.Equal(0, service.ReceiptCount);
        Assert.Equal(
            NonLethalDamageResultStatus.SessionMismatch,
            service.ApplyDamageUpToFloor(
                Apply(CorrelationA, sessionId: SessionB, before: 100, maximum: 100, requested: 10)
            ).Status
        );
    }

    [Fact]
    public void Receipt_capacity_fails_closed_without_evicting_duplicate_evidence()
    {
        var service = StartedService();
        NonLethalDamageReceipt? first = null;
        for (var index = 1; index <= NonLethalDamageReceiptRegistry.MaximumReceipts; index++)
        {
            var correlation = index.ToString("x32");
            var result = service.ApplyDamageUpToFloor(
                Apply(correlation, before: 100, maximum: 100, requested: 1)
            );
            first ??= result.Receipt;
            Assert.Equal(NonLethalDamageResultStatus.Unavailable, result.Status);
        }

        var overflow = service.ApplyDamageUpToFloor(
            Apply("ffffffffffffffffffffffffffffffff", before: 100, maximum: 100, requested: 1)
        );
        Assert.Equal(NonLethalDamageResultStatus.CapacityExceeded, overflow.Status);
        Assert.Null(overflow.Receipt);

        var duplicate = service.ApplyDamageUpToFloor(
            Apply("00000000000000000000000000000001", before: 100, maximum: 100, requested: 1)
        );
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Same(first, duplicate.Receipt);
        Assert.Equal(NonLethalDamageReceiptRegistry.MaximumReceipts, service.ReceiptCount);
    }

    [Fact]
    public void Receipt_invariant_rejects_cross_floor_or_fabricated_applied_damage()
    {
        var invalid = new NonLethalDamageReceipt(
            NonLethalDamageOperation.ApplyDamageUpToFloor,
            NonLethalDamagePurpose.DarknessAttack,
            SessionA,
            CorrelationA,
            "1",
            beforeHealth: 100,
            maximumHealth: 100,
            requestedDamage: 100,
            targetHealth: null,
            floorHealth: 20,
            maximumAllowedDamage: 80,
            appliedDamage: 81,
            afterHealth: 19,
            NonLethalDamageReceiptOutcome.Applied,
            "fabricated",
            SanityAuthorityRole.Host,
            authorityRevision: 1
        );

        Assert.False(invalid.IsInvariantSatisfied(out var reason));
        Assert.Equal("nonlethal.receipt.health-transition-invalid", reason);
    }

    [Fact]
    public void Pure_contract_sources_have_no_game_damage_health_sanity_or_audio_mutation()
    {
        var source = string.Join(
            "\n",
            ReadSource("NonLethalDamageContracts.cs"),
            ReadSource("NonLethalDamageCalculator.cs"),
            ReadSource("ControlledPhysicalDamage.cs"),
            ReadSource("DirectFloorReduction.cs"),
            ReadSource("NonLethalDamageService.cs")
        );

        Assert.DoesNotContain("using StardewValley", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".health =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISanityProcessAudioOutput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Smapi_executor_is_a_single_controlled_health_write_not_a_vanilla_chain_clone()
    {
        var source = ReadSource("SmapiApplyDamageUpToFloorExecutor.cs");

        Assert.Contains("player.buffs.Defense", source, StringComparison.Ordinal);
        Assert.Contains("Book_Defense", source, StringComparison.Ordinal);
        Assert.Contains("player.health = afterHealth", source, StringComparison.Ordinal);
        Assert.Contains("health-snapshot-drift", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReduceToFloor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity(", source, StringComparison.Ordinal);
    }

    private static NonLethalDamageService StartedService(
        IApplyDamageUpToFloorExecutor? executor = null
    )
    {
        var service = new NonLethalDamageService(executor);
        var result = service.BeginSession(SessionA);
        Assert.True(result.Accepted);
        return service;
    }

    private static ApplyDamageUpToFloorExecutionResult ApplyWithNoDefense(
        ApplyDamageUpToFloorExecutionRequest request
    )
    {
        return ApplyWithDefense(request, defense: 0);
    }

    private static ApplyDamageUpToFloorExecutionResult ApplyWithDefense(
        ApplyDamageUpToFloorExecutionRequest request,
        int defense
    )
    {
        Assert.True(
            ControlledPhysicalDamageCalculator.TryCalculateAppliedDamage(
                request.RequestedDamage,
                defense,
                request.MaximumAllowedDamage,
                out var appliedDamage,
                out var reason
            ),
            reason
        );
        Assert.True(appliedDamage > 0);
        return ApplyDamageUpToFloorExecutionResult.AppliedResult(
            appliedDamage,
            request.BeforeHealth - appliedDamage
        );
    }

    private static ApplyDamageUpToFloorRequest Apply(
        string correlationId,
        string sessionId = SessionA,
        string playerKey = "1",
        NonLethalDamagePurpose purpose = NonLethalDamagePurpose.DarknessAttack,
        SanityAuthorityRole authority = SanityAuthorityRole.Host,
        long revision = 1,
        int before = 100,
        int maximum = 100,
        int requested = 10
    )
    {
        return new ApplyDamageUpToFloorRequest(
            new NonLethalDamageContext(
                sessionId,
                correlationId,
                playerKey,
                purpose,
                authority,
                revision
            ),
            before,
            maximum,
            requested
        );
    }

    private static ReduceToFloorRequest Reduce(
        string correlationId,
        string sessionId = SessionA,
        string playerKey = "1",
        NonLethalDamagePurpose purpose = NonLethalDamagePurpose.SanityDarknessSpecialDeath,
        SanityAuthorityRole authority = SanityAuthorityRole.Host,
        long revision = 1,
        int before = 100,
        int maximum = 100
    )
    {
        return new ReduceToFloorRequest(
            new NonLethalDamageContext(
                sessionId,
                correlationId,
                playerKey,
                purpose,
                authority,
                revision
            ),
            before,
            maximum
        );
    }

    private static void AssertUnavailable(
        NonLethalDamageResult result,
        NonLethalDamageOperation operation
    )
    {
        Assert.Equal(NonLethalDamageResultStatus.Unavailable, result.Status);
        Assert.Equal(NonLethalDamageCapabilityStatus.Unavailable, result.Capability.Status);
        Assert.Equal(operation, result.Capability.Operation);
        Assert.NotNull(result.Receipt);
        Assert.Equal(NonLethalDamageReceiptOutcome.Unavailable, result.Receipt!.Outcome);
        Assert.True(result.Receipt.IsInvariantSatisfied(out var reason), reason);
        Assert.False(result.MutationApplied);
    }

    private static void AssertApplied(
        NonLethalDamageResult result,
        int expectedAppliedDamage,
        int expectedAfterHealth
    )
    {
        Assert.Equal(NonLethalDamageResultStatus.Applied, result.Status);
        Assert.Equal(NonLethalDamageCapabilityStatus.Available, result.Capability.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(NonLethalDamageReceiptOutcome.Applied, result.Receipt!.Outcome);
        Assert.Equal(expectedAppliedDamage, result.Receipt.AppliedDamage);
        Assert.Equal(expectedAfterHealth, result.Receipt.AfterHealth);
        Assert.True(result.Receipt.IsInvariantSatisfied(out var reason), reason);
        Assert.True(result.MutationApplied);
    }

    private static void AssertInvalid(
        NonLethalDamageResult result,
        string expectedReason
    )
    {
        Assert.Equal(NonLethalDamageResultStatus.Invalid, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Receipt);
        Assert.False(result.MutationApplied);
    }

    private static string ReadSource(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "NonLethalDamage",
                fileName
            )
        );
    }

    private sealed class RecordingApplyExecutor : IApplyDamageUpToFloorExecutor
    {
        private readonly Func<
            ApplyDamageUpToFloorExecutionRequest,
            ApplyDamageUpToFloorExecutionResult
        > execute;

        internal RecordingApplyExecutor(
            Func<
                ApplyDamageUpToFloorExecutionRequest,
                ApplyDamageUpToFloorExecutionResult
            > execute
        )
        {
            this.execute = execute;
        }

        internal int CallCount { get; private set; }

        public ApplyDamageUpToFloorExecutionResult Execute(
            ApplyDamageUpToFloorExecutionRequest request
        )
        {
            CallCount++;
            return execute(request);
        }
    }
}

public sealed class NonLethalDamageReduceToFloorContractTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";
    private const string CorrelationA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CorrelationB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Configured_reduce_executor_exposes_only_the_direct_floor_capability()
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var apply = service.GetCapability(
            NonLethalDamageOperation.ApplyDamageUpToFloor
        );
        var reduce = service.GetCapability(NonLethalDamageOperation.ReduceToFloor);

        Assert.Equal(NonLethalDamageCapabilityStatus.Unavailable, apply.Status);
        Assert.Equal(
            "nonlethal.apply-damage-up-to-floor.physical-seam-unavailable",
            apply.Reason
        );
        Assert.Equal(NonLethalDamageCapabilityStatus.Available, reduce.Status);
        Assert.Equal(
            "nonlethal.reduce-to-floor.direct-health-floor-available",
            reduce.Reason
        );
        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData(100, 100, 20, 80)]
    [InlineData(100, 101, 21, 79)]
    [InlineData(150, 150, 30, 120)]
    [InlineData(31, 150, 30, 1)]
    public void High_health_is_reduced_directly_to_the_exact_floor(
        int beforeHealth,
        int maximumHealth,
        int expectedFloor,
        int expectedAppliedDamage
    )
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: beforeHealth, maximum: maximumHealth)
        );

        AssertApplied(result, expectedAppliedDamage, expectedFloor);
        Assert.Null(result.Receipt!.RequestedDamage);
        Assert.Equal(expectedFloor, result.Receipt.TargetHealth);
        Assert.Equal(expectedAppliedDamage, result.Receipt.MaximumAllowedDamage);
        Assert.Equal(1, executor.CallCount);
    }

    [Theory]
    [InlineData(20, 100)]
    [InlineData(7, 100)]
    [InlineData(0, 100)]
    public void Equal_or_below_floor_never_calls_executor_heals_or_deals_more_damage(
        int beforeHealth,
        int maximumHealth
    )
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: beforeHealth, maximum: maximumHealth)
        );

        Assert.Equal(NonLethalDamageResultStatus.NoChange, result.Status);
        Assert.Equal(NonLethalDamageReceiptOutcome.NoChange, result.Receipt!.Outcome);
        Assert.Equal(beforeHealth, result.Receipt.BeforeHealth);
        Assert.Equal(beforeHealth, result.Receipt.AfterHealth);
        Assert.Equal(0, result.Receipt.AppliedDamage);
        Assert.Equal(0, executor.CallCount);
        Assert.True(result.Receipt.IsInvariantSatisfied(out var reason), reason);
    }

    [Theory]
    [InlineData(0, 0, "nonlethal.maximum-health-invalid")]
    [InlineData(101, 100, "nonlethal.before-health-out-of-range")]
    public void Invalid_direct_floor_health_never_calls_executor_or_records_receipt(
        int beforeHealth,
        int maximumHealth,
        string expectedReason
    )
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: beforeHealth, maximum: maximumHealth)
        );

        Assert.Equal(NonLethalDamageResultStatus.Invalid, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Receipt);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, service.ReceiptCount);
    }

    [Fact]
    public void Client_direct_floor_request_cannot_mutate_or_reserve_a_receipt()
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var result = service.ReduceToFloor(
            Reduce(
                CorrelationA,
                authority: SanityAuthorityRole.Client,
                before: 100,
                maximum: 100
            )
        );

        Assert.Equal(NonLethalDamageResultStatus.RequiresHostAuthority, result.Status);
        Assert.Equal("nonlethal.host-mutating-authority-required", result.Reason);
        Assert.Null(result.Receipt);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, service.ReceiptCount);
    }

    [Fact]
    public void Successful_duplicate_uses_the_original_direct_floor_receipt_once()
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);
        var request = Reduce(CorrelationA, before: 100, maximum: 100);

        var original = service.ReduceToFloor(request);
        var duplicate = service.ReduceToFloor(request);

        AssertApplied(original, expectedAppliedDamage: 80, expectedAfterHealth: 20);
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void Rejected_direct_floor_execution_records_zero_mutation_once()
    {
        var executor = new RecordingReduceExecutor(
            request => ReduceToFloorExecutionResult.Unavailable(
                request.BeforeHealth,
                "nonlethal.reduce-to-floor.health-snapshot-drift"
            )
        );
        var service = StartedService(reduceExecutor: executor);
        var request = Reduce(CorrelationA, before: 100, maximum: 100);

        var original = service.ReduceToFloor(request);
        var duplicate = service.ReduceToFloor(request);

        Assert.Equal(NonLethalDamageResultStatus.Unavailable, original.Status);
        Assert.Equal(NonLethalDamageCapabilityStatus.Available, original.Capability.Status);
        Assert.Equal(0, original.Receipt!.AppliedDamage);
        Assert.Equal(100, original.Receipt.AfterHealth);
        Assert.Equal(NonLethalDamageResultStatus.Duplicate, duplicate.Status);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void Full_receipt_window_rejects_before_direct_health_mutation()
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);
        for (var index = 1; index <= NonLethalDamageReceiptRegistry.MaximumReceipts; index++)
        {
            var result = service.ReduceToFloor(
                Reduce(index.ToString("x32"), before: 100, maximum: 100)
            );
            Assert.Equal(NonLethalDamageResultStatus.Applied, result.Status);
        }

        var overflow = service.ReduceToFloor(
            Reduce("ffffffffffffffffffffffffffffffff", before: 100, maximum: 100)
        );

        Assert.Equal(NonLethalDamageResultStatus.CapacityExceeded, overflow.Status);
        Assert.Null(overflow.Receipt);
        Assert.Equal(NonLethalDamageReceiptRegistry.MaximumReceipts, executor.CallCount);
    }

    [Fact]
    public void Different_players_and_new_session_do_not_share_direct_floor_receipts()
    {
        var executor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(reduceExecutor: executor);

        var playerOne = service.ReduceToFloor(
            Reduce(CorrelationA, playerKey: "1", before: 100, maximum: 100)
        );
        var playerTwo = service.ReduceToFloor(
            Reduce(CorrelationB, playerKey: "2", before: 100, maximum: 100)
        );
        Assert.NotSame(playerOne.Receipt, playerTwo.Receipt);
        Assert.Equal(2, service.ReceiptCount);

        Assert.Equal("nonlethal.session-replaced", service.BeginSession(SessionB).Reason);
        Assert.Equal(0, service.ReceiptCount);
        var reused = service.ReduceToFloor(
            Reduce(
                CorrelationA,
                sessionId: SessionB,
                playerKey: "1",
                before: 100,
                maximum: 100
            )
        );
        AssertApplied(reused, expectedAppliedDamage: 80, expectedAfterHealth: 20);
        Assert.Equal(3, executor.CallCount);
    }

    [Fact]
    public void Invalid_partial_direct_floor_result_is_not_recorded_as_applied()
    {
        var executor = new RecordingReduceExecutor(
            request => ReduceToFloorExecutionResult.AppliedResult(
                appliedDamage: 1,
                afterHealth: request.BeforeHealth - 1
            )
        );
        var service = StartedService(reduceExecutor: executor);

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: 100, maximum: 100)
        );

        Assert.Equal(NonLethalDamageResultStatus.Invalid, result.Status);
        Assert.Equal("nonlethal.reduce-to-floor.executor-result-invalid", result.Reason);
        Assert.Null(result.Receipt);
        Assert.Equal(0, service.ReceiptCount);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void NonLethalDamageSemantics_defense_affects_apply_but_not_direct_floor()
    {
        var applyExecutor = new RecordingApplyExecutor(
            request => ApplyDamageUpToFloorExecutionResult.AppliedResult(
                appliedDamage: 1,
                afterHealth: request.BeforeHealth - 1
            )
        );
        var reduceExecutor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(applyExecutor, reduceExecutor);

        var physical = service.ApplyDamageUpToFloor(
            Apply(CorrelationA, before: 100, maximum: 100, requested: 100)
        );
        var direct = service.ReduceToFloor(
            Reduce(CorrelationB, before: 100, maximum: 100)
        );

        AssertApplied(physical, expectedAppliedDamage: 1, expectedAfterHealth: 99);
        AssertApplied(direct, expectedAppliedDamage: 80, expectedAfterHealth: 20);
        Assert.Equal(1, applyExecutor.CallCount);
        Assert.Equal(1, reduceExecutor.CallCount);
        Assert.NotEqual(physical.Receipt!.Operation, direct.Receipt!.Operation);
        Assert.NotEqual(physical.Receipt.RequestedDamage, direct.Receipt.RequestedDamage);
    }

    [Fact]
    public void Reduce_to_floor_never_invokes_the_configured_physical_executor()
    {
        var applyExecutor = new RecordingApplyExecutor(
            _ => throw new InvalidOperationException("physical executor must not run")
        );
        var reduceExecutor = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = StartedService(applyExecutor, reduceExecutor);

        var result = service.ReduceToFloor(
            Reduce(CorrelationA, before: 100, maximum: 100)
        );

        AssertApplied(result, expectedAppliedDamage: 80, expectedAfterHealth: 20);
        Assert.Equal(0, applyExecutor.CallCount);
        Assert.Equal(1, reduceExecutor.CallCount);
    }

    [Fact]
    public void Direct_floor_request_and_adapter_exclude_physical_defense_and_iframe_inputs()
    {
        var requestProperties = typeof(ReduceToFloorExecutionRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("RequestedDamage", requestProperties);
        Assert.DoesNotContain("Defense", requestProperties);
        Assert.DoesNotContain("Invincible", requestProperties);

        var source = ReadSource("SmapiReduceToFloorExecutor.cs");
        Assert.Contains("player.health = request.FloorHealth", source, StringComparison.Ordinal);
        Assert.Contains("health-snapshot-drift", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split(
                "player.health = request.FloorHealth",
                StringSplitOptions.None
            ).Length - 1
        );
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("buffs.Defense", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Book_Defense", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temporarilyInvincible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("invincibleCountdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IApplyDamageUpToFloorExecutor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
    }

    private static NonLethalDamageService StartedService(
        IApplyDamageUpToFloorExecutor? applyExecutor = null,
        IReduceToFloorExecutor? reduceExecutor = null
    )
    {
        var service = new NonLethalDamageService(applyExecutor, reduceExecutor);
        var result = service.BeginSession(SessionA);
        Assert.True(result.Accepted);
        return service;
    }

    private static ApplyDamageUpToFloorRequest Apply(
        string correlationId,
        string playerKey = "1",
        int before = 100,
        int maximum = 100,
        int requested = 100
    )
    {
        return new ApplyDamageUpToFloorRequest(
            new NonLethalDamageContext(
                SessionA,
                correlationId,
                playerKey,
                NonLethalDamagePurpose.DarknessAttack,
                SanityAuthorityRole.Host,
                AuthorityRevision: 1
            ),
            before,
            maximum,
            requested
        );
    }

    private static ReduceToFloorRequest Reduce(
        string correlationId,
        string sessionId = SessionA,
        string playerKey = "1",
        SanityAuthorityRole authority = SanityAuthorityRole.Host,
        int before = 100,
        int maximum = 100
    )
    {
        return new ReduceToFloorRequest(
            new NonLethalDamageContext(
                sessionId,
                correlationId,
                playerKey,
                NonLethalDamagePurpose.SanityDarknessSpecialDeath,
                authority,
                AuthorityRevision: 1
            ),
            before,
            maximum
        );
    }

    private static ReduceToFloorExecutionResult ApplyDirectFloor(
        ReduceToFloorExecutionRequest request
    )
    {
        Assert.True(request.BeforeHealth > request.FloorHealth);
        Assert.Equal(
            request.BeforeHealth - request.FloorHealth,
            request.MaximumAllowedDamage
        );
        return ReduceToFloorExecutionResult.AppliedResult(
            request.MaximumAllowedDamage,
            request.FloorHealth
        );
    }

    private static void AssertApplied(
        NonLethalDamageResult result,
        int expectedAppliedDamage,
        int expectedAfterHealth
    )
    {
        Assert.Equal(NonLethalDamageResultStatus.Applied, result.Status);
        Assert.Equal(NonLethalDamageCapabilityStatus.Available, result.Capability.Status);
        Assert.Equal(NonLethalDamageReceiptOutcome.Applied, result.Receipt!.Outcome);
        Assert.Equal(expectedAppliedDamage, result.Receipt.AppliedDamage);
        Assert.Equal(expectedAfterHealth, result.Receipt.AfterHealth);
        Assert.True(result.Receipt.IsInvariantSatisfied(out var reason), reason);
        Assert.True(result.MutationApplied);
    }

    private static string ReadSource(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "NonLethalDamage",
                fileName
            )
        );
    }

    private sealed class RecordingApplyExecutor : IApplyDamageUpToFloorExecutor
    {
        private readonly Func<
            ApplyDamageUpToFloorExecutionRequest,
            ApplyDamageUpToFloorExecutionResult
        > execute;

        internal RecordingApplyExecutor(
            Func<
                ApplyDamageUpToFloorExecutionRequest,
                ApplyDamageUpToFloorExecutionResult
            > execute
        )
        {
            this.execute = execute;
        }

        internal int CallCount { get; private set; }

        public ApplyDamageUpToFloorExecutionResult Execute(
            ApplyDamageUpToFloorExecutionRequest request
        )
        {
            CallCount++;
            return execute(request);
        }
    }

    private sealed class RecordingReduceExecutor : IReduceToFloorExecutor
    {
        private readonly Func<
            ReduceToFloorExecutionRequest,
            ReduceToFloorExecutionResult
        > execute;

        internal RecordingReduceExecutor(
            Func<
                ReduceToFloorExecutionRequest,
                ReduceToFloorExecutionResult
            > execute
        )
        {
            this.execute = execute;
        }

        internal int CallCount { get; private set; }

        public ReduceToFloorExecutionResult Execute(ReduceToFloorExecutionRequest request)
        {
            CallCount++;
            return execute(request);
        }
    }
}
