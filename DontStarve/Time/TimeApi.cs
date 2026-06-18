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
    public long Time { get; private set; }

    public List<Action<long>> OnLoad { get; } = new();
    public List<Action<long>> OnUpdate { get; } = new();
    public List<Action<long, long>> OnSync { get; } = new();

    internal void Initialize(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += Load;
        helper.Events.GameLoop.UpdateTicking += Update;
        helper.Events.GameLoop.TimeChanged += Sync;
    }

    private void Load(object sender, SaveLoadedEventArgs e)
    {
        Time = Parse(Game1.timeOfDay);
        OnLoad.ForEach(action => action(Time));
    }

    private void Update(object sender, UpdateTickingEventArgs e)
    {
        if (!Context.IsWorldReady || !Game1.shouldTimePass() || !Game1.game1.IsActive)
            return;

        var currentMs = Game1.gameTimeInterval / GetMsPerGameMinute();
        if (Time % 10 >= currentMs)
            return;

        Time++;
        OnUpdate.ForEach(action => action(Time));
    }

    private void Sync(object sender, TimeChangedEventArgs e)
    {
        var syncTicks = Parse(Game1.timeOfDay);
        var delta = syncTicks - Time;

        Time = syncTicks;

        // Keep the inherited MTH contract: normal update first, sync correction second.
        OnUpdate.ForEach(action => action(Time));
        OnSync.ForEach(action => action(syncTicks, delta));
    }

    private static long Parse(int time)
    {
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
}
