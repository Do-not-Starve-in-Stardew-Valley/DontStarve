#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace DontStarve.Player.Combat;

/// <summary>
/// Narrow SMAPI/Stardew adapter for the shared kill-ring plan. Session, owner and location
/// validation belong to the consumer; this adapter only applies effects to the explicit farmer
/// and location supplied by that consumer.
/// </summary>
internal static class SmapiVanillaKillRingEffects
{
    internal static VanillaKillRingSnapshot CaptureSnapshot(Farmer farmer)
    {
        if (farmer is null)
            throw new ArgumentNullException(nameof(farmer));

        var ringIds = new List<string>(8);
        AppendRingIds(farmer.leftRing.Value, ringIds);
        AppendRingIds(farmer.rightRing.Value, ringIds);
        return new VanillaKillRingSnapshot(
            string.Join(",", ringIds),
            ringIds.Contains(VanillaKillRingCatalog.Burglar, StringComparer.Ordinal),
            farmer.stats.Get(VanillaKillRingCatalog.MonsterBookStatId) != 0,
            farmer.LuckLevel
        );
    }

    internal static void ApplyPlan(
        Farmer farmer,
        GameLocation location,
        VanillaKillRingEffectPlan plan,
        Vector2 explosionTile
    )
    {
        if (farmer is null)
            throw new ArgumentNullException(nameof(farmer));
        if (location is null)
            throw new ArgumentNullException(nameof(location));

        farmer.health = Math.Min(
            farmer.maxHealth,
            farmer.health + plan.VampireHealth
        );
        farmer.Stamina = Math.Min(
            farmer.MaxStamina,
            farmer.Stamina + plan.SoulSapperEnergy
        );

        for (var index = 0; index < plan.WarriorTriggerCount; index++)
        {
            farmer.applyBuff(VanillaKillRingCatalog.WarriorBuffId);
            if (farmer.IsLocalPlayer)
                Game1.playSound("warrior");
        }
        for (var index = 0; index < plan.SavageTriggerCount; index++)
            farmer.applyBuff(VanillaKillRingCatalog.AdrenalineRushBuffId);

        for (var index = 0; index < plan.NapalmExplosionCount; index++)
        {
            // This mirrors Ring.onMonsterSlay: Farm/SlimeHutch still receive the explosion so
            // nearby monsters can be affected, but their terrain/objects are not destroyed.
            location.explode(
                explosionTile,
                2,
                farmer,
                damageFarmers: false,
                damage_amount: -1,
                destroyObjects: location is not Farm && location is not SlimeHutch
            );
        }
    }

    private static void AppendRingIds(Ring? ring, List<string> result)
    {
        if (ring is null)
            return;
        if (ring is CombinedRing combined)
        {
            foreach (var child in combined.combinedRings)
                AppendRingIds(child, result);
        }
        if (VanillaKillRingCatalog.IsRingSequenceRelevant(ring.ItemId))
            result.Add(ring.ItemId);
    }
}
