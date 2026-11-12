#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Combat;

/// <summary>
/// Stable IDs and scalar constants shared by every monster-specific kill settlement.
/// Monster-owned drop tables (including Burglar's Ring and Book_Void) deliberately stay outside
/// this catalog's effect planner.
/// </summary>
internal static class VanillaKillRingCatalog
{
    internal const string Warrior = "521";
    internal const string Vampire = "522";
    internal const string Savage = "523";
    internal const string Burglar = "526";
    internal const string Amethyst = "529";
    internal const string Napalm = "811";
    internal const string Lucky = "859";
    internal const string HotJava = "860";
    internal const string SoulSapper = "862";

    internal const string WarriorBuffId = "20";
    internal const string AdrenalineRushBuffId = "22";
    internal const string MonsterBookStatId = "Book_Void";
    internal const string CoffeeItemId = "395";
    internal const string TripleShotEspressoItemId = "253";

    internal const int RollScale = 10_000;
    internal const int HotJavaCoffeeRollThreshold = 2_500;
    internal const int HotJavaEspressoRollThreshold = 1_000;
    internal const int MonsterBookRollThreshold = 300;
    internal const double WarriorBaseProbability = 0.1d;
    internal const double WarriorLuckDivisor = 100d;
    internal const int VampireHealthPerRing = 2;
    internal const int SoulSapperEnergyPerRing = 4;

    /// <summary>
    /// The sequence includes Burglar so ring indexes remain identical to the old shadow loop.
    /// Amethyst and Lucky are snapshot-adjacent IDs, but neither owns a death-settlement roll.
    /// </summary>
    internal static bool IsRingSequenceRelevant(string itemId)
    {
        return itemId
            is Warrior
                or Vampire
                or Savage
                or Burglar
                or Napalm
                or HotJava
                or SoulSapper;
    }
}

/// <summary>
/// Immutable capture of the killer's relevant equipment at settlement time. The ring sequence is
/// scalar text so a receipt or replay comparison never depends on a mutable game collection.
/// </summary>
internal readonly record struct VanillaKillRingSnapshot(
    string MonsterSlayRingSequence,
    bool HasBurglarRing,
    bool HasMonsterBook,
    int LuckLevel
)
{
    internal static VanillaKillRingSnapshot Empty => new(string.Empty, false, false, 0);

    internal IEnumerable<string> EnumerateRingIds()
    {
        if (string.IsNullOrEmpty(MonsterSlayRingSequence))
            yield break;

        foreach (
            var itemId in MonsterSlayRingSequence.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            yield return itemId;
        }
    }
}

/// <summary>
/// Pure output of the shared ring rules. DropItemIds contains only Hot Java item IDs and preserves
/// the exact order in which those items were rolled; monster-owned base/Burglar/Book_Void stacks
/// are merged by the consuming monster settlement.
/// </summary>
internal readonly record struct VanillaKillRingEffectPlan(
    int VampireHealth,
    int SoulSapperEnergy,
    int WarriorTriggerCount,
    int SavageTriggerCount,
    int NapalmExplosionCount,
    IReadOnlyList<string> DropItemIds
)
{
    internal static VanillaKillRingEffectPlan Empty => new(
        0,
        0,
        0,
        0,
        0,
        Array.Empty<string>()
    );
}

internal readonly record struct VanillaKillRingDropStack(
    string ItemId,
    int Quantity
);

internal readonly record struct VanillaKillRingDropPlan(
    IReadOnlyList<VanillaKillRingDropStack> DropStacks
)
{
    internal static VanillaKillRingDropPlan Empty => new(Array.Empty<VanillaKillRingDropStack>());
}

/// <summary>
/// Monster-specific custom drop tables implement this callback. The shared rules own the stable
/// Burglar purpose and provide the injected roll seam; the callback consumes one roll for its own
/// table semantics and returns the ordered stacks produced by that independent table roll.
/// </summary>
internal interface IVanillaKillBurglarDropReroller
{
    IReadOnlyList<VanillaKillRingDropStack> Roll(
        int seed,
        IVanillaKillRollRandom random
    );
}

internal interface IVanillaKillRollRandom
{
    int NextRoll10000(int seed);
}

/// <summary>
/// Pure planning failure that retains effects already planned before a roll failed. Consumers can
/// preserve their existing receipt/failure semantics without letting the rules know about SMAPI.
/// </summary>
internal sealed class VanillaKillRingPlanException : Exception
{
    internal VanillaKillRingPlanException(
        string reason,
        VanillaKillRingEffectPlan partialPlan,
        Exception? innerException = null
    )
        : base(
            string.IsNullOrWhiteSpace(reason)
                ? "vanilla-kill-ring-roll-failed"
                : reason,
            innerException
        )
    {
        Reason = string.IsNullOrWhiteSpace(reason)
            ? "vanilla-kill-ring-roll-failed"
            : reason;
        PartialPlan = partialPlan;
    }

    internal string Reason { get; }
    internal VanillaKillRingEffectPlan PartialPlan { get; }
}

internal sealed class VanillaKillRingDropPlanException : Exception
{
    internal VanillaKillRingDropPlanException(
        string reason,
        IReadOnlyList<VanillaKillRingDropStack> partialDropStacks,
        Exception? innerException = null
    )
        : base(
            string.IsNullOrWhiteSpace(reason)
                ? "vanilla-kill-ring-roll-failed"
                : reason,
            innerException
        )
    {
        Reason = string.IsNullOrWhiteSpace(reason)
            ? "vanilla-kill-ring-roll-failed"
            : reason;
        PartialDropStacks = partialDropStacks;
    }

    internal string Reason { get; }
    internal IReadOnlyList<VanillaKillRingDropStack> PartialDropStacks { get; }
}

/// <summary>
/// Stable correlation seed shared by all monster-specific consumers of the ring rules.
/// </summary>
internal static class VanillaKillRingSettlementSeed
{
    internal static int Create(string settlementId)
    {
        if (string.IsNullOrWhiteSpace(settlementId))
            return 0;

        return CreateFromCorrelation(settlementId);
    }

    internal static int CreateDerived(string settlementId, string purpose)
    {
        if (string.IsNullOrWhiteSpace(settlementId) || string.IsNullOrWhiteSpace(purpose))
            return 0;

        return CreateFromCorrelation(string.Concat(settlementId, ":", purpose));
    }

    private static int CreateFromCorrelation(string correlation)
    {
        // FNV-1a is used only as a stable correlation seed, not for security.
        var bytes = System.Text.Encoding.UTF8.GetBytes(correlation);
        var hash = 2166136261u;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 16777619u;
        }
        var seed = (int)(hash & 0x7fffffff);
        return seed == 0 ? 1 : seed;
    }
}
