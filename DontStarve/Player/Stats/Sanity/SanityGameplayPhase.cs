#nullable enable

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// 影怪玩法使用的互斥 Sanity 阶段。它与可重叠的表现/事件 tier 分开维护，
/// 只表达当前百分比落入哪个玩法区间。
/// </summary>
internal enum SanityGameplayPhase
{
    Above50,
    Harmless50,
    Hostile15,
    Hostile10,
    ThresholdBoundary,
}

internal static class SanityGameplayPhaseCatalog
{
    internal static bool TryResolve(
        double sanityRatio,
        out SanityGameplayPhase phase
    )
    {
        if (
            !double.IsFinite(sanityRatio)
            || sanityRatio < 0
            || sanityRatio > 1
        )
        {
            phase = default;
            return false;
        }

        // 合同只使用严格不等式；正好落在 50%、15% 或 10% 时不进入任一低 Sanity 阶段。
        phase = sanityRatio switch
        {
            > 0.5d => SanityGameplayPhase.Above50,
            > 0.15d and < 0.5d => SanityGameplayPhase.Harmless50,
            > 0.1d and < 0.15d => SanityGameplayPhase.Hostile15,
            < 0.1d => SanityGameplayPhase.Hostile10,
            _ => SanityGameplayPhase.ThresholdBoundary,
        };
        return true;
    }

    internal static SanityShadowPoolTier ToShadowPoolTier(
        SanityGameplayPhase phase
    )
    {
        return phase switch
        {
            SanityGameplayPhase.Above50 => SanityShadowPoolTier.Inactive,
            SanityGameplayPhase.Harmless50 => SanityShadowPoolTier.Harmless50,
            SanityGameplayPhase.Hostile15 => SanityShadowPoolTier.Hostile15,
            SanityGameplayPhase.Hostile10 => SanityShadowPoolTier.Hostile10,
            SanityGameplayPhase.ThresholdBoundary => SanityShadowPoolTier.Inactive,
            _ => SanityShadowPoolTier.Inactive,
        };
    }
}
