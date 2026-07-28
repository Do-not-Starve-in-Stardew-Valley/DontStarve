#nullable enable

using System;
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

    internal SmapiHostileAttackDamageAdapter(
        Farmer target,
        HostileShadowMonster damager
    )
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        this.damager = damager ?? throw new ArgumentNullException(nameof(damager));
    }

    public int CurrentHealth => target.health;

    public void ApplyOrdinaryDamage(int damage)
    {
        if (damage <= 0)
            throw new ArgumentOutOfRangeException(nameof(damage));
        target.takeDamage(damage, overrideParry: false, damager);
    }
}
