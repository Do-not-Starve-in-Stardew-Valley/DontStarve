#nullable enable

using System;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// 健康差分 watcher 的抑制入口（静态 seam，沿用 HungerExtensions/SanityExtensions 的
/// 本项目既有静态绑定先例）。
/// </summary>
/// <remarks>
/// 为什么用静态 seam 而不是构造注入：抑制调用点位于 HungerCycle→SmapiHungerDamageExecutor
/// 与 SmapiSleepLifecycleAdapter 的深层构造链里；这些系统不该为了播音而反向依赖音频模块。
/// 本类只暴露"调用前抑制"一个动作，ModEntry 在启动时绑定真实的差分状态表；未绑定时
/// 全部调用为 no-op（反馈关闭时睡眠/饥饿扣血不会留下任何悬挂令牌）。
/// 这是与既有静态状态字段同级别的已知权衡；收敛方向是未来把生存结算抽成显式服务后
/// 改为构造注入。
/// </remarks>
internal static class StatusFeedbackHealthSuppression
{
    private static StatusFeedbackHealthWatchStore? store;

    internal static void Bind(StatusFeedbackHealthWatchStore? boundStore)
    {
        store = boundStore;
    }

    /// <summary>
    /// 持续健康写入方在写值前调用：计划值与当前值确实不同时，抑制下一次该玩家的
    /// 健康差分播音。仅用于 overtime/系统重置语义的写入（睡眠结算、饥饿空腹伤害、
    /// 黑暗死亡清晨重演的原版复活血量写回等）；一次性伤害（暗影袭击、黑暗地板等）
    /// 不得调用。新增写回点必须接入本入口，否则差分 watcher 会按离散变化播音
    /// （2026-11-14 复活重演漏接教训）。
    /// </summary>
    internal static void SuppressNextHealthSound(
        long playerId,
        double currentHealth,
        double plannedHealth
    )
    {
        if (store is null)
            return;
        store
            .GetOrCreate(playerId)
            .SuppressNextIfHealthChanges(currentHealth, plannedHealth);
    }
}
