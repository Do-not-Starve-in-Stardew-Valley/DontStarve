using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 按 active Stardew Buff 的毫秒生命周期释放固定总量恢复，不跟随游戏内分钟速度变化。
/// </summary>
internal abstract class FixedRestoreBuff : INonTimeRelatedBuff
{
    // 原 DS 数据以约 7 秒为一个恢复脉冲；总脉冲数由 Buff 总时长除以这个基准得到。
    private const int BASELINE_PULSE_MILLISECONDS = 7000;

    // OneSecondUpdateTicked 不是逐毫秒触发，提前 1 秒视为到点，避免最后一个脉冲因采样粒度丢失。
    private const int RELEASE_LOOKAHEAD_MILLISECONDS = 1000;

    private string activeBuffId;
    private int activeTotalMilliseconds;
    private int lastRemainingMilliseconds;
    private int appliedPulses;

    protected abstract string SaveKey { get; }
    protected abstract IReadOnlyList<string> BuffIds { get; }
    protected abstract int AmountPerPulse { get; }

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetTracking();
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => Update();
    }

    protected abstract void ApplyRestore(Farmer player, int amount);

    private void Update()
    {
        if (!Context.IsWorldReady)
        {
            ResetTracking();
            return;
        }

        var player = Game1.player;
        if (player == null)
        {
            ResetTracking();
            return;
        }

        if (!TryGetBuff(player, out var buff, out var buffId))
        {
            ResetTracking();
            return;
        }

        var totalMilliseconds = GetFiniteDuration(buff.totalMillisecondsDuration, buff.millisecondsDuration);
        var remainingMilliseconds = GetFiniteDuration(buff.millisecondsDuration, totalMilliseconds);
        if (totalMilliseconds <= 0 || remainingMilliseconds <= 0)
        {
            ResetTracking();
            return;
        }

        if (
            activeBuffId != buffId
            || activeTotalMilliseconds != totalMilliseconds
            || remainingMilliseconds > lastRemainingMilliseconds
        )
        {
            // Buff id、总时长变化，或剩余时间反向增加，都视为一轮新的 Buff 生命周期。
            StartCycle(buffId, totalMilliseconds, remainingMilliseconds);
        }

        var targetPulses = GetTargetPulseCount(totalMilliseconds);
        var duePulses = GetDuePulseCount(totalMilliseconds, remainingMilliseconds, targetPulses);
        var pulsesToApply = duePulses - appliedPulses;
        if (pulsesToApply > 0)
        {
            // 记录“已释放脉冲”而不是实际恢复量：满血/满体力时被上限吃掉的恢复不延后补发。
            appliedPulses = duePulses;
            ApplyRestore(player, pulsesToApply * AmountPerPulse);
        }

        lastRemainingMilliseconds = remainingMilliseconds;
    }

    private bool TryGetBuff(Farmer player, out StardewValley.Buff buff, out string buffId)
    {
        foreach (var id in BuffIds)
        {
            if (player.buffs.AppliedBuffs.TryGetValue(id, out buff))
            {
                buffId = id;
                return true;
            }
        }

        buff = null;
        buffId = null;
        return false;
    }

    private void StartCycle(string buffId, int totalMilliseconds, int remainingMilliseconds)
    {
        activeBuffId = buffId;
        activeTotalMilliseconds = totalMilliseconds;
        lastRemainingMilliseconds = remainingMilliseconds;

        // 中途读档或启用时不补发过去时间，只从当前剩余时长对齐“已经应该释放过”的脉冲数。
        appliedPulses = GetDuePulseCount(
            totalMilliseconds,
            remainingMilliseconds,
            GetTargetPulseCount(totalMilliseconds)
        );
    }

    private static int GetTargetPulseCount(int totalMilliseconds)
    {
        return Math.Max(1, totalMilliseconds / BASELINE_PULSE_MILLISECONDS);
    }

    private static int GetDuePulseCount(
        int totalMilliseconds,
        int remainingMilliseconds,
        int targetPulses
    )
    {
        var effectiveRemaining = Math.Max(
            0,
            remainingMilliseconds - RELEASE_LOOKAHEAD_MILLISECONDS
        );
        var elapsedMilliseconds = Math.Clamp(
            totalMilliseconds - effectiveRemaining,
            0,
            totalMilliseconds
        );

        return Math.Min(
            targetPulses,
            (int)Math.Floor(targetPulses * elapsedMilliseconds / (double)totalMilliseconds)
        );
    }

    private static int GetFiniteDuration(int duration, int fallback)
    {
        // ENDLESS Buff 没有固定总量语义，直接忽略；0 或负值再尝试使用另一个 duration 字段兜底。
        if (duration == StardewValley.Buff.ENDLESS)
            return 0;

        return duration > 0 ? duration : Math.Max(0, fallback);
    }

    private void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<FixedRestoreBuffData>(SaveKey);
        activeBuffId = data?.ActiveBuffId;
        activeTotalMilliseconds = data?.ActiveTotalMilliseconds ?? 0;
        lastRemainingMilliseconds = data?.LastRemainingMilliseconds ?? 0;
        appliedPulses = data?.AppliedPulses ?? 0;
    }

    private void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SaveKey,
            new FixedRestoreBuffData
            {
                ActiveBuffId = activeBuffId,
                ActiveTotalMilliseconds = activeTotalMilliseconds,
                LastRemainingMilliseconds = lastRemainingMilliseconds,
                AppliedPulses = appliedPulses,
            }
        );
    }

    private void ResetTracking()
    {
        activeBuffId = null;
        activeTotalMilliseconds = 0;
        lastRemainingMilliseconds = 0;
        appliedPulses = 0;
    }
}

internal class FixedRestoreBuffData
{
    public string ActiveBuffId { get; init; }
    public int ActiveTotalMilliseconds { get; init; }
    public int LastRemainingMilliseconds { get; init; }
    public int AppliedPulses { get; init; }
}
