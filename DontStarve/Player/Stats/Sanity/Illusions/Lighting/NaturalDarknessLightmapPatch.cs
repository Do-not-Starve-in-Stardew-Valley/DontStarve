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
/// 一次 DrawLighting 的绘制上下文。两种模式共用同一插值出口：
/// <list type="bullet">
/// <item>Relative（黑暗路径，氛围关闭）：applied = Lerp(原生底色, 白, Progress)——进度受计划
/// 上限钳制（矿井黄昏 0.30 / 全黑 1.0），逐字节同 2026-08-18 以来的旧行为。</item>
/// <item>Absolute（氛围路径，2026-11-17）：applied = Target——服务端按计划时长推进的当前色
/// （DST 环境光换算减色），跨计划连续，矿井色不再相对原生底色混合。</item>
/// </list>
/// </summary>
internal readonly struct NaturalDarknessDrawContext
{
    private NaturalDarknessDrawContext(
        bool hasPlan,
        bool absolute,
        float progress,
        Color target
    )
    {
        HasPlan = hasPlan;
        Absolute = absolute;
        Progress = progress;
        Target = target;
    }

    public bool HasPlan { get; }

    public bool Absolute { get; }

    public float Progress { get; }

    public Color Target { get; }

    public static NaturalDarknessDrawContext None { get; } =
        new(false, false, 0f, Color.White);

    public static NaturalDarknessDrawContext Relative(float progress) =>
        new(true, false, progress, Color.White);

    public static NaturalDarknessDrawContext FromAbsolute(Color target) =>
        new(true, true, 1f, target);
}

/// <summary>
/// Applies a plan to Stardew's own native base-light inputs only while DrawLighting builds the
/// lightmap. Outdoors read outdoorLight, ordinary interiors read ambientLight, and MineShaft
/// supplies a per-level color; all three paths must be covered for the same natural-darkness rule
/// to have a real visual effect. Local LightSource instances are drawn by Stardew afterwards.
/// </summary>
internal static class NaturalDarknessLightmapPatch
{
    private static Func<int, NaturalDarknessDrawContext>? contextProvider;
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
        Func<int, NaturalDarknessDrawContext> provider
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
            contextProvider = provider;
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
        Func<int, NaturalDarknessDrawContext> provider
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

    private static NaturalDarknessDrawContext GetContextOrDefault()
    {
        var provider = contextProvider;
        if (provider is null)
            return NaturalDarknessDrawContext.None;

        try
        {
            return provider(Context.ScreenId);
        }
        catch (Exception exception)
        {
            if (!providerFailureLogged)
            {
                providerFailureLogged = true;
                monitor?.Log(
                    $"Natural darkness context provider failed closed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return NaturalDarknessDrawContext.None;
        }
    }

    private static void DrawWorldPrefix()
    {
        if (Game1.drawLighting)
            return;

        var context = GetContextOrDefault();
        // 相对模式进度 0 等价无暗化；绝对模式只要有计划就接管（Day 也会以极浅底色画光照）。
        if (!context.HasPlan || (!context.Absolute && context.Progress <= 0f))
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
        var context = GetContextOrDefault();
        if (!context.HasPlan)
        {
            activeDrawContext = null;
            __state = default;
            return;
        }

        Color appliedOutdoorLight;
        Color appliedAmbientLight;
        if (context.Absolute)
        {
            appliedOutdoorLight = context.Target;
            appliedAmbientLight = context.Target;
        }
        else
        {
            appliedOutdoorLight = Color.Lerp(
                originalOutdoorLight,
                context.Target,
                context.Progress
            );
            appliedAmbientLight = Color.Lerp(
                originalAmbientLight,
                context.Target,
                context.Progress
            );
        }
        Game1.outdoorLight = appliedOutdoorLight;
        Game1.ambientLight = appliedAmbientLight;
        activeDrawContext = new LightingDrawContext(context);
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
        if (
            !context.HasValue
            || !context.Value.Context.HasPlan
            || (!context.Value.Context.Absolute && context.Value.Context.Progress <= 0f)
        )
        {
            return original;
        }

        var adjusted = context.Value.Context.Absolute
            ? context.Value.Context.Target
            : Color.Lerp(
                original,
                context.Value.Context.Target,
                context.Value.Context.Progress
            );
        LogMineRenderDiagnostic(mine, original, adjusted, context.Value.Context);
        return adjusted;
    }

    private static void LogMineRenderDiagnostic(
        MineShaft mine,
        Color original,
        Color adjusted,
        NaturalDarknessDrawContext context
    )
    {
        int bucket;
        if (context.Absolute)
        {
            // 绝对色模式按目标相位记录，不再按白混合分桶。
            bucket = 1000;
        }
        else
        {
            bucket = context.Progress >= 0.995f
                ? 1000
                : context.Progress is >= 0.295f and <= 0.305f
                    ? 300
                    : -1;
        }
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
            $"Natural darkness mine render diagnostic (bridge=draw-lighting-transpiler, location={location}, "
                + $"mode={(context.Absolute ? "absolute" : "relative")}, progress={context.Progress:0.###}, "
                + $"original=({original.R},{original.G},{original.B}), applied=({adjusted.R},{adjusted.G},{adjusted.B})).",
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

    private static void ClearProvider(Func<int, NaturalDarknessDrawContext> provider)
    {
        if (!ReferenceEquals(contextProvider, provider))
            return;

        contextProvider = null;
        monitor = null;
        providerFailureLogged = false;
        mineLightingGetter = null;
        mineLightingDrawAdapter = null;
        mineDrawTranspilerMatched = false;
        lastLoggedMineLocation = null;
        lastLoggedMineBlendBucket = -1;
        activeDrawContext = null;
    }

    private readonly record struct LightingDrawContext(
        NaturalDarknessDrawContext Context
    );

    private readonly record struct LightingPatchState(
        Color OriginalOutdoorLight,
        Color AppliedOutdoorLight,
        Color OriginalAmbientLight,
        Color AppliedAmbientLight,
        bool WasApplied
    );
}
