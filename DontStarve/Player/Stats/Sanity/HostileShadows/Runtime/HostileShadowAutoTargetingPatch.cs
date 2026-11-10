#nullable enable

using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Shared targeting seam: wraps the match predicate of Utility.findClosestMonsterWithinRange so
/// every vanilla caller (ice orb, hungry frog, magic quiver, ...) skips binding-hidden shadows,
/// and callers currently marked by HostileShadowAutoTargetCallerScopePatch skip all hostile
/// shadows. The caller's own predicate still runs for everything else, so ordinary vanilla
/// monsters keep their original behavior. The marker must never be derived from the match
/// argument's shape (null vs not) — future vanilla callers would be misclassified.
/// </summary>
internal static class HostileShadowAutoTargetingPatch
{
    private static IMonitor? monitor;
    private static bool firstExclusionLogged;

    internal static bool TryInstall(string harmonyId, IMonitor patchMonitor)
    {
        if (patchMonitor is null)
            throw new ArgumentNullException(nameof(patchMonitor));

        try
        {
            var target = AccessTools.Method(
                typeof(Utility),
                nameof(Utility.findClosestMonsterWithinRange),
                new[]
                {
                    typeof(GameLocation),
                    typeof(Vector2),
                    typeof(int),
                    typeof(bool),
                    typeof(Func<Monster, bool>),
                }
            );
            var prefix = AccessTools.Method(
                typeof(HostileShadowAutoTargetingPatch),
                nameof(Prefix)
            );
            if (target is null || prefix is null)
            {
                patchMonitor.Log(
                    "Hostile shadow auto-target patch target unavailable; vanilla auto-targeting keeps seeing binding-hidden shadows.",
                    LogLevel.Warn
                );
                return false;
            }

            new Harmony(harmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix)
            );
            monitor = patchMonitor;
            firstExclusionLogged = false;
            patchMonitor.Log(
                "Hostile shadow auto-target filter installed for Utility.findClosestMonsterWithinRange.",
                LogLevel.Trace
            );
            return true;
        }
        catch (Exception exception)
        {
            monitor = null;
            patchMonitor.Log(
                $"Hostile shadow auto-target patch failed ({exception.GetType().Name}: {exception.Message}); vanilla auto-targeting remains active.",
                LogLevel.Warn
            );
            return false;
        }
    }

    // Harmony ref-injection: the original call runs with the wrapped predicate.
    private static void Prefix(ref Func<Monster, bool>? match)
    {
        match = WrapMatch(
            match,
            HostileShadowAutoTargetCallScope.IgnoreAllHostileShadows
        );
    }

    /// <summary>
    /// Predicate factory kept separate from the prefix so the evaluation order is explicit: shadow
    /// rules first, then the caller's own match (magic quiver name blacklist etc.). One delegate
    /// allocation per targeting call is fine — the vanilla callers run at 0.25~2 Hz, never per
    /// monster or per frame.
    /// </summary>
    internal static Func<Monster, bool> WrapMatch(
        Func<Monster, bool>? original,
        bool ignoreAllHostileShadows
    )
    {
        return monster =>
        {
            if (
                monster is HostileShadowMonster
                && HostileShadowAutoTargetRules.ExcludesFromAutoTargeting(
                    ignoreAllHostileShadows,
                    isHostileShadow: true,
                    bindingHidden: HasEnabledModData(
                        monster,
                        HostileShadowVanillaCombatContracts.BindingHiddenModDataKey
                    )
                )
            )
            {
                LogFirstExclusion();
                return false;
            }

            return original?.Invoke(monster) ?? true;
        };
    }

    private static bool HasEnabledModData(Monster monster, string key)
    {
        return monster.modData.TryGetValue(key, out var value)
            && HostileShadowAutoTargetRules.IsEnabledModDataValue(value);
    }

    private static void LogFirstExclusion()
    {
        if (firstExclusionLogged || monitor is null)
            return;

        firstExclusionLogged = true;
        monitor.Log(
            "Hostile shadow auto-target filter excluded its first shadow candidate; further exclusions are silent.",
            LogLevel.Trace
        );
    }
}
