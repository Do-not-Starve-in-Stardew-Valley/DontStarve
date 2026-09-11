using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class ShadowBudgetSharedCapContractTests
{
    private const string Owner = "101";

    public static TheoryData<string, int> TotalCapCases =>
        new()
        {
            { SanityMonsterIntensityIds.None, 0 },
            { SanityMonsterIntensityIds.Less, 2 },
            { SanityMonsterIntensityIds.Default, 3 },
            { SanityMonsterIntensityIds.More, 5 },
            { SanityMonsterIntensityIds.Many, 7 },
            { SanityMonsterIntensityIds.Insane, 9 },
        };

    [Theory]
    [MemberData(nameof(TotalCapCases))]
    public void GetCap_uses_the_policy_total_for_both_hostile_tiers(
        string intensityId,
        int expectedTotalCap
    )
    {
        Assert.True(
            SanityShadowBudgetPolicyCatalog.TryGet(intensityId, out var policy)
        );
        Assert.NotNull(policy);

        Assert.Equal(expectedTotalCap, policy!.BaseCap + policy.TerrorbeakCap);
        Assert.Equal(
            expectedTotalCap,
            policy.GetCap(SanityShadowPoolTier.Hostile15)
        );
        Assert.Equal(
            expectedTotalCap,
            policy.GetCap(SanityShadowPoolTier.Hostile10)
        );
    }

    [Theory]
    [InlineData(SanityMonsterIntensityIds.Less, 2, 120)]
    [InlineData(SanityMonsterIntensityIds.Default, 3, 60)]
    [InlineData(SanityMonsterIntensityIds.More, 5, 60)]
    public void Hostile15_budget_allows_the_same_species_until_the_shared_cap(
        string intensityId,
        int expectedCap,
        long intervalMinutes
    )
    {
        var harness = new BudgetHarness(intensityId);
        harness.EnterDanger();

        var started = harness.Evaluate(
            gameMinute: 0,
            occupancy: 0,
            species: SanityShadowSpecies.CreeperFear
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, started.Status);
        Assert.Equal(expectedCap, started.Cap);

        var due = harness.Evaluate(
            gameMinute: intervalMinutes,
            occupancy: expectedCap - 1,
            species: SanityShadowSpecies.CreeperFear
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PermitGranted, due.Status);
        Assert.Equal(expectedCap, due.Cap);
    }

    [Theory]
    [InlineData(SanityMonsterIntensityIds.Less, 2, 120)]
    [InlineData(SanityMonsterIntensityIds.Default, 3, 60)]
    [InlineData(SanityMonsterIntensityIds.More, 5, 60)]
    public void Hostile10_budget_allows_terrorbeak_against_the_same_shared_cap(
        string intensityId,
        int expectedCap,
        long intervalMinutes
    )
    {
        var harness = new BudgetHarness(intensityId);
        harness.EnterTerrorbeak();

        var started = harness.Evaluate(
            gameMinute: 0,
            occupancy: 0,
            species: SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, started.Status);
        Assert.Equal(expectedCap, started.Cap);

        var due = harness.Evaluate(
            gameMinute: intervalMinutes,
            occupancy: expectedCap - 1,
            species: SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PermitGranted, due.Status);
        Assert.Equal(expectedCap, due.Cap);
    }

    [Fact]
    public void Hostile15_rejects_terrorbeak_but_hostile10_keeps_it_eligible()
    {
        Assert.False(
            SanityShadowPoolEligibilityPolicy.TryAuthorize(
                SanityShadowPoolTier.Hostile15,
                SanityShadowSpecies.Terrorbeak,
                out var hostile15Reason
            )
        );
        Assert.Equal("budget.terrorbeak-requires-hostile10", hostile15Reason);

        Assert.True(
            SanityShadowPoolEligibilityPolicy.TryAuthorize(
                SanityShadowPoolTier.Hostile10,
                SanityShadowSpecies.Terrorbeak,
                out var hostile10Reason
            )
        );
        Assert.Equal("budget.species-eligible", hostile10Reason);
    }

    private static SanityStateEvent TierEntered(string tierId, long revision)
    {
        return new SanityStateEvent(
            SanityStateEventIds.TierEntered(tierId),
            SanityStateEventKind.TierEntered,
            Owner,
            tierId,
            revision,
            0.1d
        );
    }

    private sealed class BudgetHarness
    {
        private readonly SanityShadowBudgetGovernor governor;

        internal BudgetHarness(string intensityId)
        {
            governor = new SanityShadowBudgetGovernor(
                new FixedIntensityProvider(intensityId)
            );
            Assert.True(
                governor.ApplyStateEvent(
                    new SanityStateEvent(
                        SanityStateEventIds.SystemEnabled,
                        SanityStateEventKind.SystemEnabled,
                        string.Empty,
                        string.Empty,
                        -1,
                        null
                    ),
                    out var reason
                ),
                reason
            );
        }

        internal void EnterDanger()
        {
            ApplyTier(SanityTierIds.ShadowCreatures, 1);
            ApplyTier(SanityTierIds.Danger, 2);
        }

        internal void EnterTerrorbeak()
        {
            EnterDanger();
            ApplyTier(SanityTierIds.Terrorbeak, 3);
        }

        internal SanityShadowBudgetEvaluationResult Evaluate(
            long gameMinute,
            int occupancy,
            SanityShadowSpecies species
        )
        {
            return governor.EvaluateHostileSpawn(
                Owner,
                gameMinute,
                occupancy,
                species
            );
        }

        private void ApplyTier(string tierId, long revision)
        {
            Assert.True(
                governor.ApplyStateEvent(
                    TierEntered(tierId, revision),
                    out var reason
                ),
                reason
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
            return new SanityMonsterIntensityResolution(
                true,
                intensityId,
                "test-intensity"
            );
        }
    }
}
