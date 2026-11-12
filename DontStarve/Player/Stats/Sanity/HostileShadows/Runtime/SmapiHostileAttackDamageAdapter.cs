#nullable enable

using System;
using DontStarve.Player.Combat.InvincibilityFrames;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Narrow adapter over the frozen Stardew 1.6.15 ordinary, lethal Farmer damage seam. Defense,
/// parry, immunity, death, sounds, and vanilla side effects remain owned by Farmer.takeDamage.
/// </summary>
internal sealed class SmapiHostileAttackDamageAdapter
    : IHostileAttackLethalDamagePipeline
{
    private readonly Farmer target;
    private readonly HostileShadowMonster damager;
    private readonly SmapiInvincibilityFrameDamageService damageService;

    internal SmapiHostileAttackDamageAdapter(
        Farmer target,
        HostileShadowMonster damager,
        SmapiInvincibilityFrameDamageService damageService
    )
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        this.damager = damager ?? throw new ArgumentNullException(nameof(damager));
        this.damageService = damageService
            ?? throw new ArgumentNullException(nameof(damageService));
    }

    public int CurrentHealth => target.health;

    public void ApplyOrdinaryDamage(
        int damage,
        InvincibilityFramePolicy policy
    )
    {
        if (damage <= 0)
            throw new ArgumentOutOfRangeException(nameof(damage));
        // target.takeDamage remains the sole vanilla damage seam; the shared service owns the
        // post-hit policy so this adapter cannot grow a second combat implementation.
        damageService.Apply(target, damage, damager, policy);
    }
}
