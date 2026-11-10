#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Companions;
using StardewValley.Objects.Trinkets;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Marks the vanilla callers whose targeting must ignore every hostile shadow state: the ice orb
/// trinket (IceOrbTrinketEffect.Update) and the hungry frog companion (HungryFrogCompanion.Update).
/// During a marked Update call, HostileShadowAutoTargetingPatch additionally excludes all hostile
/// shadows from findClosestMonsterWithinRange; the magic quiver stays unmarked so it keeps
/// targeting visible (dangerous) shadows by design. The marker is a plain static flag set in the
/// prefix and cleared in the finalizer — game logic is single-threaded and the finalizer runs even
/// when the original method throws.
/// </summary>
internal static class HostileShadowAutoTargetCallerScopePatch
{
    internal static bool TryInstall(string harmonyId, IMonitor monitor)
    {
        if (monitor is null)
            throw new ArgumentNullException(nameof(monitor));

        var iceOrbUpdate = AccessTools.Method(
            typeof(IceOrbTrinketEffect),
            nameof(TrinketEffect.Update),
            new[] { typeof(Farmer), typeof(GameTime), typeof(GameLocation) }
        );
        var frogUpdate = AccessTools.Method(
            typeof(HungryFrogCompanion),
            nameof(Companion.Update),
            new[] { typeof(GameTime), typeof(GameLocation) }
        );
        var prefix = AccessTools.Method(
            typeof(HostileShadowAutoTargetCallerScopePatch),
            nameof(MarkIgnoreAllHostileShadows)
        );
        var finalizer = AccessTools.Method(
            typeof(HostileShadowAutoTargetCallerScopePatch),
            nameof(ClearIgnoreAllHostileShadows)
        );
        if (prefix is null || finalizer is null)
        {
            monitor.Log(
                "Hostile shadow auto-target caller scope patch methods unavailable; ice orb and frog keep targeting shadows.",
                LogLevel.Warn
            );
            return false;
        }

        var allInstalled = true;
        allInstalled &= PatchTarget(
            monitor,
            iceOrbUpdate,
            "IceOrbTrinketEffect.Update",
            harmonyId,
            prefix,
            finalizer
        );
        allInstalled &= PatchTarget(
            monitor,
            frogUpdate,
            "HungryFrogCompanion.Update",
            harmonyId,
            prefix,
            finalizer
        );
        return allInstalled;
    }

    private static bool PatchTarget(
        IMonitor monitor,
        MethodInfo? target,
        string targetName,
        string harmonyId,
        MethodInfo prefix,
        MethodInfo finalizer
    )
    {
        if (target is null)
        {
            monitor.Log(
                $"Hostile shadow auto-target caller scope target {targetName} unavailable; that caller keeps targeting shadows.",
                LogLevel.Warn
            );
            return false;
        }

        try
        {
            new Harmony(harmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix),
                finalizer: new HarmonyMethod(finalizer)
            );
            monitor.Log(
                $"Hostile shadow auto-target caller scope installed for {targetName}.",
                LogLevel.Trace
            );
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Hostile shadow auto-target caller scope patch failed for {targetName} ({exception.GetType().Name}: {exception.Message}); that caller keeps targeting shadows.",
                LogLevel.Warn
            );
            return false;
        }
    }

    private static void MarkIgnoreAllHostileShadows()
    {
        HostileShadowAutoTargetCallScope.IgnoreAllHostileShadows = true;
    }

    private static Exception? ClearIgnoreAllHostileShadows(Exception? __exception)
    {
        HostileShadowAutoTargetCallScope.IgnoreAllHostileShadows = false;
        return __exception;
    }
}
