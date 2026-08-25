#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Stardew 1.6.15 / MonoGame OpenGL final-composition adapter. It reuses each Game1 instance's
/// existing screen/uiScreen targets, applies the pixel effect only to screen, and then draws
/// uiScreen unchanged. No process-global viewport or camera state is mutated.
/// </summary>
internal sealed class SanityWorldCompositionRuntimeAdapter : IDisposable
{
    internal const int MaximumScreenParameters =
        SanityVisualController.MaximumOwners;

    private static SanityWorldCompositionRuntimeAdapter? activeAdapter;

    private readonly IMonitor monitor;
    private readonly string patchOwnerId;
    private readonly Dictionary<int, SanityWorldCompositionParameters>
        parametersByScreen = new();
    private readonly Harmony? harmony;
    private readonly MethodInfo? shouldDrawMethod;
    private readonly MethodInfo? singleCompositionMethod;
    private readonly MethodInfo? splitCompositionMethod;
    private Effect? worldEffect;
    private string? runtimeFailureReason;
    private bool enabled;
    private bool disposed;

    internal SanityWorldCompositionRuntimeAdapter(
        string manifestId,
        IMonitor monitor,
        bool enabled
    )
    {
        if (string.IsNullOrWhiteSpace(manifestId))
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));

        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.enabled = enabled;
        patchOwnerId = string.Concat(manifestId, ".Sanity4.WorldComposition");

        shouldDrawMethod = AccessTools.DeclaredMethod(
            typeof(Game1),
            nameof(Game1.ShouldDrawOnBuffer),
            Type.EmptyTypes
        );
        singleCompositionMethod = AccessTools.DeclaredMethod(
            typeof(Game1),
            "renderScreenBuffer",
            new[] { typeof(RenderTarget2D) }
        );
        splitCompositionMethod = AccessTools.DeclaredMethod(
            typeof(Game1),
            nameof(Game1.DrawSplitScreenWindow),
            Type.EmptyTypes
        );

        var frameworkAssembly = typeof(GraphicsDevice).Assembly;
        var gameVersion = Game1.version;
        var frameworkVersion =
            frameworkAssembly.GetName().Version?.ToString() ?? string.Empty;
        var openGlBackend =
            frameworkAssembly.GetType("MonoGame.OpenGL.GL", throwOnError: false)
            is not null;
        var gateReason = SanityRendererCapabilityGate.Validate(
            gameVersion,
            frameworkVersion,
            openGlBackend,
            Shape(shouldDrawMethod, typeof(bool), Type.EmptyTypes),
            Shape(
                singleCompositionMethod,
                typeof(void),
                new[] { typeof(RenderTarget2D) }
            ),
            Shape(splitCompositionMethod, typeof(void), Type.EmptyTypes)
        );
        if (gateReason is not null)
        {
            Capability = Unavailable(
                gateReason,
                gameVersion,
                frameworkVersion,
                openGlBackend
            );
            return;
        }
        if (activeAdapter is not null)
        {
            Capability = Unavailable(
                "visual.renderer.process-owner-conflict",
                gameVersion,
                frameworkVersion,
                openGlBackend
            );
            return;
        }

        try
        {
            worldEffect = new Effect(
                Game1.graphics.GraphicsDevice,
                SanityWorldEffectBytecode.Bytes
            );
        }
        catch (Exception exception)
        {
            Capability = Unavailable(
                "visual.renderer.effect-create-failed",
                gameVersion,
                frameworkVersion,
                openGlBackend
            );
            ReportFailure(
                "visual.renderer.effect-create-failed",
                exception
            );
            return;
        }

        try
        {
            harmony = new Harmony(patchOwnerId);
            var shouldDrawPostfix = AccessTools.DeclaredMethod(
                typeof(SanityWorldCompositionRuntimeAdapter),
                nameof(AfterShouldDrawOnBuffer)
            );
            var singlePrefix = AccessTools.DeclaredMethod(
                typeof(SanityWorldCompositionRuntimeAdapter),
                nameof(BeforeRenderScreenBuffer)
            );
            var splitPrefix = AccessTools.DeclaredMethod(
                typeof(SanityWorldCompositionRuntimeAdapter),
                nameof(BeforeDrawSplitScreenWindow)
            );
            if (
                shouldDrawPostfix is null
                || singlePrefix is null
                || splitPrefix is null
            )
            {
                throw new InvalidOperationException(
                    "The visual patch methods don't match their frozen signatures."
                );
            }

            harmony.Patch(
                shouldDrawMethod!,
                postfix: new HarmonyMethod(shouldDrawPostfix)
                {
                    priority = Priority.Last,
                }
            );
            harmony.Patch(
                singleCompositionMethod!,
                prefix: new HarmonyMethod(singlePrefix)
                {
                    priority = Priority.Last,
                }
            );
            harmony.Patch(
                splitCompositionMethod!,
                prefix: new HarmonyMethod(splitPrefix)
                {
                    priority = Priority.Last,
                }
            );
            if (
                !IsOwnedPatchInstalled(
                    shouldDrawMethod!,
                    patchOwnerId,
                    HarmonyPatchType.Postfix
                )
                || !IsOwnedPatchInstalled(
                    singleCompositionMethod!,
                    patchOwnerId,
                    HarmonyPatchType.Prefix
                )
                || !IsOwnedPatchInstalled(
                    splitCompositionMethod!,
                    patchOwnerId,
                    HarmonyPatchType.Prefix
                )
            )
            {
                throw new InvalidOperationException(
                    "The installed visual patches couldn't be verified by owner."
                );
            }

            activeAdapter = this;
            Capability = new SanityRendererCapability(
                SanityRendererCapabilityStatus.Available,
                "visual.renderer.available",
                gameVersion,
                frameworkVersion,
                SanityRendererCapabilityGate.ExpectedBackend,
                patchOwnerId
            );
        }
        catch (Exception exception)
        {
            UnpatchInstalledTargets();
            worldEffect?.Dispose();
            worldEffect = null;
            Capability = Unavailable(
                "visual.renderer.patch-install-failed",
                gameVersion,
                frameworkVersion,
                openGlBackend
            );
            ReportFailure(
                "visual.renderer.patch-install-failed",
                exception
            );
        }
    }

    internal SanityRendererCapability Capability { get; }

    internal string EffectiveReason =>
        runtimeFailureReason ?? Capability.Reason;

    internal int ParameterCount => parametersByScreen.Count;

    internal void SetEnabled(bool value)
    {
        if (disposed)
            return;

        enabled = value;
        if (!enabled)
            parametersByScreen.Clear();
    }

    internal void UpdateScreen(SanityWorldCompositionParameters parameters)
    {
        if (
            disposed
            || !enabled
            || !Capability.IsAvailable
            || runtimeFailureReason is not null
            || parameters.ScreenId < 0
            || !parameters.IsActive
        )
        {
            parametersByScreen.Remove(parameters.ScreenId);
            return;
        }
        if (
            !parametersByScreen.ContainsKey(parameters.ScreenId)
            && parametersByScreen.Count >= MaximumScreenParameters
        )
        {
            return;
        }

        parametersByScreen[parameters.ScreenId] = parameters;
    }

    internal bool RemoveScreen(int screenId)
    {
        return parametersByScreen.Remove(screenId);
    }

    internal void Clear()
    {
        parametersByScreen.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        parametersByScreen.Clear();
        if (ReferenceEquals(activeAdapter, this))
            activeAdapter = null;
        UnpatchInstalledTargets();
        worldEffect?.Dispose();
        worldEffect = null;
        disposed = true;
    }

    private bool HasActiveWorldComposition(int screenId)
    {
        return !disposed
            && enabled
            && Capability.IsAvailable
            && runtimeFailureReason is null
            && parametersByScreen.TryGetValue(screenId, out var parameters)
            && parameters.IsActive;
    }

    private bool TryDrawSingle(
        Game1 game,
        RenderTarget2D? targetScreen
    )
    {
        if (
            !HasActiveWorldComposition(game.instanceId)
            || targetScreen is null
            || targetScreen.IsContentLost
            || game.takingMapScreenshot
            || LocalMultiplayer.IsLocalMultiplayer()
            || !parametersByScreen.TryGetValue(
                game.instanceId,
                out var parameters
            )
        )
        {
            return false;
        }

        try
        {
            var device = Game1.graphics.GraphicsDevice;
            if (!EnsureEffect(device))
                return false;

            device.SetRenderTarget(null);
            device.Clear(Game1.bgColor);
            DrawWorldAndUi(
                targetScreen,
                game.uiScreen,
                Vector2.Zero,
                game.instanceOptions.zoomLevel,
                Vector2.Zero,
                game.instanceOptions.uiScale,
                parameters
            );
            return true;
        }
        catch (Exception exception)
        {
            FailRuntime("visual.renderer.single-composition-failed", exception);
            return false;
        }
    }

    private bool TryDrawSplit(Game1 game)
    {
        var screen = game.screen;
        if (
            !HasActiveWorldComposition(game.instanceId)
            || !LocalMultiplayer.IsLocalMultiplayer()
            || screen is null
            || screen.IsContentLost
            || !parametersByScreen.TryGetValue(
                game.instanceId,
                out var parameters
            )
        )
        {
            return false;
        }

        var device = Game1.graphics.GraphicsDevice;
        var previousViewport = device.Viewport;
        try
        {
            if (!EnsureEffect(device))
                return false;

            device.SetRenderTarget(null);
            device.Viewport = Game1.defaultDeviceViewport;
            var windowPosition = new Vector2(
                game.localMultiplayerWindow.X,
                game.localMultiplayerWindow.Y
            );
            DrawWorldAndUi(
                screen,
                game.uiScreen,
                windowPosition,
                game.instanceOptions.zoomLevel,
                windowPosition,
                game.instanceOptions.uiScale,
                parameters
            );
            return true;
        }
        catch (Exception exception)
        {
            FailRuntime("visual.renderer.split-composition-failed", exception);
            return false;
        }
        finally
        {
            device.Viewport = previousViewport;
        }
    }

    private void DrawWorldAndUi(
        RenderTarget2D world,
        RenderTarget2D? ui,
        Vector2 worldPosition,
        float worldScale,
        Vector2 uiPosition,
        float uiScale,
        SanityWorldCompositionParameters parameters
    )
    {
        var effect = worldEffect!;
        var overscan = parameters.OverscanPixels;
        var position = new Vector2(
            worldPosition.X + parameters.OffsetX - overscan,
            worldPosition.Y + parameters.OffsetY - overscan
        );
        var scale = new Vector2(
            worldScale + ((overscan * 2f) / world.Width),
            worldScale + ((overscan * 2f) / world.Height)
        );
        // The planner retains saturation/grayscale values for a later opt-in revisit. RGB now
        // transports a normalized distortion phase plus the independent (1 - Sanity)^2 colour
        // blend and day/dusk/night profile; alpha remains the localized edge-distortion amount.
        var colourPhase = parameters.InsanityColourBlend > 0f
            ? ResolveWorldColourPhase()
            : SanityWorldColourPhase.Day;
        var compositionInputs = new Color(
            ToVertexByte(parameters.DistortionPhase),
            ToVertexByte(parameters.InsanityColourBlend),
            SanityWorldColourPolicy.ToVertexCode(colourPhase),
            ToVertexByte(parameters.DistortionAmount)
        );

        Game1.spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.Opaque,
            SamplerState.LinearClamp,
            DepthStencilState.Default,
            RasterizerState.CullNone,
            effect
        );
        Game1.spriteBatch.Draw(
            world,
            position,
            world.Bounds,
            compositionInputs,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            1f
        );
        Game1.spriteBatch.End();

        if (ui is null || ui.IsContentLost)
            return;

        Game1.spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.Default,
            RasterizerState.CullNone
        );
        Game1.spriteBatch.Draw(
            ui,
            uiPosition,
            ui.Bounds,
            Color.White,
            0f,
            Vector2.Zero,
            uiScale,
            SpriteEffects.None,
            1f
        );
        Game1.spriteBatch.End();
    }

    private static SanityWorldColourPhase ResolveWorldColourPhase()
    {
        var location = Game1.currentLocation;
        if (location is null)
            return SanityWorldColourPhase.Day;

        return SanityWorldColourPolicy.ResolvePhase(
            Game1.timeOfDay,
            Game1.getStartingToGetDarkTime(location),
            Game1.getTrulyDarkTime(location)
        );
    }

    private static byte ToVertexByte(float value)
    {
        return (byte)Math.Clamp(
            (int)Math.Round(
                Math.Clamp(value, 0f, 1f) * byte.MaxValue,
                MidpointRounding.AwayFromZero
            ),
            byte.MinValue,
            byte.MaxValue
        );
    }

    private bool EnsureEffect(GraphicsDevice device)
    {
        if (
            worldEffect is not null
            && !worldEffect.IsDisposed
            && ReferenceEquals(worldEffect.GraphicsDevice, device)
        )
        {
            return true;
        }

        worldEffect?.Dispose();
        worldEffect = null;
        try
        {
            // This branch is a device/graphics-lifecycle recovery path, never the steady frame.
            worldEffect = new Effect(device, SanityWorldEffectBytecode.Bytes);
            return true;
        }
        catch (Exception exception)
        {
            FailRuntime("visual.renderer.effect-recreate-failed", exception);
            return false;
        }
    }

    private void FailRuntime(string reason, Exception exception)
    {
        if (runtimeFailureReason is not null)
            return;

        runtimeFailureReason = reason;
        parametersByScreen.Clear();
        ReportFailure(reason, exception);
    }

    private void ReportFailure(string reason, Exception exception)
    {
        monitor.Log(
            $"Sanity world composition failed closed ({reason}; {exception.GetType().Name}: {exception.Message}).",
            LogLevel.Error
        );
    }

    private SanityRendererCapability Unavailable(
        string reason,
        string gameVersion,
        string frameworkVersion,
        bool openGlBackend
    )
    {
        return new SanityRendererCapability(
            SanityRendererCapabilityStatus.Unavailable,
            reason,
            gameVersion,
            frameworkVersion,
            openGlBackend ? SanityRendererCapabilityGate.ExpectedBackend : "Unsupported",
            patchOwnerId
        );
    }

    private void UnpatchInstalledTargets()
    {
        if (harmony is null)
            return;

        TryUnpatch(shouldDrawMethod, HarmonyPatchType.Postfix);
        TryUnpatch(singleCompositionMethod, HarmonyPatchType.Prefix);
        TryUnpatch(splitCompositionMethod, HarmonyPatchType.Prefix);
    }

    private void TryUnpatch(
        MethodInfo? method,
        HarmonyPatchType patchType
    )
    {
        if (method is null || harmony is null)
            return;

        try
        {
            harmony.Unpatch(method, patchType, patchOwnerId);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Sanity world composition patch cleanup failed ({method.Name}; {exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private static SanityRendererMethodShape Shape(
        MethodInfo? method,
        Type expectedReturnType,
        Type[] expectedParameters
    )
    {
        if (method is null)
            return default;

        var parameters = method.GetParameters();
        var parameterTypesMatch = parameters.Length == expectedParameters.Length;
        if (parameterTypesMatch)
        {
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType == expectedParameters[index])
                    continue;
                parameterTypesMatch = false;
                break;
            }
        }
        return new SanityRendererMethodShape(
            Exists: true,
            method.IsStatic,
            method.IsVirtual,
            method.ReturnType == expectedReturnType,
            parameters.Length,
            parameterTypesMatch
        );
    }

    private static bool IsOwnedPatchInstalled(
        MethodInfo method,
        string ownerId,
        HarmonyPatchType patchType
    )
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;

        var patches = patchType == HarmonyPatchType.Prefix
            ? patchInfo.Prefixes
            : patchInfo.Postfixes;
        foreach (var patch in patches)
        {
            if (string.Equals(patch.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void AfterShouldDrawOnBuffer(
        Game1 __instance,
        ref bool __result
    )
    {
        var adapter = activeAdapter;
        if (
            adapter is not null
            && adapter.HasActiveWorldComposition(__instance.instanceId)
        )
        {
            __result = true;
        }
    }

    private static bool BeforeRenderScreenBuffer(
        Game1 __instance,
        RenderTarget2D? __0
    )
    {
        var adapter = activeAdapter;
        return adapter is null || !adapter.TryDrawSingle(__instance, __0);
    }

    private static bool BeforeDrawSplitScreenWindow(Game1 __instance)
    {
        var adapter = activeAdapter;
        return adapter is null || !adapter.TryDrawSplit(__instance);
    }
}
