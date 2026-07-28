#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Time;

/// <summary>
/// 与 SMAPI 解耦的分钟游标。正向同步由这里独占补出 (oldTime, syncTime]，
/// 消费者的 Sync 只接收一次边界通知，不能再自行补跑。
/// </summary>
internal sealed class MinuteTimeTimeline
{
    private const long LargeSyncDeltaMinutes = 24 * 60;

    private readonly Action<string>? diagnostic;
    private readonly HashSet<string> reportedDiagnostics = new(StringComparer.Ordinal);

    internal MinuteTimeTimeline(Action<string>? diagnostic = null)
    {
        this.diagnostic = diagnostic;
    }

    internal long Time { get; private set; }

    internal List<Action<long>> OnLoad { get; } = new();

    internal List<Action<long>> OnUpdate { get; } = new();

    internal List<Action<long, long>> OnSync { get; } = new();

    internal void Load(long time)
    {
        reportedDiagnostics.Clear();
        Time = time;
        Dispatch(OnLoad, time, "load");
    }

    /// <summary>
    /// 释放当前 10 分钟块中的至多一个分钟 tick。暂停时显式不推进，
    /// 同一分钟的重复 UpdateTicking 也不会重复回调。
    /// </summary>
    internal bool TryAdvanceWithinBlock(bool shouldAdvance, long elapsedMinuteInBlock)
    {
        if (!shouldAdvance || elapsedMinuteInBlock <= 0)
            return false;

        // 第 10 分钟由原生 TimeChanged/Sync 负责；把块内释放上限锁在 9，
        // 即使 interval 在事件边界短暂达到 10 也不会越过同步点到下一块。
        var releasableMinute = Math.Min(elapsedMinuteInBlock, 9);
        if (Time % 10 >= releasableMinute)
            return false;

        if (Time == long.MaxValue)
        {
            ReportOnce(
                "minute-cursor-overflow",
                "minute cursor cannot advance past Int64.MaxValue"
            );
            return false;
        }

        Time++;
        Dispatch(OnUpdate, Time, "update");
        return true;
    }

    /// <summary>
    /// 正向同步逐分钟升序发布更新，之后只发布一次 Sync。相等或回退不发布 Update。
    /// </summary>
    internal void Synchronize(long syncTime)
    {
        var oldTime = Time;
        var delta = syncTime - oldTime;

        if (delta > LargeSyncDeltaMinutes || delta < -LargeSyncDeltaMinutes)
        {
            ReportOnce(
                delta > 0 ? "large-forward-sync" : "large-backward-sync",
                $"large minute sync delta observed: old={oldTime}, sync={syncTime}, delta={delta}; no minutes were discarded"
            );
        }

        if (delta > 0)
        {
            while (Time < syncTime)
            {
                Time++;
                Dispatch(OnUpdate, Time, "update");
            }
        }
        else
        {
            Time = syncTime;
        }

        Dispatch(OnSync, syncTime, delta, "sync");
    }

    private void Dispatch(List<Action<long>> callbacks, long value, string phase)
    {
        // 注册只发生在初始化期；索引循环避免每分钟为快照数组分配内存。
        var count = callbacks.Count;
        for (var index = 0; index < count; index++)
        {
            var callback = callbacks[index];
            try
            {
                callback(value);
            }
            catch (Exception ex)
            {
                ReportCallbackFailure(phase, callback, ex);
            }
        }
    }

    private void Dispatch(
        List<Action<long, long>> callbacks,
        long time,
        long delta,
        string phase
    )
    {
        var count = callbacks.Count;
        for (var index = 0; index < count; index++)
        {
            var callback = callbacks[index];
            try
            {
                callback(time, delta);
            }
            catch (Exception ex)
            {
                ReportCallbackFailure(phase, callback, ex);
            }
        }
    }

    private void ReportCallbackFailure(string phase, Delegate callback, Exception ex)
    {
        var owner = callback.Method.DeclaringType?.FullName ?? "unknown";
        var callbackName = $"{owner}.{callback.Method.Name}";
        ReportOnce(
            $"callback:{phase}:{callbackName}:{ex.GetType().FullName}",
            $"{phase} callback failed and was not retried: callback={callbackName}, exception={ex.GetType().Name}"
        );
    }

    private void ReportOnce(string key, string message)
    {
        if (diagnostic is null || !reportedDiagnostics.Add(key))
            return;

        try
        {
            diagnostic(message);
        }
        catch (Exception)
        {
            // 诊断出口不能反向破坏 exactly-once 序列；原回调错误已经被隔离。
        }
    }
}
