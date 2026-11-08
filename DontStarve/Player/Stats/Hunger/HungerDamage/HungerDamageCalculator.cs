#nullable enable

namespace DontStarve.Player.Stats.Hunger;

internal sealed class HungerDamageCalculator
{
    internal HungerDamageResult Calculate(
        HungerDamageMode mode,
        int currentHealth,
        int maximumHealth
    )
    {
        return HungerRules.CalculateDamage(mode, currentHealth, maximumHealth);
    }
}
