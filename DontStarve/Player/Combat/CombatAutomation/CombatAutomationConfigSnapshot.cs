#nullable enable

using DontStarve.Player.Combat.CombatAutomation.AutoSpecial;
using DontStarve.Player.Combat.CombatAutomation.AutoSwing;

namespace DontStarve.Player.Combat.CombatAutomation;

/// <summary>
/// 战斗自动化运行时配置快照：Coordinator 从 schema 驱动的 TypedConfigResolver 读入
/// 8 个战斗 key 后就地刷新字段（GMCM 保存后即时生效）。纯数据、无 Stardew/SMAPI 类型，
/// 可被测试工程源码链接编译。热路径（postfix/驱动）直读字段，零解析零分配。
/// 初始值全部 fail-closed 关闭：resolver 不可用（config 整体 fail-closed）时 Coordinator
/// 不构建、快照保持初始值，所有战斗自动化功能静默不生效。单个 key 取值失败时由
/// Coordinator 写入 fail-closed 回落值，本类不感知 resolver 细节。
/// </summary>
internal sealed class CombatAutomationConfigSnapshot
{
    internal bool AutoSwingMasterEnabled;
    internal bool AutoSwingSwordEnabled;
    internal bool AutoSwingClubEnabled;
    internal bool AutoSwingDaggerEnabled;

    /// <summary>弹弓自动发射开关（SlingshotAutoRelease 布尔化，用户 2026-11-22 裁决 A 案）。</summary>
    internal bool SlingshotAutoReleaseEnabled;

    /// <summary>
    /// 弹弓力度滑轮值（SlingshotPower，25..100，步长 5）：自动发射所需的蓄力百分比。
    /// 开关关闭时本值不参与判定。
    /// </summary>
    internal int SlingshotReleasePowerPercent;

    internal string AutoSpecialMode = "off";
    internal bool MouseFacingEnabled;

    /// <summary>
    /// 自动特技模式的解析结果（阶段 4/5 热路径直读枚举，零字符串解析）：Coordinator 在
    /// ApplyConfiguration 时经 <see cref="AutoSpecial.AutoSpecialRules.TryParseMode"/> 写入；
    /// 未知值 fail-closed 回落 Off。字段名用 Parsed 前缀，避免与上方同名字符串字段在类内
    /// 遮蔽同名类型。初始化用全限定名：字符串字段 AutoSpecialMode 在类内表达式上下文
    /// 优先于同名枚举类型。
    /// </summary>
    internal AutoSpecialMode ParsedAutoSpecialMode = AutoSpecial.AutoSpecialMode.Off;

    /// <summary>
    /// 是否需要订阅 UpdateTicked 驱动：弹弓自动发射、自动特技扫描。全关 = 不订阅，零开销。
    /// 鼠标转身不在此列——它由 Farmer.canMoveNow 完成回调驱动（事件式，非轮询）。
    /// </summary>
    internal bool RequiresTickDriver =>
        SlingshotAutoReleaseEnabled
        || ParsedAutoSpecialMode != AutoSpecial.AutoSpecialMode.Off;
}
