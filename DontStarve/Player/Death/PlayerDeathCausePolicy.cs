#nullable enable

namespace DontStarve.Player.Death;

internal static class PlayerDeathCausePolicy
{
    // HungerDamage is damage applied by this sleep step/settlement, not a starvation prediction.
    // Zero HP without this damage must not be labeled as Hunger.
    internal static bool ShouldMarkHungerDeath(int healthAfter, int hungerDamage)
    {
        return healthAfter <= 0 && hungerDamage > 0;
    }

    internal static bool ShouldForgetPendingCause(bool killScreenActive, int health)
    {
        return !killScreenActive && health > 0;
    }
}
