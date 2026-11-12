#nullable enable

using System;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 决议 2 特殊公式接缝 v1：食物血量效果优先级为 特殊公式 &gt; JSON 条目 &gt; 默认。
/// 健康侧当前没有特殊公式物品（仓库先例：理智侧星果走 StarfruitFoodRules 专属公式、
/// 普通果酒才听 JSON 表），因此本接缝始终未命中；未来新增物品专属公式时在此接入，
/// 进食结算与后续 tooltip 取值都必须走这一处，保持「tooltip 与结算说同一句话」。
/// 参数收 SObject 属游戏边界类型，故本文件不参与纯规则单测。
/// </summary>
internal static class HealthFoodOverrideRules
{
    internal static bool TryResolveSpecial(SObject? food, out double value)
    {
        value = 0;
        _ = food;
        return false;
    }
}
