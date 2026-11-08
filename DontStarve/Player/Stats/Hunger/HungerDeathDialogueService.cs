#nullable enable

using System;
using HarmonyLib;
using DontStarve.Player.Death;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Replaces only approved NPC speech inside the vanilla PlayerKilled event after Hunger death.
/// It never duplicates or replaces the event script, so vanilla loss/warp/festival behavior stays
/// authoritative.
/// </summary>
internal sealed class HungerDeathDialogueService
{
    private enum DeathEventRoute
    {
        None,
        Hospital,
        Mine,
        Island,
    }

    private static HungerDeathDialogueService? activeOwner;

    private readonly IMonitor monitor;
    private readonly ITranslationHelper translation;
    private readonly PlayerDeathCauseLedger causes;
    private readonly Harmony harmony;
    private readonly IModHelper helper;
    private Event? activeEvent;
    private DeathEventRoute activeRoute;
    private PlayerDeathCause activeCause;
    private bool speakDiagnosticLogged;
    private bool pendingDisplayedDialogueDiagnostic;
    private string pendingDisplayedDialogueActor = string.Empty;
    private string pendingDisplayedDialogueText = string.Empty;

    internal HungerDeathDialogueService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        PlayerDeathCauseLedger causes
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.translation = helper.Translation;
        this.causes = causes ?? throw new ArgumentNullException(nameof(causes));
        harmony = new Harmony(string.Concat(modId, ".HungerDeathDialogue"));

        if (activeOwner is not null && !ReferenceEquals(activeOwner, this))
        {
            monitor.Log(
                "Hunger death dialogue patch owner already exists; the new owner stayed disabled.",
                LogLevel.Warn
            );
            return;
        }

        var checkForEvents = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.checkForEvents),
            Type.EmptyTypes
        );
        var speak = AccessTools.Method(
            typeof(Event.DefaultCommands),
            nameof(Event.DefaultCommands.Speak)
        );
        var drawDialogue = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.drawDialogue),
            new[] { typeof(NPC) }
        );
        var checkPrefix = AccessTools.Method(
            typeof(HungerDeathDialogueService),
            nameof(CheckForEventsPrefix)
        );
        var checkPostfix = AccessTools.Method(
            typeof(HungerDeathDialogueService),
            nameof(CheckForEventsPostfix)
        );
        var speakPrefix = AccessTools.Method(
            typeof(HungerDeathDialogueService),
            nameof(SpeakPrefix)
        );
        var drawDialoguePrefix = AccessTools.Method(
            typeof(HungerDeathDialogueService),
            nameof(DrawDialoguePrefix)
        );
        var drawDialoguePostfix = AccessTools.Method(
            typeof(HungerDeathDialogueService),
            nameof(DrawDialoguePostfix)
        );
        if (
            checkForEvents is null
            || speak is null
            || drawDialogue is null
            || checkPrefix is null
            || checkPostfix is null
            || speakPrefix is null
            || drawDialoguePrefix is null
            || drawDialoguePostfix is null
        )
        {
            monitor.Log(
                "Hunger death dialogue stayed vanilla because an event patch target was unavailable.",
                LogLevel.Warn
            );
            return;
        }

        try
        {
            harmony.Patch(
                checkForEvents,
                prefix: new HarmonyMethod(checkPrefix),
                postfix: new HarmonyMethod(checkPostfix)
            );
            harmony.Patch(speak, prefix: new HarmonyMethod(speakPrefix));
            harmony.Patch(
                drawDialogue,
                prefix: new HarmonyMethod(drawDialoguePrefix),
                postfix: new HarmonyMethod(drawDialoguePostfix)
            );
            activeOwner = this;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            monitor.Log(
                "[hunger-death-diagnostic] stage=patch-installed targets=GameLocation.checkForEvents,Event.DefaultCommands.Speak",
                LogLevel.Debug
            );
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Hunger death dialogue stayed vanilla because its patch failed ({exception.GetType().Name}).",
                LogLevel.Warn
            );
        }
    }

    internal void ClearRuntime()
    {
        activeEvent = null;
        activeRoute = DeathEventRoute.None;
        activeCause = PlayerDeathCause.Unknown;
        speakDiagnosticLogged = false;
    }

    private static void CheckForEventsPrefix(out bool __state)
    {
        __state = Game1.killScreen;
    }

    private static void CheckForEventsPostfix(bool __state, GameLocation __instance)
    {
        activeOwner?.OnCheckForEventsCompleted(__state, __instance);
    }

    private static void SpeakPrefix(Event @event, string[] args)
    {
        activeOwner?.TryReplaceSpeak(@event, args);
    }

    private static void DrawDialoguePostfix(NPC speaker)
    {
        activeOwner?.LogDisplayedDialogue(speaker);
    }

    private static void DrawDialoguePrefix(NPC speaker)
    {
        activeOwner?.ReplaceDisplayedDialogue(speaker);
    }

    private void OnCheckForEventsCompleted(bool wasKillScreen, GameLocation location)
    {
        ClearExpiredEvent();
        if (!wasKillScreen)
            return;

        var player = Game1.player;
        var currentEvent = location.currentEvent;
        var eventCreated = currentEvent is not null;
        var markerConsumed = false;
        var cause = PlayerDeathCause.Unknown;
        if (player is not null && eventCreated)
            markerConsumed = causes.TryConsume(player, out cause);
        else if (
            player is not null
            && PlayerDeathCausePolicy.ShouldForgetPendingCause(
                Game1.killScreen,
                player.health
            )
        )
        {
            // Vanilla has finished resolving a kill screen without a PlayerKilled event. Do not
            // let that unused cause tag label a later, unrelated death on the same day.
            causes.Forget(player);
        }
        monitor.Log(
            $"[player-death-diagnostic] stage=player-killed-event location={location.NameOrUniqueName} event-created={eventCreated} event-id={currentEvent?.id ?? "<null>"} cause={cause} kill-screen-after={Game1.killScreen} event-up={Game1.eventUp} marker-consumed={markerConsumed}",
            LogLevel.Debug
        );

        if (player is null || !eventCreated || !markerConsumed)
            return;

        activeEvent = currentEvent;
        activeRoute = ClassifyRoute(location);
        activeCause = cause;
        monitor.Log(
            $"[player-death-diagnostic] stage=dialogue-armed cause={activeCause} route={activeRoute} location={location.NameOrUniqueName} event-id={currentEvent?.id ?? "<null>"}",
            LogLevel.Debug
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            Game1.player is Farmer farmer
            && PlayerDeathCausePolicy.ShouldForgetPendingCause(
                Game1.killScreen,
                farmer.health
            )
        )
        {
            causes.Forget(farmer);
        }
        ClearExpiredEvent();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        causes.Clear();
        ClearRuntime();
    }

    private void TryReplaceSpeak(Event @event, string[] args)
    {
        if (
            activeEvent is null
            || !ReferenceEquals(activeEvent, @event)
            || args is null
            || args.Length < 3
            || string.IsNullOrWhiteSpace(args[1])
        )
            return;

        var actor = args[1].TrimEnd('?');
        var keys = ResolveReplacementKeys(actor);
        if (!speakDiagnosticLogged)
        {
            speakDiagnosticLogged = true;
            monitor.Log(
                $"[hunger-death-diagnostic] stage=speak-seen route={activeRoute} actor={actor} args-length={args.Length} replacement-available={keys is not null}",
                LogLevel.Debug
            );
        }

        if (keys is null)
            return;

        var first = translation.Get(keys.Value.First).ToString();
        var second = translation.Get(keys.Value.Second).ToString();
        pendingDisplayedDialogueText = string.Concat(first, "#$b#", second);
        args[2] = pendingDisplayedDialogueText;
        pendingDisplayedDialogueDiagnostic = true;
        pendingDisplayedDialogueActor = actor;
        monitor.Log(
            $"[hunger-death-diagnostic] stage=speak-replaced route={activeRoute} actor={actor} first={NormalizeDiagnosticText(first)} second={NormalizeDiagnosticText(second)}",
            LogLevel.Debug
        );
        ClearRuntime();
    }

    private void ReplaceDisplayedDialogue(NPC? speaker)
    {
        if (
            !pendingDisplayedDialogueDiagnostic
            || speaker is null
            || !string.Equals(
                speaker.Name,
                pendingDisplayedDialogueActor,
                StringComparison.Ordinal
            )
            || speaker.CurrentDialogue is null
            || speaker.CurrentDialogue.Count == 0
        )
        {
            return;
        }

        speaker.CurrentDialogue.Pop();
        speaker.CurrentDialogue.Push(
            new Dialogue(speaker, null, pendingDisplayedDialogueText)
        );
        monitor.Log(
            $"[hunger-death-diagnostic] stage=dialogue-object-replaced actor={speaker.Name}",
            LogLevel.Debug
        );
    }

    private void LogDisplayedDialogue(NPC? speaker)
    {
        if (
            !pendingDisplayedDialogueDiagnostic
            || speaker is null
            || !string.Equals(
                speaker.Name,
                pendingDisplayedDialogueActor,
                StringComparison.Ordinal
            )
            || speaker.CurrentDialogue is null
            || speaker.CurrentDialogue.Count == 0
        )
        {
            return;
        }

        var dialogue = speaker.CurrentDialogue.Peek();
        var text = dialogue?.getCurrentDialogue() ?? string.Empty;
        monitor.Log(
            $"[hunger-death-diagnostic] stage=dialogue-displayed actor={speaker.Name} translation-key={dialogue?.TranslationKey ?? "<null>"} text={NormalizeDiagnosticText(text)}",
            LogLevel.Debug
        );
        pendingDisplayedDialogueDiagnostic = false;
        pendingDisplayedDialogueActor = string.Empty;
        pendingDisplayedDialogueText = string.Empty;
    }

    private static string NormalizeDiagnosticText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "<empty>";

        var normalized = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 240
            ? normalized
            : string.Concat(normalized.AsSpan(0, 240), "...");
    }

    private (string First, string Second)? ResolveReplacementKeys(string actor)
    {
        if (
            activeCause == PlayerDeathCause.Darkness
            && activeRoute == DeathEventRoute.Hospital
            && actor == "Harvey"
        )
        {
            return ("darkness-death.hospital.1", "darkness-death.hospital.2");
        }

        if (
            activeCause == PlayerDeathCause.Hunger
            && activeRoute == DeathEventRoute.Hospital
            && actor == "Harvey"
        )
        {
            return ("hunger-death.hospital.1", "hunger-death.hospital.2");
        }

        if (activeCause != PlayerDeathCause.Hunger)
            return null;

        if (activeRoute == DeathEventRoute.Mine)
        {
            var spouse = Game1.player?.spouse;
            if (
                !string.IsNullOrWhiteSpace(spouse)
                && string.Equals(actor, spouse, StringComparison.Ordinal)
            )
                return null;

            return actor switch
            {
                "Linus" => ("hunger-death.mine.linus.1", "hunger-death.mine.linus.2"),
                "Robin" => ("hunger-death.mine.robin.1", "hunger-death.mine.robin.2"),
                "Clint" => ("hunger-death.mine.clint.1", "hunger-death.mine.clint.2"),
                "Maru" => ("hunger-death.mine.maru.1", "hunger-death.mine.maru.2"),
                _ => null,
            };
        }

        if (activeRoute == DeathEventRoute.Island)
        {
            return actor switch
            {
                "Willy" => ("hunger-death.island.willy.2", "hunger-death.island.willy.3"),
                "Leo" => ("hunger-death.island.leo.2", "hunger-death.island.leo.3"),
                _ => null,
            };
        }

        return null;
    }

    private static DeathEventRoute ClassifyRoute(GameLocation location)
    {
        if (string.Equals(location.NameOrUniqueName, "Hospital", StringComparison.Ordinal))
            return DeathEventRoute.Hospital;
        if (string.Equals(location.NameOrUniqueName, "IslandSouth", StringComparison.Ordinal))
            return DeathEventRoute.Island;
        if (
            location is MineShaft
            || string.Equals(location.NameOrUniqueName, "Mine", StringComparison.Ordinal)
            || location.NameOrUniqueName.StartsWith("UndergroundMine", StringComparison.Ordinal)
        )
            return DeathEventRoute.Mine;
        return DeathEventRoute.None;
    }

    private void ClearExpiredEvent()
    {
        if (
            activeEvent is not null
            && !ReferenceEquals(Game1.currentLocation?.currentEvent, activeEvent)
        )
        {
            monitor.Log(
                $"[hunger-death-diagnostic] stage=runtime-expired location={Game1.currentLocation?.NameOrUniqueName ?? "<null>"} active-event-id={activeEvent.id} current-event-id={Game1.currentLocation?.currentEvent?.id ?? "<null>"}",
                LogLevel.Debug
            );
            ClearRuntime();
        }
    }
}
