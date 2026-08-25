#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Draw-only gate for the original generated mine-wall sconce lights. The source lights remain in
/// Stardew's shared/current collections, so ladder, elevator, lava, player, and multiplayer light
/// ownership are not changed by a cosmetic darkness rule.
/// </summary>
internal static class MineWallSconceRenderPatch
{
    private const string MineLightIdPrefix = "Mines_";
    private const string MineWallSconceIdSuffix = "_5";

    private static Func<int, NaturalDarknessSceneState>? sceneStateProvider;
    private static IMonitor? monitor;
    private static bool providerFailureLogged;

    internal static bool TryInstall(
        Harmony harmony,
        IMonitor patchMonitor,
        Func<int, NaturalDarknessSceneState> provider
    )
    {
        if (harmony is null)
            throw new ArgumentNullException(nameof(harmony));
        if (patchMonitor is null)
            throw new ArgumentNullException(nameof(patchMonitor));
        if (provider is null)
            throw new ArgumentNullException(nameof(provider));

        var target = AccessTools.Method(
            typeof(LightSource),
            nameof(LightSource.Draw),
            new[] { typeof(SpriteBatch), typeof(GameLocation), typeof(float) }
        );
        var prefix = AccessTools.Method(
            typeof(MineWallSconceRenderPatch),
            nameof(Prefix)
        );
        if (target is null || prefix is null)
        {
            patchMonitor.Log(
                "Mine wall sconce draw target was unavailable; native mine wall lights are unchanged.",
                LogLevel.Warn
            );
            return false;
        }

        try
        {
            sceneStateProvider = provider;
            monitor = patchMonitor;
            providerFailureLogged = false;
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            return true;
        }
        catch (Exception exception)
        {
            ClearProvider(provider);
            TryUnpatch(harmony, target, patchMonitor);
            patchMonitor.Log(
                $"Mine wall sconce draw patch failed open ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
            return false;
        }
    }

    internal static void Uninstall(
        Harmony harmony,
        IMonitor patchMonitor,
        Func<int, NaturalDarknessSceneState> provider
    )
    {
        ClearProvider(provider);
        var target = AccessTools.Method(
            typeof(LightSource),
            nameof(LightSource.Draw),
            new[] { typeof(SpriteBatch), typeof(GameLocation), typeof(float) }
        );
        if (target is not null)
            TryUnpatch(harmony, target, patchMonitor);
    }

    private static bool Prefix(LightSource __instance, GameLocation location)
    {
        if (!IsNativeMineWallSconce(__instance) || location is not MineShaft mine)
            return true;

        var provider = sceneStateProvider;
        if (provider is null)
            return true;

        try
        {
            var sceneState = provider(Context.ScreenId);
            if (!sceneState.IsActive)
                return true;

            var isSkullCavern = mine.getMineArea() == 121;
            return MineWallSconcePolicy.ShouldDraw(
                new MineWallSconceInput(
                    sceneState.Phase,
                    sceneState.PhaseElapsedRealSeconds,
                    Game1.timeOfDay,
                    Game1.getStartingToGetDarkTime(mine),
                    mine.mineLevel,
                    isSkullCavern,
                    isSkullCavern && mine.GetAdditionalDifficulty() > 0
                )
            );
        }
        catch (Exception exception)
        {
            if (!providerFailureLogged)
            {
                providerFailureLogged = true;
                monitor?.Log(
                    $"Mine wall sconce state provider failed open ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return true;
        }
    }

    private static bool IsNativeMineWallSconce(LightSource source)
    {
        var id = source.Id;
        // Do not filter by sconce texture alone: original ladder, elevator, and lava lights also
        // use it. MineShaft creates wall sconces with the exact Mines_<level>_<x>_<y>_5 identity.
        return source.textureIndex.Value == LightSource.sconceLight
            && source.lightContext.Value == LightSource.LightContext.None
            && source.PlayerID == 0L
            && id is not null
            && id.StartsWith(MineLightIdPrefix, StringComparison.Ordinal)
            && id.EndsWith(MineWallSconceIdSuffix, StringComparison.Ordinal);
    }

    private static void TryUnpatch(
        Harmony harmony,
        MethodBase target,
        IMonitor patchMonitor
    )
    {
        try
        {
            harmony.Unpatch(target, HarmonyPatchType.Prefix, harmony.Id);
        }
        catch (Exception exception)
        {
            patchMonitor.Log(
                $"Mine wall sconce draw patch cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }
    }

    private static void ClearProvider(Func<int, NaturalDarknessSceneState> provider)
    {
        if (!ReferenceEquals(sceneStateProvider, provider))
            return;

        sceneStateProvider = null;
        monitor = null;
        providerFailureLogged = false;
    }
}
