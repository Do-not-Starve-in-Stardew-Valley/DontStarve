#nullable enable

using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Keeps Default darkness damage on Farmer.takeDamage while making its vanilla hurt cue explicit.
/// Farmer.takeDamage returns void, so the scoped prefix records whether the vanilla method reached
/// its own "ow" request without changing that call's execution. The caller can then replay "ow"
/// only when the vanilla method did not request it (for example, because an early-return branch
/// skipped the sound). This preserves the vanilla timing and keeps exactly one vanilla cue
/// alongside the independent darkness-attack cue.
/// </summary>
internal static class DarknessVanillaHurtSoundBridge
{
    private const string VanillaHurtSoundId = "ow";

    [ThreadStatic]
    private static int observationDepth;

    [ThreadStatic]
    private static bool vanillaHurtSoundObserved;

    private static bool installed;

    /// <summary>Whether the observer prefix is active and can make a missing-call decision.</summary>
    internal static bool IsInstalled => installed;

    internal static bool TryInstall(string harmonyId, IMonitor monitor)
    {
        if (installed)
            return true;

        try
        {
            var target = AccessTools.Method(
                typeof(Character),
                nameof(Character.playNearbySoundAll)
            );
            var prefix = AccessTools.Method(
                typeof(DarknessVanillaHurtSoundBridge),
                nameof(Prefix)
            );
            if (target is null || prefix is null)
            {
                monitor.Log(
                    "Darkness vanilla hurt sound bridge target unavailable; Default damage keeps the original sound path.",
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
                "Darkness vanilla hurt sound bridge installed for Default damage.",
                LogLevel.Trace
            );
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Darkness vanilla hurt sound bridge failed ({exception.GetType().Name}: {exception.Message}); Default damage keeps the original sound path.",
                LogLevel.Warn
            );
            return false;
        }
    }

    /// <summary>Opens a scope only when the runtime prefix was installed successfully.</summary>
    internal static Scope Enter()
    {
        if (!installed)
            return new Scope(active: false);

        if (observationDepth == 0)
            vanillaHurtSoundObserved = false;
        observationDepth++;
        return new Scope(active: true);
    }

    /// <summary>Harmony prefix for Character.playNearbySoundAll.</summary>
    internal static bool Prefix(string audioName)
    {
        if (
            observationDepth <= 0
            || !string.Equals(audioName, VanillaHurtSoundId, StringComparison.Ordinal)
        )
        {
            return true;
        }

        // This is an observer only. Returning false here would replace the vanilla call and can
        // lose the original timing/side effects of Farmer.takeDamage's sound branch.
        vanillaHurtSoundObserved = true;
        return true;
    }

    internal sealed class Scope : IDisposable
    {
        private readonly bool active;
        private bool disposed;

        internal Scope(bool active)
        {
            this.active = active;
        }

        internal bool VanillaHurtSoundObserved =>
            active && DarknessVanillaHurtSoundBridge.vanillaHurtSoundObserved;

        public void Dispose()
        {
            if (disposed || !active)
                return;

            disposed = true;
            observationDepth = Math.Max(0, observationDepth - 1);
            if (observationDepth == 0)
                vanillaHurtSoundObserved = false;
        }
    }
}
