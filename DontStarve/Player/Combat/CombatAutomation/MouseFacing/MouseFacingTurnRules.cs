#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation.MouseFacing;

/// <summary>
/// 跟随鼠标转身的纯规则裁决（可单测）：起手/完成回调触发时，是否允许朝鼠标转身。
/// 输入全部为预提取布尔，不引用游戏类型。
/// 「走路不转」由触发源保证——本规则只在起手（MeleeWeapon.setFarmerAnimating /
/// Farmer.BeginUsingTool）或动作完成（Farmer.canMoveNow）的那一刻被询问；走路/站立
/// 不会产生该回调。
/// 手持门（2026-11-17 工具加入；同日弹弓不特判）：手持任意 Tool（近战武器/工具/弹弓）
/// 均允许，空手排除——弹弓不写类型特判，拉弓瞄准转身由原版负责，两边方向一致。
/// </summary>
internal static class MouseFacingTurnRules
{
    internal static bool ShouldTurn(
        bool enabled,
        bool isLocalPlayer,
        bool interactionSuspended,
        bool worldLevelBlocked,
        bool holdingTool
    )
    {
        return enabled
            && isLocalPlayer
            && !interactionSuspended
            && !worldLevelBlocked
            && holdingTool;
    }
}
