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
    private readonly FixedRestoreBuffTiming timing = new();

    protected abstract IReadOnlyList<string> BuffIds { get; }
    protected abstract int AmountPerPulse { get; }

    public void Init(IModHelper helper)
    {
        // 恢复计量不再独立读写 SMAPI 存档：原版 active Buff 的总时长/剩余时长足以重建周期。
        // 旧的 DontStarve.Buff.* 数据保留在存档中但不读取、不覆盖、不清空。
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetTracking();
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

        var pulsesToApply = timing.Observe(
            buffId,
            totalMilliseconds,
            remainingMilliseconds
        );
        if (pulsesToApply > 0)
        {
            // 记录“已释放脉冲”而不是实际恢复量：满血/满体力时被上限吃掉的恢复不延后补发。
            ApplyRestore(player, pulsesToApply * AmountPerPulse);
        }
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

    private static int GetFiniteDuration(int duration, int fallback)
    {
        // ENDLESS Buff 没有固定总量语义，直接忽略；0 或负值再尝试使用另一个 duration 字段兜底。
        if (duration == StardewValley.Buff.ENDLESS)
            return 0;

        return duration > 0 ? duration : Math.Max(0, fallback);
    }

    private void ResetTracking()
    {
        timing.Reset();
    }
}
