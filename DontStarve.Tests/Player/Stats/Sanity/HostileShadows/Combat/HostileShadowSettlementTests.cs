using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileShadowSettlementTests
{
    public static IEnumerable<object[]> DespawnReasons()
    {
        yield return new object[] { HostileShadowCleanupReasonIds.DangerExited };
        yield return new object[] { HostileShadowCleanupReasonIds.EventOverride };
        yield return new object[] { HostileShadowCleanupReasonIds.OwnerDisconnected };
        yield return new object[] { HostileShadowCleanupReasonIds.DayEnding };
        yield return new object[] { HostileShadowCleanupReasonIds.ReturnedToTitle };
        yield return new object[] { HostileShadowCleanupReasonIds.SystemDisabled };
        yield return new object[] { HostileShadowCleanupReasonIds.WorldCleanup };
        yield return new object[] { HostileShadowCleanupReasonIds.Disposed };
        yield return new object[] { HostileShadowCleanupReasonIds.Natural };
        yield return new object[] { HostileShadowCleanupReasonIds.PhysicalMaterializationFailed };
        yield return new object[] { HostileShadowCleanupReasonIds.PhysicalEntityMissing };
        yield return new object[] { HostileShadowCleanupReasonIds.IncompatiblePeer };
        yield return new object[] { HostileShadowCleanupReasonIds.ResourceInvalidated };
        yield return new object[] { HostileShadowCleanupReasonIds.PeerCapabilityChanged };
        yield return new object[] { HostileShadowCleanupReasonIds.HitTeleportLocationInvalid };
        yield return new object[] { HostileShadowCleanupReasonIds.DyingCompleted };
        yield return new object[] { HostileShadowCleanupReasonIds.HitResponseSynchronizationFailed };
    }

    [Theory]
    [InlineData(5000, 1)]
    [InlineData(9999, 1)]
    [InlineData(4999, 2)]
    [InlineData(0, 2)]
    public void Profile_roll_guarantees_one_and_bonus_is_strictly_below_half(
        int roll,
        int expectedQuantity
    )
    {
        var fixture = Fixture(roll);

        var result = fixture.Service.Resolve(Request());

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(expectedQuantity, result.Receipt!.PlannedDropQuantity);
        Assert.Equal(expectedQuantity, fixture.Drop.Requests.Single().Quantity);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Hitter.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(33)]
    public void Valid_last_hitter_receives_exact_profile_reward(int reward)
    {
        var fixture = Fixture(5000);

        var result = fixture.Service.Resolve(Request(reward: reward));

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var sanity = Assert.Single(fixture.Sanity.Requests);
        Assert.Equal(HostileAttackTestFactory.PlayerOne, sanity.PlayerKey);
        Assert.Equal(reward, sanity.Delta);
        Assert.Equal(SanityChangeSource.HostileShadowKill, sanity.Source);
        Assert.Equal(
            HostileShadowSanityRewardStatus.Applied,
            result.Receipt!.SanityReward.Status
        );
    }

    [Fact]
    public void Sanity_clamp_no_change_is_a_successful_single_settlement()
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

        var first = fixture.Service.Resolve(Request(reward: 33));
        var replay = fixture.Service.Resolve(Request(reward: 33));

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Equal(HostileShadowSanityRewardStatus.NoChange, first.Receipt!.SanityReward.Status);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void Empty_attribution_drops_but_never_resolves_or_falls_back_reward()
    {
        var fixture = Fixture(5000);

        var result = fixture.Service.Resolve(Request(hitter: string.Empty));

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(0, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
        Assert.Equal(
            HostileShadowLastHitterStatus.NotEvaluated,
            result.Receipt!.LastHitter.Status
        );
    }

    [Theory]
    [InlineData("hostile-shadow.settlement-last-hitter-offline")]
    [InlineData("hostile-shadow.settlement-last-hitter-location-mismatch")]
    [InlineData("hostile-shadow.settlement-sanity-session-mismatch")]
    public void Invalid_offline_cross_location_or_session_hitter_gets_no_reward(
        string reason
    )
    {
        var fixture = Fixture(5000);
        fixture.Hitter.Handler = _ => HostileShadowLastHitterReceipt.Invalid(reason);

        var result = fixture.Service.Resolve(Request());

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
        Assert.Equal(reason, result.Receipt!.LastHitter.Reason);
    }

    [Fact]
    public void Hitter_authority_cannot_redirect_reward_to_another_player()
    {
        var fixture = Fixture(5000);
        fixture.Hitter.Handler = _ => HostileShadowLastHitterReceipt.Valid(
            HostileAttackTestFactory.PlayerTwo,
            "test-wrong-player"
        );

        var result = fixture.Service.Resolve(Request());

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(0, fixture.Sanity.Calls);
        Assert.Equal(HostileShadowLastHitterStatus.Invalid, result.Receipt!.LastHitter.Status);
        Assert.Equal(
            "hostile-shadow.settlement-last-hitter-player-mismatch",
            result.Receipt.LastHitter.Reason
        );
        Assert.Equal(
            "hostile-shadow.settlement-drop-only-last-hitter-invalid",
            result.Reason
        );
    }

    [Fact]
    public void Exact_replay_and_interleaved_out_of_order_replay_settle_each_death_once()
    {
        var fixture = Fixture(5000, 4999);
        var firstRequest = Request(entityId: 77, revision: 30);
        var secondRequest = Request(entityId: 88, revision: 40);

        var first = fixture.Service.Resolve(firstRequest);
        var second = fixture.Service.Resolve(secondRequest);
        var replayFirst = fixture.Service.Resolve(firstRequest);
        var replaySecond = fixture.Service.Resolve(secondRequest);

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.Settled, second.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replayFirst.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replaySecond.Status);
        Assert.Same(first.Receipt, replayFirst.Receipt);
        Assert.Same(second.Receipt, replaySecond.Receipt);
        Assert.Equal(2, fixture.Random.Calls);
        Assert.Equal(2, fixture.Drop.Calls);
        Assert.Equal(2, fixture.Sanity.Calls);
    }

    [Fact]
    public void Same_death_key_with_changed_content_is_a_terminal_conflict()
    {
        var fixture = Fixture(5000);
        var request = Request();

        var first = fixture.Service.Resolve(request);
        var conflict = fixture.Service.Resolve(request with { SanityReward = 33 });

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.CorrelationConflict, conflict.Status);
        Assert.Same(first.Receipt, conflict.Receipt);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drop_failure_or_exception_is_terminal_without_reroll_or_sanity(
        bool throws
    )
    {
        var fixture = Fixture(4999);
        fixture.Drop.Handler = _ => throws
            ? throw new InvalidOperationException("test")
            : HostileShadowDropSpawnReceipt.Rejected("test-drop-failed");
        var request = Request();

        var failed = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Rejected, failed.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Same(failed.Receipt, replay.Receipt);
        Assert.Equal(
            HostileShadowSettlementRetryDisposition.TerminalDoNotRetrySameDeath,
            failed.Receipt!.RetryDisposition
        );
        Assert.Equal(2, failed.Receipt.PlannedDropQuantity);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(0, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void Default_drop_receipt_fails_closed_instead_of_reporting_success()
    {
        var fixture = Fixture(5000);
        fixture.Drop.Handler = _ => default;

        var result = fixture.Service.Resolve(Request());

        Assert.Equal(HostileShadowSettlementStatus.Rejected, result.Status);
        Assert.Equal(HostileShadowDropSpawnStatus.Rejected, result.Receipt!.Drop.Status);
        Assert.Equal(0, fixture.Hitter.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void Sanity_failure_after_drop_is_terminal_and_never_duplicates_drop()
    {
        var fixture = Fixture(5000);
        fixture.Sanity.Handler = request => Reward(
            request,
            HostileShadowSanityRewardStatus.Rejected,
            3,
            3,
            70d,
            70d,
            "test-sanity-failed"
        );
        var request = Request();

        var failed = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.Rejected, failed.Status);
        Assert.Equal(HostileShadowSettlementReceiptStatus.PartialFailure, failed.Receipt!.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void Receipt_key_and_settlement_id_align_with_dying_correlation()
    {
        var fixture = Fixture(5000);
        var request = Request(entityId: 91, revision: 44);

        var result = fixture.Service.Resolve(request);

        var receipt = Assert.IsType<HostileShadowSettlementReceipt>(result.Receipt);
        Assert.Equal(request.LifecycleReceipt.CorrelationId, receipt.LifecycleCorrelationId);
        Assert.Equal(request.Key, receipt.Key);
        Assert.Equal(string.Concat(request.LifecycleReceipt.CorrelationId, ":settlement"), receipt.SettlementId);
        Assert.Equal(44, receipt.Key.DeathRevision);
        Assert.Equal(HostileShadowSettlementSeed.Create(request.Key), receipt.RandomSeed);
    }

    [Fact]
    public void Stable_key_seed_is_deterministic_across_services_and_rng_is_injectable()
    {
        var first = Fixture(1234);
        var second = Fixture(9876);
        var request = Request();

        var firstResult = first.Service.Resolve(request);
        var secondResult = second.Service.Resolve(request);

        Assert.Equal(firstResult.Receipt!.RandomSeed, secondResult.Receipt!.RandomSeed);
        Assert.Equal(1234, firstResult.Receipt.BonusRoll10000);
        Assert.Equal(9876, secondResult.Receipt.BonusRoll10000);
        Assert.Equal(HostileShadowSettlementSeed.Create(request.Key), first.Random.Seeds.Single());
        Assert.Equal(first.Random.Seeds.Single(), second.Random.Seeds.Single());
    }

    [Fact]
    public void Default_rng_is_stable_and_bounded_for_the_same_death_key()
    {
        var key = Request().Key;
        var seed = HostileShadowSettlementSeed.Create(key);
        var first = new StableHostileShadowSettlementRandom().NextBonusRoll10000(seed);
        var second = new StableHostileShadowSettlementRandom().NextBonusRoll10000(seed);

        Assert.Equal(first, second);
        Assert.InRange(first, 0, HostileShadowSettlementService.BonusRollScale - 1);
    }

    [Theory]
    [InlineData(1, HostileShadowStateIds.Dying)]
    [InlineData(0, HostileShadowStateIds.Idle)]
    public void Dying_receipt_still_requires_zero_health_dying_state(
        int health,
        string stateId
    )
    {
        var fixture = Fixture(0);

        var result = fixture.Service.Resolve(
            Request() with { Health = health, StateId = stateId }
        );

        Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, result.Status);
        AssertNoEffects(fixture);
    }

    [Fact]
    public void Hit_teleport_and_no_legal_point_never_enter_settlement()
    {
        var fixture = Fixture(0);
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                HostileShadowLifecycleTransitionKind.HitTeleport,
                "hostile-shadow.hit-teleport-no-legal-point",
                123,
                HostileAttackTestFactory.PlayerOne,
                out var receipt
            )
        );

        var result = fixture.Service.Resolve(
            RequestFromReceipt(receipt, HostileShadowStateIds.HitTeleport, 80)
        );

        Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, result.Status);
        AssertNoEffects(fixture);
    }

    [Theory]
    [MemberData(nameof(DespawnReasons))]
    public void Every_cleanup_reason_is_non_settleable(string cleanupReason)
    {
        var fixture = Fixture(0);
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                HostileAttackTestFactory.SessionId,
                HostileAttackTestFactory.EntityId,
                30,
                HostileShadowLifecycleTransitionKind.Despawn,
                cleanupReason,
                null,
                string.Empty,
                out var receipt
            )
        );

        var result = fixture.Service.Resolve(
            RequestFromReceipt(receipt, HostileShadowStateIds.Despawn, 80)
        );

        Assert.Equal(HostileShadowSettlementStatus.InvalidIntent, result.Status);
        AssertNoEffects(fixture);
    }

    [Fact]
    public void Client_request_has_no_rng_drop_hitter_or_sanity_side_effect()
    {
        var fixture = Fixture(0);

        var request = Request() with { Authority = SanityAuthorityRole.Client };
        var result = fixture.Service.Resolve(request);
        var replay = fixture.Service.Resolve(request);

        Assert.Equal(HostileShadowSettlementStatus.RequiresHostAuthority, result.Status);
        Assert.Equal(HostileShadowSettlementStatus.RequiresHostAuthority, replay.Status);
        AssertNoEffects(fixture);
    }

    [Fact]
    public void Session_clear_reconnect_and_new_session_reject_stale_death()
    {
        var fixture = Fixture(5000, 5000);
        var old = Request();
        Assert.Equal(HostileShadowSettlementStatus.Settled, fixture.Service.Resolve(old).Status);

        fixture.Service.ClearSession();
        Assert.Equal(HostileShadowSettlementStatus.SessionMismatch, fixture.Service.Resolve(old).Status);
        Assert.True(fixture.Service.BeginSession(OtherSession, out var reason), reason);
        Assert.Equal(HostileShadowSettlementStatus.SessionMismatch, fixture.Service.Resolve(old).Status);

        var current = Request(sessionId: OtherSession, revision: 1);
        Assert.Equal(HostileShadowSettlementStatus.Settled, fixture.Service.Resolve(current).Status);
        Assert.Equal(2, fixture.Drop.Calls);
        Assert.Equal(2, fixture.Sanity.Calls);
    }

    [Fact]
    public void Receipt_window_fails_closed_without_evicting_old_deaths()
    {
        var fixture = Fixture(Enumerable.Repeat(5000, 257).ToArray());
        for (var index = 0; index < HostileShadowSettlementService.MaximumReceipts; index++)
        {
            var result = fixture.Service.Resolve(
                Request(entityId: index + 1, revision: index + 1)
            );
            Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        }

        var overflow = fixture.Service.Resolve(Request(entityId: 999, revision: 999));
        var oldestReplay = fixture.Service.Resolve(Request(entityId: 1, revision: 1));

        Assert.Equal(HostileShadowSettlementStatus.CapacityExceeded, overflow.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, oldestReplay.Status);
        Assert.Equal(HostileShadowSettlementService.MaximumReceipts, fixture.Service.ReceiptCount);
        Assert.Equal(HostileShadowSettlementService.MaximumReceipts, fixture.Random.Calls);
    }

    private const string OtherSession = "22222222222222222222222222222222";

    private static HostileShadowSettlementRequest Request(
        string sessionId = HostileAttackTestFactory.SessionId,
        long entityId = HostileAttackTestFactory.EntityId,
        long revision = 30,
        string hitter = HostileAttackTestFactory.PlayerOne,
        int reward = 15
    )
    {
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                sessionId,
                entityId,
                revision,
                HostileShadowLifecycleTransitionKind.Dying,
                HostileShadowSettlementReasonIds.DyingHealthZero,
                null,
                hitter,
                out var receipt
            )
        );
        return RequestFromReceipt(receipt, HostileShadowStateIds.Dying, 0, reward);
    }

    private static HostileShadowSettlementRequest RequestFromReceipt(
        HostileShadowLifecycleReceipt receipt,
        string stateId,
        int health,
        int reward = 15
    )
    {
        return new HostileShadowSettlementRequest(
            receipt,
            SanityAuthorityRole.Host,
            stateId,
            health,
            HostileAttackTestFactory.LocationId,
            100d,
            200d,
            1,
            "sanity.drop.void-essence-v1",
            "stardew.item.void-essence",
            1,
            1,
            0.5d,
            reward,
            0,
            string.Empty
        );
    }

    private static SettlementFixture Fixture(params int[] rolls)
    {
        var random = new SequenceSettlementRandom(rolls);
        var drop = new FakeDropAuthority();
        var hitter = new FakeLastHitterAuthority();
        var sanity = new FakeSanityRewardAuthority();
        var service = new HostileShadowSettlementService(random, drop, hitter, sanity);
        Assert.True(service.BeginSession(HostileAttackTestFactory.SessionId, out var reason), reason);
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
        internal List<int> Seeds { get; } = new();

        public int NextBonusRoll10000(int seed)
        {
            Calls++;
            Seeds.Add(seed);
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
