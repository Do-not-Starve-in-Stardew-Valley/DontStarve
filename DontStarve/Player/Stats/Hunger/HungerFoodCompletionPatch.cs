#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

internal static class HungerFoodCompletionPatch
{
    private static readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private static HungerChangeService? service;
    private static IMonitor? monitor;
    private static Harmony? harmony;
    private static string ownerId = string.Empty;

    internal static bool IsInstalled => harmony is not null;

    internal static bool TryInstall(
        string modId,
        IMonitor log,
        HungerChangeService changeService
    )
    {
        if (harmony is not null)
        {
            if (string.Equals(ownerId, modId + ".hunger-food-completion", StringComparison.Ordinal))
            {
                service = changeService;
                monitor = log;
                return true;
            }

            log.Log(
                "Hunger food completion patch is already owned by a different mod ID.",
                LogLevel.Error
            );
            return false;
        }

        var target = AccessTools.DeclaredMethod(
            typeof(Farmer),
            nameof(Farmer.doneEating),
            Type.EmptyTypes
        );
        if (target is null)
        {
            log.Log(
                "Farmer.doneEating patch target was not found; legacy Hunger food requests remain enabled.",
                LogLevel.Warn
            );
            return false;
        }

        var candidateOwnerId = modId + ".hunger-food-completion";
        var candidateHarmony = new Harmony(candidateOwnerId);
        var prefix = AccessTools.Method(
            typeof(HungerFoodCompletionPatch),
            nameof(DoneEatingPrefix)
        );
        var postfix = AccessTools.Method(
            typeof(HungerFoodCompletionPatch),
            nameof(DoneEatingPostfix)
        );
        if (prefix is null || postfix is null)
        {
            log.Log(
                "Hunger food completion patch methods were not found; legacy Hunger food requests remain enabled.",
                LogLevel.Error
            );
            return false;
        }

        service = changeService;
        monitor = log;
        try
        {
            candidateHarmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix)
            );
            harmony = candidateHarmony;
            ownerId = candidateOwnerId;
            log.Log("Host-owned Hunger food completion patch installed.", LogLevel.Trace);
            return true;
        }
        catch (Exception exception)
        {
            candidateHarmony.Unpatch(target, HarmonyPatchType.All, candidateOwnerId);
            service = null;
            monitor = null;
            log.Log(
                $"Hunger food completion patch failed ({exception.GetType().Name}: {exception.Message}); legacy Hunger food requests remain enabled.",
                LogLevel.Warn
            );
            return false;
        }
    }

    internal static void ResetSessionDiagnostics()
    {
        loggedReasons.Clear();
    }

    private static void DoneEatingPrefix(Farmer __instance, out bool __state)
    {
        __state = __instance.isEating;
    }

    private static void DoneEatingPostfix(Farmer __instance, bool __state)
    {
        var currentService = service;
        var food = __instance.itemToEat;
        if (
            !HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                currentService?.Role ?? HungerAuthorityRole.None,
                HungerExtensions.IsEnabled,
                __state,
                food is not null && !string.IsNullOrWhiteSpace(food.ItemId)
            )
        )
        {
            return;
        }

        try
        {
            if (
                !ReferenceEquals(
                    Game1.GetPlayer(__instance.UniqueMultiplayerID, onlyOnline: true),
                    __instance
                )
            )
            {
                LogOnce(
                    "hunger-food-completion-player-is-not-the-online-farmer",
                    LogLevel.Trace
                );
                return;
            }

            var result = HungerExtensions.ApplyFood(__instance, food!);
            if (!result.Accepted)
                LogOnce($"hunger-food-completion-rejected:{result.Reason}", LogLevel.Warn);
        }
        catch (Exception exception)
        {
            LogOnce(
                $"hunger-food-completion-failed:{exception.GetType().Name}",
                LogLevel.Error,
                exception.Message
            );
        }
    }

    private static void LogOnce(string reason, LogLevel level, string? detail = null)
    {
        if (monitor is null || !loggedReasons.Add(reason))
            return;

        monitor.Log(
            string.IsNullOrWhiteSpace(detail)
                ? reason
                : $"{reason} ({detail})",
            level
        );
    }
}
