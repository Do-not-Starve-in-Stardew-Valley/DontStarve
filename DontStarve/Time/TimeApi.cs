using System;
using System.Collections.Generic;
using DontStarve.Interface;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace DontStarve.Time;

internal sealed class TimeApi : ITimeAPI
{
    private readonly MinuteTimeTimeline timeline = new(LogDiagnostic);

    // 内部时间单位是“游戏内 1 分钟”。Stardew 原生 TimeChanged 只按 10 分钟跳变，
    // 这里用 UpdateTicking 在 10 分钟区间内补出 1 分钟粒度，供饥饿、理智和 Buff 使用。
    public long Time => timeline.Time;

    // 暴露 List 是为了兼容旧 MTH 风格的注册方式；消费者只应 Add 回调，不要清空或替换集合。
    public List<Action<long>> OnLoad => timeline.OnLoad;
    public List<Action<long>> OnUpdate => timeline.OnUpdate;
    public List<Action<long, long>> OnSync => timeline.OnSync;

    internal void Initialize(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += Load;
        helper.Events.GameLoop.UpdateTicking += Update;
        helper.Events.GameLoop.TimeChanged += Sync;
    }

    private void Load(object sender, SaveLoadedEventArgs e)
    {
        timeline.Load(Parse(Game1.timeOfDay));
    }

    private void Update(object sender, UpdateTickingEventArgs e)
    {
        var shouldAdvance =
            Context.IsWorldReady && Game1.shouldTimePass() && Game1.game1.IsActive;
        if (!shouldAdvance)
        {
            timeline.TryAdvanceWithinBlock(false, 0);
            return;
        }

        // Game1.gameTimeInterval 表示当前 10 分钟块内已过去的毫秒；除以当前地点的分钟长度后，
        // 得到本块内应该释放到第几个 1 分钟 tick。Time % 10 防止同一分钟重复触发。
        var currentMinute = Game1.gameTimeInterval / GetMsPerGameMinute();
        timeline.TryAdvanceWithinBlock(true, currentMinute);
    }

    private void Sync(object sender, TimeChangedEventArgs e)
    {
        var syncTicks = Parse(Game1.timeOfDay);
        timeline.Synchronize(syncTicks);
    }

    private static long Parse(int time)
    {
        // 2:00 到 5:50 属于 Stardew 的跨日尾段；这里按下一天累计，避免清晨前时间倒退。
        var timescale = time % 100 / 10;
        var hours = time / 100 % 24;
        var hoursTimescale = hours * 6;
        var days = SDate.Now().DaysSinceStart - 1 + (hours < 6 ? 1 : 0);
        var daysTimescale = days * 24 * 6;

        return (timescale + hoursTimescale + daysTimescale) * 10L;
    }

    private static int GetMsPerGameMinute()
    {
        return Game1.realMilliSecondsPerGameMinute
            + (Game1.MasterPlayer.currentLocation == null
                ? 0
                : Game1.MasterPlayer.currentLocation.ExtraMillisecondsPerInGameMinute);
    }

    private static void LogDiagnostic(string message)
    {
        // TimeApi 没有独立持久化或 monitor 依赖；异常只在发生时以稳定前缀输出，
        // timeline 会按回调/会话去重，避免 catch-up 期间刷屏。
        Console.Error.WriteLine($"[DontStarve][TimeApi] {message}");
    }
}
