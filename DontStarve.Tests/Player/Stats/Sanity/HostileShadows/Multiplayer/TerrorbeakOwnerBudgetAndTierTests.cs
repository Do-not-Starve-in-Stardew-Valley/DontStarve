using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class TerrorbeakOwnerBudgetAndTierTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerA = "101";
    private const string OwnerB = "202";

    public static TheoryData<string, long, int, int> DensityCases =>
        new()
        {
            { SanityMonsterIntensityIds.None, 60, 0, 0 },
            { SanityMonsterIntensityIds.Less, 120, 1, 1 },
            { SanityMonsterIntensityIds.Default, 60, 1, 2 },
            { SanityMonsterIntensityIds.More, 60, 2, 3 },
            { SanityMonsterIntensityIds.Many, 30, 3, 4 },
            { SanityMonsterIntensityIds.Insane, 30, 4, 5 },
        };

    [Fact]
    public void Exact_fifteen_and_ten_boundaries_publish_diagnostic_eligibility_without_consuming_rejected_budget()
    {
        var harness = new Harness();

        var fifteen = harness.Observe(OwnerA, current: 15d, revision: 1);
        Assert.Contains(fifteen.Events, stateEvent => IsEntered(stateEvent, SanityTierIds.Danger));
        Assert.DoesNotContain(
            fifteen.Events,
            stateEvent => IsEntered(stateEvent, SanityTierIds.Terrorbeak)
        );

        var rejected = harness.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 0,
            occupancy: 0,
            SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.SpeciesIneligible,
            rejected.Status
        );
        Assert.Equal("budget.terrorbeak-requires-hostile10", rejected.Reason);
        Assert.Equal(SanityShadowPoolTier.Hostile15, rejected.PoolTier);
        Assert.Equal(SanityShadowEligibleSpecies.CreeperFear, rejected.EligibleSpecies);
        Assert.True(harness.Governor.TryGetOwnerState(OwnerA, out var untouched));
        Assert.Equal(0, untouched!.Cap);
        Assert.Null(untouched.NextDueMinute);

        var creeper = harness.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 0,
            occupancy: 0,
            SanityShadowSpecies.CreeperFear
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, creeper.Status);
        Assert.Equal(1, creeper.Cap);
        Assert.Equal(60, creeper.NextDueMinute);

        var ten = harness.Observe(OwnerA, current: 10d, revision: 2);
        Assert.Contains(ten.Events, stateEvent => IsEntered(stateEvent, SanityTierIds.Terrorbeak));
        var terrorbeak = harness.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 1,
            occupancy: 0,
            SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, terrorbeak.Status);
        Assert.Equal(SanityShadowPoolTier.Hostile10, terrorbeak.PoolTier);
        Assert.Equal(2, terrorbeak.Cap);
        Assert.Equal(
            SanityShadowEligibleSpecies.CreeperFear
                | SanityShadowEligibleSpecies.Terrorbeak,
            terrorbeak.EligibleSpecies
        );

        var due = harness.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 61,
            occupancy: 0,
            SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PermitGranted, due.Status);
        Assert.Equal(due.EligibleSpecies, due.Permit!.Value.EligibleSpecies);

        var aboveTen = harness.Observe(OwnerA, current: 10.0001d, revision: 3);
        Assert.Contains(
            aboveTen.Events,
            stateEvent => IsExited(stateEvent, SanityTierIds.Terrorbeak)
        );
        var noLongerEligible = harness.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 62,
            occupancy: 0,
            SanityShadowSpecies.Terrorbeak
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.SpeciesIneligible,
            noLongerEligible.Status
        );
        Assert.Equal(SanityShadowPoolTier.Hostile15, noLongerEligible.PoolTier);
    }

    [Theory]
    [MemberData(nameof(DensityCases))]
    public void Six_densities_keep_base_and_ten_percent_caps_intervals_and_refresh_binding(
        string intensityId,
        long intervalMinutes,
        int baseCap,
        int terrorbeakCap
    )
    {
        var hostile15 = new Harness(intensityId);
        hostile15.Observe(OwnerA, current: 15d, revision: 1);
        var creeper = hostile15.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 0,
            occupancy: 0,
            SanityShadowSpecies.CreeperFear
        );

        var hostile10 = new Harness(intensityId);
        hostile10.Observe(OwnerA, current: 10d, revision: 1);
        var terrorbeak = hostile10.Governor.EvaluateHostileSpawn(
            OwnerA,
            gameMinute: 0,
            occupancy: 0,
            SanityShadowSpecies.Terrorbeak
        );

        Assert.Equal(intervalMinutes, creeper.IntervalMinutes);
        Assert.Equal(intervalMinutes, terrorbeak.IntervalMinutes);
        Assert.Equal(baseCap, creeper.Cap);
        Assert.Equal(terrorbeakCap, terrorbeak.Cap);
        Assert.Equal(SanityShadowEligibleSpecies.CreeperFear, creeper.EligibleSpecies);
        Assert.Equal(
            SanityShadowEligibleSpecies.CreeperFear
                | SanityShadowEligibleSpecies.Terrorbeak,
            terrorbeak.EligibleSpecies
        );
        Assert.True(
            HostileShadowSpeciesBindingPolicy.TrySelectIntervalBinding(
                SanityShadowPoolTier.Hostile15,
                out var hostile15Binding,
                out var hostile15Reason
            ),
            hostile15Reason
        );
        Assert.True(
            HostileShadowSpeciesBindingPolicy.TrySelectIntervalBinding(
                SanityShadowPoolTier.Hostile10,
                out var hostile10Binding,
                out var hostile10Reason
            ),
            hostile10Reason
        );
        Assert.Equal(ShadowMonsterAssetBindingIds.CreeperFear, hostile15Binding);
        Assert.Equal(ShadowMonsterAssetBindingIds.Terrorbeak, hostile10Binding);
    }

    [Fact]
    public void Fifteen_percent_rejects_new_terrorbeak_before_timer_or_conversion_epoch_is_consumed()
    {
        var harness = new Harness();
        harness.Observe(OwnerA, current: 15d, revision: 1);

        var rejected = harness.Spawn(
            "terrorbeak-at-fifteen",
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            OwnerA,
            gameMinute: 0,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.Equal(HostileShadowSpawnStatus.Rejected, rejected.Status);
        Assert.Equal("budget.terrorbeak-requires-hostile10", rejected.Reason);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.SpeciesIneligible,
            rejected.BudgetStatus
        );
        Assert.Equal(0, harness.Authority.Count);
        Assert.True(harness.Governor.TryGetOwnerState(OwnerA, out var afterReject));
        Assert.Equal(0, afterReject!.Cap);
        Assert.Null(afterReject.NextDueMinute);

        var creeper = harness.Spawn(
            "creeper-at-fifteen",
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            OwnerA,
            gameMinute: 0,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.True(creeper.Spawned);
        Assert.Equal(1, creeper.Cap);
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerA));
    }

    [Fact]
    public void Default_ten_percent_pool_competes_at_cap_and_one_vacancy_restarts_the_clock()
    {
        var harness = new Harness();
        harness.Observe(OwnerA, current: 15d, revision: 1);
        Assert.True(
            harness.Spawn(
                "creeper-conversion",
                HostileShadowSpawnOrigin.OwnerProjectionConversion,
                OwnerA,
                gameMinute: 0,
                ShadowMonsterAssetBindingIds.CreeperFear
            ).Spawned
        );

        harness.Observe(OwnerA, current: 10d, revision: 2);
        var terrorbeak = harness.Spawn(
            "terrorbeak-cap-expansion",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 1,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.True(terrorbeak.Spawned);
        Assert.Equal(2, terrorbeak.Cap);

        var atCap = harness.Spawn(
            "same-owner-at-cap",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 2,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.Equal(HostileShadowSpawnStatus.AtCap, atCap.Status);
        var original = harness.Authority.CreateFullSnapshot().Entities.ToArray();
        Assert.Equal(2, original.Length);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                ShadowMonsterAssetBindingIds.CreeperFear,
                ShadowMonsterAssetBindingIds.Terrorbeak,
            },
            original.Select(state => state.AssetBindingId).ToHashSet(StringComparer.Ordinal)
        );

        var creeperId = original.Single(
            state => state.AssetBindingId == ShadowMonsterAssetBindingIds.CreeperFear
        ).EntityId;
        var terrorbeakId = original.Single(
            state => state.AssetBindingId == ShadowMonsterAssetBindingIds.Terrorbeak
        ).EntityId;
        Assert.True(
            harness.Authority.CleanupEntity(
                creeperId,
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var replacement = harness.Spawn(
            "single-vacancy-replacement",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 3,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.True(replacement.Spawned);
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerA));

        Assert.True(
            harness.Authority.CleanupEntity(
                terrorbeakId,
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var noSecondFill = harness.Spawn(
            "same-minute-second-vacancy",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 3,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.Equal(HostileShadowSpawnStatus.Waiting, noSecondFill.Status);
        Assert.Equal(1, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.True(harness.Governor.TryGetOwnerState(OwnerA, out var restarted));
        Assert.Equal(63, restarted!.NextDueMinute);
    }

    [Fact]
    public void Leaving_ten_percent_keeps_existing_terrorbeak_but_rejects_new_refreshes()
    {
        var harness = new Harness();
        harness.Observe(OwnerA, current: 10d, revision: 1);
        var existing = harness.Spawn(
            "existing-terrorbeak",
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            OwnerA,
            gameMinute: 0,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.True(existing.Spawned);

        var aboveTen = harness.Observe(OwnerA, current: 10.0001d, revision: 2);
        Assert.Contains(
            aboveTen.Events,
            stateEvent => IsExited(stateEvent, SanityTierIds.Terrorbeak)
        );
        Assert.DoesNotContain(
            aboveTen.Events,
            stateEvent => IsExited(stateEvent, SanityTierIds.Danger)
        );
        Assert.True(harness.Authority.TryGetEntity(existing.EntityId!.Value, out var retained));
        Assert.Equal(ShadowMonsterAssetBindingIds.Terrorbeak, retained!.AssetBindingId);

        var rejected = harness.Spawn(
            "new-terrorbeak-above-ten",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 60,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.Equal(HostileShadowSpawnStatus.Rejected, rejected.Status);
        Assert.Equal("budget.terrorbeak-requires-hostile10", rejected.Reason);
        Assert.Equal(1, harness.Authority.Count);

        var creeperAtReducedCap = harness.Spawn(
            "creeper-at-reduced-cap",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 60,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.Equal(HostileShadowSpawnStatus.AtCap, creeperAtReducedCap.Status);
        Assert.Equal(1, creeperAtReducedCap.Cap);
        Assert.Equal(1, harness.Authority.Count);
    }

    [Fact]
    public void Two_owners_have_independent_two_slot_pools_and_target_changes_never_move_owner()
    {
        var harness = new Harness();
        harness.Observe(OwnerA, current: 10d, revision: 1);
        harness.Observe(OwnerB, current: 10d, revision: 1);

        Assert.True(
            harness.Spawn(
                "owner-a-conversion",
                HostileShadowSpawnOrigin.OwnerProjectionConversion,
                OwnerA,
                gameMinute: 0,
                ShadowMonsterAssetBindingIds.Terrorbeak
            ).Spawned
        );
        Assert.True(
            harness.Spawn(
                "owner-b-conversion",
                HostileShadowSpawnOrigin.OwnerProjectionConversion,
                OwnerB,
                gameMinute: 0,
                ShadowMonsterAssetBindingIds.CreeperFear
            ).Spawned
        );
        Assert.True(
            harness.Spawn(
                "owner-a-interval",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                gameMinute: 60,
                ShadowMonsterAssetBindingIds.CreeperFear
            ).Spawned
        );
        Assert.True(
            harness.Spawn(
                "owner-b-interval",
                HostileShadowSpawnOrigin.Interval,
                OwnerB,
                gameMinute: 60,
                ShadowMonsterAssetBindingIds.Terrorbeak
            ).Spawned
        );
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerB));
        Assert.Equal(4, harness.Authority.Count);

        var ownerATerrorbeak = harness.Authority.CreateFullSnapshot().Entities.Single(
            state =>
                state.OwnerPlayerKey == OwnerA
                && state.AssetBindingId == ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.True(
            harness.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    ownerATerrorbeak.EntityId,
                    ownerATerrorbeak.LocationId,
                    HostileShadowStateIds.Chase,
                    OwnerB,
                    ownerATerrorbeak.PositionX + 64d,
                    ownerATerrorbeak.PositionY,
                    ownerATerrorbeak.Health,
                    "hostile-shadow.target-nearest-player"
                ),
                out var reason
            ),
            reason
        );
        Assert.True(
            harness.Authority.TryGetEntity(ownerATerrorbeak.EntityId, out var retargeted)
        );
        Assert.Equal(OwnerA, retargeted!.OwnerPlayerKey);
        Assert.Equal(OwnerB, retargeted.TargetPlayerKey);
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(2, harness.Authority.GetOwnerOccupancy(OwnerB));
    }

    [Fact]
    public void Dynamic_density_changes_fill_only_one_new_slot_and_preserve_overcap_entities()
    {
        var provider = new MutableIntensityProvider();
        var harness = new Harness(provider: provider);
        harness.Observe(OwnerA, current: 10d, revision: 1);
        Assert.True(
            harness.Spawn(
                "default-first",
                HostileShadowSpawnOrigin.OwnerProjectionConversion,
                OwnerA,
                gameMinute: 0,
                ShadowMonsterAssetBindingIds.Terrorbeak
            ).Spawned
        );
        Assert.True(
            harness.Spawn(
                "default-second",
                HostileShadowSpawnOrigin.Interval,
                OwnerA,
                gameMinute: 60,
                ShadowMonsterAssetBindingIds.CreeperFear
            ).Spawned
        );

        provider.Value = SanityMonsterIntensityIds.Many;
        var expanded = harness.Spawn(
            "many-single-expansion",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 61,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.True(expanded.Spawned);
        Assert.Equal(4, expanded.Cap);
        var noFillAll = harness.Spawn(
            "many-no-fill-all",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 61,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.Equal(HostileShadowSpawnStatus.Waiting, noFillAll.Status);
        Assert.Equal(3, harness.Authority.Count);
        Assert.True(harness.Governor.TryGetOwnerState(OwnerA, out var many));
        Assert.Equal(30, many!.IntervalMinutes);
        Assert.Equal(91, many.NextDueMinute);

        provider.Value = SanityMonsterIntensityIds.Less;
        var reduced = harness.Spawn(
            "less-overcap",
            HostileShadowSpawnOrigin.Interval,
            OwnerA,
            gameMinute: 62,
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
        Assert.Equal(HostileShadowSpawnStatus.AtCap, reduced.Status);
        Assert.Equal(1, reduced.Cap);
        Assert.Equal(3, harness.Authority.Count);
        Assert.True(harness.Governor.TryGetOwnerState(OwnerA, out var less));
        Assert.Equal(1, less!.Cap);
        Assert.Equal(120, less.IntervalMinutes);
        Assert.True(less.IsPausedAtCap);
    }

    [Fact]
    public void Unknown_binding_fails_closed_before_budget_and_does_not_block_creeper_regression()
    {
        var harness = new Harness();
        harness.Observe(OwnerA, current: 15d, revision: 1);

        var unknown = harness.Spawn(
            "unknown-binding",
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            OwnerA,
            gameMinute: 0,
            "sanity.shadow-monster.unknown"
        );
        Assert.Equal(HostileShadowSpawnStatus.Rejected, unknown.Status);
        Assert.Equal("hostile-shadow.asset-binding-not-budget-species", unknown.Reason);
        Assert.Null(unknown.BudgetStatus);

        var creeper = harness.Spawn(
            "creeper-after-unknown",
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            OwnerA,
            gameMinute: 0,
            ShadowMonsterAssetBindingIds.CreeperFear
        );
        Assert.True(creeper.Spawned);
        Assert.Equal(1, harness.Authority.Count);
    }

    [Fact]
    public void Smapi_host_uses_the_shared_tier_strategy_and_species_aware_budget_facade()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                "SmapiHostileShadowHost.cs"
            )
        );

        Assert.Contains(
            "HostileShadowSpeciesBindingPolicy.TrySelectIntervalBinding(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SanityShadowPoolEligibilityPolicy.TryAuthorize(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "lifecycle.EvaluateHostileShadowBudget(",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", source, StringComparison.Ordinal);
    }

    private static bool IsEntered(SanityStateEvent stateEvent, string tierId)
    {
        return stateEvent.Kind == SanityStateEventKind.TierEntered
            && string.Equals(stateEvent.TierId, tierId, StringComparison.Ordinal);
    }

    private static bool IsExited(SanityStateEvent stateEvent, string tierId)
    {
        return stateEvent.Kind == SanityStateEventKind.TierExited
            && string.Equals(stateEvent.TierId, tierId, StringComparison.Ordinal);
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile(string bindingId)
    {
        return new ShadowMonsterRuntimeProfile(
            ShadowMonsterDifficultyProfileIds.Compatible,
            bindingId,
            adapterVersion: 1,
            maxHealth: bindingId == ShadowMonsterAssetBindingIds.Terrorbeak ? 400 : 300,
            baseDamage: bindingId == ShadowMonsterAssetBindingIds.Terrorbeak ? 50 : 20,
            movementSpeed: 2d,
            defense: 0,
            detectionRadiusTiles: 8d,
            detectionRadiusPixels: 512d,
            attackRangeTiles: 1d,
            attackRangePixels: 64d,
            attackIntervalSeconds: 1d,
            naturalDespawnGameHours: 4d,
            displayNameKey: "monster.test-shadow",
            wallTraversalMode: ShadowMonsterProfileContractIds.DirectThroughTerrain,
            Array.Empty<string>(),
            new ShadowMonsterDropTable(
                1,
                ShadowMonsterProfileContractIds.VoidEssenceDropTable,
                ShadowMonsterProfileContractIds.VoidEssenceSemanticItem,
                0,
                0,
                0d
            ),
            sanityReward: 0,
            animationProfileId: "sanity.animation.test-shadow.profile",
            cueSetId: "sanity.cue.test-shadow",
            attackMotionPolicyId: "sanity.attack-motion.one-tile-v1",
            postAttackPolicyId: ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
            experienceValue: 0,
            killCounterId: null
        );
    }

    private sealed class Harness
    {
        private readonly SanityTierStateMachine tiers = new();

        internal Harness(
            string intensityId = SanityMonsterIntensityIds.Default,
            MutableIntensityProvider? provider = null
        )
        {
            Governor = new SanityShadowBudgetGovernor(
                provider ?? new MutableIntensityProvider(intensityId)
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
        }

        internal SanityShadowBudgetGovernor Governor { get; }

        internal HostileShadowAuthority Authority { get; }

        internal SanityTierEvaluationResult Observe(
            string owner,
            double current,
            long revision
        )
        {
            var result = tiers.Observe(
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
                Authority.ObserveStateEvent(stateEvent);
            }
            return result;
        }

        internal HostileShadowSpawnResult Spawn(
            string requestId,
            HostileShadowSpawnOrigin origin,
            string owner,
            long gameMinute,
            string bindingId
        )
        {
            return Authority.TrySpawn(
                new HostileShadowSpawnCommand(
                    requestId,
                    origin,
                    owner,
                    owner == OwnerA ? "Farm" : "Mine",
                    128d,
                    256d,
                    gameMinute,
                    RuntimeProfile(bindingId),
                    origin == HostileShadowSpawnOrigin.OwnerProjectionConversion
                        ? "hostile-shadow.spawn.owner-projection-conversion"
                        : "hostile-shadow.spawn.interval"
                )
            );
        }
    }

    private sealed class MutableIntensityProvider : ISanityMonsterIntensityProvider
    {
        internal MutableIntensityProvider(
            string value = SanityMonsterIntensityIds.Default
        )
        {
            Value = value;
        }

        internal string Value { get; set; }

        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(
                true,
                Value,
                "test-intensity"
            );
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

    private sealed class IncrementingIdSource : IHostileShadowEntityIdSource
    {
        private long next = 2000;

        public long Next()
        {
            return ++next;
        }
    }
}
