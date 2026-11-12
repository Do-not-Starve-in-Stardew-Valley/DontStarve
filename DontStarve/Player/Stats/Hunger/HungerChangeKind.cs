#nullable enable

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Hunger 一次主机结算的种类，随结算观察事件发布。
/// 它是状态反馈音效的离散/continuous 分类输入（见
/// StatusFeedbackTriggerPolicy.Classify），也用于日志归因。
/// </summary>
/// <remarks>
/// 数值即发布契约：新增种类只能追加，不能改动既有值。分类默认值是 open——
/// 新增种类若忘记归类，会按 Discrete（播音）处理；任何持续生效或系统重置类
/// 的新种类必须显式加入 StatusFeedbackTriggerPolicy 的静音清单。
/// </remarks>
internal enum HungerChangeKind
{
    /// <summary>未标注（不应出现在新代码中；分类按 open 默认处理）。</summary>
    Unknown = 0,

    /// <summary>吃食物结算（ApplyFood），离散事件。</summary>
    Food = 1,

    /// <summary>持续 overtime 语义的宿主值写入：每分钟自然衰减与睡眠期间结算（SetHunger 路径）。</summary>
    Overtime = 2,

    /// <summary>
    /// 确认真死亡边界的一次性饥饿重置（ApplyHealthDeathRecovery 设为复活值）。
    /// 属系统重置而非玩家可感知的离散增减，反馈分类为静音（与 Migration/Administration 同类）。
    /// </summary>
    Revive = 3,

    /// <summary>调试命令写入（SetDebugValue），静音。</summary>
    Debug = 4,
}
