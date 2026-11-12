#nullable enable

using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// Sanity 多人链路向状态反馈模块单向输出的观察 seam（依赖倒置：Sanity 域只声明
/// "我可以提供什么事实"，不引用任何音频实现）。
/// </summary>
/// <remarks>
/// 防双响契约（本任务最关键边界）：
/// - 主机结算自己本机玩家的变化 → 不经过本 seam 播音（走 HostStateChanged 直接本地播音）。
/// - 主机结算远程农场帮手的变化 → 主机侧不播音；变化通过增量快照 + DeltaHint 到达
///   帮手机器，由 <see cref="ISanityStatusFeedbackSink.ObserveClientDelta"/> 恰好播一次。
/// - DeltaHint 由主机在发送增量快照时按 <see cref="ISanityStatusFeedbackSink.ResolveDeltaHint"/>
///   写入消息；完整快照（入session/重连）恒为 Unknown，接收端不得播音。
/// - 客户端只对"自己玩家 key + Hint=Discrete"的增量回放播音，因此同一变化
///   只会在一条链路上响一次。
/// 观察事实类型（SanityClientDeltaObservation / HungerClientDeltaObservation）归属
/// 各自的域文件；本文件只声明 seam 接口。
/// </remarks>
internal interface ISanityStatusFeedbackSink
{
    /// <summary>主机侧：结算来源 → 增量快照 FeedbackHint 的唯一分类入口。</summary>
    StatusFeedbackTriggerKind ResolveDeltaHint(SanityChangeSource source);

    /// <summary>客户端侧：一次成功的增量快照应用（含应用前后值）。</summary>
    void ObserveClientDelta(SanityClientDeltaObservation observation);
}

/// <summary>Hunger 多人链路向状态反馈模块单向输出的观察 seam，契约同 Sanity 侧。</summary>
internal interface IHungerStatusFeedbackSink
{
    /// <summary>主机侧：Hunger 结算种类 → 增量快照 FeedbackHint。</summary>
    StatusFeedbackTriggerKind ResolveDeltaHint(HungerChangeKind kind);

    /// <summary>客户端侧：一次成功的增量快照应用（含应用前后值）。</summary>
    void ObserveClientDelta(HungerClientDeltaObservation observation);
}
