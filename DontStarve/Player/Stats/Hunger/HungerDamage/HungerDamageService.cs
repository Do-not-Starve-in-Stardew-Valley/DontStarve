#nullable enable

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Hunger 自己的生命后果 seam。它只返回计划结果，不进入普通 takeDamage 链。
/// </summary>
internal sealed class HungerDamageService
{
    private readonly HungerDamageCalculator calculator = new();
    private readonly HungerDamageMode mode;

    internal HungerDamageService(HungerDamageMode mode)
    {
        this.mode = mode;
    }

    internal HungerDamageResult Resolve(int currentHealth, int maximumHealth)
    {
        return calculator.Calculate(mode, currentHealth, maximumHealth);
    }
}
