#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Self-contained vanilla-combat seam contracts for this mod's hostile shadows. This file is
/// source-linked into DontStarve.Tests, so it must stay free of SMAPI / Stardew / Harmony types.
/// HostileShadowMonster aliases the modData keys defined here so the string literals have exactly
/// one source of truth; the literals themselves are frozen because already-materialized save
/// entities and the Harmony patches both depend on them.
/// </summary>
internal static class HostileShadowVanillaCombatContracts
{
    internal const string FrozenImmunityModDataKey =
        "Yurin.DontStarve/HostileShadow/Immunity/Frozen";
    internal const string BindingHiddenModDataKey =
        "Yurin.DontStarve/HostileShadow/BindingHidden";
    internal const string EnabledModDataValue = "1";
}

/// <summary>
/// Pure decision core for the two vanilla seams that have no in-engine immunity callback:
/// which monsters the shared auto-targeting helper may return, and whether a frozen-debuff
/// projectile hit must be skipped entirely. All inputs are pre-extracted booleans so tests run
/// without the game assembly; the Harmony patches only extract modData flags and delegate here.
/// </summary>
internal static class HostileShadowAutoTargetRules
{
    /// <summary>
    /// Universal rule (every findClosestMonsterWithinRange caller): a binding-hidden shadow is
    /// invisible on purpose and must never be selected. Callers marked by
    /// HostileShadowAutoTargetCallerScopePatch (vanilla ice orb, hungry frog) additionally ignore
    /// every hostile shadow state; unmarked callers such as the magic quiver keep targeting
    /// visible shadows by design (dangerous states stay attackable, 脱战恐吓 included).
    /// </summary>
    internal static bool ExcludesFromAutoTargeting(
        bool callerIgnoresAllHostileShadows,
        bool isHostileShadow,
        bool bindingHidden
    )
    {
        return bindingHidden || (callerIgnoresAllHostileShadows && isHostileShadow);
    }

    /// <summary>
    /// Vanilla frozen projectiles (DebuffingProjectile) write stunTime, play the frozen sound and
    /// spawn the attached ice-cube sprite unconditionally, so the only seam is skipping the whole
    /// hit for frozen-immune shadows; the projectile continues piercing without any effect.
    /// </summary>
    internal static bool SuppressesFrozenControlEffect(bool frozenImmunityEnabled)
    {
        return frozenImmunityEnabled;
    }

    internal static bool IsEnabledModDataValue(string? value)
    {
        return string.Equals(
            value,
            HostileShadowVanillaCombatContracts.EnabledModDataValue,
            StringComparison.Ordinal
        );
    }
}
