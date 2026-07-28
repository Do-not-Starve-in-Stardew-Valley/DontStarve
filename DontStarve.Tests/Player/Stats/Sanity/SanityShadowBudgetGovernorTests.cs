using DontStarve.Config;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Tests.Config;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityShadowBudgetGovernorTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerA = "123456789";
    private const string OwnerB = "223456789";

    public static TheoryData<string, long, int, int> DensityPolicies =>
        new()
        {
            { SanityMonsterIntensityIds.None, 60, 0, 0 },
            { SanityMonsterIntensityIds.Less, 120, 1, 1 },
            { SanityMonsterIntensityIds.Default, 60, 1, 2 },
            { SanityMonsterIntensityIds.More, 60, 2, 3 },
            { SanityMonsterIntensityIds.Many, 30, 3, 4 },
            { SanityMonsterIntensityIds.Insane, 30, 4, 5 },
        };

    [Theory]
    [MemberData(nameof(DensityPolicies))]
    public void Catalog_freezes_all_six_density_policies(
        string intensity,
        long interval,
        int baseCap,
        int terrorbeakCap
    )
    {
        Assert.True(
            SanityShadowBudgetPolicyCatalog.TryGet(intensity, out var policy)
        );
        Assert.NotNull(policy);
        Assert.Equal(interval, policy!.IntervalMinutes);
        Assert.Equal(baseCap, policy.BaseCap);
        Assert.Equal(terrorbeakCap, policy.TerrorbeakCap);
    }

    [Fact]
    public void Typed_config_provider_reads_the_cached_intensity_resolver()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var access = new MemoryFlatConfigFileAccess(
            "{\"SanityMonsterIntensity\":\"Many\"}"
        );
        var loaded = FlatConfigValueStore.Load(registry, access);
        var store = Assert.IsType<FlatConfigValueStore>(loaded.Store);
        var resolver = new TypedConfigResolver(registry, store);
        var provider = new TypedConfigSanityMonsterIntensityProvider(resolver);

        var initial = provider.Resolve();
        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.SanityMonsterIntensity] = ConfigValue.Enum(
                    SanityMonsterIntensityIds.Insane
                ),
            }
        );
        resolver.Refresh();
        var refreshed = provider.Resolve();

        Assert.True(initial.HasValue);
        Assert.Equal(SanityMonsterIntensityIds.Many, initial.Value);
        Assert.True(saved.Success, saved.Reason);
        Assert.True(refreshed.HasValue);
        Assert.Equal(SanityMonsterIntensityIds.Insane, refreshed.Value);
        Assert.Equal(1, access.ReadCount);
    }

    [Fact]
    public void Harmless_50_and_hostile_15_share_base_cap_while_10_uses_upgraded_cap()
    {
        var provider = new MutableIntensityProvider();
        var governor = EnabledGovernor(provider);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);

        var harmless = governor.Evaluate(OwnerA, 0, 0);
        Enter(governor, OwnerA, SanityTierIds.Danger);
        var hostile15 = governor.Evaluate(OwnerA, 1, 0);
        Enter(governor, OwnerA, SanityTierIds.Terrorbeak);
        var hostile10 = governor.Evaluate(OwnerA, 2, 0);

        Assert.Equal(SanityShadowPoolTier.Harmless50, harmless.PoolTier);
        Assert.Equal(1, harmless.Cap);
        Assert.Equal(SanityShadowPoolTier.Hostile15, hostile15.PoolTier);
        Assert.Equal(1, hostile15.Cap);
        Assert.Equal(SanityShadowPoolTier.Hostile10, hostile10.PoolTier);
        Assert.Equal(2, hostile10.Cap);
    }

    [Fact]
    public void Exact_sixty_game_minutes_issue_once_and_restart_the_cycle()
    {
        var governor = ReadyDefaultGovernor(OwnerA);

        var started = governor.Evaluate(OwnerA, 100, 0);
        var beforeDue = governor.Evaluate(OwnerA, 159, 0);
        var due = governor.Evaluate(OwnerA, 160, 0);
        var duplicateMinute = governor.Evaluate(OwnerA, 160, 0);
        var nextDue = governor.Evaluate(OwnerA, 220, 0);

        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, started.Status);
        Assert.Equal("budget.timer-started", started.Reason);
        Assert.Equal(160, started.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, beforeDue.Status);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PermitGranted, due.Status);
        Assert.Equal("budget.permit.interval-elapsed", due.Reason);
        Assert.Equal(220, due.NextDueMinute);
        Assert.NotNull(due.Permit);
        Assert.Equal(OwnerA, due.Permit!.Value.PlayerKey);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.Waiting,
            duplicateMinute.Status
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            nextDue.Status
        );
    }

    [Fact]
    public void Full_cap_pauses_budget_and_multiple_vacancies_immediately_grant_only_one()
    {
        var governor = ReadyDefaultGovernor(OwnerA, includeTerrorbeak: true);

        var full = governor.Evaluate(OwnerA, 0, 2);
        var stillFull = governor.Evaluate(OwnerA, 1000, 2);
        var vacancy = governor.Evaluate(OwnerA, 1001, 0);
        var sameVacancy = governor.Evaluate(OwnerA, 1001, 0);
        var beforeNext = governor.Evaluate(OwnerA, 1060, 0);
        var next = governor.Evaluate(OwnerA, 1061, 0);

        Assert.Equal(SanityShadowBudgetEvaluationStatus.PausedAtCap, full.Status);
        Assert.Null(full.NextDueMinute);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PausedAtCap,
            stillFull.Status
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            vacancy.Status
        );
        Assert.Equal("budget.permit.vacancy", vacancy.Reason);
        Assert.Equal(1061, vacancy.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, sameVacancy.Status);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, beforeNext.Status);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PermitGranted, next.Status);
    }

    [Fact]
    public void Owners_keep_independent_budget_clocks_and_permits()
    {
        var provider = new MutableIntensityProvider();
        var governor = EnabledGovernor(provider);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);
        Enter(governor, OwnerB, SanityTierIds.ShadowCreatures);

        governor.Evaluate(OwnerA, 0, 0);
        governor.Evaluate(OwnerB, 30, 0);
        var ownerADue = governor.Evaluate(OwnerA, 60, 0);
        var ownerBWaiting = governor.Evaluate(OwnerB, 60, 0);
        var ownerBDue = governor.Evaluate(OwnerB, 90, 0);

        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            ownerADue.Status
        );
        Assert.Equal(OwnerA, ownerADue.Permit!.Value.PlayerKey);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.Waiting,
            ownerBWaiting.Status
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            ownerBDue.Status
        );
        Assert.Equal(OwnerB, ownerBDue.Permit!.Value.PlayerKey);
    }

    [Fact]
    public void Density_drop_preserves_overcap_occupancy_and_resumes_only_after_space_exists()
    {
        var provider = new MutableIntensityProvider(
            SanityMonsterIntensityIds.Insane
        );
        var governor = EnabledGovernor(provider);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);
        Enter(governor, OwnerA, SanityTierIds.Danger);

        var initialFull = governor.Evaluate(OwnerA, 0, 4);
        provider.Value = SanityMonsterIntensityIds.Less;
        var overCap = governor.Evaluate(OwnerA, 1, 4);
        var atNewCap = governor.Evaluate(OwnerA, 2, 1);
        var vacancy = governor.Evaluate(OwnerA, 3, 0);

        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PausedAtCap,
            initialFull.Status
        );
        Assert.Equal(SanityShadowBudgetEvaluationStatus.PausedAtCap, overCap.Status);
        Assert.Equal(4, overCap.Occupancy);
        Assert.Equal(1, overCap.Cap);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PausedAtCap,
            atNewCap.Status
        );
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            vacancy.Status
        );
        Assert.Equal(123, vacancy.NextDueMinute);
    }

    [Fact]
    public void None_clears_the_clock_and_reenable_starts_a_fresh_interval()
    {
        var provider = new MutableIntensityProvider();
        var governor = ReadyGovernor(provider, OwnerA);

        var started = governor.Evaluate(OwnerA, 0, 0);
        provider.Value = SanityMonsterIntensityIds.None;
        var none = governor.Evaluate(OwnerA, 30, 0);
        provider.Value = SanityMonsterIntensityIds.Default;
        var restored = governor.Evaluate(OwnerA, 40, 0);
        var oldDue = governor.Evaluate(OwnerA, 60, 0);
        var newDue = governor.Evaluate(OwnerA, 100, 0);

        Assert.Equal(60, started.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Inactive, none.Status);
        Assert.Equal("budget.intensity-none", none.Reason);
        Assert.Null(none.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, restored.Status);
        Assert.Equal(100, restored.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, oldDue.Status);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            newDue.Status
        );
    }

    [Fact]
    public void Cap_increase_from_a_full_pool_grants_one_immediate_permit()
    {
        var provider = new MutableIntensityProvider();
        var governor = ReadyGovernor(provider, OwnerA);
        governor.Evaluate(OwnerA, 0, 1);

        provider.Value = SanityMonsterIntensityIds.More;
        var expanded = governor.Evaluate(OwnerA, 10, 1);
        var repeated = governor.Evaluate(OwnerA, 10, 1);

        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            expanded.Status
        );
        Assert.Equal("budget.permit.vacancy", expanded.Reason);
        Assert.Equal(2, expanded.Cap);
        Assert.Equal(70, expanded.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, repeated.Status);
    }

    [Fact]
    public void Owner_invalidation_discards_budget_and_reconnect_starts_fresh()
    {
        var provider = new MutableIntensityProvider();
        var governor = ReadyGovernor(provider, OwnerA);
        governor.Evaluate(OwnerA, 0, 0);

        Apply(
            governor,
            SanityStateEventKind.OwnerInvalidated,
            SanityStateEventIds.OwnerInvalidated,
            OwnerA
        );
        Assert.False(governor.TryGetOwnerState(OwnerA, out _));
        var whileGone = governor.Evaluate(OwnerA, 60, 0);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);
        var reconnected = governor.Evaluate(OwnerA, 100, 0);

        Assert.Equal(SanityShadowBudgetEvaluationStatus.Inactive, whileGone.Status);
        Assert.Equal("budget.owner-untracked", whileGone.Reason);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, reconnected.Status);
        Assert.Equal(160, reconnected.NextDueMinute);
    }

    [Fact]
    public void World_cleanup_clears_every_owner_and_requires_a_new_enabled_lifecycle()
    {
        var provider = new MutableIntensityProvider();
        var governor = EnabledGovernor(provider);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);
        Enter(governor, OwnerB, SanityTierIds.ShadowCreatures);
        governor.Evaluate(OwnerA, 0, 0);
        governor.Evaluate(OwnerB, 0, 0);

        Apply(
            governor,
            SanityStateEventKind.WorldCleanup,
            SanityStateEventIds.WorldCleanup,
            string.Empty
        );

        Assert.Null(governor.IsSystemEnabled);
        Assert.False(governor.TryGetOwnerState(OwnerA, out _));
        Assert.False(governor.TryGetOwnerState(OwnerB, out _));
    }

    [Fact]
    public void Tier_exit_freezes_budget_without_moving_it_to_another_owner()
    {
        var provider = new MutableIntensityProvider();
        var governor = EnabledGovernor(provider);
        Enter(governor, OwnerA, SanityTierIds.ShadowCreatures);
        governor.Evaluate(OwnerA, 0, 1);

        Exit(governor, OwnerA, SanityTierIds.ShadowCreatures);
        var inactive = governor.Evaluate(OwnerA, 60, 1);
        Enter(governor, OwnerB, SanityTierIds.ShadowCreatures);
        var ownerB = governor.Evaluate(OwnerB, 60, 0);

        Assert.Equal(SanityShadowBudgetEvaluationStatus.Inactive, inactive.Status);
        Assert.Equal(OwnerA, inactive.PlayerKey);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, ownerB.Status);
        Assert.Equal(OwnerB, ownerB.PlayerKey);
        Assert.True(governor.TryGetOwnerState(OwnerA, out var ownerAState));
        Assert.Equal(1, ownerAState!.Occupancy);
    }

    [Fact]
    public void Unavailable_intensity_fails_closed_and_recovers_without_catchup()
    {
        var provider = new MutableIntensityProvider
        {
            HasValue = false,
            Reason = "config.invalid-value:SanityMonsterIntensity",
        };
        var governor = ReadyGovernor(provider, OwnerA);

        var unavailable = governor.Evaluate(OwnerA, 0, 0);
        provider.HasValue = true;
        provider.Value = SanityMonsterIntensityIds.Default;
        var recovered = governor.Evaluate(OwnerA, 100, 0);

        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.Unavailable,
            unavailable.Status
        );
        Assert.Equal(
            "budget.intensity-unavailable:config.invalid-value:SanityMonsterIntensity",
            unavailable.Reason
        );
        Assert.Null(unavailable.Permit);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, recovered.Status);
        Assert.Equal(160, recovered.NextDueMinute);
    }

    [Fact]
    public void Time_regression_restarts_the_interval_without_issuing_backlog()
    {
        var governor = ReadyDefaultGovernor(OwnerA);
        governor.Evaluate(OwnerA, 100, 0);

        var regressed = governor.Evaluate(OwnerA, 90, 0);
        var oldDue = governor.Evaluate(OwnerA, 100, 0);
        var newDue = governor.Evaluate(OwnerA, 150, 0);

        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.Unavailable,
            regressed.Status
        );
        Assert.Equal("budget.time-regressed-restarted", regressed.Reason);
        Assert.Equal(150, regressed.NextDueMinute);
        Assert.Equal(SanityShadowBudgetEvaluationStatus.Waiting, oldDue.Status);
        Assert.Equal(
            SanityShadowBudgetEvaluationStatus.PermitGranted,
            newDue.Status
        );
    }

    [Theory]
    [InlineData("bad-owner", 0, 0, "budget.player-key-invalid")]
    [InlineData(OwnerA, -1, 0, "budget.game-minute-invalid")]
    [InlineData(OwnerA, 0, -1, "budget.occupancy-invalid")]
    public void Invalid_budget_inputs_fail_closed(
        string owner,
        long minute,
        int occupancy,
        string expectedReason
    )
    {
        var governor = ReadyDefaultGovernor(OwnerA);

        var result = governor.Evaluate(owner, minute, occupancy);

        Assert.Equal(SanityShadowBudgetEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Permit);
    }

    [Fact]
    public void Change_service_wires_authoritative_tiers_and_cached_config_into_budget_only()
    {
        var provider = new MutableIntensityProvider();
        var data = SanitySaveDataCodec.NewData(OwnerA, 20d, 200d);
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test",
            20d,
            data
        );
        var service = new SanityChangeService(
            new DefaultSanityMaximumProvider(),
            provider
        );

        Assert.True(
            service.BeginHostSession(Session, persistence, OwnerA, out var reason),
            reason
        );
        var initial = service.EvaluateShadowBudget(OwnerA, 0, 0);
        provider.Value = SanityMonsterIntensityIds.More;
        var refreshed = service.EvaluateShadowBudget(OwnerA, 1, 0);
        service.ClearSession();

        Assert.Equal(SanityShadowPoolTier.Hostile10, initial.PoolTier);
        Assert.Equal(2, initial.Cap);
        Assert.Equal(3, refreshed.Cap);
        Assert.False(service.TryGetShadowBudgetState(OwnerA, out _));
    }

    private static SanityShadowBudgetGovernor ReadyDefaultGovernor(
        string owner,
        bool includeTerrorbeak = false
    )
    {
        return ReadyGovernor(
            new MutableIntensityProvider(),
            owner,
            includeTerrorbeak
        );
    }

    private static SanityShadowBudgetGovernor ReadyGovernor(
        MutableIntensityProvider provider,
        string owner,
        bool includeTerrorbeak = false
    )
    {
        var governor = EnabledGovernor(provider);
        Enter(governor, owner, SanityTierIds.ShadowCreatures);
        if (includeTerrorbeak)
        {
            Enter(governor, owner, SanityTierIds.Danger);
            Enter(governor, owner, SanityTierIds.Terrorbeak);
        }
        return governor;
    }

    private static SanityShadowBudgetGovernor EnabledGovernor(
        MutableIntensityProvider provider
    )
    {
        var governor = new SanityShadowBudgetGovernor(provider);
        Apply(
            governor,
            SanityStateEventKind.SystemEnabled,
            SanityStateEventIds.SystemEnabled,
            string.Empty
        );
        return governor;
    }

    private static void Enter(
        SanityShadowBudgetGovernor governor,
        string owner,
        string tierId
    )
    {
        Apply(
            governor,
            SanityStateEventKind.TierEntered,
            SanityStateEventIds.TierEntered(tierId),
            owner,
            tierId
        );
    }

    private static void Exit(
        SanityShadowBudgetGovernor governor,
        string owner,
        string tierId
    )
    {
        Apply(
            governor,
            SanityStateEventKind.TierExited,
            SanityStateEventIds.TierExited(tierId),
            owner,
            tierId
        );
    }

    private static void Apply(
        SanityShadowBudgetGovernor governor,
        SanityStateEventKind kind,
        string eventId,
        string owner,
        string tierId = ""
    )
    {
        Assert.True(
            governor.ApplyStateEvent(
                new SanityStateEvent(
                    eventId,
                    kind,
                    owner,
                    tierId,
                    0,
                    null
                ),
                out var reason
            ),
            reason
        );
    }

    private sealed class MutableIntensityProvider : ISanityMonsterIntensityProvider
    {
        internal MutableIntensityProvider(
            string value = SanityMonsterIntensityIds.Default
        )
        {
            Value = value;
        }

        internal bool HasValue { get; set; } = true;

        internal string Value { get; set; }

        internal string Reason { get; set; } = "config.value-available";

        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(
                HasValue,
                HasValue ? Value : string.Empty,
                Reason
            );
        }
    }
}
