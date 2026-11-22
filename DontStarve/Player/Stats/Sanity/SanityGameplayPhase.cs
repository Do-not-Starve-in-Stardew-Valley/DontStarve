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

        // 边界口径对齐 DST 影怪种群（shadowcreaturespawner）：>50% 清场；>10% 一只档；
        // 其余两只档——恰好落在 50%、15% 或 10% 时一律归入更低（更疯）档。
        // 恰好 10% 归 Hostile15 而非 Hostile10：恐怖喙概率在 DST 本体是严格 <10%，
        // 恰好 10% 只出爬行怪，与 Terrorbeak tier 的严格进入保持同构。
        phase = sanityRatio switch
        {
            > 0.5d => SanityGameplayPhase.Above50,
            > 0.15d => SanityGameplayPhase.Harmless50,
            >= 0.1d => SanityGameplayPhase.Hostile15,
            _ => SanityGameplayPhase.Hostile10,
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
            _ => SanityShadowPoolTier.Inactive,
        };
    }
}
