using System.Collections.Generic;
using System.Linq;
using DontStarve.Player.Combat;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Combat;

public sealed class VanillaKillRingRulesTests
{
    [Theory]
    [InlineData(2499, 5000, "395")]
    [InlineData(2500, 999, "253")]
    [InlineData(2500, 1000, "")]
    public void Hot_java_uses_strict_coffee_then_espresso_thresholds(
        int firstRoll,
        int secondRoll,
        string expectedItemId
    )
    {
        var rolls = firstRoll < 2500
            ? new[] { firstRoll }
            : new[] { firstRoll, secondRoll };

        var random = new SequenceRollRandom(rolls);
        var plan = VanillaKillRingRules.Plan(
            Snapshot("860"),
            1,
            "settlement-hot-java",
            random
        );

        if (expectedItemId.Length == 0)
            Assert.Empty(plan.DropItemIds);
        else
            Assert.Equal(new[] { expectedItemId }, plan.DropItemIds);
        Assert.Equal(rolls.Length, random.Seeds.Count);
    }

    [Theory]
    [InlineData(0, 999, true)]
    [InlineData(0, 1000, false)]
    [InlineData(2, 1199, true)]
    [InlineData(2, 1201, false)]
    public void Warrior_uses_strict_luck_adjusted_probability(
        int luckLevel,
        int roll,
        bool expectedTrigger
    )
    {
        var plan = VanillaKillRingRules.Plan(
            Snapshot("521", luckLevel: luckLevel),
            1,
            "settlement-warrior",
            new SequenceRollRandom(new[] { roll })
        );

        Assert.Equal(expectedTrigger ? 1 : 0, plan.WarriorTriggerCount);
    }

    [Fact]
    public void Stackable_ring_effects_accumulate_without_extra_rolls()
    {
        var random = new SequenceRollRandom();
        var plan = VanillaKillRingRules.Plan(
            Snapshot("522,522,862,862,523,523,811,811"),
            1,
            "settlement-stackable",
            random
        );

        Assert.Equal(4, plan.VampireHealth);
        Assert.Equal(8, plan.SoulSapperEnergy);
        Assert.Equal(2, plan.SavageTriggerCount);
        Assert.Equal(2, plan.NapalmExplosionCount);
        Assert.Equal(0, plan.WarriorTriggerCount);
        Assert.Empty(plan.DropItemIds);
        Assert.Empty(random.Seeds);
    }

    [Fact]
    public void Monster_book_stays_snapshot_metadata_without_a_shared_copy_roll()
    {
        var random = new SequenceRollRandom(new[] { 0 });
        var plan = VanillaKillRingRules.Plan(
            Snapshot("860", hasMonsterBook: true),
            1,
            "settlement-book-owned-by-monster",
            random
        );

        Assert.Equal(new[] { "395" }, plan.DropItemIds);
        Assert.Single(random.Seeds);
    }

    [Fact]
    public void Kill_count_repeats_ring_rolls_and_keeps_drop_ids_in_roll_order()
    {
        var random = new SequenceRollRandom(new[] { 0, 0, 2500, 0, 9999 });
        var plan = VanillaKillRingRules.Plan(
            Snapshot("860,521"),
            2,
            "settlement-batch",
            random
        );

        Assert.Equal(new[] { "395", "253" }, plan.DropItemIds);
        Assert.Equal(1, plan.WarriorTriggerCount);
        Assert.Equal(5, random.Seeds.Count);
        Assert.Equal(
            new[]
            {
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-batch",
                    "hot-java:0:coffee"
                ),
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-batch",
                    "warrior:1"
                ),
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-batch",
                    "hot-java:2:coffee"
                ),
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-batch",
                    "hot-java:2:espresso"
                ),
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-batch",
                    "warrior:3"
                ),
            },
            random.Seeds
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_kill_count_is_an_empty_plan_without_rng(int killCount)
    {
        var random = new SequenceRollRandom(new[] { 0 });

        var plan = VanillaKillRingRules.Plan(
            Snapshot("521,860"),
            killCount,
            "settlement-empty",
            random
        );

        Assert.Equal(0, plan.WarriorTriggerCount);
        Assert.Equal(0, plan.VampireHealth);
        Assert.Equal(0, plan.SoulSapperEnergy);
        Assert.Empty(plan.DropItemIds);
        Assert.Empty(random.Seeds);
    }

    [Fact]
    public void Shared_seed_derivation_is_the_same_as_shadow_seed_derivation()
    {
        var key = new HostileShadowSettlementKey(
            "11111111111111111111111111111111",
            42,
            7
        );

        foreach (var purpose in new[] { "warrior:0", "hot-java:1:coffee", "hot-java:1:espresso" })
        {
            Assert.Equal(
                HostileShadowSettlementSeed.CreateDerived(key, purpose),
                VanillaKillRingSettlementSeed.CreateDerived(key.SettlementId, purpose)
            );
        }
    }

    [Fact]
    public void Effect_plan_exposes_only_frozen_totals_and_ordered_drop_ids()
    {
        var plan = VanillaKillRingRules.Plan(
            Snapshot("522,523,860"),
            1,
            "settlement-frozen-fields",
            new SequenceRollRandom(new[] { 0 })
        );

        Assert.Equal(2, plan.VampireHealth);
        Assert.Equal(0, plan.SoulSapperEnergy);
        Assert.Equal(1, plan.SavageTriggerCount);
        Assert.Equal(0, plan.WarriorTriggerCount);
        Assert.Equal(0, plan.NapalmExplosionCount);
        Assert.Equal(new[] { "395" }, plan.DropItemIds);
    }

    [Fact]
    public void Invalid_shared_roll_preserves_the_partial_plan_for_the_consumer()
    {
        var random = new SequenceRollRandom(new[] { 2500, 10_000 });

        var exception = Assert.Throws<VanillaKillRingPlanException>(() =>
            VanillaKillRingRules.Plan(
                Snapshot("860"),
                1,
                "settlement-roll-failure",
                random
            )
        );

        Assert.Equal("vanilla-kill-ring-roll-invalid", exception.Reason);
        Assert.Empty(exception.PartialPlan.DropItemIds);
        Assert.Equal(2, random.Seeds.Count);
    }

    [Fact]
    public void Drop_plan_applies_burglar_then_book_to_custom_drop_stacks()
    {
        var random = new SequenceRollRandom(new[] { 0, 0 });
        var reroller = new TestBurglarDropReroller();
        var baseStacks = new[]
        {
            new VanillaKillRingDropStack("custom.base", 1),
            new VanillaKillRingDropStack("395", 1),
        };

        var plan = VanillaKillRingRules.PlanDrops(
            new VanillaKillRingSnapshot("526", true, true, 0),
            baseStacks,
            reroller,
            "settlement-custom-drop",
            random
        );

        Assert.Equal(
            new[]
            {
                "custom.base",
                "395",
                "custom.burglar",
                "custom.base",
                "395",
                "custom.burglar",
            },
            plan.DropStacks.Select(stack => stack.ItemId)
        );
        Assert.Equal(
            new[]
            {
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-custom-drop",
                    "burglar"
                ),
                VanillaKillRingSettlementSeed.CreateDerived(
                    "settlement-custom-drop",
                    "monster-book"
                ),
            },
            random.Seeds
        );
        Assert.Single(reroller.Seeds);
    }

    [Theory]
    [InlineData(299, 2)]
    [InlineData(300, 1)]
    public void Monster_book_copies_the_complete_custom_list_only_below_300(
        int bookRoll,
        int expectedStackCount
    )
    {
        var plan = VanillaKillRingRules.PlanDrops(
            new VanillaKillRingSnapshot("", false, true, 0),
            new[] { new VanillaKillRingDropStack("custom.base", 1) },
            null,
            "settlement-book-boundary",
            new SequenceRollRandom(new[] { bookRoll })
        );

        Assert.Equal(expectedStackCount, plan.DropStacks.Count);
        Assert.All(plan.DropStacks, stack => Assert.Equal("custom.base", stack.ItemId));
    }

    [Fact]
    public void Drop_plan_without_burglar_or_book_does_not_roll_or_call_custom_reroller()
    {
        var reroller = new TestBurglarDropReroller();
        var random = new SequenceRollRandom(new[] { 0 });

        var plan = VanillaKillRingRules.PlanDrops(
            new VanillaKillRingSnapshot("", false, false, 0),
            new[] { new VanillaKillRingDropStack("custom.base", 2) },
            reroller,
            "settlement-no-extra-drop",
            random
        );

        Assert.Equal(2, plan.DropStacks.Single().Quantity);
        Assert.Empty(random.Seeds);
        Assert.Empty(reroller.Seeds);
    }

    [Fact]
    public void Book_roll_failure_preserves_base_and_burglar_partial_stacks()
    {
        var random = new SequenceRollRandom(new[] { 0, 10_000 });
        var reroller = new TestBurglarDropReroller();

        var exception = Assert.Throws<VanillaKillRingDropPlanException>(() =>
            VanillaKillRingRules.PlanDrops(
                new VanillaKillRingSnapshot("526", true, true, 0),
                new[] { new VanillaKillRingDropStack("custom.base", 1) },
                reroller,
                "settlement-drop-failure",
                random
            )
        );

        Assert.Equal("vanilla-kill-ring-roll-invalid", exception.Reason);
        Assert.Equal(
            new[] { "custom.base", "custom.burglar" },
            exception.PartialDropStacks.Select(stack => stack.ItemId)
        );
    }

    private static VanillaKillRingSnapshot Snapshot(
        string ringSequence,
        int luckLevel = 0,
        bool hasMonsterBook = false
    )
    {
        return new VanillaKillRingSnapshot(
            ringSequence,
            ringSequence.Split(',').Contains(VanillaKillRingCatalog.Burglar),
            hasMonsterBook,
            luckLevel
        );
    }

    private sealed class SequenceRollRandom : IVanillaKillRollRandom
    {
        private readonly Queue<int> rolls;

        internal SequenceRollRandom(IEnumerable<int>? rolls = null)
        {
            this.rolls = new Queue<int>(rolls ?? Enumerable.Empty<int>());
        }

        internal List<int> Seeds { get; } = new();

        public int NextRoll10000(int seed)
        {
            Seeds.Add(seed);
            return rolls.Count == 0 ? 5000 : rolls.Dequeue();
        }
    }

    private sealed class TestBurglarDropReroller : IVanillaKillBurglarDropReroller
    {
        internal List<int> Seeds { get; } = new();

        public IReadOnlyList<VanillaKillRingDropStack> Roll(
            int seed,
            IVanillaKillRollRandom random
        )
        {
            Seeds.Add(seed);
            Assert.Equal(0, random.NextRoll10000(seed));
            return new[] { new VanillaKillRingDropStack("custom.burglar", 1) };
        }
    }
}
