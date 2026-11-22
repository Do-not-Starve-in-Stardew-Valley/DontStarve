#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation.AutoSwing;

/// <summary>
/// 弹弓自动发射力度纯规则（GMCM key=SlingshotAutoRelease 开关 + SlingshotPower 力度滑轮，
/// 用户 2026-11-22 裁决：旧 off/min/mid/max 档位改为 25–100% 连续滑轮，步长 5%）。
/// 事实基线（1.6.15 Slingshot.cs，与旧档位版一致）：
/// - 原版发射门 = <c>PerformFire</c> 内 <c>GetBackArmDistance &gt; 4</c>（Slingshot.cs:137）；
///   非 legacy 口径下 <c>GetBackArmDistance = (int)(20f × 蓄力 0..1)</c>，int 截断后需要
///   蓄力 ≥ 0.25 才能满足 &gt; 4 —— 滑轮下限 25% 直接贴住原版门槛，不引入独立魔法数字，
///   低于 25% 的力度在原版语义下拉不出有效发射（死区），因此不开放。
/// - 伤害只看弹药，力度只改发射时机；弹速由原版固定（15-20px/tick）。
/// </summary>
internal static class SlingshotAutoReleaseRules
{
    internal const int MinPowerPercent = 25;
    internal const int MaxPowerPercent = 100;
    internal const int VanillaFireGateBackArmDistance = 4;

    /// <summary>
    /// 是否达到力度发射阈值。chargeTime = <c>GetSlingshotChargeTime()</c>（0..1），
    /// backArmDistance = <c>GetBackArmDistance(player)</c>（已含 legacy/非 legacy 口径），
    /// powerPercent = SlingshotPower 滑轮值（25..100）。原版发射门（backArmDistance &gt; 4）
    /// 保留：力度 25% 时该门即发射条件，与旧 Min 档"贴住原版门槛即射"行为一致；
    /// 力度更高时阈值先于该门满足，与旧 Mid(50%)/Max(100%) 档行为一致。
    /// </summary>
    internal static bool ShouldRelease(
        int powerPercent,
        float chargeTime,
        int backArmDistance
    )
    {
        if (backArmDistance <= VanillaFireGateBackArmDistance)
            return false;

        return chargeTime >= powerPercent / 100f;
    }
}
