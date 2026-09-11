#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace DontStarve.Player.Stats.Sanity.Minigames;

/// <summary>
/// Observes the vanilla Farmer damage seam and terminates only the local player's multiplayer
/// BobberBar after a real Monster hit. The host additionally forwards a target-private fact when
/// it applies damage to a remote Farmer, since that Farmer's menu exists only on the target peer.
/// </summary>
internal sealed class MinigameFishingInterruptionService : IDisposable
{
    internal const string MessageType = "Sanity.MinigameFishingInterruption.v1";

    private static MinigameFishingInterruptionService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly Func<string> sessionIdProvider;
    private readonly Func<bool> isBlockingEnabled;
    private readonly Harmony harmony;
    private MethodBase? patchedTakeDamage;
    private long nextEventId;
    private string lastReceivedSessionId = string.Empty;
    private long lastReceivedEventId;
    private bool disposed;

    internal MinigameFishingInterruptionService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        Func<string> sessionIdProvider,
        Func<bool> isBlockingEnabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        this.sessionIdProvider = sessionIdProvider
            ?? throw new ArgumentNullException(nameof(sessionIdProvider));
        this.isBlockingEnabled = isBlockingEnabled
            ?? throw new ArgumentNullException(nameof(isBlockingEnabled));

        harmony = new Harmony(string.Concat(modId, ".Sanity4.MinigameFishingInterruption"));
        activeInstance = this;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        InstallTakeDamagePatch();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        if (patchedTakeDamage is not null)
        {
            try
            {
                harmony.Unpatch(patchedTakeDamage, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception exception)
            {
                monitor.Log(
                    $"Fishing interruption patch cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Error
                );
            }
            patchedTakeDamage = null;
        }

        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
    }

    private void InstallTakeDamagePatch()
    {
        var original = AccessTools.Method(
            typeof(Farmer),
            nameof(Farmer.takeDamage),
            new[] { typeof(int), typeof(bool), typeof(Monster) }
        );
        var prefix = AccessTools.Method(
            typeof(MinigameFishingInterruptionService),
            nameof(TakeDamagePrefix)
        );
        var postfix = AccessTools.Method(
            typeof(MinigameFishingInterruptionService),
            nameof(TakeDamagePostfix)
        );
        if (original is null || prefix is null || postfix is null)
        {
            monitor.Log(
                "Fishing interruption takeDamage patch target unavailable; the safety interruption is disabled.",
                LogLevel.Error
            );
            return;
        }

        try
        {
            harmony.Patch(
                original,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix)
            );
            patchedTakeDamage = original;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Fishing interruption takeDamage patch failed ({exception.GetType().Name}: {exception.Message}); the safety interruption is disabled.",
                LogLevel.Error
            );
        }
    }

    private static void TakeDamagePrefix(
        Farmer __instance,
        Monster? damager,
        ref int __state
    )
    {
        __state = 0;
        var owner = activeInstance;
        if (owner is null || !owner.ShouldObserveDamage(__instance, damager))
            return;

        // Zero means no observation; +1 preserves a valid before-health value of zero without
        // allocating a per-call state object on the hot damage path.
        __state = __instance.health + 1;
    }

    private static void TakeDamagePostfix(Farmer __instance, int __state)
    {
        if (__state <= 0)
            return;

        var owner = activeInstance;
        if (owner is null)
            return;

        try
        {
            owner.OnDamageResolved(__instance, __state - 1);
        }
        catch (Exception exception)
        {
            owner.monitor.Log(
                $"Fishing interruption damage observer failed open ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private bool ShouldObserveDamage(Farmer target, Monster? damager)
    {
        return !disposed
            && Game1.IsMultiplayer
            && isBlockingEnabled()
            && target is not null
            && damager is not null;
    }

    private void OnDamageResolved(Farmer target, int healthBefore)
    {
        if (disposed || !Game1.IsMultiplayer || !isBlockingEnabled())
            return;

        var healthAfter = target.health;
        if (healthAfter >= healthBefore)
            return;

        if (IsLocalTarget(target))
        {
            TryInterruptLocalBobberBar(healthBefore, healthAfter);
            return;
        }

        if (Game1.IsMasterGame)
            SendRemoteInterruption(target);
    }

    private void SendRemoteInterruption(Farmer target)
    {
        if (target.UniqueMultiplayerID == 0)
            return;

        var sessionId = sessionIdProvider();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            monitor.Log(
                "Fishing interruption notification skipped because the multiplayer session is unavailable.",
                LogLevel.Warn
            );
            return;
        }

        var message = new MinigameFishingInterruptionMessage
        {
            SessionId = sessionId,
            EventId = ++nextEventId,
            TargetPlayerId = target.UniqueMultiplayerID,
        };
        helper.Multiplayer.SendMessage(
            message,
            MessageType,
            new[] { modId },
            new[] { target.UniqueMultiplayerID }
        );
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        _ = sender;
        if (
            disposed
            || !Game1.IsMultiplayer
            || Game1.IsMasterGame
            || !string.Equals(e.FromModID, modId, StringComparison.Ordinal)
            || !string.Equals(e.Type, MessageType, StringComparison.Ordinal)
            || !IsHostSender(e.FromPlayerID)
            || !isBlockingEnabled()
        )
        {
            return;
        }

        try
        {
            var localPlayer = Game1.player;
            var message = e.ReadAs<MinigameFishingInterruptionMessage>();
            var reason = "fishing-interruption.local-player-unavailable";
            if (
                localPlayer is null
                || !MinigameFishingInterruptionProtocol.IsValid(
                    message,
                    sessionIdProvider(),
                    localPlayer.UniqueMultiplayerID,
                    out reason
                )
            )
            {
                monitor.Log(
                    $"Fishing interruption notification ignored ({reason}).",
                    LogLevel.Trace
                );
                return;
            }

            if (!AcceptEvent(message!))
            {
                monitor.Log(
                    "Fishing interruption notification ignored because it is stale or duplicated.",
                    LogLevel.Trace
                );
                return;
            }

            TryInterruptLocalBobberBarFromHost();
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Fishing interruption notification could not be processed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private bool AcceptEvent(MinigameFishingInterruptionMessage message)
    {
        if (!string.Equals(lastReceivedSessionId, message.SessionId, StringComparison.Ordinal))
        {
            lastReceivedSessionId = message.SessionId;
            lastReceivedEventId = 0;
        }

        if (message.EventId <= lastReceivedEventId)
            return false;

        lastReceivedEventId = message.EventId;
        return true;
    }

    private void TryInterruptLocalBobberBar(int healthBefore, int healthAfter)
    {
        if (Game1.player is not { } player)
            return;

        var bobberBar = Game1.activeClickableMenu as BobberBar;
        if (
            !MinigameFishingInterruptionPolicy.ShouldInterrupt(
                isMultiplayer: Game1.IsMultiplayer,
                blockingEnabled: isBlockingEnabled(),
                hasBobberBar: bobberBar is not null,
                bobberBarResultHandled: bobberBar?.handledFishResult ?? false,
                damageSourceIsMonster: true,
                healthBefore,
                healthAfter
            )
        )
        {
            return;
        }

        if (bobberBar is null || bobberBar.handledFishResult)
            return;
        InterruptBobberBar(bobberBar, player);
    }

    private void TryInterruptLocalBobberBarFromHost()
    {
        if (Game1.player is not { } player)
            return;

        var bobberBar = Game1.activeClickableMenu as BobberBar;
        if (
            bobberBar is null
            || bobberBar.handledFishResult
            || !Game1.IsMultiplayer
            || !isBlockingEnabled()
        )
        {
            return;
        }

        InterruptBobberBar(bobberBar, player);
    }

    private void InterruptBobberBar(BobberBar bobberBar, Farmer player)
    {
        // This is deliberately not emergencyShutDown(): vanilla adds a 500ms shake/fade there,
        // while this safety path must release the player immediately without a failure animation.
        bobberBar.handledFishResult = true;
        bobberBar.fadeIn = false;
        bobberBar.fadeOut = false;
        bobberBar.everythingShakeTimer = 0f;
        bobberBar.everythingShake = Vector2.Zero;
        BobberBar.unReelSound?.Stop(AudioStopOptions.Immediate);
        BobberBar.reelSound?.Stop(AudioStopOptions.Immediate);
        Game1.playSound("fishEscape");

        player.completelyStopAnimatingOrDoingAction();
        if (player.CurrentTool is FishingRod fishingRod)
            fishingRod.doneFishing(player, consumeBaitAndTackle: true);
        Game1.exitActiveMenu();
        monitor.Log(
            $"Fishing BobberBar interrupted by actual Monster damage (player={player.UniqueMultiplayerID}).",
            LogLevel.Trace
        );
    }

    private static bool IsLocalTarget(Farmer target)
    {
        return Game1.player is { } local
            && local.UniqueMultiplayerID == target.UniqueMultiplayerID;
    }

    private static bool IsHostSender(long playerId)
    {
        return Game1.MasterPlayer is { } host
            && host.UniqueMultiplayerID == playerId;
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        lastReceivedSessionId = string.Empty;
        lastReceivedEventId = 0;
    }
}
