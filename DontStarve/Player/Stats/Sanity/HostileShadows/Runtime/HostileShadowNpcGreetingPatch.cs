#nullable enable

using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Prevents vanilla NPC social greetings from treating this mod's physical shadow monsters as
/// ordinary nearby characters. The filter is deliberately narrower than <see cref="Monster"/>:
/// vanilla monsters and all other NPCs keep their original behavior.
/// </summary>
internal static class HostileShadowNpcGreetingPatch
{
    private static bool installed;

    internal static bool TryInstall(string harmonyId, IMonitor monitor)
    {
        if (installed)
            return true;

        try
        {
            var target = AccessTools.Method(
                typeof(NPC),
                nameof(NPC.sayHiTo),
                new[] { typeof(Character) }
            );
            var prefix = AccessTools.Method(
                typeof(HostileShadowNpcGreetingPatch),
                nameof(Prefix)
            );
            if (target is null || prefix is null)
            {
                monitor.Log(
                    "Hostile shadow NPC greeting patch target unavailable; vanilla greeting behavior remains active.",
                    LogLevel.Warn
                );
                return false;
            }

            new Harmony(harmonyId).Patch(
                target,
                prefix: new HarmonyMethod(prefix)
            );
            installed = true;
            monitor.Log(
                "Hostile shadow NPC greeting filter installed for this mod's shadow monsters.",
                LogLevel.Trace
            );
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Hostile shadow NPC greeting patch failed ({exception.GetType().Name}: {exception.Message}); vanilla greeting behavior remains active.",
                LogLevel.Warn
            );
            return false;
        }
    }

    /// <summary>
    /// Harmony prefix for the final vanilla greeting sink. Harmless shadow projections are
    /// mod-private and never enter GameLocation.characters, so they cannot reach this method.
    /// </summary>
    internal static bool Prefix(Character character)
    {
        return character is not HostileShadowMonster;
    }
}
