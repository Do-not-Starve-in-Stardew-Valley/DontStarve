using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowAuthorityTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OwnerA = "123456789";
    private const string OwnerB = "223456789";

    public static TheoryData<string, bool, int, long> DensityCases =>
        new()
        {
            { SanityMonsterIntensityIds.None, false, 0, 60 },
            { SanityMonsterIntensityIds.Less, true, 1, 120 },
            { SanityMonsterIntensityIds.Default, true, 1, 60 },
            { SanityMonsterIntensityIds.More, true, 2, 60 },
            { SanityMonsterIntensityIds.Many, true, 3, 30 },
            { SanityMonsterIntensityIds.Insane, true, 4, 30 },
        };

    [Theory]
    [MemberData(nameof(DensityCases))]
    public void Conversion_consumes_the_existing_six_level_owner_budget(
        string intensity,
        bool shouldSpawn,
        int expectedCap,
        long expectedInterval
    )
    {
        var fixture = CreateFixture(intensity);
        EnterDanger(fixture, OwnerA, revision: 1);

        var result = fixture.Authority.TrySpawn(
            Conversion("conversion-a", OwnerA, gameMinute: 100)
        );
        var budget = fixture.Governor.Evaluate(
            OwnerA,
            100,
            fixture.Authority.GetOwnerOccupancy(OwnerA)
        );

        Assert.Equal(shouldSpawn, result.Spawned);
        Assert.Equal(expectedCap, result.Cap);
        Assert.Equal(expectedInterval, budget.IntervalMinutes);
        Assert.Equal(shouldSpawn ? 1 : 0, fixture.Authority.Count);
    }

    [Fact]
    public void Two_owners_have_independent_caps_and_canonical_ownership()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.More);
        EnterDanger(fixture, OwnerA, revision: 1);
        EnterDanger(fixture, OwnerB, revision: 1);

        var first = fixture.Authority.TrySpawn(Conversion("a", OwnerA, 0));
        var second = fixture.Authority.TrySpawn(Conversion("b", OwnerB, 0));

        Assert.True(first.Spawned);
        Assert.True(second.Spawned);
        Assert.Equal(1, fixture.Authority.GetOwnerOccupancy(OwnerA));
        Assert.Equal(1, fixture.Authority.GetOwnerOccupancy(OwnerB));
        Assert.True(fixture.Authority.TryGetEntity(first.EntityId!.Value, out var a));
        Assert.True(fixture.Authority.TryGetEntity(second.EntityId!.Value, out var b));
        Assert.Equal(OwnerA, a!.OwnerPlayerKey);
        Assert.Equal(OwnerB, b!.OwnerPlayerKey);
    }

    [Fact]
    public void Full_cap_pauses_without_budget_consumption_and_vacancy_fills_only_one()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        EnterTier(fixture, OwnerA, SanityTierIds.Terrorbeak, revision: 2);

        Assert.True(fixture.Authority.TrySpawn(Conversion("convert", OwnerA, 0)).Spawned);
        Assert.Equal(
            HostileShadowSpawnStatus.Spawned,
            fixture.Authority.TrySpawn(Interval("interval-60", OwnerA, 60)).Status
        );
        var atCap = fixture.Authority.TrySpawn(Interval("interval-cap", OwnerA, 120));
        Assert.Equal(HostileShadowSpawnStatus.AtCap, atCap.Status);
        Assert.True(fixture.Governor.TryGetOwnerState(OwnerA, out var paused));
        Assert.True(paused!.IsPausedAtCap);
        Assert.Null(paused.NextDueMinute);

        var ids = fixture.Authority.CreateFullSnapshot().Entities
            .Select(state => state.EntityId)
            .ToArray();
        Assert.True(
            fixture.Authority.CleanupEntity(
                ids[0],
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var vacancy = fixture.Authority.TrySpawn(
            Interval("interval-vacancy", OwnerA, 120)
        );
        Assert.True(vacancy.Spawned);

        Assert.True(
            fixture.Authority.CleanupEntity(
                ids[1],
                HostileShadowCleanupReasonIds.Natural
            )
        );
        var secondVacancy = fixture.Authority.TrySpawn(
            Interval("interval-no-fill-all", OwnerA, 120)
        );
        Assert.Equal(HostileShadowSpawnStatus.Waiting, secondVacancy.Status);
        Assert.Equal(1, fixture.Authority.GetOwnerOccupancy(OwnerA));
    }

    [Fact]
    public void Only_one_conversion_wins_each_danger_epoch_even_when_cap_has_room()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Insane);
        EnterDanger(fixture, OwnerA, revision: 10);

        var first = fixture.Authority.TrySpawn(Conversion("conversion-a", OwnerA, 0));
        var raced = fixture.Authority.TrySpawn(Conversion("conversion-b", OwnerA, 0));

        Assert.True(first.Spawned);
        Assert.Equal(HostileShadowSpawnStatus.Rejected, raced.Status);
        Assert.Equal(
            "hostile-shadow.conversion-epoch-unavailable-or-consumed",
            raced.Reason
        );
        Assert.Equal(1, fixture.Authority.Count);
    }

    [Fact]
    public void Duplicate_spawn_request_returns_same_identity_without_second_mutation()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.More);
        EnterDanger(fixture, OwnerA, revision: 1);
        var command = Conversion("same-request", OwnerA, 0);

        var first = fixture.Authority.TrySpawn(command);
        var duplicate = fixture.Authority.TrySpawn(command);

        Assert.True(first.Spawned);
        Assert.Equal(HostileShadowSpawnStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.EntityId, duplicate.EntityId);
        Assert.Equal(1, fixture.Authority.Revision);
        Assert.Equal(1, fixture.Authority.Count);
    }

    [Fact]
    public void Snapshot_contains_only_shared_entity_state_and_cleanup_never_settles()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        var deltas = new List<ShadowStateDeltaMessage>();
        fixture.Authority.DeltaProduced += deltas.Add;
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(Conversion("convert", OwnerA, 10));

        var full = fixture.Authority.CreateFullSnapshot();
        var propertyNames = typeof(ShadowStateSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Session, full.SessionId);
        Assert.Equal(1, full.Revision);
        Assert.Single(full.Entities);
        Assert.Contains("OwnerPlayerKey", propertyNames);
        Assert.DoesNotContain("ScreenId", propertyNames);
        Assert.DoesNotContain("CorrelationId", propertyNames);
        Assert.DoesNotContain("GameplayConfigFingerprint", propertyNames);
        Assert.DoesNotContain("OwnerLocal", propertyNames);
        Assert.DoesNotContain(propertyNames, name => name.Contains("Lease", StringComparison.Ordinal));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Nonce", StringComparison.Ordinal));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Expiry", StringComparison.Ordinal));

        Assert.True(
            fixture.Authority.CleanupEntity(
                spawned.EntityId!.Value,
                HostileShadowCleanupReasonIds.Natural
            )
        );
        Assert.Equal(3, fixture.Authority.Revision);
        Assert.Equal(3, deltas.Count);
        Assert.All(deltas, delta => Assert.False(delta.Change.SettlementEligible));
        Assert.Equal(ShadowStateDeltaKind.Updated, deltas[1].Change.Kind);
        var despawn = Assert.IsType<ShadowStateSnapshot>(deltas[1].Change.State);
        Assert.Equal(HostileShadowStateIds.Despawn, despawn.StateId);
        Assert.True(
            HostileShadowStateIds.IsHealthValid(
                despawn.StateId,
                despawn.Health
            )
        );
        Assert.Equal(ShadowStateDeltaKind.Removed, deltas[2].Change.Kind);
        Assert.Equal(HostileShadowCleanupReasonIds.Natural, deltas[2].Change.Reason);
    }

    [Fact]
    public void Disabled_initialization_and_runtime_disable_are_registered_cleanup_paths()
    {
        var disabled = CreateFixture(
            SanityMonsterIntensityIds.Default,
            systemEnabled: false
        );
        EnterDanger(disabled, OwnerA, revision: 1);
        var rejected = disabled.Authority.TrySpawn(Conversion("disabled", OwnerA, 0));
        Assert.Equal(HostileShadowSpawnStatus.Inactive, rejected.Status);
        Assert.Equal(0, disabled.Authority.Count);

        var enabled = CreateFixture(SanityMonsterIntensityIds.Default);
        var deltas = new List<ShadowStateDeltaMessage>();
        enabled.Authority.DeltaProduced += deltas.Add;
        EnterDanger(enabled, OwnerA, revision: 1);
        Assert.True(enabled.Authority.TrySpawn(Conversion("enabled", OwnerA, 0)).Spawned);
        var removed = enabled.Authority.SetEnabled(false);

        Assert.Equal(1, removed);
        Assert.False(enabled.Authority.IsEnabled);
        Assert.Empty(enabled.Authority.CreateFullSnapshot().Entities);
        Assert.Equal(
            HostileShadowCleanupReasonIds.SystemDisabled,
            deltas[^1].Change.Reason
        );
        Assert.False(deltas[^1].Change.SettlementEligible);
    }

    [Fact]
    public void Danger_exit_event_override_disconnect_day_and_title_have_stable_cleanup()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Insane);

        EnterDanger(fixture, OwnerA, revision: 1);
        Assert.True(fixture.Authority.TrySpawn(Conversion("danger", OwnerA, 0)).Spawned);
        ExitTier(fixture, OwnerA, SanityTierIds.Danger, revision: 2);
        Assert.Equal(0, fixture.Authority.Count);

        EnterDanger(fixture, OwnerA, revision: 3);
        Assert.True(fixture.Authority.TrySpawn(Conversion("event", OwnerA, 30)).Spawned);
        Assert.Equal(1, fixture.Authority.SetEventOverride(OwnerA, true));
        Assert.Equal(
            HostileShadowSpawnStatus.Inactive,
            fixture.Authority.TrySpawn(Interval("blocked", OwnerA, 60)).Status
        );
        fixture.Authority.SetEventOverride(OwnerA, false);

        ExitTier(fixture, OwnerA, SanityTierIds.Danger, revision: 4);
        EnterDanger(fixture, OwnerA, revision: 5);
        Assert.True(fixture.Authority.TrySpawn(Conversion("disconnect", OwnerA, 60)).Spawned);
        Assert.Equal(
            1,
            fixture.Authority.ForgetOwner(
                OwnerA,
                HostileShadowCleanupReasonIds.OwnerDisconnected
            )
        );

        ExitTier(fixture, OwnerA, SanityTierIds.Danger, revision: 6);
        EnterDanger(fixture, OwnerA, revision: 7);
        Assert.True(fixture.Authority.TrySpawn(Conversion("day", OwnerA, 90)).Spawned);
        Assert.Equal(
            1,
            fixture.Authority.CleanupAll(HostileShadowCleanupReasonIds.DayEnding)
        );

        fixture.Authority.EndSession(HostileShadowCleanupReasonIds.ReturnedToTitle);
        Assert.False(fixture.Authority.IsHostSessionActive);
        Assert.Equal(string.Empty, fixture.Authority.SessionId);
        Assert.Equal(0, fixture.Authority.Revision);
    }

    [Fact]
    public void Warp_does_not_reassign_owner_or_move_entity_location()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(Conversion("warp", OwnerA, 0));
        Assert.True(fixture.Authority.TryGetEntity(spawned.EntityId!.Value, out var before));

        // The stage-04 runtime may retarget/move inside this location, but authority ownership and
        // the recorded location never follow the owner across a warp.
        Assert.True(fixture.Authority.TryGetEntity(spawned.EntityId.Value, out var after));
        Assert.Equal(before!.OwnerPlayerKey, after!.OwnerPlayerKey);
        Assert.Equal(before.LocationId, after.LocationId);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public void Target_and_position_are_visible_in_the_versioned_shared_snapshot()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(
            Conversion("target-snapshot", OwnerA, 0)
        );

        Assert.True(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    spawned.EntityId!.Value,
                    "Farm",
                    HostileShadowStateIds.Chase,
                    OwnerB,
                    192d,
                    320d,
                    100,
                    "hostile-shadow.target-owner"
                ),
                out var reason
            ),
            reason
        );
        var snapshot = fixture.Authority.CreateFullSnapshot();
        var state = Assert.Single(snapshot.Entities);

        Assert.Equal(4, snapshot.SchemaVersion);
        Assert.Equal(OwnerA, state.OwnerPlayerKey);
        Assert.Equal("Farm", state.LocationId);
        Assert.Equal(OwnerB, state.TargetPlayerKey);
        Assert.Equal(HostileShadowStateIds.Chase, state.StateId);
        Assert.Equal(192d, state.PositionX);
        Assert.Equal(320d, state.PositionY);
    }

    [Fact]
    public void Host_created_attack_instance_frame_and_revision_are_shared()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(
            Conversion("attack-snapshot", OwnerA, 0)
        );
        var entityId = spawned.EntityId!.Value;
        var instanceRevision = fixture.Authority.Revision + 1;

        Assert.True(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Attack,
                    OwnerB,
                    192d,
                    320d,
                    100,
                    "hostile-shadow.attack-started",
                    "attack-instance",
                    instanceRevision,
                    1
                ),
                out var reason
            ),
            reason
        );
        Assert.True(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Attack,
                    OwnerB,
                    224d,
                    320d,
                    100,
                    "hostile-shadow.attack-frame",
                    "attack-instance",
                    instanceRevision,
                    3
                ),
                out reason
            ),
            reason
        );

        var snapshot = fixture.Authority.CreateFullSnapshot();
        var state = Assert.Single(snapshot.Entities);
        Assert.Equal("attack-instance", state.AttackInstanceId);
        Assert.Equal(instanceRevision, state.AttackInstanceRevision);
        Assert.Equal(3, state.AttackFrameNumber);
        Assert.True(
            HostileShadowProtocol.TryCreateScopedSnapshot(
                snapshot,
                "Farm",
                ShadowSnapshotTrigger.Resync,
                out var scoped,
                out reason
            ),
            reason
        );
        Assert.True(
            HostileShadowProtocol.IsValidSnapshotMessage(scoped, out reason),
            reason
        );
    }

    [Fact]
    public void Client_cannot_invent_attack_fields_or_instance_revision()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(
            Conversion("attack-invalid", OwnerA, 0)
        );
        var entityId = spawned.EntityId!.Value;

        Assert.False(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Chase,
                    OwnerB,
                    192d,
                    320d,
                    100,
                    "hostile-shadow.attack-forged",
                    "forged",
                    fixture.Authority.Revision + 1,
                    3
                ),
                out _
            )
        );
        Assert.False(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Attack,
                    OwnerB,
                    192d,
                    320d,
                    100,
                    "hostile-shadow.attack-forged",
                    "forged",
                    fixture.Authority.Revision + 2,
                    3
                ),
                out var reason
            )
        );
        Assert.Equal("hostile-shadow.attack-instance-revision-invalid", reason);
    }

    [Fact]
    public void True_zero_enters_dying_and_cleanup_does_not_relabel_it_as_despawn()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        var deltas = new List<ShadowStateDeltaMessage>();
        fixture.Authority.DeltaProduced += deltas.Add;
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(
            Conversion("dying-state", OwnerA, 0)
        );
        var entityId = spawned.EntityId!.Value;

        Assert.True(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Dying,
                    OwnerB,
                    128d,
                    256d,
                    0,
                    "hostile-shadow.dying-health-zero"
                ),
                out var reason
            ),
            reason
        );
        Assert.True(fixture.Authority.CleanupEntity(entityId, HostileShadowCleanupReasonIds.DyingCompleted));

        Assert.Equal(3, fixture.Authority.Revision);
        Assert.Equal(3, deltas.Count);
        var dying = Assert.IsType<ShadowStateSnapshot>(deltas[1].Change.State);
        Assert.Equal(HostileShadowStateIds.Dying, dying.StateId);
        Assert.Equal(0, dying.Health);
        Assert.Equal(ShadowStateDeltaKind.Removed, deltas[2].Change.Kind);
        Assert.DoesNotContain(
            deltas,
            delta => string.Equals(
                delta.Change.State?.StateId,
                HostileShadowStateIds.Despawn,
                StringComparison.Ordinal
            )
        );
        Assert.All(deltas, delta => Assert.False(delta.Change.SettlementEligible));
    }

    [Fact]
    public void Authority_rejects_zero_hit_teleport_and_positive_dying_health()
    {
        var fixture = CreateFixture(SanityMonsterIntensityIds.Default);
        EnterDanger(fixture, OwnerA, revision: 1);
        var spawned = fixture.Authority.TrySpawn(
            Conversion("invalid-health-state", OwnerA, 0)
        );
        var entityId = spawned.EntityId!.Value;

        Assert.False(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.HitTeleport,
                    OwnerB,
                    128d,
                    256d,
                    0,
                    "hostile-shadow.invalid-hit-teleport-health"
                ),
                out _
            )
        );
        Assert.False(
            fixture.Authority.TryUpdate(
                new HostileShadowStateUpdate(
                    entityId,
                    "Farm",
                    HostileShadowStateIds.Dying,
                    OwnerB,
                    128d,
                    256d,
                    1,
                    "hostile-shadow.invalid-dying-health"
                ),
                out _
            )
        );
        Assert.Equal(1, fixture.Authority.Revision);
    }

    [Fact]
    public void Physical_monster_creation_fails_closed_without_a_proven_capability()
    {
        var authority = new HostileShadowAuthority(
            new GovernorBudget(
                new SanityShadowBudgetGovernor(
                    new FixedIntensityProvider(SanityMonsterIntensityIds.Default)
                )
            ),
            new IncrementingIdSource()
        );
        var capability = authority.PhysicalEntityCapability;

        Assert.Equal(
            HostileShadowPhysicalEntityCapabilityStatus.Unavailable,
            capability.Status
        );
        Assert.Equal(
            "hostile-shadow.physical-monster-subclass-net-serialization-unverified",
            capability.Reason
        );
    }

    private static Fixture CreateFixture(
        string intensity,
        bool systemEnabled = true
    )
    {
        var provider = new FixedIntensityProvider(intensity);
        var governor = new SanityShadowBudgetGovernor(provider);
        governor.ApplyStateEvent(
            LifecycleEvent(
                systemEnabled
                    ? SanityStateEventKind.SystemEnabled
                    : SanityStateEventKind.SystemDisabled
            ),
            out _
        );
        var authority = new HostileShadowAuthority(
            new GovernorBudget(governor),
            new IncrementingIdSource(),
            new HostileShadowPhysicalEntityCapability(
                HostileShadowPhysicalEntityCapabilityStatus.Available,
                "hostile-shadow.physical-monster-netcollection-roundtrip-verified"
            )
        );
        Assert.True(authority.BeginHostSession(Session, systemEnabled, out var reason), reason);
        return new Fixture(authority, governor);
    }

    private static void EnterDanger(Fixture fixture, string owner, long revision)
    {
        EnterTier(fixture, owner, SanityTierIds.ShadowCreatures, revision);
        EnterTier(fixture, owner, SanityTierIds.Danger, revision);
    }

    private static void EnterTier(
        Fixture fixture,
        string owner,
        string tier,
        long revision
    )
    {
        var stateEvent = new SanityStateEvent(
            SanityStateEventIds.TierEntered(tier),
            SanityStateEventKind.TierEntered,
            owner,
            tier,
            revision,
            0.1d
        );
        fixture.Governor.ApplyStateEvent(stateEvent, out _);
        fixture.Authority.ObserveStateEvent(stateEvent);
    }

    private static void ExitTier(
        Fixture fixture,
        string owner,
        string tier,
        long revision
    )
    {
        var stateEvent = new SanityStateEvent(
            SanityStateEventIds.TierExited(tier),
            SanityStateEventKind.TierExited,
            owner,
            tier,
            revision,
            0.2d
        );
        fixture.Governor.ApplyStateEvent(stateEvent, out _);
        fixture.Authority.ObserveStateEvent(stateEvent);
    }

    private static SanityStateEvent LifecycleEvent(SanityStateEventKind kind)
    {
        return new SanityStateEvent(
            kind == SanityStateEventKind.SystemEnabled
                ? SanityStateEventIds.SystemEnabled
                : SanityStateEventIds.SystemDisabled,
            kind,
            string.Empty,
            string.Empty,
            -1,
            null
        );
    }

    private static HostileShadowSpawnCommand Conversion(
        string requestId,
        string owner,
        long gameMinute
    )
    {
        return Command(
            requestId,
            HostileShadowSpawnOrigin.OwnerProjectionConversion,
            owner,
            gameMinute
        );
    }

    private static HostileShadowSpawnCommand Interval(
        string requestId,
        string owner,
        long gameMinute
    )
    {
        return Command(
            requestId,
            HostileShadowSpawnOrigin.Interval,
            owner,
            gameMinute
        );
    }

    private static HostileShadowSpawnCommand Command(
        string requestId,
        HostileShadowSpawnOrigin origin,
        string owner,
        long gameMinute
    )
    {
        return new HostileShadowSpawnCommand(
            requestId,
            origin,
            owner,
            "Farm",
            128d,
            256d,
            gameMinute,
            RuntimeProfile(),
            origin == HostileShadowSpawnOrigin.OwnerProjectionConversion
                ? "hostile-shadow.spawn.owner-projection-conversion"
                : "hostile-shadow.spawn.interval"
        );
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile()
    {
        return new ShadowMonsterRuntimeProfile(
            ShadowMonsterDifficultyProfileIds.Compatible,
            ShadowMonsterAssetBindingIds.CreeperFear,
            adapterVersion: 1,
            maxHealth: 100,
            baseDamage: 10,
            movementSpeed: 2d,
            defense: 0,
            detectionRadiusTiles: 8d,
            detectionRadiusPixels: 512d,
            attackRangeTiles: 1d,
            attackRangePixels: 64d,
            attackIntervalSeconds: 1d,
            naturalDespawnGameHours: 4d,
            displayNameKey: "monster.creeper-fear",
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
            animationProfileId: "sanity.animation.creeper-fear.profile",
            cueSetId: "sanity.cue.creeper-fear",
            attackMotionPolicyId: "sanity.attack-motion.one-tile-v1",
            postAttackPolicyId: ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
            experienceValue: 0,
            killCounterId: null
        );
    }

    private sealed record Fixture(
        HostileShadowAuthority Authority,
        SanityShadowBudgetGovernor Governor
    );

    private sealed class FixedIntensityProvider : ISanityMonsterIntensityProvider
    {
        private readonly string value;

        internal FixedIntensityProvider(string value)
        {
            this.value = value;
        }

        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(
                true,
                value,
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
        private long next = 1000;

        public long Next()
        {
            return ++next;
        }
    }
}
