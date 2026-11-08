#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Death;

internal enum PlayerDeathCause
{
    Unknown,
    Hunger,
    Darkness,
}

/// <summary>
/// One-shot host evidence for the cause of the current vanilla kill-screen transition.
/// The vanilla death event remains authoritative; this ledger only supplies presentation context.
/// </summary>
internal sealed class PlayerDeathCauseLedger
{
    private readonly IMonitor? monitor;
    private readonly Dictionary<string, LedgerEntry> pending =
        new(StringComparer.Ordinal);

    internal PlayerDeathCauseLedger(IMonitor? monitor = null)
    {
        this.monitor = monitor;
    }

    internal void Mark(Farmer player, PlayerDeathCause cause)
    {
        if (player is null || cause == PlayerDeathCause.Unknown)
            return;

        var playerKey = player.UniqueMultiplayerID.ToString(
            CultureInfo.InvariantCulture
        );
        var day = Math.Max(0, Game1.Date.TotalDays);
        var duplicate = pending.ContainsKey(playerKey);
        pending[playerKey] = new LedgerEntry(day, cause);
        monitor?.Log(
            $"[player-death-diagnostic] stage=mark player={playerKey} cause={cause} day={day} duplicate={duplicate}",
            LogLevel.Debug
        );
    }

    internal void MarkHunger(Farmer player) =>
        Mark(player, PlayerDeathCause.Hunger);

    internal void MarkDarkness(Farmer player) =>
        Mark(player, PlayerDeathCause.Darkness);

    internal void Forget(Farmer player)
    {
        if (player is null)
            return;

        // An unused cause must not survive a revival and label another death later that day.
        pending.Remove(player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture));
    }

    internal bool TryConsume(
        Farmer player,
        out PlayerDeathCause cause
    )
    {
        cause = PlayerDeathCause.Unknown;
        if (player is null)
            return false;

        var playerKey = player.UniqueMultiplayerID.ToString(
            CultureInfo.InvariantCulture
        );
        if (!pending.TryGetValue(playerKey, out var entry))
        {
            monitor?.Log(
                $"[player-death-diagnostic] stage=consume player={playerKey} found=false current-day={Math.Max(0, Game1.Date.TotalDays)}",
                LogLevel.Debug
            );
            return false;
        }

        pending.Remove(playerKey);
        var currentDay = Math.Max(0, Game1.Date.TotalDays);
        var matched = entry.Day == currentDay;
        cause = matched ? entry.Cause : PlayerDeathCause.Unknown;
        monitor?.Log(
            $"[player-death-diagnostic] stage=consume player={playerKey} found=true cause={entry.Cause} marked-day={entry.Day} current-day={currentDay} matched={matched}",
            LogLevel.Debug
        );
        return matched;
    }

    internal void Clear() => pending.Clear();

    private readonly record struct LedgerEntry(
        int Day,
        PlayerDeathCause Cause
    );
}
