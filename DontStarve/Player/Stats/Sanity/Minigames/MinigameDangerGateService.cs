#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using DontStarve.Display;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Minigames;
using StardewValley.Monsters;
using xTile.Dimensions;

namespace DontStarve.Player.Stats.Sanity.Minigames;

internal enum BlockedMinigame
{
    PrairieKing,
    MineCart,
    CalicoJack,
    Slots,
    Darts,
    CraneGame,
}

/// <summary>
/// Runtime boundary for the six dangerous arcade/casino interactions. Patches are installed at
/// both the initial action and the confirmation/paid creation stage so a danger transition during
/// a dialogue cannot consume money, chips, fade the screen, or create a minigame.
/// </summary>
internal sealed class MinigameDangerGateService : IDisposable
{
    private const int NearbyMonsterDistanceTiles = 20;
    private const long DuplicateMessageWindowTicks = 15;
    private const string BlockedMessageKey = "minigame-danger-blocked";
    private const string AbigailStoryEventAssetName = "Data\\Events\\SeedShop";
    private const string AbigailStoryEventId = "1";
    private const string AbigailStoryCutsceneCommand = "cutscene AbigailGame";

    private static MinigameDangerGateService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SmapiHostileShadowHost hostileShadowHost;
    private readonly Func<bool> isBlockingEnabled;
    private readonly Harmony harmony;
    private readonly List<MethodBase> patchedMethods = new();
    private long lastBlockedMessageTick = long.MinValue;
    private bool disposed;

    internal MinigameDangerGateService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        SmapiHostileShadowHost hostileShadowHost,
        Func<bool> isBlockingEnabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.hostileShadowHost =
            hostileShadowHost ?? throw new ArgumentNullException(nameof(hostileShadowHost));
        this.isBlockingEnabled =
            isBlockingEnabled ?? throw new ArgumentNullException(nameof(isBlockingEnabled));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));

        harmony = new Harmony(string.Concat(modId, ".Sanity4.MinigameDangerGate"));
        activeInstance = this;
        InstallPatches();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var method in patchedMethods)
        {
            try
            {
                harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);
            }
            catch (Exception exception)
            {
                monitor.Log(
                    $"Danger minigame patch cleanup failed ({method.Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
        }
        patchedMethods.Clear();
        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
    }

    private void InstallPatches()
    {
        TryPatch(
            typeof(GameLocation),
            nameof(GameLocation.performAction),
            new[] { typeof(string[]), typeof(Farmer), typeof(Location) },
            nameof(PrefixGameLocationPerformAction)
        );
        TryPatch(
            typeof(MovieTheater),
            nameof(MovieTheater.performAction),
            new[] { typeof(string[]), typeof(Farmer), typeof(Location) },
            nameof(PrefixMovieTheaterPerformAction)
        );
        TryPatch(
            typeof(IslandSouthEastCave),
            nameof(IslandSouthEastCave.performAction),
            new[] { typeof(string[]), typeof(Farmer), typeof(Location) },
            nameof(PrefixIslandSouthEastCavePerformAction)
        );
        TryPatch(
            typeof(GameLocation),
            nameof(GameLocation.answerDialogueAction),
            new[] { typeof(string), typeof(string[]) },
            nameof(PrefixGameLocationAnswerDialogueAction)
        );
        TryPatch(
            typeof(IslandSouthEastCave),
            nameof(IslandSouthEastCave.answerDialogueAction),
            new[] { typeof(string), typeof(string[]) },
            nameof(PrefixIslandSouthEastCaveAnswerDialogueAction)
        );
        TryPatch(
            typeof(GameLocation),
            nameof(GameLocation.showPrairieKingMenu),
            Type.EmptyTypes,
            nameof(PrefixShowPrairieKingMenu)
        );
        TryPatch(
            typeof(MovieTheater),
            "tryToStartCraneGame",
            new[] { typeof(Farmer), typeof(string) },
            nameof(PrefixTryToStartCraneGame)
        );
    }

    private void TryPatch(
        Type declaringType,
        string methodName,
        Type[] argumentTypes,
        string prefixName
    )
    {
        var original = AccessTools.Method(declaringType, methodName, argumentTypes);
        var prefix = AccessTools.Method(typeof(MinigameDangerGateService), prefixName);
        if (original is null || prefix is null)
        {
            monitor.Log(
                $"Danger minigame patch target unavailable ({declaringType.FullName}.{methodName}).",
                LogLevel.Warn
            );
            return;
        }

        try
        {
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            patchedMethods.Add(original);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Danger minigame patch failed ({declaringType.Name}.{methodName}: {exception.Message}).",
                LogLevel.Warn
            );
        }
    }

    private static bool PrefixGameLocationPerformAction(
        string[] action,
        Farmer who,
        Location tileLocation,
        ref bool __result
    )
    {
        _ = tileLocation;
        if (!TryGetInitialGame(action, out var game))
            return true;
        return activeInstance?.Allow(game, who, notify: true, storyCandidate: game == BlockedMinigame.PrairieKing) is true
            ? true
            : Consume(ref __result);
    }

    private static bool PrefixMovieTheaterPerformAction(
        string[] action,
        Farmer who,
        Location tileLocation,
        ref bool __result
    )
    {
        _ = tileLocation;
        if (!string.Equals(GetAction(action), "CraneGame", StringComparison.Ordinal))
            return true;
        return activeInstance?.Allow(BlockedMinigame.CraneGame, who, notify: true) is true
            ? true
            : Consume(ref __result);
    }

    private static bool PrefixIslandSouthEastCavePerformAction(
        string[] action,
        Farmer who,
        Location tileLocation,
        ref bool __result
    )
    {
        _ = tileLocation;
        if (!string.Equals(GetAction(action), "DartsGame", StringComparison.Ordinal))
            return true;
        return activeInstance?.Allow(BlockedMinigame.Darts, who, notify: true) is true
            ? true
            : Consume(ref __result);
    }

    private static bool PrefixGameLocationAnswerDialogueAction(
        string questionAndAnswer,
        string[] questionParams,
        ref bool __result
    )
    {
        _ = questionParams;
        if (!TryGetConfirmationGame(questionAndAnswer, out var game))
            return true;
        var storyCandidate = game == BlockedMinigame.PrairieKing;
        return activeInstance?.Allow(game, Game1.player, notify: true, storyCandidate) is true
            ? true
            : Consume(ref __result);
    }

    private static bool PrefixIslandSouthEastCaveAnswerDialogueAction(
        string questionAndAnswer,
        string[] questionParams,
        ref bool __result
    )
    {
        _ = questionParams;
        if (!string.Equals(questionAndAnswer, "DartsGame_Yes", StringComparison.Ordinal))
            return true;
        return activeInstance?.Allow(BlockedMinigame.Darts, Game1.player, notify: true) is true
            ? true
            : Consume(ref __result);
    }

    private static bool PrefixShowPrairieKingMenu()
    {
        return activeInstance?.Allow(
            BlockedMinigame.PrairieKing,
            Game1.player,
            notify: true,
            storyCandidate: true
        ) is true;
    }

    private static bool PrefixTryToStartCraneGame(Farmer who, string whichAnswer)
    {
        if (!string.Equals(whichAnswer, "yes", StringComparison.OrdinalIgnoreCase))
            return true;
        return activeInstance?.Allow(BlockedMinigame.CraneGame, who, notify: true) is true;
    }

    private bool Allow(
        BlockedMinigame game,
        Farmer? player,
        bool notify,
        bool storyCandidate = false
    )
    {
        if (disposed || player is null)
            return false;

        var decision = Evaluate(player, storyCandidate && IsAbigailStoryEvent(player));
        if (decision.Allowed)
            return true;
        if (notify)
            ShowBlockedMessage();
        monitor.Log(
            $"Danger minigame blocked ({game}, reason={decision.Reason}).",
            LogLevel.Trace
        );
        return false;
    }

    private MinigameDangerDecision Evaluate(Farmer player, bool storyException)
    {
        var location = player.currentLocation;
        var nearbyMonster = location is not null && HasNearbyMonster(player, location);
        var hostileShadowTargeting = hostileShadowHost.IsPlayerTargeted(player);
        var isMultiplayer = Game1.IsMultiplayer;
        var multiplayerDanger = false;
        var multiplayerStateAvailable = true;
        if (isMultiplayer)
        {
            multiplayerStateAvailable = TryGetDangerState(player, out multiplayerDanger);
        }

        return MinigameDangerGatePolicy.Evaluate(
            isBlockingEnabled(),
            storyException,
            nearbyMonster,
            hostileShadowTargeting,
            isMultiplayer,
            multiplayerDanger,
            multiplayerStateAvailable
        );
    }

    private bool TryGetDangerState(Farmer player, out bool danger)
    {
        danger = false;
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID);
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
        )
        {
            return false;
        }

        foreach (var tierId in tier.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.Danger, StringComparison.Ordinal))
            {
                danger = true;
                break;
            }
        }
        return true;
    }

    private static bool HasNearbyMonster(Farmer player, GameLocation location)
    {
        var playerCenter = player.GetBoundingBox().Center;
        var maximumDistance = NearbyMonsterDistanceTiles * Game1.tileSize;
        var maximumDistanceSquared = (double)maximumDistance * maximumDistance;
        foreach (var character in location.characters)
        {
            if (character is not Monster monster)
                continue;
            var monsterCenter = monster.GetBoundingBox().Center;
            var dx = (double)playerCenter.X - monsterCenter.X;
            var dy = (double)playerCenter.Y - monsterCenter.Y;
            if ((dx * dx) + (dy * dy) <= maximumDistanceSquared)
                return true;
        }
        return false;
    }

    private static bool IsAbigailStoryEvent(Farmer player)
    {
        var currentEvent = Game1.CurrentEvent;
        return Game1.eventUp
            && currentEvent is not null
            && !currentEvent.isFestival
            && ReferenceEquals(currentEvent.farmer, player)
            && string.Equals(
                currentEvent.fromAssetName,
                AbigailStoryEventAssetName,
                StringComparison.Ordinal
            )
            && string.Equals(currentEvent.id, AbigailStoryEventId, StringComparison.Ordinal)
            && string.Equals(
                currentEvent.GetCurrentCommand(),
                AbigailStoryCutsceneCommand,
                StringComparison.OrdinalIgnoreCase
            )
            && Game1.currentMinigame is AbigailGame
            && AbigailGame.playingWithAbigail;
    }

    private void ShowBlockedMessage()
    {
        var currentTick = (long)Game1.ticks;
        if (
            lastBlockedMessageTick != long.MinValue
            && currentTick - lastBlockedMessageTick < DuplicateMessageWindowTicks
        )
            return;
        lastBlockedMessageTick = currentTick;

        var translated = helper.Translation.Get(BlockedMessageKey);
        Game1.addHUDMessage(new HUDMessage(translated, HUDMessage.error_type));
    }

    private static bool Consume(ref bool result)
    {
        result = true;
        return false;
    }

    private static bool TryGetInitialGame(string[]? action, out BlockedMinigame game)
    {
        game = default;
        return GetAction(action) switch
        {
            "Arcade_Prairie" => Set(out game, BlockedMinigame.PrairieKing),
            "Arcade_Minecart" => Set(out game, BlockedMinigame.MineCart),
            "ClubCards" => Set(out game, BlockedMinigame.CalicoJack),
            "BlackJack" => Set(out game, BlockedMinigame.CalicoJack),
            "ClubSlots" => Set(out game, BlockedMinigame.Slots),
            _ => false,
        };
    }

    private static bool TryGetConfirmationGame(
        string? questionAndAnswer,
        out BlockedMinigame game
    )
    {
        game = default;
        return questionAndAnswer switch
        {
            "MinecartGame_Endless" => Set(out game, BlockedMinigame.MineCart),
            "MinecartGame_Progress" => Set(out game, BlockedMinigame.MineCart),
            "CalicoJack_Play" => Set(out game, BlockedMinigame.CalicoJack),
            "CalicoJackHS_Play" => Set(out game, BlockedMinigame.CalicoJack),
            "CowboyGame_NewGame" => Set(out game, BlockedMinigame.PrairieKing),
            "CowboyGame_Continue" => Set(out game, BlockedMinigame.PrairieKing),
            _ => false,
        };
    }

    private static bool Set(out BlockedMinigame target, BlockedMinigame value)
    {
        target = value;
        return true;
    }

    private static string? GetAction(string[]? action) =>
        action is { Length: > 0 } ? action[0] : null;
}
