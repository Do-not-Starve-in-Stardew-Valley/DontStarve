#nullable enable

using System;

namespace DontStarve.Display;

/// <summary>进食恢复弹窗的门控输入：是否本机玩家的结算、是否 Food 来源/种类。</summary>
/// <remarks>
/// 两个门控分别对应两条失败路径：
/// - IsLocalPlayerChange：主机的 ApplyFood / ApplyHostValue 同时服务主机自己与远程农场工，
///   只有本机玩家的结算才在本机屏幕弹（农场工的变化弹在农场工自己的机器上）。
/// - IsFoodSettlement：快照附加字段（DeltaKind / DeltaSource）缺失或非 Food 时为 false；
///   旧主机消息缺字段反序列化为默认值，fail-safe 不弹。
/// </remarks>
internal readonly record struct FoodRestoreHudGate(bool IsLocalPlayerChange, bool IsFoodSettlement)
{
    /// <summary>全通过的门控；语义上等价于「本机玩家 + Food 来源/种类」。</summary>
    internal static FoodRestoreHudGate LocalFood { get; } = new(true, true);
}

/// <summary>一次进食恢复弹窗的判定结果：是否弹、显示数值（原版 (int) 截断后的增量）。</summary>
internal readonly record struct FoodRestoreHudDecision(bool Show, int DisplayedAmount)
{
    internal static FoodRestoreHudDecision Hidden { get; } = new(false, 0);
}

/// <summary>
/// 进食恢复弹窗的纯判定规则，与原版 Farmer.doneEating（1.6.15 Farmer.cs:8934-8952）的
/// HUD 提示语义逐条对齐：
/// - 原版先夹上限再比较结算前后值，只有恢复量 &gt; 0 才弹，弹的是实际增量
///   （如 130/150 吃 +40 弹 +20）；恢复 0 或负数（毒食物）不弹。
/// - 显示数值为 double 增量按原版 (int) 截断；极端情况下截断为 0 的微小增量
///   在原版同样会弹一条 "+0"，这里保持一致照弹，不做额外过滤（显式边界，勿改）。
/// </summary>
internal static class FoodRestoreHudRule
{
    internal static FoodRestoreHudDecision Decide(
        FoodRestoreHudGate gate,
        double previousValue,
        double currentValue
    )
    {
        if (!gate.IsLocalPlayerChange || !gate.IsFoodSettlement)
            return FoodRestoreHudDecision.Hidden;
        // 前值/后值任一非有限（快照异常、缺基线）时静默不弹：弹窗只是反馈，不能拿坏数据弹误导值。
        if (!double.IsFinite(previousValue) || !double.IsFinite(currentValue))
            return FoodRestoreHudDecision.Hidden;

        var delta = currentValue - previousValue;
        if (delta <= 0d)
            return FoodRestoreHudDecision.Hidden;

        // 原版 doneEating 对 (int)(Stamina - num) 的截断行为：微小增量截断为 0 时仍弹 "+0"。
        return new FoodRestoreHudDecision(true, (int)delta);
    }
}
