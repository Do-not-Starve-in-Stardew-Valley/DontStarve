#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// 「饥荒的光照氛围」（2026-11-17 用户决议）的场景计划矩阵。黑暗关闭时的一切目标色/上限
/// 由本矩阵给出；黑暗开启时的所有「压到全黑」计划（NightThreeSeconds/MineNight/FullDark）
/// 仍沿用既有黑暗语义与时长，仅黄昏段被本矩阵接管。
/// <list type="bullet">
/// <item>白天（地表/室内）：DST ambientlighting 默认季 (255,230,158)、春季 (255,244,213)，即时切换。</item>
/// <item>黄昏（startDarkTime 起 3 秒）：DST 黄昏灰 (150,150,150)/春季 (171,146,147)。黑暗关闭时
/// 该计划在真黑时刻不再改变，整夜保持黄昏值（用户决议 2/8）。</item>
/// <item>矿井/火山（用户拍板「整个矿井等效漏光柱铺满」）：白天 0.85×(180,195,150) 即时、
/// 黄昏 0.6×(91,164,255) 4 秒（cavelight dusk time=4 代码口径）、夜晚黑暗开=全黑/黑暗关=
/// 保持黄昏漏光态。恒全黑层（31-39/骷髅 1000+/危险骷髅/77377）黑暗开=恒全黑、黑暗关=黄昏漏光态。</item>
/// <item>室内默认走室外相位值（白天暖/黄昏灰/夜晚停黄昏）；规则标记 FullDark 的室内黑暗开时
/// 仍恒全黑；诊所（Unchanged）恒定不黑；祝尼魔保护地点不进氛围。</item>
/// </list>
/// 满月态（cavelight 0.6×(131,194,255)）留接口不做——星露谷无月相系统。
/// </summary>
internal static class DontStarveLightAmbiencePolicy
{
    /// <summary>地表/室内黄昏过渡：用户指定 3 秒。</summary>
    internal const double OutdoorDuskTransitionSeconds = 3d;

    /// <summary>矿井黄昏过渡：DST cavelight 黄昏 lerp time = 4 秒（代码口径）。</summary>
    internal const double MineDuskTransitionSeconds = 4d;

    internal static NaturalDarknessScenePlan Resolve(
        NaturalDarknessLocationInput input,
        bool darknessEnabled
    )
    {
        if (input.IsJunimoBlessingProtected)
            return NaturalDarknessScenePlan.None;

        if (input.IsMineShaft || input.IsVolcanoDungeon)
            return ResolveMine(input, darknessEnabled);

        if (input.IsOutdoors)
            return ResolveSurface(input, darknessEnabled);

        return ResolveInterior(input, darknessEnabled);
    }

    private static NaturalDarknessScenePlan ResolveSurface(
        NaturalDarknessLocationInput input,
        bool darknessEnabled
    )
    {
        if (
            darknessEnabled
            && IsAtOrAfter(input.TimeOfDay, input.TrulyDarkTime)
        )
        {
            return new NaturalDarknessScenePlan(
                NaturalDarknessPhase.NightThreeSeconds,
                NaturalDarknessTargetColor.White,
                1d,
                NaturalDarknessTransitionPolicy.NightTransitionDurationSeconds
            );
        }

        if (IsAtOrAfter(input.TimeOfDay, input.StartingToGetDarkTime))
        {
            return DuskPlan();
        }

        return DayPlan(input.IsSpringSeason);
    }

    private static NaturalDarknessScenePlan ResolveInterior(
        NaturalDarknessLocationInput input,
        bool darknessEnabled
    )
    {
        if (input.Profile == NaturalDarknessProfile.FullDark)
        {
            return darknessEnabled
                ? FullDarkPlan()
                : DuskPlan();
        }

        if (
            darknessEnabled
            && input.Profile == NaturalDarknessProfile.NightThreeSeconds
            && IsAtOrAfter(input.TimeOfDay, input.TrulyDarkTime)
        )
        {
            return new NaturalDarknessScenePlan(
                NaturalDarknessPhase.NightThreeSeconds,
                NaturalDarknessTargetColor.White,
                1d,
                NaturalDarknessTransitionPolicy.NightTransitionDurationSeconds
            );
        }

        if (IsAtOrAfter(input.TimeOfDay, input.StartingToGetDarkTime))
        {
            return DuskPlan();
        }

        return DayPlan(input.IsSpringSeason);
    }

    private static NaturalDarknessScenePlan ResolveMine(
        NaturalDarknessLocationInput input,
        bool darknessEnabled
    )
    {
        var isFullDarkLayer =
            input.MineLevel == 77377
            || (input.IsSkullCavern && input.IsDangerousSkullCavern)
            || (input.IsSkullCavern && input.MineLevel >= 1000)
            || (!input.IsSkullCavern && input.MineLevel is >= 31 and <= 39);
        if (isFullDarkLayer)
        {
            return darknessEnabled
                ? FullDarkPlan()
                : MineDuskPlan();
        }

        if (IsAtOrAfter(input.TimeOfDay, input.TrulyDarkTime))
        {
            return darknessEnabled
                ? new NaturalDarknessScenePlan(
                    NaturalDarknessPhase.MineNight,
                    NaturalDarknessTargetColor.White,
                    1d,
                    NaturalDarknessTransitionPolicy.MineNightTransitionDurationSeconds
                )
                : MineDuskPlan();
        }

        if (IsAtOrAfter(input.TimeOfDay, input.StartingToGetDarkTime))
        {
            return MineDuskPlan();
        }

        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.MineDay,
            DontStarveAmbienceTargets.FromRetained(
                DontStarveAmbienceTargets.MineDayRetainedR,
                DontStarveAmbienceTargets.MineDayRetainedG,
                DontStarveAmbienceTargets.MineDayRetainedB
            ),
            1d,
            0d
        );
    }

    private static NaturalDarknessScenePlan DuskPlan()
    {
        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.AmbientDusk,
            DontStarveAmbienceTargets.FromRetained(
                DontStarveAmbienceTargets.DuskRetainedDefaultR,
                DontStarveAmbienceTargets.DuskRetainedDefaultG,
                DontStarveAmbienceTargets.DuskRetainedDefaultB
            ),
            1d,
            OutdoorDuskTransitionSeconds
        );
    }

    private static NaturalDarknessScenePlan DayPlan(bool isSpringSeason)
    {
        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.AmbientDay,
            DontStarveAmbienceTargets.FromRetained(
                isSpringSeason
                    ? DontStarveAmbienceTargets.DayRetainedSpringR
                    : DontStarveAmbienceTargets.DayRetainedDefaultR,
                isSpringSeason
                    ? DontStarveAmbienceTargets.DayRetainedSpringG
                    : DontStarveAmbienceTargets.DayRetainedDefaultG,
                isSpringSeason
                    ? DontStarveAmbienceTargets.DayRetainedSpringB
                    : DontStarveAmbienceTargets.DayRetainedDefaultB
            ),
            1d,
            0d
        );
    }

    private static NaturalDarknessScenePlan MineDuskPlan()
    {
        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.MineDusk,
            DontStarveAmbienceTargets.FromRetained(
                DontStarveAmbienceTargets.MineDuskRetainedR,
                DontStarveAmbienceTargets.MineDuskRetainedG,
                DontStarveAmbienceTargets.MineDuskRetainedB
            ),
            1d,
            MineDuskTransitionSeconds
        );
    }

    private static NaturalDarknessScenePlan FullDarkPlan()
    {
        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.FullDark,
            NaturalDarknessTargetColor.White,
            1d,
            0d
        );
    }

    private static bool IsAtOrAfter(int timeOfDay, int boundary)
    {
        return boundary >= 0 && timeOfDay >= boundary;
    }
}
