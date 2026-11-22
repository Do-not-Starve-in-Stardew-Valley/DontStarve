#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// 【2026-11-17 用户决议】「饥荒的光照氛围」的目标光照值，全部取自饥荒脚本代码口径：
/// <list type="bullet">
/// <item>地表环境光：DST ambientlighting.lua（经 TheSim:SetAmbientColour 进引擎）。白天默认季
/// (255,230,158)、春季 (255,244,213)；黄昏默认季灰 (150,150,150)、春季 (171,146,147)；夜晚
/// (0,0,0)；洞穴 (0,0,0) 恒定（因此矿井/火山不使用洞穴环境底光）。</item>
/// <item>矿井/火山（用户拍板「整个矿井等效漏光柱铺满」）：DST cavelight.lua 的漏光柱参数，
/// 强度 × 颜色直接作为环境光——白天 0.85×(180,195,150)、黄昏 0.6×(91,164,255)、夜晚 0（黑暗
/// 开关管）、满月 0.6×(131,194,255)（月相系统未实现，仅冻结数值留接口）。</item>
/// </list>
/// 星露谷光照是减色制（lightmap 减色 c，对白参考保留 1−c²），因此「保留值」（DST 环境光的
/// 每通道乘量）换算为减色 c = sqrt(1−保留)。所有保留值冻结于此，不做运行期猜测。
/// </summary>
internal static class DontStarveAmbienceTargets
{
    // —— 地表白天（保留值 = DST ambient 颜色通道） ——
    internal const double DayRetainedDefaultR = 1d;
    internal const double DayRetainedDefaultG = 230d / 255d;
    internal const double DayRetainedDefaultB = 158d / 255d;
    internal const double DayRetainedSpringR = 1d;
    internal const double DayRetainedSpringG = 244d / 255d;
    internal const double DayRetainedSpringB = 213d / 255d;

    // —— 地表黄昏 ——
    internal const double DuskRetainedDefaultR = 150d / 255d;
    internal const double DuskRetainedDefaultG = 150d / 255d;
    internal const double DuskRetainedDefaultB = 150d / 255d;
    internal const double DuskRetainedSpringR = 171d / 255d;
    internal const double DuskRetainedSpringG = 146d / 255d;
    internal const double DuskRetainedSpringB = 147d / 255d;

    // —— 矿井/火山（cavelight 强度 × 颜色） ——
    internal const double MineDayRetainedR = 0.85d * (180d / 255d);
    internal const double MineDayRetainedG = 0.85d * (195d / 255d);
    internal const double MineDayRetainedB = 0.85d * (150d / 255d);
    internal const double MineDuskRetainedR = 0.6d * (91d / 255d);
    internal const double MineDuskRetainedG = 0.6d * (164d / 255d);
    internal const double MineDuskRetainedB = 0.6d * (255d / 255d);

    /// <summary>满月夜漏光柱（0.6×(131,194,255)）。月相系统落地前不进任何计划，仅冻结数值。</summary>
    internal const double MineFullMoonRetainedR = 0.6d * (131d / 255d);
    internal const double MineFullMoonRetainedG = 0.6d * (194d / 255d);
    internal const double MineFullMoonRetainedB = 0.6d * (255d / 255d);

    /// <summary>
    /// DST 黄昏/黑暗开关关闭时的整夜保持值即黄昏值本身；这里只提供换算，不做第二套数值。
    /// </summary>
    internal static NaturalDarknessTargetColor FromRetained(
        double retainedR,
        double retainedG,
        double retainedB
    )
    {
        return new NaturalDarknessTargetColor(
            DarknessFromRetained(retainedR),
            DarknessFromRetained(retainedG),
            DarknessFromRetained(retainedB)
        );
    }

    /// <summary>白参考保留 1−c² ⇒ c = sqrt(1−保留)。保留值越界按边界钳制，绝不产生 NaN。</summary>
    private static double DarknessFromRetained(double retained)
    {
        if (!double.IsFinite(retained))
            return 1d;
        var clamped = Math.Clamp(retained, 0d, 1d);
        return Math.Sqrt(1d - clamped);
    }
}

/// <summary>
/// 氛围/黑暗计划的目标减色（每通道 0..1，1 = 全黑）。纯逻辑层不引用 XNA；
/// White(1,1,1) 即既有黑暗路径的目标，保证氛围关闭时逐字节同旧行为。
/// </summary>
internal readonly record struct NaturalDarknessTargetColor(
    double R,
    double G,
    double B
)
{
    internal static NaturalDarknessTargetColor White { get; } = new(1d, 1d, 1d);

    internal bool IsWhite =>
        R >= 1d && G >= 1d && B >= 1d;

    internal bool IsValid =>
        double.IsFinite(R)
        && double.IsFinite(G)
        && double.IsFinite(B)
        && R is >= 0d and <= 1d
        && G is >= 0d and <= 1d
        && B is >= 0d and <= 1d;
}
