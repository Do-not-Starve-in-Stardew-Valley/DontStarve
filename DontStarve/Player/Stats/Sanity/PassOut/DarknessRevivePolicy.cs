#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Pure core of the vanilla post-death revive target resolution (医院直醒方案). The Stardew-side
/// wrapper (SmapiSanityTwoAmSpecialDeathService.ResolveDarknessReviveTarget) mirrors Game1.updatePause's
/// killScreen warp branch: the dying location's context ReviveLocations (GameStateQuery gated) wins,
/// otherwise the vanilla fallback warp target Hospital (20,12) is used. The resolved target is written
/// into lastSleepLocation/lastSleepPoint after PassOutNewDay so the next morning's
/// BedFurniture.ApplyWakeUpPosition lands the player on the same tile vanilla would have warped them to.
/// 注意：本文件被 DontStarve.Tests 以源码链接方式编译，而测试工程不引用游戏程序集；
/// 因此这里禁止出现 Stardew/MonoGame 类型（原 Xna Point 快捷属性已由 SMAPI 适配层就地构造）。
/// </summary>
internal static class DarknessRevivePolicy
{
    internal const string FallbackLocation = "Hospital";
    internal const int FallbackTileX = 20;
    internal const int FallbackTileY = 12;

    internal static ReviveTarget ResolveCore(
        IEnumerable<ReviveCandidate>? candidates,
        Func<string?, bool> conditionMatches
    )
    {
        ArgumentNullException.ThrowIfNull(conditionMatches);
        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (conditionMatches(candidate.Condition))
                    return new ReviveTarget(candidate.Location, candidate.TileX, candidate.TileY);
            }
        }
        return new ReviveTarget(FallbackLocation, FallbackTileX, FallbackTileY);
    }

    internal readonly record struct ReviveCandidate(
        string? Condition,
        string Location,
        int TileX,
        int TileY
    );

    internal readonly record struct ReviveTarget(
        string Location,
        int TileX,
        int TileY
    );
}
