#nullable enable

using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.AutoSwing;

/// <summary>
/// 弹弓自动发射驱动（设计稿 4.3/3f；2026-11-22 力度滑轮化）：SlingshotAutoRelease 开关
/// 开启且玩家处于拉弓姿势（<c>player.usingSlingshot</c>）且使用键按住中时，每 3 tick 读
/// 一次蓄力，达到 SlingshotPower 力度阈值即程序化发射。发射入口 =
/// <c>Slingshot.DoFunction</c>（与原版松手链 <c>onRelease → DoFunction</c> 同形：
/// PerformFire + finish；PerformFire 的 x/y 参数不参与瞄准，瞄准仍由
/// <c>updateAimPos</c> 跟随玩家鼠标——本阶段不做瞄准锁，那是索敌阶段的事）。
/// 发射后原版 doFinish 结束拉弓姿势，玩家仍按住时原版工具连用链自动重新拉弓
/// （弹弓不是 MeleeWeapon，Game1.cs:11714 门天然放行），形成「发射 → ~150ms 内重新拉弓 →
/// 再发射」循环，无需本驱动额外干预朝向或移动。
/// canPlaySound 门：PerformFire 只在「满蓄力提示音已播」时真正出弹（Slingshot.cs:137/444），
/// 力度 &lt;100% 时在满蓄力前发射会静默失败——发射前显式清位（公开字段直写，无反射）；
/// 100% 满蓄力时提示音已由原版 tickUpdate 播过，清位为幂等兜底。
/// </summary>
internal sealed class SmapiSlingshotAutoReleaseDriver
{
    // 设计稿口径：每 3 tick（~50ms）查一次蓄力；发射节奏本身由力度阈值决定，不受此影响。
    private const int ChargeCheckIntervalTicks = 3;

    private readonly CombatAutomationConfigSnapshot snapshot;
    private int ticksSinceLastCheck;

    internal SmapiSlingshotAutoReleaseDriver(CombatAutomationConfigSnapshot snapshot)
    {
        this.snapshot = snapshot;
    }

    internal void Tick()
    {
        if (!snapshot.SlingshotAutoReleaseEnabled)
            return;

        // 节流：非检查 tick 只递增计数器。
        ticksSinceLastCheck++;
        if (ticksSinceLastCheck < ChargeCheckIntervalTicks)
            return;
        ticksSinceLastCheck = 0;

        var player = Game1.player;
        if (player is null || !player.IsLocalPlayer || !player.usingSlingshot)
            return;

        // 输入守卫与长按挥舞同源：挂起（菜单/暂停/失焦）与世界级阻断期间不发射。
        if (
            CombatInputGuards.IsGameInteractionSuspended()
            || CombatInputGuards.HasWorldLevelBlocker()
        )
        {
            return;
        }

        // 按住中判定与长按挥舞同源（原版松开归零）；松手时原版 onRelease 自然接管发射。
        if (Game1.mouseClickPolling <= 0)
            return;

        if (player.CurrentTool is not Slingshot slingshot)
            return;

        if (
            !SlingshotAutoReleaseRules.ShouldRelease(
                snapshot.SlingshotReleasePowerPercent,
                slingshot.GetSlingshotChargeTime(),
                slingshot.GetBackArmDistance(player)
            )
        )
        {
            return;
        }

        // x/y 走 PerformFire 时完全不参与弹道计算（瞄准来自 updateAimPos），传玩家位置即可。
        slingshot.canPlaySound = false;
        slingshot.DoFunction(
            player.currentLocation,
            (int)player.Position.X,
            (int)player.Position.Y,
            1,
            player
        );
    }
}
