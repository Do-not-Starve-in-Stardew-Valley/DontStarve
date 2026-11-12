#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 进食结算接缝（镜像 HungerFoodCompletionPatch 形态）：prefix 快照血量，postfix 用快照差值
/// 回调宿主结算。差值已含原版钳制与 773/434/349/874 特例，宿主无需复刻原版公式。
/// 本类不写任何业务逻辑，只做快照与转发；本地玩家/物品/开关等门控全部在宿主回调里做。
/// 安装失败 Warn 不静默（与饥饿同款纪律）。
/// </summary>
internal static class SmapiHealthFoodCompletionPatch
{
    private static readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private static IMonitor? monitor;
    private static Harmony? harmony;
    private static string ownerId = string.Empty;
    private static Action<Farmer, Item?, int>? settleFood;

    internal static bool IsInstalled => harmony is not null;

    internal static bool TryInstall(
        string modId,
        IMonitor log,
        Action<Farmer, Item?, int> settleCallback
    )
    {
        if (harmony is not null)
        {
            if (
                string.Equals(
                    ownerId,
                    modId + ".Stats.Health.FoodCompletion",
                    StringComparison.Ordinal
                )
            )
            {
                // 同 owner 重入只换回调，不重复 patch（镜像 Hunger 形态）。
                settleFood = settleCallback;
                monitor = log;
                return true;
            }

            log.Log(
                "Health food completion patch is already owned by a different mod ID.",
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
                "Farmer.doneEating patch target was not found; Health food effects remain disabled.",
                LogLevel.Warn
            );
            return false;
        }

        var candidateOwnerId = modId + ".Stats.Health.FoodCompletion";
        var candidateHarmony = new Harmony(candidateOwnerId);
        var prefix = AccessTools.Method(
            typeof(SmapiHealthFoodCompletionPatch),
            nameof(DoneEatingPrefix)
        );
        var postfix = AccessTools.Method(
            typeof(SmapiHealthFoodCompletionPatch),
            nameof(DoneEatingPostfix)
        );
        if (prefix is null || postfix is null)
        {
            log.Log(
                "Health food completion patch methods were not found; Health food effects remain disabled.",
                LogLevel.Error
            );
            return false;
        }

        settleFood = settleCallback;
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
            log.Log("Health food completion patch installed.", LogLevel.Trace);
            return true;
        }
        catch (Exception exception)
        {
            candidateHarmony.Unpatch(target, HarmonyPatchType.All, candidateOwnerId);
            settleFood = null;
            monitor = null;
            log.Log(
                $"Health food completion patch failed ({exception.GetType().Name}: {exception.Message}); Health food effects remain disabled.",
                LogLevel.Warn
            );
            return false;
        }
    }

    internal static void ResetSessionDiagnostics()
    {
        loggedReasons.Clear();
    }

    private static void DoneEatingPrefix(Farmer __instance, out int __state)
    {
        __state = __instance.health;
    }

    private static void DoneEatingPostfix(Farmer __instance, int __state)
    {
        // 只转发快照差值（appliedVanillaDelta）；不判空、不门控、不改血。
        var callback = settleFood;
        if (callback is null)
            return;

        try
        {
            callback(__instance, __instance.itemToEat, __instance.health - __state);
        }
        catch (Exception exception)
        {
            LogOnce(
                $"health-food-completion-failed:{exception.GetType().Name}",
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
