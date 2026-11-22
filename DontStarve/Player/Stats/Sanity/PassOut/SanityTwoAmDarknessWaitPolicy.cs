#nullable enable

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// 纯策略：2 点黑暗特殊流的"其他人等待"口径。
/// 用户决议（2026-11-13）：还原原版"有人看事件大家等"的对等行为——任一玩家的黑暗流活跃期间，
/// 其他玩家纯黑屏、输入冻结、时钟冻结；多人同时触发时各自看各自的事件，其余人黑屏等待，
/// 等全部流结束才统一放行。演出承载端（正在播放自己的 owner-local 事件的机器）不进入等待。
/// </summary>
internal static class SanityTwoAmDarknessWaitPolicy
{
    /// <summary>主机侧：只要还有活跃流，等待状态就成立（多流并存时持续等待，全部结束才放行）。</summary>
    internal static bool ShouldHoldForActiveFlows(int activeFlowCount) => activeFlowCount > 0;

    /// <summary>
    /// 本地端是否应用冻结：等待状态成立且本机没有在播放自己的 owner-local 演出。
    /// 本机演出是全屏黑，与等待画面天然重合，不需要（也不能）叠加 farmEvent 冻结。
    /// </summary>
    internal static bool ShouldApplyLocalFreeze(bool flowWaitActive, bool localPresentationActive)
        => flowWaitActive && !localPresentationActive;
}
