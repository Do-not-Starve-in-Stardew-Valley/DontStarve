using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Damage;
using DontStarve.Player.Stats.Sanity.PassOut.Damage;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut.Damage;

public sealed class SanityDarknessSpecialDeathNonLethalPolicyTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string CorrelationA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(4, 4, 1, 3, true)]
    [InlineData(6, 6, 2, 4, true)]
    [InlineData(99, 21, 20, 1, true)]
    [InlineData(101, 101, 21, 80, true)]
    [InlineData(99, 20, 20, 0, false)]
    [InlineData(99, 19, 20, 0, false)]
    [InlineData(1, 1, 1, 0, false)]
    [InlineData(1, 0, 1, 0, false)]
    public void Fractional_twenty_percent_boundaries_reduce_once_or_do_not_heal(
        int maximumHealth,
        int currentHealth,
        int expectedFloor,
        int expectedDamage,
        bool expectedApplied
    )
    {
        var fixture = Fixture();

        var result = fixture.Policy.ReduceToFloor(
            Request(currentHealth, maximumHealth)
        );

        Assert.Equal(
            expectedApplied
                ? SanityDarknessSpecialDeathNonLethalStatus.Applied
                : SanityDarknessSpecialDeathNonLethalStatus.NoChange,
            result.Status
        );
        Assert.Equal(expectedApplied, result.ShouldEmitDamageFeedback);
        Assert.Equal(
            expectedApplied
                ? NonLethalDamageResultStatus.Applied
                : NonLethalDamageResultStatus.NoChange,
            result.NonLethalStatus
        );
        var receipt = Assert.IsType<NonLethalDamageReceipt>(result.Receipt);
        Assert.Equal(NonLethalDamageOperation.ReduceToFloor, receipt.Operation);
        Assert.Equal(
            NonLethalDamagePurpose.SanityDarknessSpecialDeath,
            receipt.Purpose
        );
        Assert.Equal(expectedFloor, receipt.FloorHealth);
        Assert.Equal(expectedFloor, receipt.TargetHealth);
        Assert.Equal(expectedDamage, receipt.AppliedDamage);
        Assert.Equal(
            expectedApplied ? expectedFloor : currentHealth,
            receipt.AfterHealth
        );
        Assert.Null(receipt.RequestedDamage);
        Assert.True(receipt.IsInvariantSatisfied(out var reason), reason);
        Assert.Equal(expectedApplied ? 1 : 0, fixture.Reduce.CallCount);
        Assert.Equal(0, fixture.Apply.CallCount);
    }

    [Fact]
    public void Duplicate_correlation_reuses_one_receipt_and_emits_feedback_only_once()
    {
        var fixture = Fixture();
        var request = Request(currentHealth: 100, maximumHealth: 101);

        var original = fixture.Policy.ReduceToFloor(request);
        var duplicate = fixture.Policy.ReduceToFloor(request);

        Assert.Equal(
            SanityDarknessSpecialDeathNonLethalStatus.Applied,
            original.Status
        );
        Assert.Equal(
            SanityDarknessSpecialDeathNonLethalStatus.Duplicate,
            duplicate.Status
        );
        Assert.True(original.ShouldEmitDamageFeedback);
        Assert.False(duplicate.ShouldEmitDamageFeedback);
        Assert.Same(original.Receipt, duplicate.Receipt);
        Assert.Equal(79, original.Receipt!.AppliedDamage);
        Assert.Equal(21, original.Receipt.AfterHealth);
        Assert.Equal(1, fixture.Reduce.CallCount);
        Assert.Equal(0, fixture.Apply.CallCount);
        Assert.Equal(1, fixture.Service.ReceiptCount);
    }

    [Fact]
    public void Correlation_conflict_returns_original_receipt_without_mutation_or_feedback()
    {
        var fixture = Fixture();
        var original = fixture.Policy.ReduceToFloor(
            Request(currentHealth: 100, maximumHealth: 100)
        );

        var conflict = fixture.Policy.ReduceToFloor(
            Request(currentHealth: 99, maximumHealth: 100)
        );

        Assert.Equal(
            SanityDarknessSpecialDeathNonLethalStatus.Rejected,
            conflict.Status
        );
        Assert.Equal(
            NonLethalDamageResultStatus.CorrelationConflict,
            conflict.NonLethalStatus
        );
        Assert.Equal("nonlethal.correlation-conflict", conflict.Reason);
        Assert.Same(original.Receipt, conflict.Receipt);
        Assert.False(conflict.ShouldEmitDamageFeedback);
        Assert.Equal(1, fixture.Reduce.CallCount);
        Assert.Equal(1, fixture.Service.ReceiptCount);
    }

    [Fact]
    public void Client_authority_is_rejected_before_executor_or_receipt()
    {
        var fixture = Fixture();

        var result = fixture.Policy.ReduceToFloor(
            Request(
                currentHealth: 100,
                maximumHealth: 100,
                authority: SanityAuthorityRole.Client
            )
        );

        Assert.Equal(
            SanityDarknessSpecialDeathNonLethalStatus.Rejected,
            result.Status
        );
        Assert.Equal(
            NonLethalDamageResultStatus.RequiresHostAuthority,
            result.NonLethalStatus
        );
        Assert.Equal("nonlethal.host-mutating-authority-required", result.Reason);
        Assert.Null(result.Receipt);
        Assert.False(result.ShouldEmitDamageFeedback);
        Assert.Equal(0, fixture.Reduce.CallCount);
        Assert.Equal(0, fixture.Apply.CallCount);
        Assert.Equal(0, fixture.Service.ReceiptCount);
    }

    [Fact]
    public void Policy_contract_has_no_physical_damage_defense_or_iframe_inputs()
    {
        var requestProperties = typeof(SanityDarknessSpecialDeathNonLethalRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("RequestedDamage", requestProperties);
        Assert.DoesNotContain("Defense", requestProperties);
        Assert.DoesNotContain("Invincible", requestProperties);

        var source = ReadContract(
            "PassOutDamage",
            "SanityDarknessSpecialDeathNonLethalPolicy.cs"
        );
        Assert.Equal(
            1,
            source.Split(
                "nonLethalDamage.ReduceToFloor(",
                StringSplitOptions.None
            ).Length - 1
        );
        Assert.Contains(
            "NonLethalDamagePurpose.SanityDarknessSpecialDeath",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("ApplyDamageUpToFloor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Defense", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invincible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temporarilyInvincible", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_wires_both_executors_and_policy_to_one_shared_service()
    {
        var source = ReadContract(
            "DarknessAttack",
            "SmapiDarknessAttackResolutionService.cs"
        );

        Assert.Equal(
            1,
            source.Split("new NonLethalDamageService(", StringSplitOptions.None)
                .Length - 1
        );
        Assert.Contains(
            "new SmapiApplyDamageUpToFloorExecutor(resolvePlayer)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new SmapiReduceToFloorExecutor(resolvePlayer)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "specialDeathNonLethal = new SanityDarknessSpecialDeathNonLethalPolicy(\n"
                + "            nonLethalDamage\n"
                + "        );",
            Normalize(source),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "nonLethalDamage.BeginSession(request.SessionId)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "return specialDeathNonLethal.ReduceToFloor(request);",
            source,
            StringComparison.Ordinal
        );
    }

    private static PolicyFixture Fixture()
    {
        var apply = new RecordingApplyExecutor(
            _ => throw new InvalidOperationException(
                "special death must not invoke the physical executor"
            )
        );
        var reduce = new RecordingReduceExecutor(ApplyDirectFloor);
        var service = new NonLethalDamageService(apply, reduce);
        Assert.True(service.BeginSession(SessionA).Accepted);
        return new PolicyFixture(
            service,
            new SanityDarknessSpecialDeathNonLethalPolicy(service),
            apply,
            reduce
        );
    }

    private static SanityDarknessSpecialDeathNonLethalRequest Request(
        int currentHealth,
        int maximumHealth,
        SanityAuthorityRole authority = SanityAuthorityRole.Host
    )
    {
        return new SanityDarknessSpecialDeathNonLethalRequest(
            SessionA,
            CorrelationA,
            PlayerKey: "1",
            authority,
            AuthorityRevision: 7,
            currentHealth,
            maximumHealth
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

    private static string ReadContract(string directory, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", directory, fileName)
        );
    }

    private static string Normalize(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private sealed record PolicyFixture(
        NonLethalDamageService Service,
        SanityDarknessSpecialDeathNonLethalPolicy Policy,
        RecordingApplyExecutor Apply,
        RecordingReduceExecutor Reduce
    );

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

        public ReduceToFloorExecutionResult Execute(
            ReduceToFloorExecutionRequest request
        )
        {
            CallCount++;
            return execute(request);
        }
    }
}
