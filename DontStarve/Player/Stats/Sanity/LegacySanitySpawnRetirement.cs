#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

internal readonly record struct LegacySanityResidualCleanupDecision(
    bool CanRemove,
    string Reason
);

/// <summary>
/// 六个旧 Spawn 的退场契约。旧类和 save key 保留作回滚/诊断证据，但不再进入
/// 时间调度；旧对象没有可靠 ownership 标识，所以本阶段一律拒绝猜测式清理。
/// </summary>
internal static class LegacySanitySpawnRetirement
{
    internal const string SchedulingStatus = "legacy-spawn-scheduling-retired";
    internal const string ReplacementStatus = "new-hallucinations-not-implemented";
    internal const string ResidualCleanupReason =
        "legacy-spawn-ownership-unavailable";

    private static readonly IReadOnlyList<string> behaviorTypeNames =
        Array.AsReadOnly(
            new[]
            {
                "SpawnMrSkitts",
                "SpawnDarkHand",
                "SpawnDarkWatcher",
                "SpawnEye",
                "SpawnCreeperFear",
                "SpawnTerrifyingSharpBeak",
            }
        );

    private static readonly IReadOnlyList<string> saveKeys = Array.AsReadOnly(
        new[]
        {
            "DontStarve.Sanity.SpawnMrSkitts",
            "DontStarve.Sanity.SpawnDarkHand",
            "DontStarve.Sanity.SpawnDarkWatcher",
            "DontStarve.Sanity.SpawnEye",
            "DontStarve.Sanity.SpawnCreeperFear",
            "DontStarve.Sanity.SpawnTerrifyingSharpBeak",
        }
    );

    internal static IReadOnlyList<string> BehaviorTypeNames => behaviorTypeNames;

    internal static IReadOnlyList<string> SaveKeys => saveKeys;

    internal static LegacySanityResidualCleanupDecision EvaluateResidualCleanup(
        string? ownershipMarker
    )
    {
        // 旧对象从未写入稳定 ownership；任意外观、类型、位置或临时字符串都不能当证明。
        _ = ownershipMarker;
        return new LegacySanityResidualCleanupDecision(
            false,
            ResidualCleanupReason
        );
    }
}
