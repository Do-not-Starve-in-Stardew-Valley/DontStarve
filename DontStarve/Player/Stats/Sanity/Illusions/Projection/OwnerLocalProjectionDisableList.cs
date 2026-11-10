#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// 【占位素材停用门 2026-11-10】四种 owner-local 无害幻觉生物的显式停用清单（单一事实来源）。
///
/// 背景：这四种生物（眼睛 / 暗影观察者 / 暗影之手 / Sk先生）当前使用占位素材。设计师对照
/// 设计稿盘点后决定：素材定稿前整体停用生成与交互，避免玩家在正式素材落地前反复看到占位
/// 表现。本清单不删除任何代码、不触碰 SanityTierCatalog 状态机、不触碰任何存档 key。
///
/// 停用方式：物种不进入注册 seam（<see cref="SmapiHarmlessProjectionHost.RegisterSpecies"/>）。
/// 注册是这四种生物唯一的生成入口——不注册即不会生成投影、不会挂载物种行为；暗影之手的
/// 点火源 / 机器交互（SmapiDarkHandFireThiefService / SmapiDarkHandHarassmentService /
/// SmapiDarkHandThiefService / SmapiDarkHandLeaseCoordinatorService）没有独立的事件订阅入口，
/// 全部由 DarkHandProjectionBehavior 驱动，因此随本清单一并封住，不存在绕过投影的触发路径。
///
/// 明确不受影响的相邻功能（新 AI 不要在这里"顺手"处理）：
/// - 低理智音频（whispers / ambience）与暗影 creature SFX：不经过本 seam；
/// - vignette 视觉与待机惊恐循环（SanityIdlePanicLoop）：与物种注册无关；
/// - SanityTierCatalog tier 状态机与所有存档 key：保持原样；
/// - creeper-fear / terrorbeak（50% 危险档影怪）：走 RegisterShadowCreatureSpecies 独立 seam，
///   不在本清单管辖范围内。
///
/// 恢复方法（素材定稿后）：从 <see cref="DisabledSpeciesIds"/> 删除对应物种 ID，重新构建部署，
/// 并按 文档/当前在做计划/san值系统当前缺少的内容.md 条目 c) 复核恢复清单
/// （含暗影之手目标行 JSON 与觅食替换行 JSON 的重新启用）。恢复后务必实机确认生成与交互。
/// </summary>
internal static class OwnerLocalProjectionDisableList
{
    /// <summary>命中清单的物种在注册 seam 被拒绝时使用的统一 reason（含日志）。</summary>
    internal const string DisabledReason =
        "projection.species-disabled-pending-placeholder-assets";

    private static readonly HashSet<string> disabledSpeciesIds =
        new(StringComparer.Ordinal)
        {
            EyesProjectionContract.SpeciesId,
            DarkWatcherProjectionContract.SpeciesId,
            DarkHandProjectionContract.SpeciesId,
            MrSkittsProjectionContract.SpeciesId,
        };

    /// <summary>
    /// 当前被停用的 owner-local 物种 ID。物种 ID 与各 ProjectionContract 的 SpeciesId 常量
    /// 保持同一来源，避免字符串漂移；素材定稿后从这里删除对应条目即恢复注册。
    /// </summary>
    internal static IReadOnlyCollection<string> DisabledSpeciesIds => disabledSpeciesIds;

    internal static bool IsDisabled(string speciesId)
    {
        return disabledSpeciesIds.Contains(speciesId);
    }
}
