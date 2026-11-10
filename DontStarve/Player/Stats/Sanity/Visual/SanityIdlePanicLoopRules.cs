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
    private static readonly int[] FaintFrames = { 16, 0, 16, 4, 5 };
    private static readonly int[] FaintDurations = { 1000, 500, 1000, 200, 2000 };

    // 受惊段沿用原版 "surprised" EmoteType 的 1500ms 受惊帧（帧 94），另加 300ms 落地缓冲。
    internal static readonly TimeSpan FaintPhaseDuration = CreateFaintPhaseDuration();
    internal static readonly TimeSpan StartledPhaseDuration = TimeSpan.FromMilliseconds(1800);

    // 与 SanityVisualController 的低理智 idle 判定同一口径：严格小于 50% 才进入循环。
    internal const double SanityEligibleRatio = 0.5d;

    private TimeSpan phaseElapsed;

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
        return maximum > 0d && current / maximum < SanityEligibleRatio;
    }

    private static TimeSpan CreateFaintPhaseDuration()
    {
        var total = 0;
        foreach (var milliseconds in FaintDurations)
            total += milliseconds;
        return TimeSpan.FromMilliseconds(total);
    }
}
