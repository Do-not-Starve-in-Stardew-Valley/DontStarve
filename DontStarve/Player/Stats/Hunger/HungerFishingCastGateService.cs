#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Blocks new fishing-rod casts while the local player is starving (Hunger 0 with the Hunger
/// system enabled). Only the cast start is gated: an already-fighting BobberBar is never
/// interrupted by hunger damage, because SmapiHungerDamageExecutor writes health directly and
/// never routes through Farmer.takeDamage where the multiplayer fishing interruption observes.
///
/// The patch mirrors the vanilla exhausted-stamina branch at the top of FishingRod.beginUsing:
/// performBeginUsingTool has already set CanMove=false / UsingTool=true / canReleaseTool=true,
/// so a blocked cast must reset exactly those states or the farmer is stuck holding the rod.
/// </summary>
internal sealed class HungerFishingCastGateService : IDisposable
{
    private const string StarvingCastBlockedMessageKey = "fishing-starving-blocked";
    private const long DuplicateMessageWindowTicks = 15;

    private static HungerFishingCastGateService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Harmony harmony;
    private MethodBase? patchedBeginUsing;
    private long lastBlockedMessageTick = long.MinValue;
    private bool disposed;

    internal HungerFishingCastGateService(
        IModHelper helper,
        IMonitor monitor,
        string modId
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));

        harmony = new Harmony(string.Concat(modId, ".Hunger.FishingCastGate"));
        activeInstance = this;
        InstallPatch();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (patchedBeginUsing is not null)
        {
            try
            {
                harmony.Unpatch(patchedBeginUsing, HarmonyPatchType.Prefix, harmony.Id);
            }
            catch (Exception exception)
            {
                monitor.Log(
                    $"Fishing cast gate patch cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            patchedBeginUsing = null;
        }

        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
    }

    private void InstallPatch()
    {
        var original = AccessTools.Method(
            typeof(FishingRod),
            nameof(FishingRod.beginUsing),
            new[] { typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer) }
        );
        var prefix = AccessTools.Method(
            typeof(HungerFishingCastGateService),
            nameof(PrefixBeginUsing)
        );
        if (original is null || prefix is null)
        {
            monitor.Log(
                "Fishing cast gate patch target unavailable; the starving cast gate is disabled.",
                LogLevel.Error
            );
            return;
        }

        try
        {
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            patchedBeginUsing = original;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Fishing cast gate patch failed ({exception.GetType().Name}: {exception.Message}); the starving cast gate is disabled.",
                LogLevel.Error
            );
        }
    }

    private static bool PrefixBeginUsing(FishingRod __instance, Farmer? who)
    {
        var owner = activeInstance;
        if (owner is null || owner.disposed || who is null)
            return true;

        if (
            !HungerFishingCastGatePolicy.ShouldBlock(
                isLocalPlayer: who.IsLocalPlayer,
                eventUp: Game1.eventUp,
                hungerEnabled: HungerExtensions.IsEnabled,
                starving: HungerStarvationRules.IsStarving(who.GetHunger())
            )
        )
        {
            return true;
        }

        owner.BlockCast(__instance, who);
        return false;
    }

    private void BlockCast(FishingRod rod, Farmer player)
    {
        // 1:1 镜像原版体力耗尽中止分支（FishingRod.beginUsing 开头）。performBeginUsingTool
        // 已先置 CanMove=false / UsingTool=true / canReleaseTool=true，这三项必须复位，
        // 否则玩家会卡在持杆状态；doneFishing(null) 与原版分支一致，未持杆时为安全空操作。
        if (!player.isEmoting)
            player.doEmote(36);
        player.CanMove = !Game1.eventUp;
        player.UsingTool = false;
        player.canReleaseTool = false;
        rod.doneFishing(null);
        ShowStarvingMessage();
        monitor.Log(
            "Fishing cast blocked because the local player is starving (Hunger 0).",
            LogLevel.Trace
        );
    }

    private void ShowStarvingMessage()
    {
        var currentTick = (long)Game1.ticks;
        if (
            lastBlockedMessageTick != long.MinValue
            && currentTick - lastBlockedMessageTick < DuplicateMessageWindowTicks
        )
            return;
        lastBlockedMessageTick = currentTick;

        var translated = helper.Translation.Get(StarvingCastBlockedMessageKey);
        Game1.addHUDMessage(new HUDMessage(translated, HUDMessage.error_type));
    }
}
