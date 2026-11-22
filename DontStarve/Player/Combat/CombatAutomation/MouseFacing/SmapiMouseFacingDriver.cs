#nullable enable

using System;
using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.MouseFacing;

/// <summary>
/// 跟随鼠标转身执行器（设计稿 5.4；2026-11-15 用户口径第三次收敛；2026-11-17 用户拍板
/// 「真正快速点击不转」口径作废）：**两个事件式触发源，不轮询**。
/// 触发源一 = <see cref="SmapiMouseFacingStartTurnPatch"/> 双挂点（2026-11-17 新增）：
/// 近战 prefix <c>MeleeWeapon.setFarmerAnimating</c> + 工具 prefix <c>Farmer.BeginUsingTool</c>
/// ——每次挥舞/用工具起手（朝向读取前）先朝鼠标转一次。原版起手转向在鼠标离玩家 &gt;1 格
/// 时会退化为面前格（Character.GetToolLocation），旧完成式转身必然滞后一刀、第一刀永远
/// 旧朝向（2026-11-17 用户实报）；起手转身让每一刀/每次工具使用（含第一下与快速点击）
/// 起手即朝鼠标，且工具超 1 格点击的作用格（FireTool 释放时按当前朝向取面前格，
/// Game1.cs:11083）随之自动朝向鼠标，无错位。鼠标不活跃（手柄/光标隐藏）时不转，避免
/// 手柄被陈旧鼠标带偏。
/// 触发源二 = <see cref="SmapiMouseFacingTurnPatch"/> postfix <c>Farmer.canMoveNow</c>
/// （public static，武器挥舞/特技/工具动作动画最后一帧的 <c>behaviorAtEndOfFrame</c> 回调，
/// 内部设 CanMove=true/UsingTool=false，FarmerSprite.cs:1265 等；工具动作段 160/168/176/184
/// 系列同样以此收尾）：每挥完一刀/每次用完工具再对齐一次鼠标。
/// - 走路/站立不产生任一回调 → 完全不转（用户 2026-11-15：「走路不应该跟随鼠标」）；
/// - 快速点击（单击）每刀起手 → 转（2026-11-17 用户拍板作废旧口径「真正快速点击不转」：
///   起手瞬间无法区分单击与长按第一刀，二者互斥，按最新指令取「第一刀即朝鼠标」）；
/// - 特技/格挡动作也走 setFarmerAnimating/canMoveNow（用户 2026-11-15：不专门排除）；
/// - 工具加入（用户 2026-11-17 拍板）：近战 + 全部工具（斧/锄/镐/水壶/剪/奶桶/钓鱼竿等）；
///   弹弓不特判（拉弓瞄准转身由原版负责，两边方向一致）；空手无动作回调天然不转。
/// 条件 = <see cref="MouseFacingTurnRules.ShouldTurn"/>：开关 + 本地玩家 + 输入守卫 +
/// 手持工具（CurrentTool is Tool；近战武器与弹弓均为 Tool 子类天然覆盖，
/// 不写任何类型特判）。
/// 时序安全：起手转身发生在挥舞动画读朝向之前（CanMove 尚未改变属原版语义）；完成转身
/// 发生在动画完成之后（CanMove=true、UsingTool=false）——绝不掉进挥舞中段改朝向，
/// 2026-11-15 前两轮实机卡死的根因即挥舞中转身；两触发源均不在动画帧间轮询。
/// 坐标口径：鼠标世界 = <c>Game1.getMousePosition(ui_scale: false)</c> + <c>Game1.viewport</c>
/// （原版先例 Farmer.cs:4669）；分区原点 = 玩家所在格中心（决议 9：格四角对角线分区）。
/// 频率 = 每次起手/完成各一次，无需节流；无分配、无热路径日志。
/// </summary>
internal sealed class SmapiMouseFacingDriver
{
    private readonly CombatAutomationConfigSnapshot snapshot;

    internal SmapiMouseFacingDriver(CombatAutomationConfigSnapshot snapshot)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>
    /// 挥舞/用工具起手时（setFarmerAnimating + BeginUsingTool prefix）朝鼠标转身一次，
    /// 让本次动作的朝向（近战动画与伤害判定、工具精灵朝向）直接跟随鼠标
    /// （修复第一下旧朝向与滞后一刀）。
    /// 鼠标不活跃（手柄操作/光标隐藏）时直接不转：起手转身只认鼠标瞄准。
    /// 调用方（<see cref="SmapiMouseFacingStartTurnPatch"/>）已做异常兜底。
    /// </summary>
    internal void TryTurnOnAttackStarting(Farmer? player)
    {
        // 与原版 Game1.cs:11633 同源判据：光标隐藏 = 当前帧非鼠标瞄准（手柄）。
        if (!Game1.wasMouseVisibleThisFrame)
            return;

        TryTurnTowardsMouse(player);
    }

    /// <summary>
    /// 一次完整攻击动作完成时（canMoveNow postfix）尝试朝鼠标转身一次。
    /// 调用方（<see cref="SmapiMouseFacingTurnPatch"/>）已做异常兜底。
    /// </summary>
    internal void TryTurnOnAttackCompleted(Farmer? player)
    {
        TryTurnTowardsMouse(player);
    }

    /// <summary>两触发源共享的转身体：规则裁决 + 坐标口径 + 分区映射，均与既有部署一致。</summary>
    private void TryTurnTowardsMouse(Farmer? player)
    {
        if (player is null || Game1.player is null)
            return;

        if (
            !MouseFacingTurnRules.ShouldTurn(
                snapshot.MouseFacingEnabled,
                ReferenceEquals(player, Game1.player),
                CombatInputGuards.IsGameInteractionSuspended(),
                CombatInputGuards.HasWorldLevelBlocker(),
                // 手持门（2026-11-17 工具加入；同日弹弓不特判）：手持任意 Tool 均允许
                // （近战武器/工具/弹弓）；弹弓不写类型特判，拉弓瞄准转身由原版负责，两边
                // 方向一致；空手（null）排除。
                player.CurrentTool is Tool
            )
        )
        {
            return;
        }

        var mouseUiPosition = Game1.getMousePosition(ui_scale: false);
        int mouseWorldX = mouseUiPosition.X + Game1.viewport.X;
        int mouseWorldY = mouseUiPosition.Y + Game1.viewport.Y;

        // 玩家所在格中心（决议 9 分区锚点）：TilePoint 为站立格坐标（Character.TilePoint）。
        var tilePoint = player.TilePoint;
        int tileCenterX = tilePoint.X * Game1.tileSize + Game1.tileSize / 2;
        int tileCenterY = tilePoint.Y * Game1.tileSize + Game1.tileSize / 2;

        int direction = MouseFacingMapper.MapDirection(
            mouseWorldX - tileCenterX,
            mouseWorldY - tileCenterY
        );

        if (direction == player.FacingDirection)
            return;

        player.faceDirection(direction);
    }
}
