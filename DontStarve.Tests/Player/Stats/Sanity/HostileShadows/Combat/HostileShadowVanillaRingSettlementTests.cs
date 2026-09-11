using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileShadowVanillaRingSettlementTests
{
    private const string SessionId = "11111111111111111111111111111111";
    private const string PlayerKey = "123456789";
    private const string LocationId = "Mine";

    [Fact]
    public void Two_warrior_rings_each_use_the_vanilla_probability_roll()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("521,521", false, false, 0),
            5000,
            0,
            9999
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var effects = Assert.Single(fixture.KillEffects.Requests);
        Assert.Equal(1, effects.WarriorTriggerCount);
        Assert.Equal(3, fixture.Random.Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Luck_level_changes_warrior_probability_but_daily_luck_is_not_read(
        int luckLevel,
        bool expectedTrigger
    )
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("521", false, false, luckLevel),
            5000,
            1050
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(
            expectedTrigger ? 1 : 0,
            Assert.Single(fixture.KillEffects.Requests).WarriorTriggerCount
        );
    }

    [Fact]
    public void Two_burglar_rings_still_add_only_one_extra_base_roll()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("526,526", true, false, 0),
            0,
            0
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(2, result.Receipt!.PlannedDropStacks.Count);
        Assert.Equal(4, result.Receipt.PlannedDropQuantity);
        Assert.Equal(2, fixture.Random.Calls);
    }

    [Fact]
    public void Burglar_extra_base_roll_keeps_guaranteed_drop_when_bonus_fails()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("526", true, false, 0),
            5000,
            9999
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(2, result.Receipt!.PlannedDropStacks.Count);
        Assert.Equal(2, result.Receipt.PlannedDropQuantity);
        Assert.All(
            result.Receipt.PlannedDropStacks,
            stack => Assert.Equal(1, stack.Quantity)
        );
        Assert.Equal(2, fixture.Random.Calls);
    }

    [Fact]
    public void Monster_book_copies_the_complete_list_once_after_burglar_and_hot_java()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("860", true, true, 0),
            5000,
            0,
            0,
            0
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var stacks = result.Receipt!.PlannedDropStacks;
        Assert.Equal(6, stacks.Count);
        Assert.Equal(4, stacks.Count(stack => stack.ItemSemanticId == "test.item"));
        Assert.Equal(
            2,
            stacks.Count(
                stack =>
                    stack.ItemSemanticId
                    == HostileShadowSettlementItemSemanticIds.Coffee
            )
        );
        Assert.Equal(2, stacks.Count(stack => stack.Quantity == 2));
        Assert.Equal(8, result.Receipt.PlannedDropQuantity);
    }

    [Fact]
    public void Four_hot_java_rings_roll_independently()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("860,860,860,860", false, false, 0),
            5000,
            0,
            3000,
            0,
            0,
            3000,
            1000
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var stacks = result.Receipt!.PlannedDropStacks;
        Assert.Equal(4, stacks.Count);
        Assert.Equal(
            2,
            stacks.Count(
                stack =>
                    stack.ItemSemanticId
                    == HostileShadowSettlementItemSemanticIds.Coffee
            )
        );
        Assert.Single(
            stacks,
            stack =>
                stack.ItemSemanticId
                == HostileShadowSettlementItemSemanticIds.TripleShotEspresso
        );
        Assert.Equal(7, fixture.Random.Calls);
    }

    [Fact]
    public void Vampire_and_soul_sapper_rings_stack_their_player_effects()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("522,522,862,862", false, false, 0),
            5000
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        var effects = Assert.Single(fixture.KillEffects.Requests);
        Assert.Equal(4, effects.VampireHealth);
        Assert.Equal(8, effects.SoulSapperEnergy);
    }

    [Fact]
    public void Multiple_savage_rings_repeat_the_same_buff_application()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("523,523", false, false, 0),
            5000
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(2, Assert.Single(fixture.KillEffects.Requests).SavageTriggerCount);
    }

    [Fact]
    public void Multiple_napalm_rings_repeat_the_vanilla_explosion_call()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("811,811", false, false, 0),
            5000
        );

        var result = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, result.Status);
        Assert.Equal(2, Assert.Single(fixture.KillEffects.Requests).NapalmExplosionCount);
    }

    [Fact]
    public void Settlement_replay_does_not_repeat_drop_or_kill_effects()
    {
        var fixture = Fixture(
            new HostileShadowRingSnapshot("521,522,523,811,860,862", false, false, 0),
            5000
        );

        var first = fixture.Resolve();
        var replay = fixture.Resolve();

        Assert.Equal(HostileShadowSettlementStatus.Settled, first.Status);
        Assert.Equal(HostileShadowSettlementStatus.Duplicate, replay.Status);
        Assert.Same(first.Receipt, replay.Receipt);
        Assert.Equal(1, fixture.Drop.Calls);
        Assert.Equal(1, fixture.KillEffects.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    private static SettlementFixture Fixture(
        HostileShadowRingSnapshot snapshot,
        params int[] rolls
    )
    {
        var random = new SequenceSettlementRandom(rolls);
        var drop = new FakeDropAuthority();
        var hitter = new FakeLastHitterAuthority();
        var sanity = new FakeSanityRewardAuthority();
        var ringSnapshot = new FakeRingSnapshotAuthority(snapshot);
        var killEffects = new FakeKillEffectAuthority();
        var service = new HostileShadowSettlementService(
            random,
            drop,
            hitter,
            sanity,
            ringSnapshot,
            killEffects
        );
        Assert.True(service.BeginSession(SessionId, out var reason), reason);
        return new SettlementFixture(
            service,
            random,
            drop,
            sanity,
            ringSnapshot,
            killEffects
        );
    }

    private static HostileShadowSettlementRequest Request()
    {
        Assert.True(
            HostileShadowLifecycleReceipt.TryCreate(
                SessionId,
                1,
                1,
                HostileShadowLifecycleTransitionKind.Dying,
                HostileShadowSettlementReasonIds.DyingHealthZero,
                null,
                PlayerKey,
                out var receipt
            )
        );
        return new HostileShadowSettlementRequest(
            receipt,
            SanityAuthorityRole.Host,
            HostileShadowStateIds.Dying,
            0,
            LocationId,
            100d,
            200d,
            1,
            "test.drop-table",
            "test.item",
            1,
            1,
            0.5d,
            15,
            0,
            string.Empty
        )
        {
            ExplosionTileX = 1d,
            ExplosionTileY = 2d,
        };
    }

    private sealed record SettlementFixture(
        HostileShadowSettlementService Service,
        SequenceSettlementRandom Random,
        FakeDropAuthority Drop,
        FakeSanityRewardAuthority Sanity,
        FakeRingSnapshotAuthority RingSnapshot,
        FakeKillEffectAuthority KillEffects
    )
    {
        internal HostileShadowSettlementResult Resolve()
        {
            return Service.Resolve(Request());
        }
    }

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
        internal int Calls { get; private set; }

        public HostileShadowDropSpawnReceipt Spawn(HostileShadowDropSpawnRequest request)
        {
            Calls++;
            return HostileShadowDropSpawnReceipt.Success("test-drop-spawned");
        }
    }

    private sealed class FakeLastHitterAuthority : IHostileShadowLastHitterAuthority
    {
        public HostileShadowLastHitterReceipt Resolve(HostileShadowLastHitterRequest request)
        {
            return HostileShadowLastHitterReceipt.Valid(
                request.AttributedPlayerKey,
                "test-last-hitter-valid"
            );
        }
    }

    private sealed class FakeSanityRewardAuthority : IHostileShadowSanityRewardAuthority
    {
        internal int Calls { get; private set; }

        public HostileShadowSanityRewardReceipt Apply(HostileShadowSanityRewardRequest request)
        {
            Calls++;
            return new HostileShadowSanityRewardReceipt(
                HostileShadowSanityRewardStatus.Applied,
                request.PlayerKey,
                request.Delta,
                request.Source,
                1,
                2,
                100d,
                100d + request.Delta,
                "test-sanity-applied"
            );
        }
    }

    private sealed class FakeRingSnapshotAuthority : IHostileShadowRingSnapshotAuthority
    {
        private readonly HostileShadowRingSnapshot snapshot;

        internal FakeRingSnapshotAuthority(HostileShadowRingSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public HostileShadowRingSnapshotReceipt Resolve(
            HostileShadowRingSnapshotRequest request
        )
        {
            return HostileShadowRingSnapshotReceipt.Valid(
                snapshot,
                "test-ring-snapshot-valid"
            );
        }
    }

    private sealed class FakeKillEffectAuthority : IHostileShadowKillEffectAuthority
    {
        internal int Calls { get; private set; }
        internal List<HostileShadowKillEffectRequest> Requests { get; } = new();

        public HostileShadowKillEffectReceipt Apply(HostileShadowKillEffectRequest request)
        {
            Calls++;
            Requests.Add(request);
            return new HostileShadowKillEffectReceipt(
                HostileShadowKillEffectStatus.Applied,
                request.PlayerKey,
                request.VampireHealth,
                request.SoulSapperEnergy,
                request.WarriorTriggerCount,
                request.SavageTriggerCount,
                request.NapalmExplosionCount,
                "test-kill-effects-applied"
            );
        }
    }
}
