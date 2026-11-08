#nullable enable

namespace DontStarve.Player.Stats.Hunger;

internal readonly record struct HungerStarvationStep(
    bool IsStarving,
    bool DamageDue
);

internal static class HungerStarvationRules
{
    internal static HungerStarvationStep ResumeAfterSleep(double hunger)
    {
        return Evaluate(double.IsFinite(hunger) && hunger <= 0d, hunger);
    }

    internal static HungerStarvationStep Evaluate(
        bool wasStarving,
        double boundedHunger
    )
    {
        if (double.IsFinite(boundedHunger) && boundedHunger > 0d)
            return new(false, false);

        return new(true, wasStarving);
    }
}
