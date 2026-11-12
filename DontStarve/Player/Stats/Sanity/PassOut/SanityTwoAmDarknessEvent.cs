#nullable enable

using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmDarknessEventCommand
{
    Warning,
    Hit,
    Completed,
}

/// <summary>
/// A generated vanilla Event keeps the original event/message renderer: a full-screen fade and
/// `message` commands create the narrow no-speaker window. Only the three mod-owned synchronization
/// commands are overridden; all rendering and dialogue dismissal remains Stardew-owned.
/// </summary>
internal sealed class SanityTwoAmDarknessEvent : Event
{
    internal const string EventId = "sanity-two-am-special-death-v2";
    private const string WarningCommand = "dontStarveDarknessWarning";
    private const string HitCommand = "dontStarveDarknessHit";
    private const string CompletedCommand = "dontStarveDarknessCompleted";

    private readonly Action<SanityTwoAmDarknessEventCommand> commandHandler;

    internal SanityTwoAmDarknessEvent(
        Farmer farmer,
        string enteredPrompt,
        string warningPrompt,
        string resolvedPrompt,
        Action<SanityTwoAmDarknessEventCommand> commandHandler
    )
        : base(
            BuildScript(farmer, enteredPrompt, warningPrompt, resolvedPrompt),
            fromAssetName: null,
            eventID: EventId,
            farmerActor: farmer
        )
    {
        this.commandHandler = commandHandler
            ?? throw new ArgumentNullException(nameof(commandHandler));
        skippable = false;
    }

    public override void tryEventCommand(
        GameLocation location,
        GameTime time,
        string[] args
    )
    {
        var command = args.Length == 0 ? string.Empty : args[0];
        if (string.Equals(command, WarningCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Warning);
            CurrentCommand++;
            return;
        }
        if (string.Equals(command, HitCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Hit);
            CurrentCommand++;
            return;
        }
        if (string.Equals(command, CompletedCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Completed);
            CurrentCommand++;
            return;
        }
        base.tryEventCommand(location, time, args);
    }

    private static string BuildScript(
        Farmer farmer,
        string enteredPrompt,
        string warningPrompt,
        string resolvedPrompt
    )
    {
        if (farmer is null)
            throw new ArgumentNullException(nameof(farmer));

        var tile = farmer.TilePoint;
        var direction = Math.Clamp(farmer.FacingDirection, 0, 3);
        return string.Join(
            "/",
            "none",
            "-1000 -1000",
            $"farmer {tile.X} {tile.Y} {direction}",
            "fade",
            $"message \"{Escape(enteredPrompt)}\"",
            WarningCommand,
            $"message \"{Escape(warningPrompt)}\"",
            HitCommand,
            $"message \"{Escape(resolvedPrompt)}\"",
            CompletedCommand,
            "end"
        );
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("/", "\\/", StringComparison.Ordinal);
    }
}
