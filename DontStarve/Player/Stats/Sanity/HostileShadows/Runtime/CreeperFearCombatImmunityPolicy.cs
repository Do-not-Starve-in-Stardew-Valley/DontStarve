#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Freezes a hostile shadow control-effect contract after profile adaptation. The SMAPI bridge
/// consumes these booleans once during materialization; update never reparses profile data.
/// </summary>
internal abstract class HostileShadowCombatImmunityPolicy
{
    // Current Stardew 1.6 DebuffingProjectile itself treats stunTime < 51 as not frozen;
    // Ice Orb then writes a 2000-4000 ms freeze. Preserve the separate 50 ms weapon hit-stun.
    internal const int MaximumNonFrozenStunMilliseconds = 50;

    protected HostileShadowCombatImmunityPolicy()
    {
    }

    internal bool BlocksKnockback => true;
    internal bool BlocksFrozen => true;

    internal static bool IsFrozenStun(int stunMilliseconds)
    {
        return stunMilliseconds > MaximumNonFrozenStunMilliseconds;
    }

    protected static bool TryValidateProfile(
        ShadowMonsterRuntimeProfile? profile,
        string expectedBindingId,
        string reasonPrefix,
        out string reason
    )
    {
        if (
            profile is null
            || !string.Equals(
                profile.AssetBindingId,
                expectedBindingId,
                StringComparison.Ordinal
            )
            || profile.ImmunityTags is not { } tags
        )
        {
            reason = string.Concat(reasonPrefix, "-profile-invalid");
            return false;
        }

        var hasKnockback = false;
        var hasFrozen = false;
        for (var index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];
            if (
                string.Equals(
                    tag,
                    ShadowMonsterProfileContractIds.KnockbackImmunity,
                    StringComparison.Ordinal
                )
            )
            {
                if (hasKnockback)
                {
                    reason = string.Concat(reasonPrefix, "-tag-duplicate");
                    return false;
                }
                hasKnockback = true;
                continue;
            }
            if (
                string.Equals(
                    tag,
                    ShadowMonsterProfileContractIds.FrozenImmunity,
                    StringComparison.Ordinal
                )
            )
            {
                if (hasFrozen)
                {
                    reason = string.Concat(reasonPrefix, "-tag-duplicate");
                    return false;
                }
                hasFrozen = true;
                continue;
            }

            reason = string.Concat(reasonPrefix, "-tag-unsupported");
            return false;
        }

        if (tags.Count != 2 || !hasKnockback || !hasFrozen)
        {
            reason = string.Concat(reasonPrefix, "-required-tag-missing");
            return false;
        }

        reason = string.Concat(reasonPrefix, "-validated");
        return true;
    }
}

internal sealed class CreeperFearCombatImmunityPolicy
    : HostileShadowCombatImmunityPolicy
{
    private const string ReasonPrefix = "creeper-fear.combat-immunity";

    private CreeperFearCombatImmunityPolicy()
    {
    }

    internal static bool TryCreate(
        ShadowMonsterRuntimeProfile? profile,
        out CreeperFearCombatImmunityPolicy? policy,
        out string reason
    )
    {
        policy = null;
        if (
            !TryValidateProfile(
                profile,
                ShadowMonsterAssetBindingIds.CreeperFear,
                ReasonPrefix,
                out reason
            )
        )
        {
            return false;
        }

        policy = new CreeperFearCombatImmunityPolicy();
        reason = "creeper-fear.combat-immunity-ready";
        return true;
    }
}

internal sealed class TerrorbeakCombatImmunityPolicy
    : HostileShadowCombatImmunityPolicy
{
    private const string ReasonPrefix = "terrorbeak.combat-immunity";

    private TerrorbeakCombatImmunityPolicy()
    {
    }

    internal static bool TryCreate(
        ShadowMonsterRuntimeProfile? profile,
        out TerrorbeakCombatImmunityPolicy? policy,
        out string reason
    )
    {
        policy = null;
        if (
            !TryValidateProfile(
                profile,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                ReasonPrefix,
                out reason
            )
        )
        {
            return false;
        }

        policy = new TerrorbeakCombatImmunityPolicy();
        reason = "terrorbeak.combat-immunity-ready";
        return true;
    }
}
