#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Interface;

namespace DontStarve.Player.Stats;

internal enum SurvivalStateTimePhase
{
    Ordinary,
    RealtimeSleep,
    BlackoutSleep,
}

internal interface ISurvivalStateTimeBehavior<in TPlayer>
    where TPlayer : class
{
    void OnMinute(
        TPlayer player,
        string playerKey,
        long minute,
        SurvivalStateTimePhase phase
    );
}

/// <summary>
/// The sole minute dispatcher for per-player survival state. Its watermark replaces per-status
/// rewind/sleep fences and stays session-local; sleep end removes only the active phase so a later
/// TimeApi catch-up can skip settled minutes and continue immediately after the sleep endpoint.
/// </summary>
internal sealed class SurvivalStateTimeOwner<TPlayer> : IDisposable
    where TPlayer : class
{
    internal const int MaxTrackedPlayers = 64;

    private readonly ITimeAPI timeApi;
    private readonly Func<IEnumerable<TPlayer>> getOnlinePlayers;
    private readonly Func<TPlayer, string> getPlayerKey;
    private readonly Action<string>? diagnostic;
    private readonly Dictionary<string, PlayerCursor> players = new(StringComparer.Ordinal);
    private readonly List<ISurvivalStateTimeBehavior<TPlayer>> behaviors = new();
    private readonly HashSet<string> reportedBehaviorFailures = new(StringComparer.Ordinal);
    private bool disposed;

    internal SurvivalStateTimeOwner(
        ITimeAPI timeApi,
        Func<IEnumerable<TPlayer>> getOnlinePlayers,
        Func<TPlayer, string> getPlayerKey,
        Action<string>? diagnostic = null
    )
    {
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.getOnlinePlayers = getOnlinePlayers
            ?? throw new ArgumentNullException(nameof(getOnlinePlayers));
        this.getPlayerKey = getPlayerKey
            ?? throw new ArgumentNullException(nameof(getPlayerKey));
        this.diagnostic = diagnostic;

        timeApi.OnLoad.Add(OnLoad);
        timeApi.OnUpdate.Add(OnTimeUpdate);
    }

    internal bool Register(ISurvivalStateTimeBehavior<TPlayer> behavior)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(SurvivalStateTimeOwner<TPlayer>));
        if (behavior is null)
            throw new ArgumentNullException(nameof(behavior));
        if (behaviors.Contains(behavior))
            return false;

        behaviors.Add(behavior);
        return true;
    }

    internal bool BeginSleep(string playerKey, string sleepSessionId)
    {
        if (
            string.IsNullOrWhiteSpace(playerKey)
            || string.IsNullOrWhiteSpace(sleepSessionId)
            || !TryGetOrCreateCursor(playerKey, out var cursor)
        )
            return false;

        if (cursor.SleepSessionId is not null)
            return string.Equals(cursor.SleepSessionId, sleepSessionId, StringComparison.Ordinal);

        cursor.SleepSessionId = sleepSessionId;
        cursor.SleepPhase = SurvivalStateTimePhase.RealtimeSleep;
        return true;
    }

    internal bool SetSleepPhase(
        string playerKey,
        string sleepSessionId,
        SurvivalStateTimePhase phase
    )
    {
        if (
            phase is not SurvivalStateTimePhase.RealtimeSleep
                and not SurvivalStateTimePhase.BlackoutSleep
            || !TryGetCursor(playerKey, out var cursor)
            || !string.Equals(cursor.SleepSessionId, sleepSessionId, StringComparison.Ordinal)
        )
            return false;

        cursor.SleepPhase = phase;
        return true;
    }

    internal bool CanAdvanceSleepInterval(
        string playerKey,
        TPlayer player,
        string sleepSessionId,
        long startExclusive,
        long endInclusive,
        SurvivalStateTimePhase phase
    )
    {
        return endInclusive >= startExclusive
            && TryGetSleepCursor(playerKey, player, sleepSessionId, phase, out _);
    }

    internal bool AdvanceSleepInterval(
        string playerKey,
        TPlayer player,
        string sleepSessionId,
        long startExclusive,
        long endInclusive,
        SurvivalStateTimePhase phase,
        out long dispatchedMinutes
    )
    {
        dispatchedMinutes = 0;
        if (
            endInclusive < startExclusive
            || !TryGetSleepCursor(playerKey, player, sleepSessionId, phase, out _)
        )
            return false;

        if (endInclusive == startExclusive)
            return true;

        var minute = startExclusive;
        while (minute < endInclusive)
        {
            if (minute == long.MaxValue)
                break;

            minute++;
            if (DispatchMinute(player, playerKey, minute, phase))
                dispatchedMinutes++;
        }

        return true;
    }

    internal bool EndSleep(string playerKey, string sleepSessionId)
    {
        if (
            !TryGetCursor(playerKey, out var cursor)
            || !string.Equals(cursor.SleepSessionId, sleepSessionId, StringComparison.Ordinal)
        )
            return false;

        cursor.SleepSessionId = null;
        cursor.SleepPhase = SurvivalStateTimePhase.Ordinary;
        return true;
    }

    internal void EndAllSleepContexts()
    {
        foreach (var cursor in players.Values)
        {
            cursor.SleepSessionId = null;
            cursor.SleepPhase = SurvivalStateTimePhase.Ordinary;
        }
    }

    internal bool ForgetPlayer(string playerKey)
    {
        return !string.IsNullOrWhiteSpace(playerKey) && players.Remove(playerKey);
    }

    internal void ClearSession()
    {
        players.Clear();
        reportedBehaviorFailures.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        timeApi.OnLoad.Remove(OnLoad);
        timeApi.OnUpdate.Remove(OnTimeUpdate);
        ClearSession();
        behaviors.Clear();
    }

    private void OnLoad(long _)
    {
        ClearSession();
    }

    private void OnTimeUpdate(long minute)
    {
        IEnumerable<TPlayer> onlinePlayers;
        try
        {
            onlinePlayers = getOnlinePlayers();
        }
        catch (Exception exception)
        {
            ReportOnce("player-enumeration", exception);
            return;
        }

        if (onlinePlayers is null)
            return;

        foreach (var player in onlinePlayers)
        {
            if (player is null)
                continue;

            string playerKey;
            try
            {
                playerKey = getPlayerKey(player);
            }
            catch (Exception exception)
            {
                ReportOnce("player-key", exception);
                continue;
            }

            if (string.IsNullOrWhiteSpace(playerKey))
                continue;
            if (!TryGetOrCreateCursor(playerKey, out var cursor))
            {
                ReportCapacityOnce();
                continue;
            }
            if (cursor.SleepSessionId is not null)
                continue;

            DispatchMinute(player, playerKey, minute, SurvivalStateTimePhase.Ordinary);
        }
    }

    private bool DispatchMinute(
        TPlayer player,
        string playerKey,
        long minute,
        SurvivalStateTimePhase phase
    )
    {
        if (!TryGetOrCreateCursor(playerKey, out var cursor))
        {
            ReportCapacityOnce();
            return false;
        }
        if (cursor.HasDispatchedMinute && minute <= cursor.LastDispatchedMinute)
            return false;

        // Mark before callbacks so a failing behavior is not retried by a duplicate source.
        cursor.HasDispatchedMinute = true;
        cursor.LastDispatchedMinute = minute;

        var count = behaviors.Count;
        for (var index = 0; index < count; index++)
        {
            var behavior = behaviors[index];
            try
            {
                behavior.OnMinute(player, playerKey, minute, phase);
            }
            catch (Exception exception)
            {
                ReportOnce(behavior.GetType().FullName ?? behavior.GetType().Name, exception);
            }
        }

        return true;
    }

    private bool TryGetCursor(string playerKey, out PlayerCursor cursor)
    {
        if (!string.IsNullOrWhiteSpace(playerKey) && players.TryGetValue(playerKey, out cursor!))
            return true;

        cursor = null!;
        return false;
    }

    private bool TryGetSleepCursor(
        string playerKey,
        TPlayer player,
        string sleepSessionId,
        SurvivalStateTimePhase phase,
        out PlayerCursor cursor
    )
    {
        cursor = null!;
        if (
            player is null
            || string.IsNullOrWhiteSpace(playerKey)
            || string.IsNullOrWhiteSpace(sleepSessionId)
            || phase is not SurvivalStateTimePhase.RealtimeSleep
                and not SurvivalStateTimePhase.BlackoutSleep
        )
            return false;

        string resolvedPlayerKey;
        try
        {
            resolvedPlayerKey = getPlayerKey(player);
        }
        catch (Exception exception)
        {
            ReportOnce("player-key", exception);
            return false;
        }

        return string.Equals(resolvedPlayerKey, playerKey, StringComparison.Ordinal)
            && TryGetCursor(playerKey, out cursor)
            && string.Equals(cursor.SleepSessionId, sleepSessionId, StringComparison.Ordinal)
            && cursor.SleepPhase == phase;
    }

    private bool TryGetOrCreateCursor(string playerKey, out PlayerCursor cursor)
    {
        if (players.TryGetValue(playerKey, out cursor!))
            return true;
        if (players.Count >= MaxTrackedPlayers)
        {
            cursor = null!;
            return false;
        }

        cursor = new PlayerCursor();
        players.Add(playerKey, cursor);
        return true;
    }

    private void ReportCapacityOnce()
    {
        const string message =
            "Survival time dispatch reached its per-session player limit; additional players were not dispatched.";
        if (reportedBehaviorFailures.Add("player-capacity"))
            Report(message);
    }

    private void ReportOnce(string owner, Exception exception)
    {
        var key = string.Concat(owner, ":", exception.GetType().FullName);
        if (!reportedBehaviorFailures.Add(key))
            return;

        Report(
            $"Survival state time callback failed and will not be retried: owner={owner}, exception={exception.GetType().Name}."
        );
    }

    private void Report(string message)
    {
        if (diagnostic is null)
            return;

        try
        {
            diagnostic(message);
        }
        catch (Exception)
        {
            // A diagnostic sink must not break minute dispatch.
        }
    }

    private sealed class PlayerCursor
    {
        internal bool HasDispatchedMinute { get; set; }
        internal long LastDispatchedMinute { get; set; }
        internal string? SleepSessionId { get; set; }
        internal SurvivalStateTimePhase SleepPhase { get; set; }
    }
}
