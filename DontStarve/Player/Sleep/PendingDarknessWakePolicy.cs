#nullable enable

namespace DontStarve.Player.Sleep;

/// <summary>
/// Pure timing rules for the pending-darkness "hospital direct wake" death. A persisted
/// darkness death marker replays the vanilla kill-screen handoff on the first fully stable
/// morning tick: no fade, no event, no menu, no day-change coroutine. Deliberately has no
/// health requirement — the old hidden-hospital 0-blood hold is retired, and the death replays
/// with whatever HP the farmer carries (vanilla's PlayerKilled event sets health=10 itself).
/// The one exception is the exact-zero boundary: when the farmer's health is already 0, the
/// vanilla morning death block owns the death and the mod must not stack a second sequence.
/// </summary>
internal static class PendingDarknessWakePolicy
{
    /// <summary>
    /// 原版黑暗死亡复活血量：GameLocation.checkForEvents 的 killScreen 块在建立 PlayerKilled
    /// 事件时固定写 health=10（1.6.15 GameLocation.cs:15614），死亡事件结束不改动血量。
    /// 复活血量的落盘时机与饥饿对齐（用户决议 2026-11-14）：由夜间保存前最终值缝
    /// （SpecialPassOutSanitySettlement.RegisterPendingDarknessWakeHealth → dayupdate postfix /
    /// SaveFarmhand prefix / OnSaving）提前写 10，清晨 vanilla 的再次赋值成为无害幂等重复。
    /// </summary>
    internal const int VanillaReviveHealth = 10;

    /// <summary>
    /// 同一会话内清晨死亡序列的最大尝试次数（修复1，2026-11-14）：任一 fail-open 后允许重试，
    /// 但预算耗尽即停——标记保留在册、下次存档仍落盘、读档必重演，同时打一次 Warn。
    /// 没有预算上限时，稳定门会在每次 fail-open 后的下一个稳定 tick 立即重触发，形成每 tick
    /// 无限重试刷日志的循环。
    /// </summary>
    internal const int MaximumAttempts = 3;

    /// <summary>会话级触发封锁：在途（等待完成上报）或重试预算耗尽。封锁不影响落盘——
    /// 只有死亡确认完成才从落盘排除（修复1）。</summary>
    internal static bool IsWakeSequenceBlockedThisSession(bool inFlight, int attempts) =>
        inFlight || attempts >= MaximumAttempts;

    /// <summary>
    /// 血 0 边界（修复2，2026-11-14）：标记在册且血量已为 0 时，原版清晨 0 血死亡块
    /// （Farmer.Update 的 CanMove+health<=0+!killScreen+timeOfDay&lt;2600）已经或即将原生
    /// 触发本次死亡，mod 序列不得再叠加（否则双扣费/双掉物/双计数）。三条边界刻意收窄：
    /// <br/>· 不按稳定门门控——原版死亡块不等世界稳定（淡入期间 CanMove 一恢复就触发），
    /// 委托判定必须先于稳定门跑，否则淡入帧里原版死亡先完成、稳定门事后看到 health=10
    /// 会再补一刀；<br/>· 不排除 killScreen=true——那正是原版死亡块已触发、死亡正在原版
    /// 链中进行的信号，此时才需要同日 MarkDarkness 供台词 seam；<br/>· 只在
    /// timeOfDay&lt;2600 窗口内成立——窗口外原版死亡块本身不会触发，死亡仍由 mod 序列负责。
    /// </summary>
    internal static bool ShouldDelegateDeathToVanilla(
        bool markerPresent,
        bool healthZero,
        bool vanillaDeathBlockWindow,
        bool wakeSequenceBlockedThisSession
    )
    {
        return markerPresent
            && healthZero
            && vanillaDeathBlockWindow
            && !wakeSequenceBlockedThisSession;
    }

    internal static bool ShouldBeginDeath(
        bool markerPresent,
        bool wakeSequenceBlockedThisSession,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool newDay,
        bool killScreen,
        bool messageAfterPauseEmpty
    )
    {
        return markerPresent
            && !wakeSequenceBlockedThisSession
            && worldReady
            && !isFading
            && !eventUp
            && !menuOpen
            && !newDay
            && !killScreen
            && messageAfterPauseEmpty;
    }
}
