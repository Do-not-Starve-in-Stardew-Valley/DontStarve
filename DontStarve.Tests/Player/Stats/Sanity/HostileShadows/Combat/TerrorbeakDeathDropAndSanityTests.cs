using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class TerrorbeakDeathDropAndSanityTests
{
    public static IEnumerable<object[]> ValidLastHitters()
    {
        yield return new object[] { HostileAttackTestFactory.PlayerOne };
        yield return new object[] { HostileAttackTestFactory.PlayerTwo };
    }

    [Fact]
    public void Lethal_terrorbeak_hit_stages_one_hp_then_captures_true_zero_dying_profile()
    {
        var profile = RuntimeProfile();
        var damage = HostileShadowIncomingDamagePolicy.Evaluate(
            profile.MaxHealth,
            profile.MaxHealth + profile.Defense,
            profile.Defense
        );

        Assert.Equal(400, profile.MaxHealth);
        Assert.True(damage.Valid, damage.Reason);
        Assert.Equal(0, damage.LogicalHealthAfter);
        Assert.Equal(1, damage.PhysicalHealthAfter);
        Assert.True(damage.PendingDying);

        var machine = HostileAttackTestFactory.StartAttack().Machine;
        var controller = new HostileShadowHitResponseController(machine);
        var decision = controller.HandleHit(
            new HostileShadowHitResponseInput
            {
                SessionId = HostileAttackTestFactory.SessionId,
                EntityId = HostileAttackTestFactory.EntityId,
                ProposedRevision = 30,
                LocationId = HostileAttackTestFactory.LocationId,
                PositionX = 100d,
                PositionY = 200d,
                TileSizePixels = 64d,
                Health = damage.LogicalHealthAfter,
                AttackerPlayerKey = HostileAttackTestFactory.PlayerTwo,
            }
        );

        Assert.Equal(HostileShadowStateIds.Dying, decision.StateId);
        var dying = Assert.IsType<HostileShadowLifecycleReceipt>(decision.Receipt);
        Assert.Equal(HostileShadowLifecycleTransitionKind.Dying, dying.Kind);
        Assert.Equal(30, dying.DeathRevision);
        Assert.Equal(HostileAttackTestFactory.PlayerTwo, dying.AttributedPlayerKey);
        Assert.True(dying.SettlementEligible);
        Assert.True(dying.DropEligible);
        Assert.True(dying.RewardEligible);

        var store = new HostileShadowLifecycleReceiptStore();
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            store.Record(dying).Status
        );
        Assert.True(
            store.TryGetDying(
                dying.SessionId,
                dying.EntityId,
                dying.DeathRevision,
                out var stored
            )
        );

        var request = Capture(profile, stored);
        Assert.Equal(profile.DropTable.SchemaVersion, request.DropTableSchemaVersion);
        Assert.Equal(profile.DropTable.DropTableId, request.DropTableId);
        Assert.Equal(profile.DropTable.ItemSemanticId, request.ItemSemanticId);
        Assert.Equal(profile.DropTable.GuaranteedQuantity, request.GuaranteedQuantity);
        Assert.Equal(profile.DropTable.BonusQuantity, request.BonusQuantity);
        Assert.Equal(profile.DropTable.BonusChance, request.BonusChance);
        Assert.Equal(33, profile.SanityReward);
        Assert.Equal(profile.SanityReward, request.SanityReward);
        Assert.Equal(0, request.ExperienceValue);
        Assert.Equal(string.Empty, request.KillCounterId);

        var fixture = Fixture(5000);
        var result = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var drop = Assert.Single(fixture.Drop.Requests);
        Assert.Equal(profile.DropTable.ItemSemanticId, drop.ItemSemanticId);
        Assert.Equal(1, drop.Quantity);
        var sanity = Assert.Single(fixture.Sanity.Requests);
        Assert.Equal(HostileAttackTestFactory.PlayerTwo, sanity.PlayerKey);
        Assert.Equal(profile.SanityReward, sanity.Delta);
        Assert.Equal(SanityChangeSource.HostileShadowKill, sanity.Source);
        Assert.Equal(13, (int)sanity.Source);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4999, 2)]
    [InlineData(5000, 1)]
    [InlineData(9999, 1)]
    public void Shipped_terrorbeak_drop_uses_strict_half_boundary_once(
        int roll,
        int expectedQuantity
    )
    {
        var profile = RuntimeProfile();
        var fixture = Fixture(roll);

        var result = fixture.Service.Resolve(Request(profile));

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(expectedQuantity, result.Receipt!.PlannedDropQuantity);
        var drop = Assert.Single(fixture.Drop.Requests);
        Assert.Equal(profile.DropTable.DropTableId, drop.DropTableId);
        Assert.Equal(profile.DropTable.ItemSemanticId, drop.ItemSemanticId);
        Assert.Equal(expectedQuantity, drop.Quantity);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Theory]
    [MemberData(nameof(ValidLastHitters))]
    public void Owner_or_non_owner_last_hitter_receives_the_same_profile_reward(
        string playerKey
    )
    {
        var profile = RuntimeProfile();
        var fixture = Fixture(5000);

        var result = fixture.Service.Resolve(Request(profile, hitter: playerKey));

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var sanity = Assert.Single(fixture.Sanity.Requests);
        Assert.Equal(playerKey, sanity.PlayerKey);
        Assert.Equal(profile.SanityReward, sanity.Delta);
        Assert.Equal(SanityChangeSource.HostileShadowKill, sanity.Source);
    }

    [Theory]
    [InlineData("hostile-shadow.settlement-last-hitter-offline")]
    [InlineData("hostile-shadow.settlement-last-hitter-location-mismatch")]
    [InlineData("hostile-shadow.settlement-sanity-session-mismatch")]
    public void Disconnected_warped_or_stale_hitter_keeps_drop_but_gets_no_reward(
        string reason
    )
    {
        var fixture = Fixture(5000);
        fixture.Hitter.Handler = _ => HostileShadowLastHitterReceipt.Invalid(reason);

        var result = fixture.Service.Resolve(Request(RuntimeProfile()));

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
        Assert.Equal(reason, result.Receipt!.LastHitter.Reason);
    }

    [Fact]
    public void Last_hitter_authority_cannot_redirect_non_owner_kill_to_owner()
    {
        var fixture = Fixture(5000);
        fixture.Hitter.Handler = _ => HostileShadowLastHitterReceipt.Valid(
            HostileAttackTestFactory.PlayerOne,
            "test-owner-redirect"
        );

        var result = fixture.Service.Resolve(
            Request(RuntimeProfile(), hitter: HostileAttackTestFactory.PlayerTwo)
        );

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
        Assert.Equal(
            HostileShadowLastHitterStatus.Invalid,
            result.Receipt!.LastHitter.Status
        );
    }

    [Fact]
    public void Reward_clamp_no_change_is_terminal_and_replay_safe()
    {
        var fixture = Fixture(5000);
        fixture.Sanity.Handler = request => Reward(
            request,
            HostileShadowSanityRewardStatus.NoChange,
            beforeRevision: 8,
            afterRevision: 8,
            before: 200d,
            after: 200d,
            "sanity-value-is-unchanged"
        );
        var request = Request(RuntimeProfile());

        var first = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Equal(
            HostileShadowSanityRewardStatus.NoChange,
            first.Receipt!.SanityReward.Status
        );
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drop_failure_is_terminal_without_reroll_or_reward(bool throws)
    {
        var fixture = Fixture(4999);
        fixture.Drop.Handler = _ => throws
            ? throw new InvalidOperationException("test-drop-exception")
            : HostileShadowDropSpawnReceipt.Rejected("test-drop-rejected");
        var request = Request(RuntimeProfile());

        var failed = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Rejected, failed.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Equal(
            HostileShadowSettlementRetryDisposition.TerminalDoNotRetrySameDeath,
            failed.Receipt!.RetryDisposition
        );
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(0, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void Sanity_failure_after_drop_is_terminal_without_duplicate_drop()
    {
        var fixture = Fixture(5000);
        fixture.Sanity.Handler = request => Reward(
            request,
            HostileShadowSanityRewardStatus.Rejected,
            beforeRevision: 3,
            afterRevision: 3,
            before: 70d,
            after: 70d,
            "test-sanity-rejected"
        );
        var request = Request(RuntimeProfile());

        var failed = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Rejected, failed.Status);
        Assert.Equal(HostileShadowSettlementReceiptStatus.PartialFailure, failed.Receipt!.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void Duplicate_dying_delta_and_request_have_no_nonce_or_second_settlement()
    {
        var profile = RuntimeProfile();
        var dying = DyingReceipt();
        var store = new HostileShadowLifecycleReceiptStore();

        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Recorded,
            store.Record(dying).Status
        );
        Assert.Equal(
            HostileShadowLifecycleReceiptRecordStatus.Duplicate,
            store.Record(dying).Status
        );
        Assert.True(
            store.TryGetDying(
                dying.SessionId,
                dying.EntityId,
                dying.DeathRevision,
                out var stored
            )
        );

        var fixture = Fixture(4999);
        var request = Capture(profile, stored);
        var first = fixture.Service.Resolve(request);
        var firstReplay = fixture.Service.Resolve(request);
        var secondReplay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, firstReplay.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, secondReplay.Status);
        Assert.Same(first.Receipt, firstReplay.Receipt);
        Assert.Same(first.Receipt, secondReplay.Receipt);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
        Assert.DoesNotContain(
            typeof(HostileShadowSettlementRequest).GetProperties(),
            property => string.Equals(property.Name, "Nonce", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            typeof(HostileShadowSettlementRequest).GetProperties(),
            property => property.Name.Contains("Owner", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Every_non_true_death_reason_and_hit_teleport_have_no_effects()
    {
        var profile = RuntimeProfile();
        var fixture = Fixture();
        var revision = 40L;

        foreach (var reason in NonTrueDeathReasons)
        {
            var receipt = LifecycleReceipt(
                revision++,
                HostileShadowLifecycleTransitionKind.Despawn,
                reason,
                string.Empty
            );
            var result = fixture.Service.Resolve(
                Capture(profile, receipt, HostileShadowStateIds.Despawn, health: 1)
            );
            Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, result.Status);
        }

        var hitTeleport = LifecycleReceipt(
            revision,
            HostileShadowLifecycleTransitionKind.HitTeleport,
            "hostile-shadow.hit-teleport-no-legal-point",
            HostileAttackTestFactory.PlayerOne,
            teleportRandomSeed: 123
        );
        var teleportResult = fixture.Service.Resolve(
            Capture(
                profile,
                hitTeleport,
                HostileShadowStateIds.HitTeleport,
                health: 1
            )
        );

        Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, teleportResult.Status);
        AssertNoEffects(fixture);
    }

    [Fact]
    public void Client_old_session_old_revision_and_changed_correlation_fail_closed()
    {
        var profile = RuntimeProfile();

        var clientFixture = Fixture();
        var client = clientFixture.Service.Resolve(
            Request(profile, authority: SanityAuthorityRole.Client)
        );
        Assert.Equal(HostileShadowSettlementStatus.RequiresHostAuthority, client.Status);
        AssertNoEffects(clientFixture);

        var staleSessionFixture = Fixture();
        staleSessionFixture.Service.ClearSession();
        Assert.True(
            staleSessionFixture.Service.BeginSession(OtherSession, out var beginReason),
            beginReason
        );
        var staleSession = staleSessionFixture.Service.Resolve(Request(profile));
        Assert.Equal(HostileShadowSettlementStatus.SessionMismatch, staleSession.Status);
        AssertNoEffects(staleSessionFixture);

        var staleRevisionFixture = Fixture();
        var request = Request(profile);
        var staleRevision = request with
        {
            LifecycleReceipt = request.LifecycleReceipt with
            {
                Revision = request.LifecycleReceipt.Revision - 1,
            },
        };
        var invalidRevision = staleRevisionFixture.Service.Resolve(staleRevision);
        Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, invalidRevision.Status);
        AssertNoEffects(staleRevisionFixture);

        var conflictFixture = Fixture(5000);
        var first = conflictFixture.Service.Resolve(request);
        var conflict = conflictFixture.Service.Resolve(request with { PositionX = 101d });
        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.CorrelationConflict, conflict.Status);
        Assert.Same(first.Receipt, conflict.Receipt);
        Assert.Equal(1, conflictFixture.Random.Calls);
        Assert.Equal(1, conflictFixture.Drop.Calls);
        Assert.Equal(1, conflictFixture.Sanity.Calls);
    }

    [Fact]
    public void Terrorbeak_terminal_window_keeps_all_256_receipts_without_eviction()
    {
        var profile = RuntimeProfile();
        var fixture = Fixture();

        for (var index = 0; index < HostileShadowSettlementService.MaximumReceipts; index++)
        {
            var result = fixture.Service.Resolve(
                Request(profile, entityId: index + 1, revision: index + 1)
            );
            Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        }

        var overflow = fixture.Service.Resolve(
            Request(profile, entityId: 999, revision: 999)
        );
        var oldestReplay = fixture.Service.Resolve(
            Request(profile, entityId: 1, revision: 1)
        );

        Assert.Equal(HostileShadowSettlementStatus.CapacityExceeded, overflow.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, oldestReplay.Status);
        Assert.Equal(
            HostileShadowSettlementService.MaximumReceipts,
            fixture.Service.ReceiptCount
        );
        Assert.Equal(HostileShadowSettlementService.MaximumReceipts, fixture.Random.Calls);
        Assert.Equal(HostileShadowSettlementService.MaximumReceipts, fixture.Drop.Calls);
        Assert.Equal(HostileShadowSettlementService.MaximumReceipts, fixture.Sanity.Calls);
    }

    private static readonly string[] NonTrueDeathReasons =
    {
        HostileShadowCleanupReasonIds.DangerExited,
        HostileShadowCleanupReasonIds.EventOverride,
        HostileShadowCleanupReasonIds.OwnerDisconnected,
        HostileShadowCleanupReasonIds.DayEnding,
        HostileShadowCleanupReasonIds.ReturnedToTitle,
        HostileShadowCleanupReasonIds.SystemDisabled,
        HostileShadowCleanupReasonIds.WorldCleanup,
        HostileShadowCleanupReasonIds.Disposed,
        HostileShadowCleanupReasonIds.Natural,
        HostileShadowCleanupReasonIds.PhysicalMaterializationFailed,
        HostileShadowCleanupReasonIds.PhysicalEntityMissing,
        HostileShadowCleanupReasonIds.IncompatiblePeer,
        HostileShadowCleanupReasonIds.ResourceInvalidated,
        HostileShadowCleanupReasonIds.PeerCapabilityChanged,
        HostileShadowCleanupReasonIds.HitTeleportLocationInvalid,
        HostileShadowCleanupReasonIds.DyingCompleted,
        HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed,
    };

    private const string OtherSession = "22222222222222222222222222222222";

    private static ShadowMonsterRuntimeProfile RuntimeProfile()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(
            catalog.TryGetProfile(
                ShadowMonsterDifficultyProfileIds.Compatible,
                out var difficulty
            )
        );
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
        var adapted = capability.Adapter!.Adapt(
            difficulty!,
            ShadowMonsterAssetBindingIds.Terrorbeak,
            64
        );
        Assert.True(adapted.Success, adapted.Reason);
        return Assert.IsType<ShadowMonsterRuntimeProfile>(adapted.Profile);
    }

    private static HostileShadowSettlementRequest Request(
        ShadowMonsterRuntimeProfile profile,
        string hitter = HostileAttackTestFactory.PlayerOne,
        string sessionId = HostileAttackTestFactory.SessionId,
        long entityId = HostileAttackTestFactory.EntityId,
        long revision = 30,
        SanityAuthorityRole authority = SanityAuthorityRole.Host
    )
    {
        var receipt = DyingReceipt(sessionId, entityId, revision, hitter);
        return Capture(profile, receipt, authority: authority);
    }

    private static HostileShadowLifecycleReceipt DyingReceipt(
        string sessionId = HostileAttackTestFactory.SessionId,
        long entityId = HostileAttackTestFactory.EntityId,
        long revision = 30,
        string hitter = HostileAttackTestFactory.PlayerOne
    )
    {
        return LifecycleReceipt(
            revision,
            HostileShadowLifecycleTransitionKind.Dying,
            HostileShadowSettlementReasonIds.DyingHealthZero,
            hitter,
            sessionId,
            entityId
        );
    }

    private static HostileShadowLifecycleReceipt LifecycleReceipt(
        long revision,
        HostileShadowLifecycleTransitionKind kind,
        string reason,
        string hitter,
        string sessionId = HostileAttackTestFactory.SessionId,
        long entityId = HostileAttackTestFactory.EntityId,
        int? teleportRandomSeed = null
    )
    {
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                sessionId,
                entityId,
                revision,
                kind,
                reason,
                teleportRandomSeed,
                hitter,
                out var receipt
            )
        );
        return receipt;
    }

    private static HostileShadowSettlementRequest Capture(
        ShadowMonsterRuntimeProfile profile,
        HostileShadowLifecycleReceipt receipt,
        string stateId = HostileShadowStateIds.Dying,
        int health = 0,
        SanityAuthorityRole authority = SanityAuthorityRole.Host
    )
    {
        return HostileShadowSettlementRequest.Capture(
            receipt,
            authority,
            stateId,
            health,
            HostileAttackTestFactory.LocationId,
            100d,
            200d,
            profile
        );
    }

    private static SettlementFixture Fixture(params int[] rolls)
    {
        var random = new SequenceSettlementRandom(rolls);
        var drop = new FakeDropAuthority();
        var hitter = new FakeLastHitterAuthority();
        var sanity = new FakeSanityRewardAuthority();
        var service = new HostileShadowSettlementService(random, drop, hitter, sanity);
        Assert.True(
            service.BeginSession(HostileAttackTestFactory.SessionId, out var reason),
            reason
        );
        return new SettlementFixture(service, random, drop, hitter, sanity);
    }

    private static HostileShadowSanityRewardReceipt Reward(
        HostileShadowSanityRewardRequest request,
        HostileShadowSanityRewardStatus status,
        long beforeRevision,
        long afterRevision,
        double before,
        double after,
        string reason
    )
    {
        return new HostileShadowSanityRewardReceipt(
            status,
            request.PlayerKey,
            request.Delta,
            request.Source,
            beforeRevision,
            afterRevision,
            before,
            after,
            reason
        );
    }

    private static void AssertNoEffects(SettlementFixture fixture)
    {
        Assert.Equal(0, fixture.Random.Calls);
        Assert.Equal(0, fixture.Drop.Calls);
        Assert.Equal(0, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    private sealed record SettlementFixture(
        HostileShadowSettlementService Service,
        SequenceSettlementRandom Random,
        FakeDropAuthority Drop,
        FakeLastHitterAuthority Hitter,
        FakeSanityRewardAuthority Sanity
    );

    private sealed class SequenceSettlementRandom : IHostileShadowSettlementRandom
    {
        private readonly Queue<int> rolls;

        internal SequenceSettlementRandom(IEnumerable<int> rolls)
        {
            this.rolls = new Queue<int>(rolls);
        }

        internal int Calls { get; private set; }

        public int NextBonusRoll10000(int seed)
        {
            Calls++;
            return rolls.Count > 0 ? rolls.Dequeue() : 5000;
        }
    }

    private sealed class FakeDropAuthority : IHostileShadowDropSpawnAuthority
    {
        internal Func<HostileShadowDropSpawnRequest, HostileShadowDropSpawnReceipt>? Handler
        {
            get;
            set;
        }
        internal int Calls { get; private set; }
        internal List<HostileShadowDropSpawnRequest> Requests { get; } = new();

        public HostileShadowDropSpawnReceipt Spawn(HostileShadowDropSpawnRequest request)
        {
            Calls++;
            Requests.Add(request);
            return Handler?.Invoke(request)
                ?? HostileShadowDropSpawnReceipt.Success("test-drop-spawned");
        }
    }

    private sealed class FakeLastHitterAuthority : IHostileShadowLastHitterAuthority
    {
        internal Func<HostileShadowLastHitterRequest, HostileShadowLastHitterReceipt>? Handler
        {
            get;
            set;
        }
        internal int Calls { get; private set; }

        public HostileShadowLastHitterReceipt Resolve(HostileShadowLastHitterRequest request)
        {
            Calls++;
            return Handler?.Invoke(request)
                ?? HostileShadowLastHitterReceipt.Valid(
                    request.AttributedPlayerKey,
                    "test-last-hitter-valid"
                );
        }
    }

    private sealed class FakeSanityRewardAuthority : IHostileShadowSanityRewardAuthority
    {
        internal Func<HostileShadowSanityRewardRequest, HostileShadowSanityRewardReceipt>? Handler
        {
            get;
            set;
        }
        internal int Calls { get; private set; }
        internal List<HostileShadowSanityRewardRequest> Requests { get; } = new();

        public HostileShadowSanityRewardReceipt Apply(HostileShadowSanityRewardRequest request)
        {
            Calls++;
            Requests.Add(request);
            return Handler?.Invoke(request)
                ?? Reward(
                    request,
                    HostileShadowSanityRewardStatus.Applied,
                    beforeRevision: 3,
                    afterRevision: 4,
                    before: 70d,
                    after: 70d + request.Delta,
                    "test-sanity-applied"
                );
        }
    }
}
