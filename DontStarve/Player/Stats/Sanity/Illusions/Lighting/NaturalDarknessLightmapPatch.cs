#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Applies a plan to Stardew's own native base-light inputs only while DrawLighting builds the
/// lightmap. Outdoors read outdoorLight, ordinary interiors read ambientLight, and MineShaft
/// supplies a per-level color; all three paths must be covered for the same natural-darkness rule
/// to have a real visual effect. Local LightSource instances are drawn by Stardew afterwards.
/// </summary>
internal static class NaturalDarknessLightmapPatch
{
    private static Func<int, double>? progressProvider;
    private static IMonitor? monitor;
    private static bool providerFailureLogged;
    private static MethodInfo? mineLightingGetter;
    private static MethodInfo? mineLightingDrawAdapter;
    private static bool mineDrawTranspilerMatched;
    private static string? lastLoggedMineLocation;
    private static int lastLoggedMineBlendBucket = -1;

    [ThreadStatic]
    private static LightingDrawContext? activeDrawContext;

    internal static bool TryInstall(
        Harmony harmony,
        IMonitor patchMonitor,
        Func<int, double> provider
    )
    {
        if (harmony is null)
            throw new ArgumentNullException(nameof(harmony));
        if (patchMonitor is null)
            throw new ArgumentNullException(nameof(patchMonitor));
        if (provider is null)
            throw new ArgumentNullException(nameof(provider));

        var drawLightingTarget = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.DrawLighting),
            new[] { typeof(GameTime), typeof(RenderTarget2D) }
        );
        var drawWorldTarget = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.DrawWorld),
            new[] { typeof(GameTime), typeof(RenderTarget2D) }
        );
        var mineLightingTarget = AccessTools.Method(
            typeof(MineShaft),
            nameof(MineShaft.getLightingColor),
            new[] { typeof(GameTime) }
        );
        var mineLightingAdapter = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(GetAdjustedMineLightingColor),
            new[] { typeof(MineShaft), typeof(GameTime) }
        );
        var drawWorldPrefix = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(DrawWorldPrefix)
        );
        var prefix = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(Prefix)
        );
        var postfix = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(Postfix)
        );
        var finalizer = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(Finalizer)
        );
        var drawLightingTranspiler = AccessTools.Method(
            typeof(NaturalDarknessLightmapPatch),
            nameof(DrawLightingTranspiler)
        );
        if (
            drawLightingTarget is null
            || drawWorldTarget is null
            || mineLightingTarget is null
            || mineLightingAdapter is null
            || drawWorldPrefix is null
            || prefix is null
            || postfix is null
            || finalizer is null
            || drawLightingTranspiler is null
        )
        {
            patchMonitor.Log(
                "Natural darkness lightmap patch target was unavailable; natural darkness is disabled.",
                LogLevel.Error
            );
            return false;
        }

        try
        {
            progressProvider = provider;
            monitor = patchMonitor;
            providerFailureLogged = false;
            mineLightingGetter = mineLightingTarget;
            mineLightingDrawAdapter = mineLightingAdapter;
            mineDrawTranspilerMatched = false;
            harmony.Patch(
                drawWorldTarget,
                prefix: new HarmonyMethod(drawWorldPrefix)
            );
            harmony.Patch(
                drawLightingTarget,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix),
                finalizer: new HarmonyMethod(finalizer),
                transpiler: new HarmonyMethod(drawLightingTranspiler)
            );
            if (!mineDrawTranspilerMatched)
            {
                patchMonitor.Log(
                    "Natural darkness mine lightmap bridge could not find Stardew's verified DrawLighting mine-color call; mine-only darkness is unavailable while other locations remain active.",
                    LogLevel.Warn
                );
            }
            return true;
        }
        catch (Exception exception)
        {
            ClearProvider(provider);
            UnpatchTargets(
                harmony,
                drawWorldTarget,
                drawLightingTarget,
                patchMonitor
            );
            patchMonitor.Log(
                $"Natural darkness lightmap patch failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return false;
        }
    }

    internal static void Uninstall(
        Harmony harmony,
        IMonitor patchMonitor,
        Func<int, double> provider
    )
    {
        ClearProvider(provider);
        var drawLightingTarget = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.DrawLighting),
            new[] { typeof(GameTime), typeof(RenderTarget2D) }
        );
        var drawWorldTarget = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.DrawWorld),
            new[] { typeof(GameTime), typeof(RenderTarget2D) }
        );
        UnpatchTargets(
            harmony,
            drawWorldTarget,
            drawLightingTarget,
            patchMonitor
        );
    }

    private static void DrawWorldPrefix()
    {
        if (Game1.drawLighting || GetLightmapWhiteBlend() <= 0f)
            return;

        // Game1 otherwise skips DrawLighting entirely for a naturally white interior. Leave the
        // native flag set for this frame so its later lightmap composition and final sampler see
        // the same dark scene; Stardew recalculates the flag on every subsequent update.
        Game1.drawLighting = true;
    }

    private static void Prefix(out LightingPatchState __state)
    {
        var originalOutdoorLight = Game1.outdoorLight;
        var originalAmbientLight = Game1.ambientLight;
        var blend = GetLightmapWhiteBlend();
        if (blend <= 0f)
        {
            activeDrawContext = null;
            __state = default;
            return;
        }

        var appliedOutdoorLight = Color.Lerp(
            originalOutdoorLight,
            Color.White,
            blend
        );
        var appliedAmbientLight = Color.Lerp(
            originalAmbientLight,
            Color.White,
            blend
        );
        Game1.outdoorLight = appliedOutdoorLight;
        Game1.ambientLight = appliedAmbientLight;
        activeDrawContext = new LightingDrawContext(blend);
        __state = new LightingPatchState(
            originalOutdoorLight,
            appliedOutdoorLight,
            originalAmbientLight,
            appliedAmbientLight,
            WasApplied: true
        );
    }

    private static void Postfix(LightingPatchState __state)
    {
        RestoreLighting(__state);
    }

    private static Exception? Finalizer(
        Exception? __exception,
        LightingPatchState __state
    )
    {
        // Postfix covers the normal path; finalizer covers original-method exceptions.
        RestoreLighting(__state);
        return __exception;
    }

    private static IEnumerable<CodeInstruction> DrawLightingTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var list = new List<CodeInstruction>(instructions);
        var mineLightingTarget = mineLightingGetter;
        var adapter = mineLightingDrawAdapter;
        if (mineLightingTarget is null || adapter is null)
            return list;

        var matchIndex = -1;
        var matchCount = 0;
        for (var index = 0; index < list.Count; index++)
        {
            if (!list[index].Calls(mineLightingTarget))
                continue;

            matchIndex = index;
            matchCount++;
        }

        // Stardew 1.6.15 has exactly one MineShaft base-color call in DrawLighting. Replacing the
        // call site avoids relying on a tiny getter postfix which can be bypassed by JIT or another
        // patch, while keeping the mine's replicated lighting value untouched.
        if (matchCount != 1)
        {
            monitor?.Log(
                $"Natural darkness mine lightmap bridge found {matchCount} MineShaft lighting calls in DrawLighting; expected exactly one, so it left mine lighting unchanged.",
                LogLevel.Warn
            );
            return list;
        }

        list[matchIndex].opcode = OpCodes.Call;
        list[matchIndex].operand = adapter;
        mineDrawTranspilerMatched = true;
        return list;
    }

    private static Color GetAdjustedMineLightingColor(MineShaft mine, GameTime time)
    {
        var original = mine.getLightingColor(time);
        var context = activeDrawContext;
        if (!context.HasValue || context.Value.WhiteBlend <= 0f)
            return original;

        var adjusted = Color.Lerp(original, Color.White, context.Value.WhiteBlend);
        LogMineRenderDiagnostic(mine, original, adjusted, context.Value.WhiteBlend);
        return adjusted;
    }

    private static void LogMineRenderDiagnostic(
        MineShaft mine,
        Color original,
        Color adjusted,
        float whiteBlend
    )
    {
        var bucket = whiteBlend >= 0.995f
            ? 1000
            : whiteBlend is >= 0.295f and <= 0.305f
                ? 300
                : -1;
        if (bucket < 0)
            return;

        var location = mine.NameOrUniqueName;
        if (
            lastLoggedMineBlendBucket == bucket
            && string.Equals(lastLoggedMineLocation, location, StringComparison.Ordinal)
        )
        {
            return;
        }

        lastLoggedMineLocation = location;
        lastLoggedMineBlendBucket = bucket;
        monitor?.Log(
            $"Natural darkness mine render diagnostic (bridge=draw-lighting-transpiler, location={location}, white-blend={whiteBlend:0.###}, original=({original.R},{original.G},{original.B}), applied=({adjusted.R},{adjusted.G},{adjusted.B})).",
            LogLevel.Debug
        );
    }

    private static void RestoreLighting(LightingPatchState state)
    {
        // Do not overwrite a later patch which intentionally changed a native light value.
        if (
            state.WasApplied
            && Game1.outdoorLight == state.AppliedOutdoorLight
        )
        {
            Game1.outdoorLight = state.OriginalOutdoorLight;
        }
        if (
            state.WasApplied
            && Game1.ambientLight == state.AppliedAmbientLight
        )
        {
            Game1.ambientLight = state.OriginalAmbientLight;
        }

        activeDrawContext = null;
    }

    private static float GetLightmapWhiteBlend()
    {
        var provider = progressProvider;
        if (provider is null)
            return 0f;

        try
        {
            return NaturalDarknessTransitionPolicy.GetLightmapWhiteBlend(
                provider(Context.ScreenId)
            );
        }
        catch (Exception exception)
        {
            if (!providerFailureLogged)
            {
                providerFailureLogged = true;
                monitor?.Log(
                    $"Natural darkness progress provider failed closed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return 0f;
        }
    }

    private static void UnpatchTargets(
        Harmony harmony,
        System.Reflection.MethodBase? drawWorldTarget,
        System.Reflection.MethodBase? drawLightingTarget,
        IMonitor patchMonitor
    )
    {
        try
        {
            if (drawWorldTarget is not null)
                harmony.Unpatch(drawWorldTarget, HarmonyPatchType.Prefix, harmony.Id);
            if (drawLightingTarget is not null)
            {
                harmony.Unpatch(drawLightingTarget, HarmonyPatchType.Prefix, harmony.Id);
                harmony.Unpatch(drawLightingTarget, HarmonyPatchType.Postfix, harmony.Id);
                harmony.Unpatch(drawLightingTarget, HarmonyPatchType.Finalizer, harmony.Id);
                harmony.Unpatch(drawLightingTarget, HarmonyPatchType.Transpiler, harmony.Id);
            }
        }
        catch (Exception exception)
        {
            patchMonitor.Log(
                $"Natural darkness lightmap patch cleanup failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }
    }

    private static void ClearProvider(Func<int, double> provider)
    {
        if (!ReferenceEquals(progressProvider, provider))
            return;

        progressProvider = null;
        monitor = null;
        providerFailureLogged = false;
        mineLightingGetter = null;
        mineLightingDrawAdapter = null;
        mineDrawTranspilerMatched = false;
        lastLoggedMineLocation = null;
        lastLoggedMineBlendBucket = -1;
        activeDrawContext = null;
    }

    private readonly record struct LightingDrawContext(float WhiteBlend);

    private readonly record struct LightingPatchState(
        Color OriginalOutdoorLight,
        Color AppliedOutdoorLight,
        Color OriginalAmbientLight,
        Color AppliedAmbientLight,
        bool WasApplied
    );
}
