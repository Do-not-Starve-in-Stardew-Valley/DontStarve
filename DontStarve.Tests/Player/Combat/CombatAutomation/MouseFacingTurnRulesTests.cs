using DontStarve.Player.Combat.CombatAutomation.MouseFacing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 跟随鼠标转身纯规则（2026-11-15 第三次收敛；2026-11-17 工具加入口径）：起手/完成回调
/// 触发时的裁决矩阵。
/// 「走路不转」由触发源（只在起手或完整动作完成时被询问）保证，本规则只裁决回调时刻
/// 是否允许转身。
/// 手持门语义（2026-11-17）：holdingTool = 手持任意 Tool（近战武器/工具/弹弓
/// 为 Tool 子类一并覆盖）；弹弓与空手由调用方预提取时排除。
/// </summary>
public sealed class MouseFacingTurnRulesTests
{
    [Fact]
    public void All_conditions_true_allows_turning()
    {
        Assert.True(
            MouseFacingTurnRules.ShouldTurn(
                enabled: true,
                isLocalPlayer: true,
                interactionSuspended: false,
                worldLevelBlocked: false,
                holdingTool: true
            )
        );
    }

    [Theory]
    [InlineData(false, true, false, false, true)] // 开关关
    [InlineData(true, false, false, false, true)] // 非本地玩家
    [InlineData(true, true, true, false, true)] // 菜单/暂停/失焦
    [InlineData(true, true, false, true, true)] // 事件/小游戏/传送/死亡屏
    [InlineData(true, true, false, false, false)] // 空手/弹弓
    public void Any_missing_condition_blocks_turning(
        bool enabled,
        bool isLocalPlayer,
        bool interactionSuspended,
        bool worldLevelBlocked,
        bool holdingTool
    )
    {
        Assert.False(
            MouseFacingTurnRules.ShouldTurn(
                enabled,
                isLocalPlayer,
                interactionSuspended,
                worldLevelBlocked,
                holdingTool
            )
        );
    }
}
