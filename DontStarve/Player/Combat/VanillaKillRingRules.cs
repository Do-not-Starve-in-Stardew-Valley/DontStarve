#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Combat;

/// <summary>
/// Pure, deterministic implementation of the six vanilla kill-ring effects. It intentionally
/// stops before monster-specific Burglar/Book_Void drop planning.
/// </summary>
internal static class VanillaKillRingRules
{
    internal static VanillaKillRingEffectPlan Plan(
        VanillaKillRingSnapshot snapshot,
        int killCount,
        string settlementId,
        IVanillaKillRollRandom random
    )
    {
        if (random is null)
            throw new ArgumentNullException(nameof(random));
        if (killCount <= 0)
            return VanillaKillRingEffectPlan.Empty;

        var dropItemIds = new List<string>();
        var vampireRings = 0;
        var soulSapperRings = 0;
        var warriorTriggers = 0;
        var savageRings = 0;
        var napalmRings = 0;
        var ringIndex = 0;

        for (var killIndex = 0; killIndex < killCount; killIndex++)
        {
            foreach (var ringId in snapshot.EnumerateRingIds())
            {
                switch (ringId)
                {
                    case VanillaKillRingCatalog.HotJava:
                    {
                        var coffeeRoll = Roll(
                            random,
                            VanillaKillRingSettlementSeed.CreateDerived(
                                settlementId,
                                string.Concat("hot-java:", ringIndex, ":coffee")
                            ),
                            dropItemIds,
                            vampireRings,
                            soulSapperRings,
                            warriorTriggers,
                            savageRings,
                            napalmRings
                        );
                        if (coffeeRoll < VanillaKillRingCatalog.HotJavaCoffeeRollThreshold)
                        {
                            dropItemIds.Add(VanillaKillRingCatalog.CoffeeItemId);
                        }
                        else
                        {
                            var espressoRoll = Roll(
                                random,
                                VanillaKillRingSettlementSeed.CreateDerived(
                                    settlementId,
                                    string.Concat("hot-java:", ringIndex, ":espresso")
                                ),
                                dropItemIds,
                                vampireRings,
                                soulSapperRings,
                                warriorTriggers,
                                savageRings,
                                napalmRings
                            );
                            if (
                                espressoRoll
                                < VanillaKillRingCatalog.HotJavaEspressoRollThreshold
                            )
                            {
                                dropItemIds.Add(
                                    VanillaKillRingCatalog.TripleShotEspressoItemId
                                );
                            }
                        }
                        break;
                    }

                    case VanillaKillRingCatalog.Warrior:
                    {
                        var warriorRoll = Roll(
                            random,
                            VanillaKillRingSettlementSeed.CreateDerived(
                                settlementId,
                                string.Concat("warrior:", ringIndex)
                            ),
                            dropItemIds,
                            vampireRings,
                            soulSapperRings,
                            warriorTriggers,
                            savageRings,
                            napalmRings
                        );
                        if (
                            warriorRoll / (double)VanillaKillRingCatalog.RollScale
                            < VanillaKillRingCatalog.WarriorBaseProbability
                                + snapshot.LuckLevel
                                    / VanillaKillRingCatalog.WarriorLuckDivisor
                        )
                        {
                            warriorTriggers++;
                        }
                        break;
                    }

                    case VanillaKillRingCatalog.Vampire:
                        vampireRings++;
                        break;
                    case VanillaKillRingCatalog.Savage:
                        savageRings++;
                        break;
                    case VanillaKillRingCatalog.SoulSapper:
                        soulSapperRings++;
                        break;
                    case VanillaKillRingCatalog.Napalm:
                        napalmRings++;
                        break;
                }

                // Burglar is intentionally part of the captured sequence even though its drop
                // planning remains monster-specific; keep its slot in every derived purpose.
                ringIndex++;
            }
        }

        return BuildPlan(
            dropItemIds,
            vampireRings,
            soulSapperRings,
            warriorTriggers,
            savageRings,
            napalmRings
        );
    }

    /// <summary>
    /// Applies the generic post-table semantics shared by custom-drop monsters. The caller has
    /// already merged its base stacks and the ordered Hot Java stacks. A monster-specific reroller
    /// owns only how one independent Burglar table roll produces stacks; this method owns whether
    /// that roll runs and whether Book_Void copies the resulting complete list.
    /// </summary>
    internal static VanillaKillRingDropPlan PlanDrops(
        VanillaKillRingSnapshot snapshot,
        IReadOnlyList<VanillaKillRingDropStack> currentDropStacks,
        IVanillaKillBurglarDropReroller? burglarDropReroller,
        string settlementId,
        IVanillaKillRollRandom random
    )
    {
        if (currentDropStacks is null)
            throw new ArgumentNullException(nameof(currentDropStacks));
        if (random is null)
            throw new ArgumentNullException(nameof(random));

        var dropStacks = CopyDropStacks(currentDropStacks);
        if (snapshot.HasBurglarRing)
        {
            if (burglarDropReroller is null)
            {
                throw new VanillaKillRingDropPlanException(
                    "vanilla-kill-ring-burglar-reroller-missing",
                    dropStacks
                );
            }

            try
            {
                var burglarSeed = VanillaKillRingSettlementSeed.CreateDerived(
                    settlementId,
                    "burglar"
                );
                var burglarStacks = burglarDropReroller.Roll(burglarSeed, random);
                if (burglarStacks is null)
                {
                    throw new InvalidOperationException(
                        "vanilla-kill-ring-burglar-reroller-returned-null"
                    );
                }
                foreach (var stack in burglarStacks)
                    dropStacks.Add(stack);
            }
            catch (Exception exception)
            {
                throw new VanillaKillRingDropPlanException(
                    string.IsNullOrWhiteSpace(exception.Message)
                        ? "vanilla-kill-ring-burglar-roll-failed"
                        : exception.Message,
                    dropStacks,
                    exception
                );
            }
        }

        if (snapshot.HasMonsterBook)
        {
            var bookRoll = RollDropPlan(
                random,
                VanillaKillRingSettlementSeed.CreateDerived(
                    settlementId,
                    "monster-book"
                ),
                dropStacks
            );
            if (bookRoll < VanillaKillRingCatalog.MonsterBookRollThreshold)
            {
                var copy = CopyDropStacks(dropStacks);
                dropStacks.AddRange(copy);
            }
        }

        return new VanillaKillRingDropPlan(CopyDropStacks(dropStacks));
    }

    private static int Roll(
        IVanillaKillRollRandom random,
        int seed,
        IReadOnlyList<string> dropItemIds,
        int vampireRings,
        int soulSapperRings,
        int warriorTriggers,
        int savageRings,
        int napalmRings
    )
    {
        int roll;
        try
        {
            roll = random.NextRoll10000(seed);
        }
        catch (Exception exception)
        {
            throw new VanillaKillRingPlanException(
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "vanilla-kill-ring-roll-failed"
                    : exception.Message,
                BuildPlan(
                    dropItemIds,
                    vampireRings,
                    soulSapperRings,
                    warriorTriggers,
                    savageRings,
                    napalmRings
                ),
                exception
            );
        }

        if (roll < 0 || roll >= VanillaKillRingCatalog.RollScale)
        {
            throw new VanillaKillRingPlanException(
                "vanilla-kill-ring-roll-invalid",
                BuildPlan(
                    dropItemIds,
                    vampireRings,
                    soulSapperRings,
                    warriorTriggers,
                    savageRings,
                    napalmRings
                )
            );
        }

        return roll;
    }

    private static VanillaKillRingEffectPlan BuildPlan(
        IReadOnlyList<string> dropItemIds,
        int vampireRings,
        int soulSapperRings,
        int warriorTriggers,
        int savageRings,
        int napalmRings
    )
    {
        return new VanillaKillRingEffectPlan(
            vampireRings * VanillaKillRingCatalog.VampireHealthPerRing,
            soulSapperRings * VanillaKillRingCatalog.SoulSapperEnergyPerRing,
            warriorTriggers,
            savageRings,
            napalmRings,
            CopyDropItemIds(dropItemIds)
        );
    }

    private static int RollDropPlan(
        IVanillaKillRollRandom random,
        int seed,
        IReadOnlyList<VanillaKillRingDropStack> partialDropStacks
    )
    {
        int roll;
        try
        {
            roll = random.NextRoll10000(seed);
        }
        catch (Exception exception)
        {
            throw new VanillaKillRingDropPlanException(
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "vanilla-kill-ring-roll-failed"
                    : exception.Message,
                partialDropStacks,
                exception
            );
        }

        if (roll < 0 || roll >= VanillaKillRingCatalog.RollScale)
        {
            throw new VanillaKillRingDropPlanException(
                "vanilla-kill-ring-roll-invalid",
                partialDropStacks
            );
        }

        return roll;
    }

    private static List<VanillaKillRingDropStack> CopyDropStacks(
        IReadOnlyList<VanillaKillRingDropStack> dropStacks
    )
    {
        var copy = new List<VanillaKillRingDropStack>(dropStacks.Count);
        for (var index = 0; index < dropStacks.Count; index++)
            copy.Add(dropStacks[index]);
        return copy;
    }

    private static IReadOnlyList<string> CopyDropItemIds(IReadOnlyList<string> dropItemIds)
    {
        if (dropItemIds.Count == 0)
            return Array.Empty<string>();

        var copy = new string[dropItemIds.Count];
        for (var index = 0; index < dropItemIds.Count; index++)
            copy[index] = dropItemIds[index];
        return copy;
    }
}
