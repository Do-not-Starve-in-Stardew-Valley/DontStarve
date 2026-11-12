#nullable enable

namespace DontStarve.Player.Stats.Hunger;

internal readonly record struct HungerStarvationStep(
    bool IsStarving,
    bool DamageDue
);

internal static class HungerStarvationRules
{
    /// <summary>
    /// 饥饿危险唯一边界：饥饿值有限且 &lt;= 0。睡眠恢复判定与危险小游戏拦截共用此边界，
    /// 调整阈值时只改这里，不要在消费点复制判断表达式。
    /// </summary>
    internal static bool IsStarving(double hunger)
    {
        return double.IsFinite(hunger) && hunger <= 0d;
    }

    internal static HungerStarvationStep ResumeAfterSleep(double hunger)
    {
        return Evaluate(IsStarving(hunger), hunger);
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
