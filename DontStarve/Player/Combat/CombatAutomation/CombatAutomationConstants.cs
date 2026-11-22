#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation;

/// <summary>
/// 战斗自动化调参常量（设计稿 5.0）：单一调参入口，不进 GMCM（决议 2）。用户口头指定
/// 新数值时只改这里；原版几何常量同步收录并注明 1.6.15 反编译出处，便于逐行核对。
/// </summary>
internal static class CombatAutomationConstants
{
    /// <summary>
    /// 剑格挡触发距离（像素）：怪物碰撞框与玩家碰撞框的矩形最近距离 &lt; 该值时自动格挡
    /// （设计稿 4.2 / 决议 2）。用户口头指定数值时在此单点调整，不进 GMCM。
    /// 2026-11-15 实机平衡调整：8 → 16（原 8px 在快速怪物/影怪攻击框场景偶发漏挡；
    /// 16px 为试验值，若仍不足需再评估扫描频率与判定对象）。
    /// </summary>
    internal const int BlockTriggerDistancePx = 16;

    /// <summary>
    /// 自动特技最小按住时长（毫秒，2026-11-15 用户平衡调整）：快速点击（每次按下短于该值）
    /// 不触发自动特技；长按超过该值后的每次挥击才触发。原版对"长按"的定义是
    /// <c>mouseClickPolling &gt; 250</c> 才合成重触发（Game1.cs:12854），取同值对齐。
    /// 注意不能直接用 <c>mouseClickPolling</c> 判时长——原版在合成重触发时把它重置为 100
    /// （Game1.cs:12857），需由驱动每 tick 自行累计按住时长。
    /// </summary>
    internal const int AutoSpecialMinHoldMs = 250;

    /// <summary>自动特技扫描周期（tick）：每 4 tick（~66ms）扫一次怪物（设计稿 5.3）。</summary>
    internal const int SpecialScanIntervalTicks = 4;

    /// <summary>
    /// 锤砸地 AoE 边长（px）：原版 triggerClubFunction 的伤害盒 384×384
    /// （1.6.15 MeleeWeapon.cs:914，<c>damageMonster(new Rectangle(..., 384, 384), ...)</c>）。
    /// </summary>
    internal const int ClubSpecialAreaSizePx = 384;

    /// <summary>
    /// 锤砸地 AoE 半边长（px）：原版盒的 X 从玩家 Position.X、Y 从玩家碰撞框 Y 各回退
    /// 192（1.6.15 MeleeWeapon.cs:914）。注意 X/Y 锚点不同，镜像时不得混用。
    /// </summary>
    internal const int ClubSpecialAreaHalfSizePx = 192;

    /// <summary>
    /// 匕首特技探测距离（px）：原版 doDaggerFunction 以
    /// <c>getUniformPositionAwayFromBox(FacingDirection, 48)</c> 取判定点
    /// （1.6.15 MeleeWeapon.cs:886、Farmer.cs:5617-5628）。
    /// </summary>
    internal const int DaggerProbeDistancePx = 48;
}
