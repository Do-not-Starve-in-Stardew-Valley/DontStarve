using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVisualRuntimeStaticTests
{
    [Fact]
    public void Adapter_composes_instance_owned_world_then_ui_without_global_viewport_mutation()
    {
        var service = ReadVisual();
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.Contains("Events.Display.RenderingHud", service, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Width", service, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Height", service, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.ShouldDrawOnBuffer)", runtime, StringComparison.Ordinal);
        Assert.Contains("\"renderScreenBuffer\"", runtime, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.DrawSplitScreenWindow)", runtime, StringComparison.Ordinal);
        Assert.Contains("game.instanceId", runtime, StringComparison.Ordinal);
        Assert.Contains("game.screen", runtime, StringComparison.Ordinal);
        Assert.Contains("game.uiScreen", runtime, StringComparison.Ordinal);
        Assert.Contains("Game1.defaultDeviceViewport", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.viewport =", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderedWorld +=", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Steady_composition_and_hud_paths_only_draw_cached_resources()
    {
        var service = ReadVisual();
        var hudStart = service.IndexOf("private void OnRenderingHud", StringComparison.Ordinal);
        var hudEnd = service.IndexOf("private void", hudStart + 1, StringComparison.Ordinal);
        var hud = service[hudStart..hudEnd];
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");
        var drawStart = runtime.IndexOf("private void DrawWorldAndUi", StringComparison.Ordinal);
        var drawEnd = runtime.IndexOf("private bool EnsureEffect", drawStart, StringComparison.Ordinal);
        var draw = runtime[drawStart..drawEnd];

        Assert.Contains("DrawDangerBorder", hud, StringComparison.Ordinal);
        Assert.Contains("worldEffect!", draw, StringComparison.Ordinal);
        Assert.Contains("Game1.spriteBatch.Draw", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadVisualSlot", hud, StringComparison.Ordinal);
        Assert.DoesNotContain("new Texture2D", hud, StringComparison.Ordinal);
        Assert.DoesNotContain("new SpriteBatch", hud, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("new Effect", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor.Log", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void World_composition_pauses_saturation_but_keeps_offset_distortion_and_independent_colour_inputs()
    {
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.DoesNotContain("parameters.Saturation", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.OffsetX", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.OffsetY", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.DistortionAmount", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.DistortionPhase", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.InsanityColourBlend", runtime, StringComparison.Ordinal);
        Assert.Contains("SanityWorldColourPolicy.ResolvePhase", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_borrows_existing_loader_slot_and_never_disposes_texture()
    {
        var source = ReadVisual();

        Assert.Contains("resources.LoadVisualSlot(DangerBorderProfileId, 0)", source, StringComparison.Ordinal);
        Assert.Contains("XnaSanityTextureResource", source, StringComparison.Ordinal);
        Assert.Contains("preview.IsPlaceholder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("texture.Dispose", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SanityRuntimeResourceLoader", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_adapter_uses_owned_negative_token_and_never_passout_side_effects()
    {
        var service = ReadVisual();
        var runtime = ReadRuntime("SanityIdlePresentationRuntime.cs");

        Assert.Contains("player.IsBusyDoingSomething()", service, StringComparison.Ordinal);
        Assert.Contains("player.isMoving()", service, StringComparison.Ordinal);
        Assert.Contains("player.movementDirections.Count", service, StringComparison.Ordinal);
        Assert.Contains("sprite.PauseForSingleAnimation", service, StringComparison.Ordinal);
        Assert.Contains("sprite.IsPlayingBasicAnimation(player.FacingDirection, player.IsCarrying())", service, StringComparison.Ordinal);
        Assert.Contains("Game1.currentGameTime.ElapsedGameTime", service, StringComparison.Ordinal);
        Assert.Contains("Events.Input.ButtonPressed +=", service, StringComparison.Ordinal);
        Assert.Contains("Events.Input.MouseWheelScrolled +=", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Events.Input.CursorMoved +=", service, StringComparison.Ordinal);
        Assert.Contains("sprite.animateOnce(animation)", runtime, StringComparison.Ordinal);
        Assert.Contains("SanityIdlePresentationContract.AnimationToken", runtime, StringComparison.Ordinal);
        Assert.Contains("sprite.loopThisAnimation = true", runtime, StringComparison.Ordinal);
        Assert.Contains("IsOwnedToken", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("animateOnce(293", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("passOutFromTired", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("CanMove =", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain(".Halt(", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_is_owner_screen_session_effective_and_resource_scoped()
    {
        var source = ReadVisual();

        Assert.Contains("lifecycle.EventOwnerCoverageChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.WorldBoundaryStarting +=", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.SessionClearing +=", source, StringComparison.Ordinal);
        Assert.Contains("resources.WorldResourcesReleasing +=", source, StringComparison.Ordinal);
        Assert.Contains("effectiveSanity.TryGetEffectiveRatio", source, StringComparison.Ordinal);
        Assert.Contains("new SanityEffectiveOverlayKey(playerKey, screenId, lifecycle.SessionId)", source, StringComparison.Ordinal);
        Assert.Contains("RemoveScreen(change.Key.ScreenId)", source, StringComparison.Ordinal);
        Assert.Contains("pair.Key == Context.ScreenId", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseBorrowedDangerBorder(resetLoadAttempt: true)", source, StringComparison.Ordinal);
        Assert.Contains("worldComposition.RemoveScreen(screenId)", source, StringComparison.Ordinal);
        Assert.Contains("idlePresentation.CancelAll()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mod_entry_wires_visual_config_without_touching_audio_or_music_switches()
    {
        var source = ReadContract("ShadowProjection", "ModEntry.cs");
        var service = ReadVisual();

        Assert.Contains("ConfigKeys.EnableSanityVisualEffects", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.EnableLowSanityScreenDistortion", source, StringComparison.Ordinal);
        Assert.Contains("new SanitySmapiVisualService", source, StringComparison.Ordinal);
        Assert.Contains("ModManifest.UniqueID", source, StringComparison.Ordinal);
        Assert.Contains("_sanityVisual?.SetEnabled", source, StringComparison.Ordinal);
        Assert.Contains("_sanityVisual?.SetScreenDistortionEnabled", source, StringComparison.Ordinal);
        Assert.Contains("screenDistortionEnabled", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_is_exact_version_backend_shape_gated_and_exactly_unpatched()
    {
        var source = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.Contains("SanityRendererCapabilityGate.Validate", source, StringComparison.Ordinal);
        Assert.Contains("\"MonoGame.OpenGL.GL\"", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Postfix", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Prefix", source, StringComparison.Ordinal);
        Assert.Contains("harmony.Unpatch(method, patchType, patchOwnerId)", source, StringComparison.Ordinal);
        Assert.Contains("worldEffect.IsDisposed", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(worldEffect.GraphicsDevice, device)", source, StringComparison.Ordinal);
    }

    private static string ReadVisual()
    {
        return ReadContract("VisualRuntime", "SanitySmapiVisualService.cs");
    }

    private static string ReadRuntime(string fileName)
    {
        return ReadContract("VisualRuntime", fileName);
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
