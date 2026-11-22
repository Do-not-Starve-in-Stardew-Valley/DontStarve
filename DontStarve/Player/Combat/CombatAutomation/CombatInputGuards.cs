#nullable enable

using DontStarve.Player.Stats.Sanity.Visual;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Combat.CombatAutomation;

/// <summary>
/// 共享玩家输入守卫（2026-11-13 从 SanityIdlePanicLoopService 原样提取，行为零变化）：
/// 低理智惊恐循环与战斗自动化（长按挥舞放行、弹弓档位驱动，后续自动特技/索敌驱动）
/// 共用同一套「菜单/暂停/失焦挂起」与「操作中打断」口径。两个提取方法的语义与返回值
/// 逐字保持提取时状态；修改任何一侧判定前必须同时复核全部调用方（惊恐循环的打断原因
/// 字符串同时是日志契约与 SanityIdlePanicInterruptionPolicy 的释放白名单输入）。
/// </summary>
internal static class CombatInputGuards
{
    /// <summary>
    /// 挂起判定：存在可交互菜单（背包/设置/确认对话）、游戏暂停或窗口失焦时，调用方整体
    /// 冻结——相位与待机计时都不推进，解除后原地恢复（用户口径：暂停后循环也暂停）。
    /// 与打断不同：打断会取消循环并重置计时，挂起只冻结现场。
    /// </summary>
    internal static bool IsGameInteractionSuspended()
    {
        return Game1.activeClickableMenu is not null
            || Game1.paused
            || (Game1.game1 is not null && !Game1.game1.IsActiveNoOverlay);
    }

    /// <summary>
    /// 惊恐循环口径的「操作中」判定。按用户口径只保留会改变角色动画状态的操作信号：
    /// 移动、使用工具/弹弓、进食、传送与画面过渡、事件/小游戏、骑马、坐家具、游泳、
    /// 引擎控制锁（freezePause/CanMove）。菜单开关、窗口失焦走「挂起」（见
    /// <see cref="IsGameInteractionSuspended"/>），不在此打断；受击无敌与原始按键输入
    /// 同样不算操作。基础动画/foreign single-animation 姿势不在判定内：原版
    /// IsPlayingBasicAnimation 的静立判定要求呼吸帧，不能当空闲信号。
    /// 返回 null = 无打断；否则为打断原因字符串（历史日志契约，勿改字面值）。
    /// </summary>
    internal static string? DescribePanicInterruption(Farmer player)
    {
        if (!Context.IsWorldReady)
            return "world-not-ready";
        if (Game1.eventUp || Game1.CurrentEvent is not null || Game1.farmEvent is not null)
            return "event-active";
        if (Game1.currentMinigame is not null)
            return "minigame";
        if (Game1.isWarping)
            return "warping";
        if (Game1.fadeToBlack)
            return "fade-to-black";
        if (Game1.killScreen)
            return "kill-screen";
        if (player.UsingTool)
            return SanityIdlePanicInterruptionPolicy.UsingToolReason;
        if (player.usingSlingshot)
            return SanityIdlePanicInterruptionPolicy.UsingSlingshotReason;
        if (player.isEating)
            return SanityIdlePanicInterruptionPolicy.EatingReason;
        if (player.freezePause > 0)
            return $"freezePause={player.freezePause}";
        // 原版 performPlayerEmote 会设 noMovementPause=200，Farmer.Update 在此期间强制
        // CanMove=false——这是我们自己触发的受惊表情造成的自我锁，不能当"操作"打断，
        // 否则受惊段活不过一帧（实机日志：phase=startled 与 interrupted 同秒、跳跃永
        // 远来不及展示）。表情动画期间豁免该判定；表情结束后 CanMove 自动恢复。
        if (!player.CanMove && !player.isEmoteAnimating)
            return "cannot-move";
        if (player.isMoving() || player.movementDirections.Count > 0)
            return "moving";
        if (player.isRidingHorse())
            return "riding-horse";
        if (player.IsSitting())
            return "sitting";
        if (player.swimming.Value)
            return "swimming";
        return null;
    }

    /// <summary>
    /// 世界级阻断（事件/小游戏/传送/画面过渡/死亡屏）的廉价布尔守卫，供不需要原因字符串
    /// 分配的战斗自动化驱动（弹弓档位等热路径）复用。分支与
    /// <see cref="DescribePanicInterruption"/> 的对应判定同源；扩展任一侧时两处必须同步。
    /// </summary>
    internal static bool HasWorldLevelBlocker()
    {
        return Game1.eventUp
            || Game1.farmEvent is not null
            || Game1.currentMinigame is not null
            || Game1.isWarping
            || Game1.fadeToBlack
            || Game1.killScreen;
    }
}
