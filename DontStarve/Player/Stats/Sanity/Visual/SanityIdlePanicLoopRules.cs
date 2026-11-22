#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal enum SanityIdlePanicPhase
{
    Inactive,
    Faint,
    Startled
}

internal enum SanityIdlePanicLoopCommand
{
    None,
    BeginFaint,
    BeginStartled
}

/// <summary>
/// Pure phase and threshold rules for the low-sanity idle panic loop. The SMAPI adapter feeds
/// per-tick elapsed time and executes the returned commands against vanilla APIs; this class
/// owns no game state and never decides player control.
/// </summary>
internal sealed class SanityIdlePanicLoopRules
{
    // 原版 FarmerSprite case 293（passOutTired）的帧序与逐帧时长（见 1.6.15 反编译）。
    // 原版第 5 帧带有 doSleepEmote/passOutFromTired 帧动作（上床结算并结束当天）；本循环
    // 只复刻动作帧本身，绝不挂接这些帧动作，也不使用 293 这个动画 token。
    // 末帧 5250ms 为用户实测反馈调整：仅拉长倒地末帧，使单轮循环约 1.5 倍（6500→9750ms），
    // 后续凭手感在此值上微调。
    private static readonly int[] FaintFrames = { 16, 0, 16, 4, 5 };
    private static readonly int[] FaintDurations = { 1000, 500, 1000, 200, 5250 };

    // 受惊段沿用原版 "surprised" EmoteType 的 1500ms 受惊帧（帧 94），另加 300ms 落地缓冲。
    internal static readonly TimeSpan FaintPhaseDuration = CreateFaintPhaseDuration();
    internal static readonly TimeSpan StartledPhaseDuration = TimeSpan.FromMilliseconds(1800);

    // 与 SanityVisualController 的低理智 idle 判定同一口径：小于等于 50% 才进入循环
    // （卡线归更疯一侧，与 ShadowCreatures tier 的含边界进入保持一致）。
    internal const double SanityEligibleRatio = 0.5d;

    // 惊恐循环自己的待机阈值。刻意不复用 SanityIdleDetector：后者的中断契约包含
    // "精灵必须处于基础动画/中立姿势"，而原版 IsPlayingBasicAnimation 的静立判定要求当前帧
    // 恰好等于呼吸帧（朝下=1、朝上=17、举物=97/113），静立时持续返回 false，会让待机计时
    // 永远清零。惊恐循环是接管式表现，启动时本来就会 completelyStop 精灵，因此只看真实
    // 操作信号（输入/移动/工具/菜单/事件/冻结），不要求姿势中立。
    internal static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(1);

    private TimeSpan phaseElapsed;
    private TimeSpan idleElapsed;

    internal bool IdleThresholdReached => idleElapsed >= IdleThreshold;

    /// <summary>累计待机时长；interrupted 为真时立刻清零。返回当前累计值。</summary>
    internal TimeSpan ObserveIdle(TimeSpan elapsed, bool interrupted)
    {
        if (interrupted || elapsed < TimeSpan.Zero)
        {
            idleElapsed = TimeSpan.Zero;
            return TimeSpan.Zero;
        }
        if (idleElapsed >= IdleThreshold)
            return idleElapsed;
        var remaining = IdleThreshold - idleElapsed;
        idleElapsed = elapsed >= remaining ? IdleThreshold : idleElapsed + elapsed;
        return idleElapsed;
    }

    internal void ResetIdle()
    {
        idleElapsed = TimeSpan.Zero;
    }

    internal SanityIdlePanicPhase Phase { get; private set; }

    internal bool IsActive => Phase != SanityIdlePanicPhase.Inactive;

    internal static IReadOnlyList<int> FaintFrameIds => FaintFrames;

    internal static IReadOnlyList<int> FaintFrameMilliseconds => FaintDurations;

    /// <summary>进入循环；第一段固定是昏迷，Angry 气泡由适配层随动作一起发出。</summary>
    internal void Start()
    {
        Phase = SanityIdlePanicPhase.Faint;
        phaseElapsed = TimeSpan.Zero;
    }

    internal void Cancel()
    {
        Phase = SanityIdlePanicPhase.Inactive;
        phaseElapsed = TimeSpan.Zero;
    }

    internal SanityIdlePanicLoopCommand Tick(TimeSpan elapsed)
    {
        if (!IsActive || elapsed < TimeSpan.Zero)
            return SanityIdlePanicLoopCommand.None;

        phaseElapsed += elapsed;
        if (Phase == SanityIdlePanicPhase.Faint)
        {
            if (phaseElapsed < FaintPhaseDuration)
                return SanityIdlePanicLoopCommand.None;
            Phase = SanityIdlePanicPhase.Startled;
            phaseElapsed = TimeSpan.Zero;
            return SanityIdlePanicLoopCommand.BeginStartled;
        }

        if (phaseElapsed < StartledPhaseDuration)
            return SanityIdlePanicLoopCommand.None;
        Phase = SanityIdlePanicPhase.Faint;
        phaseElapsed = TimeSpan.Zero;
        return SanityIdlePanicLoopCommand.BeginFaint;
    }

    internal static bool IsSanityEligible(double current, double maximum)
    {
        return maximum > 0d && current / maximum <= SanityEligibleRatio;
    }

    private static TimeSpan CreateFaintPhaseDuration()
    {
        var total = 0;
        foreach (var milliseconds in FaintDurations)
            total += milliseconds;
        return TimeSpan.FromMilliseconds(total);
    }
}
